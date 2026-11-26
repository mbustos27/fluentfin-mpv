using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using FluentFin.Core.SmartCollections.Builders;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.SmartCollections;

/// <summary>
/// Nixie 1.6: every Made by Nixie collection of an index, one builder per kind (the plan's 1.3). Pure and quick (the
/// 1,334-film budget test): the pages call it off the UI thread with the index they have.
/// <para>
/// 1.7.3 (P0-3): the builders are apart. The published ones (<see cref="PublishedBuilders"/>) make what the Collections page,
/// Home, the collection of the day and the presets' published part show (<see cref="BuildAll"/>). The on-demand ones
/// (<see cref="OnDemandBuilders"/>: decades, directors, actors, studios, networks, genre mixes, countries) make a
/// collection only when it is asked for by its id (<see cref="Find"/>, title pages later) or by its kind
/// (<see cref="BuildKinds"/>, a library's filters): "too many, too generic", the owner said of 1.7.2's.
/// </para>
/// </summary>
public static class SmartCollectionCatalog
{
	// 1.7.3 (P0-3): published and on-demand builders apart
	/// <summary>The builders of the kinds Nixie publishes, in the order they are made.</summary>
	public static IReadOnlyList<ISmartCollectionBuilder> PublishedBuilders { get; } =
	[
		.. Anchors.AnchoredSlots.Builders, // 1.8 (P1-2, stream a): Next in, More like, More from (none without an anchor)
		new FranchiseBuilder(), new KeywordBuilder(), new HighlyRatedBuilder(), new UnfinishedSeriesBuilder(), new SeasonalBuilder(), new ShortFilmsBuilder(),
		new AwardBuilder(), // 1.7 keyless: Wikidata's awards (nothing without its lists)
	];

	/// <summary>The builders of the kinds made only when asked for (<see cref="Find"/>, <see cref="BuildKinds"/>), never published.</summary>
	public static IReadOnlyList<ISmartCollectionBuilder> OnDemandBuilders { get; } =
	[
		new DecadeBuilder(), new DirectorBuilder(), new ActorBuilder(), new StudioBuilder(IndexKind.Movie), new StudioBuilder(IndexKind.Series),
		new GenreMixBuilder(), new CountryBuilder(),
	];

	// 1.8 (P1-3, stream s; the review's Major 2)
	/// <summary>
	/// The builders of the kinds published after the first rows (the week's Spotlight): never in <see cref="BuildAllAsync"/>, which
	/// Home's first Nixie row waits on (at 25,000 titles with people, steady rounds took 294–397 ms with the Spotlight, 149–259 ms
	/// without: SpotlightPeople180Tests), but in
	/// <see cref="BuildLaterAsync"/>, which a page calls once its first rows are shown.
	/// </summary>
	public static IReadOnlyList<ISmartCollectionBuilder> LaterBuilders { get; } = [new SpotlightBuilder(onDemand: true)];
	// end 1.8

	/// <summary>Every builder: the published ones, the on-demand ones, then those published later.</summary>
	public static IReadOnlyList<ISmartCollectionBuilder> Builders { get; } = [.. PublishedBuilders, .. OnDemandBuilders, .. LaterBuilders];

	/// <summary>
	/// The kinds Nixie publishes: the Collections page, Home, <see cref="OfTheDay"/>, the presets' published part. Every kind but
	/// the seven 1.7.3 made on demand only (<see cref="OnDemandOnly"/>); the saved filters' and the public lists' (trending)
	/// kinds are published.
	/// </summary>
	public static IReadOnlySet<SmartCollectionKind> Published { get; }

	/// <summary>
	/// The seven kinds 1.7.3 stopped publishing: decades, directors, actors, studios, networks, genre mixes, countries. The one
	/// list of them. Their numbers stay (Home's saved page stores kinds as numbers), and Home's saved rows of them are never
	/// painted. A library's presets strip keeps decades, genre mixes and countries as filters (<see cref="NixiePresets"/>);
	/// pins and saved filters of the other four are removed once (<see cref="RetiredCollections"/>).
	/// </summary>
	public static IReadOnlySet<SmartCollectionKind> OnDemandOnly { get; } = new[]
	{
		SmartCollectionKind.Decade, SmartCollectionKind.Director, SmartCollectionKind.Actor, SmartCollectionKind.Studio, SmartCollectionKind.Network,
		SmartCollectionKind.GenreMix, SmartCollectionKind.Country,
	}.ToFrozenSet();

