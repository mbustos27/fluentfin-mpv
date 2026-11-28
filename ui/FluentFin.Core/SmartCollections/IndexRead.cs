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
		var began = time.GetUtcNow(); // 1.9: what the next read of what changed asks from (TitlesAt)
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
		return new IndexSnapshot(libraries, entries, plays ?? new Dictionary<Guid, SeriesPlays>(), false, time.GetUtcNow()) { Check = check, PlaysDue = plays is null, TitlesAt = began };
	}

	// 1.7.3 (P0-2): each title once, with every library it came in
	private List<IndexEntry> Entries(IndexPages pages) =>
		[.. pages.Items().Select(x => IndexEntry.From(x.Item, _libraries[x.Libraries[0]].Id!.Value, x.Libraries.Count > 1 ? x.Libraries.Select(l => _libraries[l].Id!.Value) : null)).OfType<IndexEntry>()];

	// 1.7.3 (BH-171-q1): how many films and shows the server's check counted ("25736:<id>"); null when it didn't answer
	internal static int? CountOf(string? check) =>
		check?.Split(':')[0] is { } count && int.TryParse(count, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;

	// 1.7.5 (fx1, BH-171-m5)
	/// <summary>
	/// How long before the index's titles were last brought up to date the titles saved since are asked from: the read's own
	/// time is this PC's clock, the server's save dates its own, and a title saved during the read may have come after its page.
	/// </summary>
	public static readonly TimeSpan SavedSinceMargin = TimeSpan.FromDays(1);

	// the review's Major 2 (the index's load): a read of what changed reads at most this many titles saved since (and of the
	// libraries new to the index): 10,000, or a quarter of the index, but never fewer than 1,000 (a small server's day of saves)
	public const int SavedSinceMostTitles = 10_000;

	public const int SavedSinceLeastCap = 1_000;

	/// <summary>The review's Major 2: the most titles saved since a read of what changed reads for <paramref name="known"/>.</summary>
	public static int SavedSinceMost(IndexSnapshot known) => Math.Min(SavedSinceMostTitles, Math.Max(SavedSinceLeastCap, known.Entries.Count / 4));

	/// <summary>
	/// The review's Major 2: the last <see cref="ChangedAsync"/> stopped at its first pages: more titles were saved since than it
	/// reads (<see cref="SavedSinceMost"/>). The caller reads every title when the day allows, else leaves them to the day's read.
	/// </summary>
	public bool TooManySaved { get; private set; }

	/// <summary>
	/// Nixie 1.7.5 (BH-171-m5); 1.9 (the index's load on a server that isn't the user's): a library change, or R within the day
	/// of the last read of every title, read as only what changed: the titles each library's server saved since the index's
	/// titles were last brought up to date (MinDateLastSaved, from <see cref="IndexSnapshot.TitlesAt"/> less
	/// <see cref="SavedSinceMargin"/>), paged as every title is (<see cref="IndexPages"/>: two at a time, a slow page halved), every
	/// title of a library the index didn't have, and each library's own count (<see cref="IJellyfinClient.GetLibraryTitleCount"/>,
	/// one request of one title); merged into <paramref name="known"/> (a title it had is taken as the server has it now, and a
	/// library gone goes with its titles). A title added is saved when it is added, whatever its DateCreated, so every addition
	/// is among them. A library whose count moved otherwise than by its titles new to it lost titles this read can't name: the
	/// index says so (<see cref="IndexSnapshot.RemovalsDue"/>, Why), and the caller reads every title when the day allows.
	/// 1.9: more than 4,100 titles saved since, fewer titles in all, and a library added or removed are read here too: each made
	/// a read of every title (about 190 requests of 1,000 titles on a server of 190,000). Null, with why, when the libraries, a
	/// count or a page couldn't be read, or the index is of an older format.
	/// </summary>
	/// <param name="check">The server's check that found the change: the merged index's; null, asked here (R).</param>
	public async Task<(IndexSnapshot? Index, string Why)> ChangedAsync(IndexSnapshot known, string? check, CancellationToken cancellationToken)
	{
		if (!known.BackingKnown)
		{
			return (null, "the saved index is of an older format");
		}

		var began = time.GetUtcNow();
		var since = (known.TitlesAt < began ? known.TitlesAt : began) - SavedSinceMargin; // the review: a time after now (the clock moved back) asks from now
		var requests = 0;
		if (check is null)
		{
			using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			limit.CancelAfter(CheckTimeout);
			requests++;
			check = await client.GetIndexCheck(limit.Token).ConfigureAwait(false);
		}

		var views = await client.GetUserViewsOrNull().ConfigureAwait(false);
		if (views is null)
		{
			return (null, "the libraries couldn't be read");
		}

		cancellationToken.ThrowIfCancellationRequested();
		_libraries = [.. views.Where(v => v.Id is not null && JellyfinClient.IndexKinds(v) is not null)];
		var had = known.Libraries.Select(l => l.Id).ToHashSet();
		var isNew = _libraries.Select(l => !had.Contains(l.Id!.Value)).ToArray();

		// 1.8.1 (BH-175-m3): each library the index had, its own count now (one request of one title), two at a time
		var counted = new int?[_libraries.Count];
		await Parallel.ForEachAsync(Enumerable.Range(0, _libraries.Count).Where(l => !isNew[l]), new ParallelOptions { MaxDegreeOfParallelism = IndexPages.AtOnce, CancellationToken = cancellationToken },
			async (library, token) =>
			{
				Interlocked.Increment(ref requests);
				counted[library] = await client.GetLibraryTitleCount(_libraries[library], token).ConfigureAwait(false);
			}).ConfigureAwait(false);
		if (Enumerable.Range(0, _libraries.Count).Any(l => !isNew[l] && counted[l] is null))
		{
			return (null, "a library's count couldn't be read");
		}

		// then the titles saved since (a library new to the index: every title of it), paged as a read of every title. The
		// review's Major 2: at most SavedSinceMost(known) of them, by the libraries' first pages' counts: past it (a mass save
		// again on the server), the pages stop there (TooManySaved): every R read about every title again within the margin
		var whole = _libraries.Where((_, l) => isNew[l]).Select(l => l.Id!.Value).ToHashSet();
		var most = SavedSinceMost(known);
		var saved = 0;
		using var tooMany = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		var pages = new IndexPages(_libraries, async (library, start, limit, total, token) =>
		{
			var answer = await (whole.Contains(library.Id!.Value)
				? client.GetLibraryIndexPage(library, start, limit, total, token)
				: client.GetLibraryIndexPageSavedSince(library, since, start, limit, total, token)).ConfigureAwait(false);
			if (total && answer?.TotalRecordCount is { } count && Interlocked.Add(ref saved, count) > most)
			{
				TooManySaved = true;
				await tooMany.CancelAsync().ConfigureAwait(false);
			}

			return answer;
		}, "changes", log, TitlesPageSize);
		try
		{
			if (!await pages.ReadAsync(tooMany.Token).ConfigureAwait(false))
			{
				return (null, "a page of the titles saved since couldn't be read");
			}
		}
		catch (OperationCanceledException) when (TooManySaved && !cancellationToken.IsCancellationRequested)
		{
			return (null, $"{Volatile.Read(ref saved)} titles saved since the last read or in a library new to the index, more than a read of what changed reads ({most})");
		}
		finally
		{
			TitlesPageSize = pages.PageSizeNow; // review M3
			requests += pages.Requests;
		}

		var found = pages.Items(); // each title once, with every library it came in (P0-2)

		// every title added since is among them, so each library's count moving by exactly the titles new to it says none went.
		// 1.8.1 (BH-175-m3): library by library, the indexed ones only (one removed from Movies while one is added to a library
		// the index skips moved the server's check by 0). 1.9: a title the index had, new to this library, is new to it too
		var at = new Dictionary<Guid, int>(known.Entries.Count);
		for (var i = 0; i < known.Entries.Count; i++)
		{
			at.TryAdd(known.Entries[i].Id, i);
		}

		var newTo = new int[_libraries.Count];
		foreach (var (libraries, item) in found)
		{
			foreach (var library in libraries)
			{
				if (item.Id is not { } id || !at.TryGetValue(id, out var i) || !known.Entries[i].InLibrary(_libraries[library].Id!.Value))
				{
					newTo[library]++;
				}
			}
		}

		// the review's item 4: a count that fell (titles removed: RemovalsDue) said apart from one that rose (titles came that the
		// titles saved since didn't bring: a library the user was given back, a title that moved without a save)
		string? uncounted = null;
		var fell = false;
		for (var library = 0; library < _libraries.Count; library++)
		{
			var id = _libraries[library].Id!.Value;
			var before = isNew[library] ? 0 : known.Entries.Count(x => x.InLibrary(id));
			if (isNew[library] || counted[library] is not { } now || now == before + newTo[library])
			{
				continue;
			}

			var said = $"library {library + 1} of {_libraries.Count}: {newTo[library]} titles new to it since the last read, but its count moved by {now - before}";
			uncounted ??= said;
			if (now < before + newTo[library])
			{
				fell = true;
				log?.LogInformation("Library index: titles were removed ({Said}): the day's read of every title takes them out", said);
			}
			else
			{
				log?.LogInformation("Library index: titles came that the titles saved since didn't bring ({Said}): the day's read of every title brings them", said);
			}
		}

		// a library gone (removed, or no longer the user's) takes its titles with it; a title also in another stays there
		var current = _libraries.Select(l => l.Id!.Value).ToHashSet();
		var entries = new List<IndexEntry>(known.Entries.Count + found.Count);
		foreach (var entry in known.Entries)
		{
			if (current.Contains(entry.LibraryId) && Array.TrueForAll(entry.LibraryIds, current.Contains))
			{
				entries.Add(entry);
			}
			else if (entry.LibraryIds.Prepend(entry.LibraryId).Where(current.Contains).Distinct().ToList() is [var first, ..] left)
			{
				entries.Add(entry with { LibraryId = first, LibraryIds = [.. left] });
			}
		}

		at.Clear();
		for (var i = 0; i < entries.Count; i++)
		{
			at.TryAdd(entries[i].Id, i);
		}

		var added = 0;
		foreach (var (libraries, item) in found)
		{
			var mine = libraries.Select(l => _libraries[l].Id!.Value).ToList();
			if (item.Id is { } id && at.TryGetValue(id, out var i))
			{
				// a title the index had, saved again (changed): as the server has it now, in every library it was in
				var was = entries[i];
				var all = was.LibraryIds.Prepend(was.LibraryId).Concat(mine).Distinct().ToList();
				entries[i] = IndexEntry.From(item, was.LibraryId, all.Count > 1 ? all.Skip(1) : null) ?? was;
			}
			else if (IndexEntry.From(item, mine[0], mine.Count > 1 ? mine.Skip(1) : null) is { } entry)
			{
				at[entry.Id] = entries.Count;
				entries.Add(entry);
				added++;
			}
		}

		var gone = known.Entries.Count + added - entries.Count;
		log?.LogInformation("Library index: {Added} titles added and {Changed} changed since the last read, read in {Requests} requests (every title was not read)",
			added, found.Count - added, requests);
		if (whole.Count > 0 || gone > 0)
		{
			log?.LogInformation("Library index: {New} libraries new to the index read whole, {Gone} titles gone with a library", whole.Count, gone);
		}

		return (known with
		{
			Libraries = Described(_libraries),
			Entries = entries,
			Check = check ?? known.Check,
			TitlesAt = began,
			MarksAt = known.MarksAt, // the review's item 6: the marks' own time, moved only by a read of them
			RemovalsDue = known.RemovalsDue || fell,
		}, uncounted ?? "");
	}

	// 1.9 (the index's load): the user's marks
	/// <summary>A page of the titles played lately.</summary>
	public const int PlayedPageSize = 100;

	/// <summary>The most pages of titles played lately one read asks (500 plays since the last read): more is the day's read's.</summary>
	public const int PlayedPagesMost = 5;

	/// <summary>A page of favourites; more favourites than <see cref="FavoritesMost"/> are left to the day's read.</summary>
	public const int FavoritesPageSize = 500;

	public const int FavoritesMost = 2000;

	/// <summary>The most shows whose played episodes one read counts again (each one request of one episode).</summary>
	public const int ShowsCountedMost = 50;

	/// <summary>What a read of the user's marks brought: the index with them, how many titles changed, why it stopped, and whether the server didn't answer as asked.</summary>
	public sealed record MarksRun(IndexSnapshot? Index, int Changed, string Why, bool Unanswered, int Requests);

	/// <summary>
	/// Nixie 1.9 (the index's load on a server that isn't the user's): the user's played marks, favourites and played shows
	/// brought up to date without reading every title (the day's read brings them otherwise: no save date moves for them).
	/// The films and episodes played, latest first, until one played before <paramref name="since"/> (at most
	/// <see cref="PlayedPagesMost"/> pages): a film's played mark, play count and time; a show with an episode played after
	/// the time the index has: its played episodes counted again and its own marks (<see cref="ShowsCountedMost"/> at most).
	/// The favourites, whole (up to <see cref="FavoritesMost"/>, counted first; only when <paramref name="withFavourites"/>: a start, R):
	/// added and taken off, or, when a page didn't come, none of them (the review's Major 1). A mark taken off a played title waits
	/// for the day's read. MinDateLastSavedForUser can't do this: on 10.11.11 and 12.4.0 it follows the titles' own save
	/// date. A server that answers titles not played, not favourite, or not latest first ignored the filter or the order:
	/// <see cref="MarksRun.Unanswered"/>, nothing applied, and at most a page asked. Index null when a request failed.
	/// </summary>
	public async Task<MarksRun> MarksAsync(IndexSnapshot known, DateTimeOffset since, CancellationToken cancellationToken, bool withFavourites = true)
	{
		// the review's item 5: a 401 or 403 on any of them ends the read (the recounts left are cancelled with it), and no marks are
		// asked again that run, as for a server that ignores the filter
		try
		{
			return await MarksReadAsync(known, since, cancellationToken, withFavourites).ConfigureAwait(false);
		}
		catch (IndexRefusedException ex)
		{
			return new(null, 0, $"the server refused them (HTTP {ex.Status})", true, 0);
		}
	}

	private async Task<MarksRun> MarksReadAsync(IndexSnapshot known, DateTimeOffset since, CancellationToken cancellationToken, bool withFavourites)
	{
		var requests = 0;
		var films = new Dictionary<Guid, BaseItemDto>();
		var showPlayed = new Dictionary<Guid, DateTimeOffset>();
		var crossed = false;
		DateTimeOffset? last = null;
		for (var page = 0; page < PlayedPagesMost && !crossed; page++)
		{
			requests++;
			if (await client.GetPlayedLatelyPage(page * PlayedPageSize, PlayedPageSize, cancellationToken).ConfigureAwait(false) is not { Items: { } items })
			{
				return new(null, 0, "the titles played lately couldn't be read", false, requests);
			}

			foreach (var item in items)
			{
				if (item.UserData?.Played != true || item.UserData.LastPlayedDate is { } outOfOrder && last is { } newer && outOfOrder > newer + TimeSpan.FromSeconds(1))
				{
					return new(null, 0, "the server didn't answer the titles played, latest first", true, requests);
				}

				if (item.UserData.LastPlayedDate is not { } played || played < since)
				{
					crossed = true; // played before the last read (or marked played with no time): what follows is older
					break;
				}

				last = played;
				if (item.Id is not { } id)
				{
					continue;
				}

				if (item.Type == BaseItemDto_Type.Movie)
				{
					films.TryAdd(id, item);
				}
				else if (item.SeriesId is { } show && (!showPlayed.TryGetValue(show, out var was) || played > was))
				{
					showPlayed[show] = played;
				}
			}

			crossed |= items.Count < PlayedPageSize;
		}

		// the favourites, whole: their count first (one request of one), none past FavoritesMost (the day's read's), then every
		// page. Only at a start and on R (withFavourites). The review's Major 1: a page that fails or ends early applies nothing
		// (each favourite on a later page was taken off); every page holds favourites only, or the server ignored the filter
		HashSet<Guid>? favourites = null;
		if (withFavourites)
		{
			requests++;
			if (await client.GetFavoritesPage(0, 1, cancellationToken).ConfigureAwait(false) is not { Items: { } favouritesCounted } answer)
			{
				return new(null, 0, "the favourites couldn't be read", false, requests);
			}

			if (favouritesCounted.Any(x => x.UserData?.IsFavorite != true))
			{
				return new(null, 0, "the server didn't answer the favourites alone", true, requests);
			}

			if (answer.TotalRecordCount is { } total && total <= FavoritesMost)
			{
				var all = new List<BaseItemDto>(total);
				while (all.Count < total)
				{
					requests++;
					if (await client.GetFavoritesPage(all.Count, FavoritesPageSize, cancellationToken).ConfigureAwait(false) is not { Items: { Count: > 0 } more })
					{
						return new(null, 0, $"a page of the favourites couldn't be read ({all.Count} of {total} in)", false, requests);
					}

					if (more.Any(x => x.UserData?.IsFavorite != true))
					{
						return new(null, 0, "the server didn't answer the favourites alone", true, requests);
					}

					all.AddRange(more);
				}

				favourites = [.. all.Where(x => x.Id is not null).Select(x => x.Id!.Value)];
			}
		}
		// the shows of the index played after the time the index has for them: counted again, and their own marks
		var at = new Dictionary<Guid, int>(known.Entries.Count);
		for (var i = 0; i < known.Entries.Count; i++)
		{
			at.TryAdd(known.Entries[i].Id, i);
		}

		var shows = showPlayed.Where(x => at.TryGetValue(x.Key, out var i) && known.Entries[i].Kind == IndexKind.Series
				&& (!known.SeriesPlays.TryGetValue(x.Key, out var had) || x.Value > had.LastPlayed))
			.OrderByDescending(x => x.Value).Take(ShowsCountedMost).ToList();
		var counted = new int?[shows.Count];
		await Parallel.ForEachAsync(Enumerable.Range(0, shows.Count), new ParallelOptions { MaxDegreeOfParallelism = IndexPages.AtOnce, CancellationToken = cancellationToken },
			async (show, token) =>
			{
				Interlocked.Increment(ref requests);
				counted[show] = await client.GetPlayedEpisodeCount(shows[show].Key, token).ConfigureAwait(false);
			}).ConfigureAwait(false);
		var showData = new Dictionary<Guid, BaseItemDto>();
		if (shows.Count > 0)
		{
			requests++;
			foreach (var show in await client.GetUserDataByIds([.. shows.Select(x => x.Key)], cancellationToken).ConfigureAwait(false) ?? [])
			{
				if (show.Id is { } id)
				{
					showData[id] = show;
				}
			}
		}

		var entries = known.Entries.ToList();
		var changed = 0;
		for (var i = 0; i < entries.Count; i++)
		{
			var was = entries[i];
			var now = was;
			if (films.TryGetValue(was.Id, out var film) && film.UserData is { } data)
			{
				now = now with { Played = data.Played == true, PlayCount = Math.Max(data.PlayCount ?? 0, 0), LastPlayed = data.LastPlayedDate };
			}

			if (showData.TryGetValue(was.Id, out var show) && show.UserData is { } its)
			{
				now = now with { Played = its.Played == true, Unplayed = its.UnplayedItemCount };
			}

			if (favourites is not null && favourites.Contains(was.Id) != was.Favorite)
			{
				now = now with { Favorite = !was.Favorite };
			}

			if (!now.Equals(was))
			{
				entries[i] = now;
				changed++;
			}
		}

		var plays = new Dictionary<Guid, SeriesPlays>(known.SeriesPlays);
		for (var show = 0; show < shows.Count; show++)
		{
			if (counted[show] is { } count)
			{
				plays[shows[show].Key] = new SeriesPlays(count, shows[show].Value);
				changed++;
			}
		}

		log?.LogInformation("Library index: marks since the last read: {Films} films and {Shows} shows played, favourites {Favourites}, {Changed} titles changed, in {Requests} requests{More}",
			films.Count, shows.Count, favourites is not null ? "read" : withFavourites ? "left to the day's read (more than the most)" : "not asked (a start and R ask them)", changed, requests, crossed ? "" : " (more played since: the rest at the day's read)");
		return new(changed == 0 ? known : known with { Entries = entries, SeriesPlays = plays }, changed, crossed ? "" : "more played since than a read of the marks reads", false, requests);
	}
	// end 1.9

	// the libraries read, as the index keeps them
	private static List<IndexLibrary> Described(IReadOnlyList<BaseItemDto> libraries) =>
		[.. libraries.Select(l => new IndexLibrary(l.Id!.Value, l.Name ?? "", l.CollectionType == BaseItemDto_CollectionType.Movies ? IndexKind.Movie : IndexKind.Series))];
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
