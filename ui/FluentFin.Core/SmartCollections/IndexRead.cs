using System.Diagnostics;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.SmartCollections;

/// <summary>
/// Nixie 1.7.1 (the index on a big server): one read of the library index, in two steps. <see cref="TitlesAsync"/>: the
/// server's check, the libraries, every title (<see cref="IndexPages"/>, two pages at a time) and the played episodes: an
/// index every kind but the people's is made of. <see cref="PeopleAsync"/>: the people pass on top of it (about twice the
/// titles' cost on the owner's server: 682 s of 1,198), lean, also two pages at a time.
/// </summary>
internal sealed class IndexRead(IJellyfinClient client, TimeProvider time, ILogger? log)
{
	private IReadOnlyList<BaseItemDto> _libraries = [];

	/// <summary>Review M3: the size of the titles' pages, from the read before; smaller after a page ran out of time.</summary>
	public int TitlesPageSize { get; set; } = IndexPages.PageSize;

	/// <summary>Review M3: the size of the people's pages, from the read before; smaller after a page ran out of time.</summary>
	public int PeoplePageSize { get; set; } = IndexPages.PageSize;

	/// <summary>BH-171-M1: the played episodes didn't come (the index has none of them: a read with an index before keeps its plays).</summary>
	public bool PlaysFailed { get; private set; }

	/// <summary>BH-171-m5: how long the server's check may take.</summary>
	public TimeSpan CheckTimeout { get; init; } = LibraryIndex.DefaultCheckTimeout;

	/// <summary>How far the titles have got, at each page (off the UI thread).</summary>
	public event Action<IndexProgress>? Progress;

	/// <summary>The titles read so far, as an index (<see cref="IndexSnapshot.Reading"/> set), at most every <see cref="LibraryIndex.PartialEvery"/>.</summary>
	public event Action<IndexSnapshot>? Partial;

	/// <summary>The index without its people; null when the libraries or a page of titles could not be read.</summary>
	public async Task<IndexSnapshot?> TitlesAsync(CancellationToken cancellationToken)
	{
		using var checkLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		checkLimit.CancelAfter(CheckTimeout); // BH-171-m5: a check that doesn't answer is not known, not a wait of 100 s
		var checking = client.GetIndexCheck(checkLimit.Token); // asked beside the libraries, not before them
		var views = await client.GetUserViewsOrNull().ConfigureAwait(false);
		var check = await checking.ConfigureAwait(false);
		if (views is null)
		{
			return null;
		}

		cancellationToken.ThrowIfCancellationRequested();
		_libraries = [.. views.Where(v => v.Id is not null && JellyfinClient.IndexKinds(v) is not null)];
		var libraries = _libraries.Select(l => new IndexLibrary(l.Id!.Value, l.Name ?? "", l.CollectionType == BaseItemDto_CollectionType.Movies ? IndexKind.Movie : IndexKind.Series)).ToList();
		IndexKind? only = libraries.Count > 0 && libraries.All(l => l.Kind == libraries[0].Kind) ? libraries[0].Kind : null;
		var pages = new IndexPages(_libraries, client.GetLibraryIndexPage, "titles", log, TitlesPageSize);
		var watch = Stopwatch.StartNew();
		var lastPartial = TimeSpan.MinValue;
		var partialLock = new Lock();
		pages.PageIn += (read, total) =>
		{
			var progress = new IndexProgress(read, total, only);
			Progress?.Invoke(progress);
			if (Partial is not { } partial)
			{
				return;
			}

			lock (partialLock)
			{
				// the first page's rows at once, then every few seconds (each is every collection made again on the page)
				if (lastPartial != TimeSpan.MinValue && watch.Elapsed - lastPartial < LibraryIndex.PartialEvery)
				{
					return;
				}

				lastPartial = watch.Elapsed;
			}

			partial(new IndexSnapshot(libraries, Entries(pages), new Dictionary<Guid, SeriesPlays>(), false, time.GetUtcNow()) { Reading = progress });
		};

		try
		{
			if (!await pages.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				return null;
			}
		}
		finally
		{
			TitlesPageSize = pages.PageSizeNow; // review M3
		}

		var made = Stopwatch.StartNew();
		var entries = Entries(pages);
		log?.LogInformation("Library index: {Titles} titles made into the index in {Ms} ms", entries.Count, made.ElapsedMilliseconds);
		var plays = await PlayedAsync(cancellationToken).ConfigureAwait(false);
		PlaysFailed = plays is null; // BH-171-M1
		return new IndexSnapshot(libraries, entries, plays ?? new Dictionary<Guid, SeriesPlays>(), false, time.GetUtcNow()) { Check = check };
	}