	static SmartCollectionCatalog() =>
		Published = Enum.GetValues<SmartCollectionKind>().Where(x => !OnDemandOnly.Contains(x)).ToFrozenSet();
	// end 1.7.3

	/// <summary>
	/// The collections Nixie publishes (<see cref="Published"/> kinds only), kind by kind in <see cref="PublishedBuilders"/>'
	/// order, but those of the kinds turned off in Settings and the franchises an admin's box set already is
	/// (<paramref name="boxedFranchises"/>: their TMDb ids). Of the whole index or a part (a library's presets): it says no
	/// census (<see cref="BuildAllAsync"/>, the pages' build of the whole index, does).
	/// </summary>
	/// 1.7 keyless: with the outside lists of <paramref name="outside"/>, else the app's (<see cref="Outside.OutsideListsSnapshot.Shared"/>).
	public static IReadOnlyList<SmartCollection> BuildAll(IndexSnapshot index, DateTimeOffset now, IReadOnlySet<string>? boxedFranchises = null, IReadOnlySet<SmartCollectionKind>? kindsOff = null,
		Outside.OutsideListsSnapshot? outside = null) =>
		BuildPublished(index, now, boxedFranchises, kindsOff, outside, census: false);

	// 1.7.3 (P0-3): the published builders, and only their kinds whatever a builder makes
	private static IReadOnlyList<SmartCollection> BuildPublished(IndexSnapshot index, DateTimeOffset now, IReadOnlySet<string>? boxedFranchises, IReadOnlySet<SmartCollectionKind>? kindsOff,
		Outside.OutsideListsSnapshot? outside, bool census) =>
		[.. BuildAll(PublishedBuilders, index, now, boxedFranchises, kindsOff, outside, Log, census, publishing: true).Where(x => Published.Contains(x.Kind))];

	/// <summary>
	/// 1.7.3: the collections of <paramref name="kinds"/>, published or on demand (a library's presets: its decades, genre
	/// mixes and countries as filters), in <see cref="Builders"/>' order; the franchises an admin's box set already is left
	/// out. Says no census (a library's titles are not the whole index).
	/// </summary>
	/// <param name="whole">1.7.5: every title of each rule, without the display cap (<see cref="BuildContext.Whole"/>): a library's presets strip.</param>
	public static IReadOnlyList<SmartCollection> BuildKinds(IndexSnapshot index, DateTimeOffset now, IReadOnlySet<SmartCollectionKind> kinds, IReadOnlySet<string>? boxedFranchises = null,
		Outside.OutsideListsSnapshot? outside = null, bool whole = false) =>
		BuildAll([.. Builders.Where(x => kinds.Contains(x.Kind))], index, now, boxedFranchises, null, outside, Log, whole: whole);

	/// <summary>
	/// 1.7.3 (P0-3): one collection, of any kind, by its id ("director:avaabara", "franchise:2344", "pick:highly-rated"): made
	/// now from <paramref name="index"/> by its kind's builder alone (title pages, a pin or a saved filter of a kind no longer
	/// published). Null when the index makes none with that id, or the id names no kind Nixie builds.
	/// </summary>
	/// <param name="whole">
	/// 1.7.3 fix round (BH-173-M2): every title of the collection's rule, without the display cap (<see cref="BuildContext.Whole"/>):
	/// a saved filter's preset.
	/// </param>
	public static SmartCollection? Find(IndexSnapshot index, string collectionId, DateTimeOffset now, bool whole = false) => Find(index, collectionId, now, null, Log, whole);

	internal static SmartCollection? Find(IndexSnapshot index, string collectionId, DateTimeOffset now, Outside.OutsideListsSnapshot? outside, ILogger? log, bool whole = false)
	{
		if (string.IsNullOrEmpty(collectionId) || KindOf(collectionId) is not { } kind)
		{
			return null;
		}

		return BuildAll([.. Builders.Where(x => x.Kind == kind)], index, now, null, null, outside, log, whole: whole, titles: CollapsedOf(index)).FirstOrDefault(x => x.Id == collectionId);
	}

	// 1.7.5 (fx1; M1's review of 1.7.3): an index's titles, copies collapsed, made once and kept while the index is. Find is asked
	// once a pin (the Collections page's PinnedOnDemand), and collapsing 36,000 entries took about 170 ms each time
	private static readonly ConditionalWeakTable<IndexSnapshot, Lazy<CanonicalTitles>> Collapsed = new();

