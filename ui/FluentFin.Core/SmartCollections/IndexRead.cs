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
/// <para>
/// 1.8 (P1-2): the people come back, after the titles (<see cref="PeopleAsync"/>, <see cref="Anchors.PeoplePass"/>): the top
/// three billed only; every library's people once, then only the new titles' by id.
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
	internal static int? CountOf(string? check) =>
		check?.Split(':')[0] is { } count && int.TryParse(count, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;

	// 1.7.5 (fx1, BH-171-m5)
	/// <summary>The first page of a library's titles saved since the last read; the pages after it are <see cref="SavedSincePageMost"/> each.</summary>
	public const int SavedSincePageFirst = 100;

	/// <summary>The size of a library's titles saved since the last read, past its first page.</summary>
	public const int SavedSincePageMost = 1000;

	/// <summary>
	/// The most pages a library's titles saved since the last read are read in: more than this (a big import, the server's
	/// metadata refreshed) is read whole, as before.
	/// </summary>
	public const int SavedSincePagesMost = 5;

	/// <summary>
	/// How long before the saved index's read the titles saved since are asked from: the read's own time is this PC's clock,
	/// the server's save dates its own, and a title saved during the read may have come after its page.
	/// </summary>
	public static readonly TimeSpan SavedSinceMargin = TimeSpan.FromDays(1);

	/// <summary>
	/// Whether a library change may be read as the titles saved since the last read (<see cref="AddedAsync"/>), from what is
	/// known before asking: both counts, no fewer titles than before, an index of this revision with its read's date. Else every
	/// title is read at once.
	/// </summary>
	public static bool MayBeAdded(IndexSnapshot known, int? was, int? now) =>
		was is { } before && now is { } after && after >= before && known.BackingKnown && known.ReadAt > DateTimeOffset.MinValue + SavedSinceMargin;

	/// <summary>
	/// Nixie 1.7.5 (BH-171-m5): a library change the server's check confirmed, read as only the titles each library's server
	/// saved since the saved index was read (MinDateLastSaved, from its <see cref="IndexSnapshot.ReadAt"/> less
	/// <see cref="SavedSinceMargin"/>), merged into <paramref name="known"/> (a title it had is taken as the server has it now).
	/// A title added is saved when it is added, whatever its DateCreated (a file's own date, years back, on a server that dates
	/// by the file): the first build read newest first by DateCreated and missed it, so a removal at the same time hid behind
	/// it (the 1.7.5 review). A library that changed every day meant a read of every title about once a start. Null, with why,
	/// when that can't be all that changed, and every title is read as before: a library's count didn't move by exactly the
	/// titles found in it (a title removed), the libraries changed, the index is of an older revision, a page failed, or more
	/// than <see cref="SavedSincePagesMost"/> pages of a library were saved since. 1.8.1 (BH-175-m3; the review): each indexed
	/// library's count is asked (<see cref="IJellyfinClient.GetLibraryTitleCount"/>, one request of one title), and compared with
	/// what the saved index has of it: one added in a library the index skips no longer hides one removed from an indexed one,
	/// and a title saved there doesn't make every title read.
	/// </summary>
	/// <param name="was">The count of the check the saved index was compared with.</param>
	/// <param name="now">The count of the check that found the change.</param>
	/// <param name="check">The check that found the change: the merged index's.</param>
	public async Task<(IndexSnapshot? Index, string Why)> AddedAsync(IndexSnapshot known, int? was, int? now, string check, CancellationToken cancellationToken)
	{
		if (was is not { } before || now is not { } after)
		{
			return (null, "the server's counts aren't known");
		}

		if (after < before)
		{
			return (null, "titles were removed");
		}

		if (!known.BackingKnown)
		{
			return (null, "the saved index is of an older revision");
		}

		var since = known.ReadAt - SavedSinceMargin;
		var views = await client.GetUserViewsOrNull().ConfigureAwait(false);
		if (views is null)
		{
			return (null, "the libraries couldn't be read");
		}

		cancellationToken.ThrowIfCancellationRequested();
		_libraries = [.. views.Where(v => v.Id is not null && JellyfinClient.IndexKinds(v) is not null)];
		if (!_libraries.Select(l => l.Id!.Value).ToHashSet().SetEquals(known.Libraries.Select(l => l.Id)))
		{
			return (null, "the libraries changed");
		}

		// each library's titles saved since, two libraries at a time: one request a library when few were
		var found = new List<BaseItemDto>?[_libraries.Count];
		var requests = 0;

		// the pages of one read of the titles saved since: all of them, or null when a page failed or there are more than
		// SavedSincePagesMost (read whole)
		async Task<List<BaseItemDto>?> SavedSinceAsync(Func<int, int, bool, CancellationToken, Task<BaseItemDtoQueryResult?>> page, CancellationToken token)
		{
			var mine = new List<BaseItemDto>();
			var (start, limit) = (0, SavedSincePageFirst);
			int? total = null;
			for (var n = 0; n < SavedSincePagesMost; n++)
			{
				Interlocked.Increment(ref requests);
				if (await page(start, limit, n == 0, token).ConfigureAwait(false) is not { Items: { } items } answer)
				{
					return null;
				}

				total ??= answer.TotalRecordCount;
				mine.AddRange(items);
				start += items.Count;
				if (items.Count < limit || (total is { } all && start >= all))
				{
					return mine;
				}

				limit = SavedSincePageMost;
			}

			return null;
		}

		// 1.8.1 (BH-175-m3; the review): each library's own count now (one request of one title), then its titles saved since; a
		// library at a time per task, two at a time in all
		var counted = new int?[_libraries.Count];
		await Parallel.ForEachAsync(Enumerable.Range(0, _libraries.Count), new ParallelOptions { MaxDegreeOfParallelism = IndexPages.AtOnce, CancellationToken = cancellationToken },
			async (library, token) =>
			{
				Interlocked.Increment(ref requests);
				if ((counted[library] = await client.GetLibraryTitleCount(_libraries[library], token).ConfigureAwait(false)) is not null)
				{
					found[library] = await SavedSinceAsync((start, limit, total, t) => client.GetLibraryIndexPageSavedSince(_libraries[library], since, start, limit, total, t), token).ConfigureAwait(false);
				}
			}).ConfigureAwait(false);
		if (found.Any(x => x is null) || counted.Any(x => x is null))
		{
			return (null, "a library's count or its titles saved since couldn't be read, or there are too many");
		}

		// each title once, with every library it came in (as a read's pages: P0-2)
		var libraryOf = new Dictionary<Guid, List<int>>();
		var items = new List<BaseItemDto>();
		for (var library = 0; library < found.Length; library++)
		{
			foreach (var item in found[library]!.Where(x => x.Id is not null))
			{
				if (!libraryOf.TryGetValue(item.Id!.Value, out var libraries))
				{
					libraryOf[item.Id.Value] = libraries = [];
					items.Add(item);
				}

				if (!libraries.Contains(library))
				{
					libraries.Add(library);
				}
			}
		}

		var at = new Dictionary<Guid, int>(known.Entries.Count);
		for (var i = 0; i < known.Entries.Count; i++)
		{
			at.TryAdd(known.Entries[i].Id, i);
		}

		// every title added since is among them (whatever its DateCreated), so each library's count moving by exactly its own says
		// none went. 1.8.1 (BH-175-m3; the review): counted library by library, the indexed ones only. The server's check counts
		// every library's films and shows: one removed from Movies while one was added to a library the index skips (a mixed
		// one, one hidden from My Media) moved it by 0, and the removed title stayed in Made by Nixie until the day's read; and
		// a title saved in such a library (added or only changed) has nothing to do with the index
		var added = items.Count(x => !at.ContainsKey(x.Id!.Value));
		for (var library = 0; library < _libraries.Count; library++)
		{
			var id = _libraries[library].Id!.Value;
			var had = 0;
			foreach (var entry in known.Entries)
			{
				if (entry.InLibrary(id))
				{
					had++;
				}
			}

			var addedHere = found[library]!.Where(x => x.Id is not null).Select(x => x.Id!.Value).Distinct().Count(x => !at.ContainsKey(x));
			if (counted[library] != had + addedHere)
			{
				return (null, $"library {library + 1} of {_libraries.Count}: {addedHere} titles added since the last read, but its count moved by {counted[library] - had}");
			}
		}

		var entries = known.Entries.ToList();
		foreach (var item in items)
		{
			var mine = libraryOf[item.Id!.Value].Select(l => _libraries[l].Id!.Value).ToList();
			if (at.TryGetValue(item.Id.Value, out var i))
			{
				// a title the index had, saved again (changed): as the server has it now, in every library it was in
				var had = entries[i];
				var libraries = new[] { had.LibraryId }.Concat(had.LibraryIds).Concat(mine).Distinct().ToList();
				entries[i] = IndexEntry.From(item, had.LibraryId, libraries.Count > 1 ? libraries.Skip(1) : null) ?? had;
			}
			else if (IndexEntry.From(item, mine[0], mine.Count > 1 ? mine.Skip(1) : null) is { } entry)
			{
				entries.Add(entry);
			}
		}

		log?.LogInformation("Library index: {Added} titles added and {Changed} changed since the last read, read in {Requests} requests (every title was not read)",
			added, items.Count - added, requests);
		return (known with { Entries = entries, Check = check }, "");
	}
	// end 1.7.5

	// 1.8 (P1-2, stream a)
	/// <summary>The size the whole people pass's pages start at (they double while the server is quick: <see cref="IndexPages"/>).</summary>
	public const int PeoplePageSize = IndexPages.MinCarriedSize;

	/// <summary>1.8: how often a long people step hands what came so far to be kept (<see cref="LibraryIndex"/>'s; tests shorten it).</summary>
	public TimeSpan PeopleKeptEvery { get; init; } = Anchors.PeoplePass.DefaultKeepEvery;

	/// <summary>
	/// 1.8 (P1-2): the people of <paramref name="index"/>'s titles (read by this read, or by <see cref="LibrariesOfAsync"/>'s
	/// libraries): every library's when a whole pass is due (<see cref="IndexSnapshot.PeopleDue"/>, or the index has none),
	/// else those still to ask (<see cref="IndexSnapshot.PeopleToAsk"/>) by id. The index with what came, the rest still to
	/// ask; never null, also when cut short (review, Major: what came is kept). <paramref name="keep"/> keeps what came so far
	/// during a long step.
	/// </summary>
	public Task<Anchors.PeopleRun> PeopleAsync(IndexSnapshot index, Anchors.PeoplePass.Keep? keep, CancellationToken cancellationToken) =>
		new Anchors.PeoplePass(log) { KeepEvery = PeopleKeptEvery }.RunAsync(index, index, index.PeopleToAsk, _libraries, client.GetLibraryPeoplePage, client.GetPeopleByIds,
			PeoplePageSize, cancellationToken, keep);

	/// <summary>
	/// 1.8 (the people alone, for an index whose people are due): the server's libraries that <paramref name="known"/> was read
	/// from; false when they couldn't be read, or none of them is there any more.
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
		return _libraries.Count > 0;
	}
	// end 1.8

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