	private List<IndexEntry> Entries(IndexPages pages) =>
		[.. pages.Items().Select(x => IndexEntry.From(x.Item, _libraries[x.Library].Id!.Value)).OfType<IndexEntry>()];

	/// <summary>
	/// The index with its people (<see cref="IndexSnapshot.PeopleDue"/> no longer); null when the people pass failed (review
	/// M2: the caller keeps the people it had, lent by <see cref="Lend"/>, and reads them again later).
	/// </summary>
	public async Task<IndexSnapshot?> PeopleAsync(IndexSnapshot titles, CancellationToken cancellationToken)
	{
		var pages = new IndexPages(_libraries, client.GetLibraryPeoplePage, "people", log, PeoplePageSize);
		try
		{
			if (!await pages.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				cancellationToken.ThrowIfCancellationRequested();
				return null;
			}
		}
		finally
		{
			PeoplePageSize = pages.PageSizeNow; // review M3
		}

		var people = new Dictionary<Guid, IndexPerson[]>();
		foreach (var (_, item) in pages.Items())
		{
			if (item.Id is { } id)
			{
				people.TryAdd(id, IndexEntry.PeopleOf(item));
			}
		}

		return titles with { Entries = [.. titles.Entries.Select(x => people.TryGetValue(x.Id, out var p) ? x with { People = p } : x)], HasPeople = true, PeopleDue = false };
	}

	/// <summary>
	/// Review M2: the people pass alone, over the libraries of an index read before (<paramref name="known"/>: its titles stay);
	/// false when the server's libraries could not be read.
	/// </summary>
	public async Task<bool> LibrariesOfAsync(IndexSnapshot known)
	{
		var views = await client.GetUserViewsOrNull().ConfigureAwait(false);
		if (views is null)
		{
			return false;
		}

		var ids = known.Libraries.Select(l => l.Id).ToHashSet();
		_libraries = [.. views.Where(v => v.Id is { } id && ids.Contains(id) && JellyfinClient.IndexKinds(v) is not null)];
		return _libraries.Count > 0 || ids.Count == 0; // re-review (Minor): none of its libraries is there: no people were read
	}

	/// <summary>
	/// An index read before (<paramref name="previous"/>) lends its people to the titles it had, so that new titles never take
	/// the Actor and Director collections away while their own people pass is to come or failed; none when it had none.
	/// </summary>
	public static IndexSnapshot Lend(IndexSnapshot titles, IndexSnapshot previous)
	{
		if (!previous.HasPeople)
		{
			return titles;
		}

		var before = previous.Entries.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().People);
		return titles with { Entries = [.. titles.Entries.Select(x => before.TryGetValue(x.Id, out var p) ? x with { People = p } : x)], HasPeople = true };
	}

	// the user's played episodes, latest first: each show's count and last play ("Unfinished series"); BH-171-M1: null when
	// they didn't come (an empty answer said "none played", and a read again saved that)
	private async Task<Dictionary<Guid, SeriesPlays>?> PlayedAsync(CancellationToken cancellationToken)
	{
		var plays = new Dictionary<Guid, SeriesPlays>();
		if (!_libraries.Any(l => l.CollectionType == BaseItemDto_CollectionType.Tvshows))
		{
			return plays;
		}

		var played = new BaseItemDto { Name = "played episodes" };
		var reader = new LibraryPageReader(played, (start, limit, total, ct) => client.GetPlayedEpisodesPage(start, limit, total, ct)) { Log = log };
		if (await reader.ReadAllAsync(cancellationToken).ConfigureAwait(false) is not { } episodes)
		{
			return null;
		}

		foreach (var show in episodes.Where(x => x.SeriesId is not null).GroupBy(x => x.SeriesId!.Value))
		{
			var last = show.Select(x => x.UserData?.LastPlayedDate).Where(x => x is not null).DefaultIfEmpty(null).Max();
			plays[show.Key] = new SeriesPlays(show.Count(), last ?? DateTimeOffset.MinValue);
		}

		return plays;
	}
}