	private static CanonicalTitles CollapsedOf(IndexSnapshot index) =>
		Collapsed.GetValue(index, x => new Lazy<CanonicalTitles>(() => BuildContext.Collapse(x.Entries))).Value;
	// end 1.7.5

	/// <summary>1.7.3: the kind a collection's id names ("director:x" a director's); null for one no builder makes (a saved filter's, a public list's).</summary>
	public static SmartCollectionKind? KindOf(string collectionId)
	{
		foreach (var (prefix, kind) in IdPrefixes)
		{
			if (collectionId.StartsWith(prefix, StringComparison.Ordinal))
			{
				return kind;
			}
		}

		return null;
	}

	// the ids each builder makes (Make.Of's first argument)
	private static readonly (string Prefix, SmartCollectionKind Kind)[] IdPrefixes =
	[
		.. Anchors.AnchoredSlots.IdPrefixes, // 1.8 (P1-2, stream a)
		("franchise:", SmartCollectionKind.Franchise), ("decade:", SmartCollectionKind.Decade), ("director:", SmartCollectionKind.Director),
		("actor:", SmartCollectionKind.Actor), ("studio:", SmartCollectionKind.Studio), ("network:", SmartCollectionKind.Network),
		("genres:", SmartCollectionKind.GenreMix), ("keyword:", SmartCollectionKind.Keyword), ("country:", SmartCollectionKind.Country),
		("pick:highly-rated", SmartCollectionKind.HighlyRated), ("pick:short-films", SmartCollectionKind.ShortFilms),
		("pick:unfinished-series", SmartCollectionKind.UnfinishedSeries), ("seasonal:", SmartCollectionKind.Seasonal), ("award:", SmartCollectionKind.Award),
		("spotlight:", SmartCollectionKind.Spotlight), // 1.8 (P1-3, stream s)
	];

	// 1.7.2: a kind whose builder throws is left out, and said once in the log (its kind and the exception's type: no titles);
	// the other kinds still come. One builder's throw (a title listed twice) took Made by Nixie, and the Collections page, down
	/// <summary>1.7.2: where a kind that couldn't be made is said (the app's log); nothing is said without one. 1.7.3: and the census.</summary>
	public static ILogger? Log { get; set; }

	private static readonly ConditionalWeakTable<ILogger, ConcurrentDictionary<SmartCollectionKind, byte>> Said = new();

	// 1.7.3 (P0-1): the indexes a census was said of, by log: whose index, when it was read, how many entries
	private static readonly ConditionalWeakTable<ILogger, ConcurrentDictionary<(string? Key, DateTimeOffset ReadAt, int Entries), byte>> Censused = new();

	internal static IReadOnlyList<SmartCollection> BuildAll(IReadOnlyList<ISmartCollectionBuilder> builders, IndexSnapshot index, DateTimeOffset now, IReadOnlySet<string>? boxedFranchises,
		IReadOnlySet<SmartCollectionKind>? kindsOff, Outside.OutsideListsSnapshot? outside, ILogger? log, bool census = false, bool whole = false, CanonicalTitles? titles = null,
		bool publishing = false)
	{
		var watch = Stopwatch.StartNew();
		// 1.8.1 (fix 7): publishing: the pages' build (BuildContext.Publishing), not one asked for by its id or a library's presets
		var context = new BuildContext(index, now, boxedFranchises ?? new HashSet<string>()) { Outside = outside ?? Outside.OutsideListsSnapshot.Shared, Whole = whole, Collapsed = titles, Publishing = publishing };
		var all = new List<SmartCollection>();
		var said = new List<string>();
		foreach (var builder in builders)
		{
			if (kindsOff?.Contains(builder.Kind) == true)
			{
				said.Add($"{builder.Kind} off");
				continue;
			}

			List<SmartCollection> made;
			try
			{
				made = [.. builder.Build(context).Where(x => x.ItemIds.Count > 0)]; // all of it here: a builder's throw is part way through
			}
			catch (Exception ex)
			{
				if (log is not null && Said.GetOrCreateValue(log).TryAdd(builder.Kind, 0))
				{
					log.LogWarning("Made by Nixie: the {Kind} collections couldn't be made ({Error}); the other kinds still are", builder.Kind, ex.GetType().Name);
				}

				said.Add($"{builder.Kind} failed");
				continue;
			}

			var gate = context.Gates.Of(builder.Kind) ?? new BuildGates.Gate(made.Count, made.Count, made.Count);
			var note = context.Gates.NoteOf(builder.Kind) is { } how ? $" [{how}]" : ""; // 1.7.3 fix round (BH-173-m1)
			said.Add(string.Create(CultureInfo.InvariantCulture, $"{builder.Kind} {gate.Candidates}/{gate.Floor}/{gate.Cap}{note}"));
			all.AddRange(made);
		}

		if (census && log is not null && index.Reading is null && Censused.GetOrCreateValue(log).TryAdd((index.Key, index.ReadAt, index.Entries.Count), 0))
		{
			Census(log, context, said, all.Count, watch.Elapsed);
		}

		return all;
	}

