using System.Diagnostics;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.SmartCollections;

/// <summary>
/// Nixie 1.7.1 (the index on a big server): one read of the library index. <see cref="TitlesAsync"/>: the server's check, the
/// libraries, every title (<see cref="IndexPages"/>, two pages at a time) and the played episodes.
/// <para>
/// 1.7.3: no people pass (the owner's answer to the 1.7.3 plan: nothing published reads people any more, and the pass cost
/// about twice the titles on the server the owner uses, 682 s of 1,198); a title in two libraries is one entry with both
/// (P0-2); the reading line has the check's count as its total until every library's own is in (BH-171-q1); and the played
/// episodes can be asked again on their own (<see cref="PlaysAsync"/>, BH-171-M1's other half).
/// </para>
/// </summary>
internal sealed class IndexRead(IJellyfinClient client, TimeProvider time, ILogger? log)
{
	private IReadOnlyList<BaseItemDto> _libraries = [];

	/// <summary>Review M3: the size of the titles' pages, from the read before; smaller after a page ran out of time.</summary>
	public int TitlesPageSize { get; set; } = IndexPages.PageSize;

	/// <summary>BH-171-M1: the played episodes didn't come (the index has none of them: a read with an index before keeps its plays).</summary>
	public bool PlaysFailed { get; private set; }

	/// <summary>BH-171-m5: how long the server's check may take.</summary>
	public TimeSpan CheckTimeout { get; init; } = LibraryIndex.DefaultCheckTimeout;

	/// <summary>How far the titles have got, at each page (off the UI thread).</summary>
	public event Action<IndexProgress>? Progress;

	/// <summary>The titles read so far, as an index (<see cref="IndexSnapshot.Reading"/> set), at most every <see cref="LibraryIndex.PartialEvery"/>.</summary>
	public event Action<IndexSnapshot>? Partial;

	/// <summary>The index; null when the libraries or a page of titles could not be read.</summary>
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
		var counted = CountOf(check); // 1.7.3 (BH-171-q1)
		var pages = new IndexPages(_libraries, client.GetLibraryIndexPage, "titles", log, TitlesPageSize);
		var watch = Stopwatch.StartNew();
		var lastPartial = TimeSpan.MinValue;
		var partialLock = new Lock();
		pages.PageIn += (read, total) =>
		{
			// 1.7.3 (BH-171-q1): until every library's first page (and its count) is in, the server's check's count, "of about"
			var progress = total is null && counted is { } about ? new IndexProgress(read, Math.Max(read, about), only) { About = true } : new IndexProgress(read, total, only);
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
		log?.LogInformation("Library index: {Titles} titles made into the index in {Ms} ms ({Shared} in more than one library)", entries.Count, made.ElapsedMilliseconds,
			entries.Count(x => x.LibraryIds.Length > 1));
		var plays = await PlayedAsync(_libraries.Any(l => l.CollectionType == BaseItemDto_CollectionType.Tvshows), cancellationToken).ConfigureAwait(false);
		PlaysFailed = plays is null; // BH-171-M1
		return new IndexSnapshot(libraries, entries, plays ?? new Dictionary<Guid, SeriesPlays>(), false, time.GetUtcNow()) { Check = check, PlaysDue = plays is null };
	}

	// 1.7.3 (P0-2): each title once, with every library it came in
	private List<IndexEntry> Entries(IndexPages pages) =>
		[.. pages.Items().Select(x => IndexEntry.From(x.Item, _libraries[x.Libraries[0]].Id!.Value, x.Libraries.Count > 1 ? x.Libraries.Select(l => _libraries[l].Id!.Value) : null)).OfType<IndexEntry>()];

	// 1.7.3 (BH-171-q1): how many films and shows the server's check counted ("25736:<id>"); null when it didn't answer
	private static int? CountOf(string? check) =>
		check?.Split(':')[0] is { } count && int.TryParse(count, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;

	/// <summary>
	/// 1.7.3 (BH-171-M1's other half): the played episodes alone, for an index whose own didn't come
	/// (<see cref="IndexSnapshot.PlaysDue"/>); null when they didn't come again.
	/// </summary>
	public Task<Dictionary<Guid, SeriesPlays>?> PlaysAsync(IndexSnapshot known, CancellationToken cancellationToken) =>
		PlayedAsync(known.Libraries.Any(l => l.Kind == IndexKind.Series), cancellationToken);

	// the user's played episodes, latest first: each show's count and last play ("Unfinished series"); BH-171-M1: null when
	// they didn't come (an empty answer said "none played", and a read again saved that)
	private async Task<Dictionary<Guid, SeriesPlays>?> PlayedAsync(bool hasShows, CancellationToken cancellationToken)
	{
		var plays = new Dictionary<Guid, SeriesPlays>();
		if (!hasShows)
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