	// 1.7.3 (P0-1): one line a whole index (none for a first read's partial index, nor a library's own titles): its entries
	// and its titles once copies are collapsed, then each kind's candidates, those past its floor and those past its cap.
	// Counts and kinds only: no titles or names (1.8's "Report a problem" may attach the log)
	private static void Census(ILogger log, BuildContext context, List<string> kinds, int collections, TimeSpan took)
	{
		int titles;
		try
		{
			titles = context.Titles.Entries.Count;
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			titles = -1;
		}

		log.LogInformation("Made by Nixie census: {Entries} entries, {Titles} titles once copies are one; kind candidates/floor/cap: {Kinds}; {Collections} collections in {Ms} ms",
			context.Index.Entries.Count, titles, string.Join(", ", kinds), collections, (int)took.TotalMilliseconds);
	}
	// end 1.7.2

	/// <summary>
	/// <see cref="BuildAll"/> off the calling thread: a 25,000-film index takes a moment to group, and a page must not wait
	/// for it on the UI thread. The pages' build of the whole index (Home, the Collections and Libraries pages): 1.7.3 (P0-1)
	/// says its census once in the log (counts and kinds only), none for a first read's partial index.
	/// </summary>
	public static Task<IReadOnlyList<SmartCollection>> BuildAllAsync(IndexSnapshot index, DateTimeOffset now, IReadOnlySet<string>? boxedFranchises = null, IReadOnlySet<SmartCollectionKind>? kindsOff = null,
		Outside.OutsideListsSnapshot? outside = null)
	{
		var lists = outside ?? Outside.OutsideListsSnapshot.Shared; // 1.7 keyless: the lists as they are now, not when the task runs
		return Task.Run(() => BuildPublished(index, now, boxedFranchises, kindsOff, lists, census: true));
	}

	// 1.8 (P1-3, stream s; the review's Major 2 and Minor 3)
	/// <summary>
	/// The collections published after the first rows (<see cref="LaterBuilders"/>: the week's Spotlight), off the calling thread,
	/// once an index (with its outside lists, the week and Settings' switch): every page that asks gets the same work, and only the
	/// first pays. Call it after showing <see cref="BuildAllAsync"/>'s rows, and add what it gives when it comes (1.8.1,
	/// BH-180-q5: its task says so; the LaterReady event nothing listened to is gone). The week's Spotlight is kept on this PC
	/// (<paramref name="picks"/>, the signed-in user's <see cref="SmartCollectionStore"/> by default): it holds all week and
	/// next week's is another. Counts only in the log.
	/// </summary>
	public static Task<IReadOnlyList<SmartCollection>> BuildLaterAsync(IndexSnapshot index, DateTimeOffset now, IReadOnlySet<SmartCollectionKind>? kindsOff = null,
		Outside.OutsideListsSnapshot? outside = null, ISpotlightPicks? picks = null)
	{
		var lists = outside ?? Outside.OutsideListsSnapshot.Shared;
		var keep = picks ?? ViewModels.SmartCollectionServices.Store;
		var off = kindsOff?.Contains(SmartCollectionKind.Spotlight) == true;
		var week = SpotlightWeek.Of(now).Seed;
		lock (Later)
		{
			if (Later.TryGetValue(index, out var made) && ReferenceEquals(made.Outside, lists) && made.Week == week && made.Off == off && ReferenceEquals(made.Picks, keep))
			{
				return made.Task;
			}

			var task = Task.Run(() => MakeLater(index, now, off, lists, keep));
			Later.AddOrUpdate(index, new LaterMade(lists, week, off, keep, task));
			return task;
		}
	}

	private sealed record LaterMade(Outside.OutsideListsSnapshot Outside, string Week, bool Off, ISpotlightPicks? Picks, Task<IReadOnlyList<SmartCollection>> Task);

	private static readonly ConditionalWeakTable<IndexSnapshot, LaterMade> Later = new();

	private static async Task<IReadOnlyList<SmartCollection>> MakeLater(IndexSnapshot index, DateTimeOffset now, bool off, Outside.OutsideListsSnapshot lists, ISpotlightPicks? picks)
	{
		if (off || index.Reading is not null)
		{
			return []; // turned off in Settings; a first read's partial index makes none (it would change as the rest came)
		}

		var watch = Stopwatch.StartNew();
		IReadOnlyList<SmartCollection> made = [];
		try
		{
			// 1.8.1 (BH-180-q1): the picks of whose index it is (its key), read and saved for that sign-in only
			var kept = picks is null ? null : await picks.SpotlightPicksAsync(index.Key).ConfigureAwait(false);
			var context = new BuildContext(index, now, new HashSet<string>()) { Outside = lists, Collapsed = CollapsedOf(index) };
			made = [.. new SpotlightBuilder(kept).Build(context).Where(x => x.ItemIds.Count > 0)];
			var gate = context.Gates.Of(SmartCollectionKind.Spotlight) ?? new BuildGates.Gate(made.Count, made.Count, made.Count);
			if (Log is { } log && index.Key is not null && LaterSaid.GetOrCreateValue(log).TryAdd((index.Key, index.ReadAt, index.Entries.Count), 0))
			{
				log.LogInformation("Made by Nixie, after the first rows: Spotlight {Candidates}/{Floor}/{Cap} [{Note}] in {Ms} ms",
					gate.Candidates, gate.Floor, gate.Cap, context.Gates.NoteOf(SmartCollectionKind.Spotlight), (int)watch.Elapsed.TotalMilliseconds);
			}

			var week = SpotlightWeek.Of(now).Seed;
			// 1.8 (stream l, the re-review): kept for the week only once the people are in (PeopleCovered): a first start's pick
			// (a studio's, the people not read yet) held the whole week against the directors and actors that came minutes later
			if (picks is not null && index.PeopleCovered && made.FirstOrDefault() is { } spotlight && kept?.GetValueOrDefault(week) != spotlight.Id)
			{
				await picks.SaveSpotlightPick(week, spotlight.Id, index.Key).ConfigureAwait(false);
			}
		}
		catch (Exception ex)
		{
			// as a builder's throw in BuildAll: said once, its type only; the published rows are untouched
			Log?.LogWarning("Made by Nixie: the Spotlight couldn't be made ({Error})", ex.GetType().Name);
		}

		return made;
	}

	private static readonly ConditionalWeakTable<ILogger, ConcurrentDictionary<(string Key, DateTimeOffset ReadAt, int Entries), byte>> LaterSaid = new();
	// end 1.8

	/// <summary>
	/// Nixie 1.6.1 (bug hunt BH-160-m1): the clock every page builds Nixie's collections by, the PC's local time: the seasonal
	/// row's month and the day's row (<see cref="OfTheDay"/>) follow the date on the user's clock. Home and the Collections
	/// page went by UTC, so in California the Halloween row left them at 5 PM on October 31, while the Libraries page (local)
	/// still counted it, and the day's row changed in the afternoon.
	/// </summary>
	public static DateTimeOffset Now(TimeProvider? time = null) => (time ?? TimeProvider.System).GetLocalNow();

	/// <summary>
	/// The collection of a kind a day for Home's rotating row: the same all day, the next one tomorrow; none when there are none.
	/// 1.7.3 (P0-3): of the page's kinds that are published only, whatever <paramref name="all"/> holds (a director's, made on demand, never).
	/// </summary>
	public static SmartCollection? OfTheDay(IReadOnlyList<SmartCollection> all, DateTimeOffset now)
	{
		var rotating = all.Where(x => Published.Contains(x.Kind) && SmartCollectionKinds.PageOrder.Contains(x.Kind)).OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
		return rotating.Count == 0 ? null : rotating[(int)(now.Date.Ticks / TimeSpan.TicksPerDay % rotating.Count)];
	}
}
