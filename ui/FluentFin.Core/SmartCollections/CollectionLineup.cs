using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.SmartCollections.Builders;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.SmartCollections;

// Nixie 1.8 (P1-1, the report "Useful Made by Nixie collections": "The weekly lineup" and "Selection rules"): the step between
// the builders' pool and the page. Every candidate passes the gates (a reason, its size, its artwork, its coherence), then one
// greedy pass takes the best, scores the rest again for novelty, keeps each kind under its cap and stops at ten. Every random
// choice is seeded by the local date, so the lineup is the same all day and changes tomorrow. Home takes at most three rows
// of it (two by default); the Collections page's This week takes the ten. Pure: no I/O, no clock of its own.

/// <summary>Nixie 1.8: the place a collection takes in the lineup (the report's slot table).</summary>
public enum LineupSlot
{
	/// <summary>"Next in &lt;franchise&gt;", once the user has started one (stream a's anchors).</summary>
	NextIn,

	/// <summary>"More like &lt;title&gt;": the report's "Because you watched" (stream a's anchors).</summary>
	MoreLike,

	/// <summary>"More from &lt;person&gt;" (stream a's anchors).</summary>
	MoreFrom,

	/// <summary>One person a week (stream s).</summary>
	Spotlight,

	/// <summary>A canon or award list (Wikidata's awards).</summary>
	Canon,

	/// <summary>Trending on the server (Trakt and TMDb merged).</summary>
	Trending,

	/// <summary>A theme: an allowlisted keyword today, the recipe table later.</summary>
	Theme,

	/// <summary>"Under 90 minutes".</summary>
	UnderNinety,

	/// <summary>The seasonal row, in its window.</summary>
	Seasonal,

	/// <summary>"Acclaimed, not seen yet": only when no canon list is in the lineup.</summary>
	Acclaimed,

	/// <summary>"Unfinished series": live.</summary>
	Unfinished,

	/// <summary>
	/// 1.9 (Collections 2): a category of the catalogue (<see cref="SmartCollectionKind.MicroGenre"/>), chosen weekly by how
	/// near it is to what the user watched (<see cref="Lineup.Categories"/>). Last, so every 1.8 slot keeps its place
	/// in a tie.
	/// </summary>
	Category,
}

// 1.9 (Collections 2, stream lineup)
/// <summary>
/// Nixie 1.9: the categories one ISO week showed, in the week's order (the Collections page's Categories row; This week takes
/// its first that pass), and the one chosen to explore. Kept on this PC (<see cref="ICategoryWeeks"/>): the week holds them,
/// and the next three weeks leave them out.
/// </summary>
public sealed record CategoryWeek(IReadOnlyList<string> Ids, string? Explore = null);

/// <summary>Nixie 1.9: where the weeks' categories are kept, per server and user (<see cref="SmartCollectionStore"/>).</summary>
public interface ICategoryWeeks
{
	/// <summary>The weeks kept ("2026-W48" → what it showed), of <paramref name="of"/>'s sign-in (an index's key) only; null: the signed-in one's.</summary>
	Task<IReadOnlyDictionary<string, CategoryWeek>> CategoryWeeksAsync(string? of = null);

	/// <summary>
	/// Keeps what <paramref name="week"/> shows, for <paramref name="of"/>'s sign-in only; the review's Major 1: not when that
	/// user signed out after <paramref name="since"/> (the read's <see cref="KeptCategoryWeeks.ReadAfter"/>).
	/// </summary>
	Task SaveCategoryWeek(string week, CategoryWeek shown, string? of = null, long? since = null);
}

/// <summary>
/// Nixie 1.9 (the review's Major 1): the weeks <see cref="SmartCollectionStore"/> read, and the number of the sign-outs they
/// were read after (1.6.1's <c>UserFiles.Generation</c>): the week kept after them passes it on, so a sign-out during the
/// build leaves the user's file gone.
/// </summary>
public sealed class KeptCategoryWeeks(long readAfter) : Dictionary<string, CategoryWeek>(StringComparer.Ordinal)
{
	public long ReadAfter { get; } = readAfter;
}

/// <summary>Nixie 1.9: what the week's choice of categories did (counts only: the lineup's log line says them).</summary>
/// <param name="Passed">Categories past the gates (floor, their kind's ceiling, artwork).</param>
/// <param name="Ranked">Those in the week's order: one a family, none of the last three weeks' while others are left.</param>
/// <param name="Recent">Those left out (or put last) as shown in the last three weeks (1.9 Phase 3: not the taste categories carried over, <see cref="CollectionLineup.CarriedMost"/>).</param>
/// <param name="Kept">Whether the week's order was kept from an earlier build this week.</param>
public sealed record CategoryCounts(int Passed, int Ranked, int Recent, bool Kept)
{
	// 1.9 (Phase 3)
	/// <summary>1.9 (Phase 3, R1): those reached through a title played in the last 30 days.</summary>
	public int Anchored { get; init; }

	/// <summary>1.9 (Phase 3, R3): those of filters alone kept from the row for want of two played titles inside.</summary>
	public int FiltersOnly { get; init; }

	/// <summary>1.9 (Phase 3): the played titles the row's categories hold.</summary>
	public int PlayedInRow { get; init; }

	/// <summary>
	/// 1.9 (the Phase 3 review's Major): how many of the row's categories are of the user's taste carried over from the three
	/// weeks before, at most <see cref="CollectionLineup.CarriedMost"/> (<see cref="Lineup.CarriedCategories"/>); the rest rotate.
	/// </summary>
	public int Carried { get; init; }
	// end 1.9 (Phase 3)
}
// end 1.9

/// <summary>Nixie 1.8: how often a slot's choice changes, and so the date part its seed is made of.</summary>
public enum LineupRefresh
{
	/// <summary>No choice to make: it follows what the user does (Unfinished series).</summary>
	Live,

	/// <summary>The local day.</summary>
	Daily,

	/// <summary>Three days at a time (the personal slots hold three days).</summary>
	ThreeDays,

	/// <summary>The ISO week (Monday to Sunday).</summary>
	Weekly,

	/// <summary>The month.</summary>
	Monthly,
}

/// <summary>Nixie 1.8: a collection offered to the lineup, with the slot it would fill.</summary>
public sealed record LineupCandidate(SmartCollection Collection, LineupSlot Slot);

/// <summary>Nixie 1.8: a collection the lineup took: its slot, and the score it was taken at.</summary>
public sealed record LineupPick(SmartCollection Collection, LineupSlot Slot, double Score);

/// <summary>Nixie 1.8: one slot's counts through the gates (counts only: the log line says them, never a title).</summary>
public sealed record LineupSlotCount(int Candidates, int Floor, int Ceiling, int Artwork, int Coherence, int Novelty, int Picked);

/// <summary>Nixie 1.8: the lineup: the collections taken, in order, and what each gate let through.</summary>
public sealed class Lineup
{
	public static Lineup Empty { get; } = new([], new Dictionary<LineupSlot, LineupSlotCount>(), DateTimeOffset.MinValue);

	public Lineup(IReadOnlyList<LineupPick> picks, IReadOnlyDictionary<LineupSlot, LineupSlotCount> counts, DateTimeOffset day)
	{
		Picks = picks;
		Counts = counts;
		Day = day;
	}

	/// <summary>The collections taken, best first: This week's tiles, in this order.</summary>
	public IReadOnlyList<LineupPick> Picks { get; }

	/// <summary>What each slot's candidates did at each gate.</summary>
	public IReadOnlyDictionary<LineupSlot, LineupSlotCount> Counts { get; }

	/// <summary>The local date it was made for.</summary>
	public DateTimeOffset Day { get; }

	public IReadOnlyList<SmartCollection> Collections => [.. Picks.Select(x => x.Collection)];

	// 1.9 (Collections 2, stream lineup)
	/// <summary>
	/// 1.9: the week's categories, in order: the Collections page's Categories row (at most <see cref="CollectionLineup.CategoryRow"/>).
	/// This week's categories first (the order's first that pass the novelty rules), then the rest of the order: one a family,
	/// none shown in the three weeks before (while others are left), the explore pick second. Each is the same collection here
	/// as in This week and in See all.
	/// </summary>
	public IReadOnlyList<SmartCollection> Categories { get; init; } = [];

	/// <summary>1.9: the week's explore pick (a category nothing the user watched points at), its id; null for none.</summary>
	public string? Explore { get; init; }

	/// <summary>1.9: the week's choice of categories, counted; null when no category passed the gates.</summary>
	public CategoryCounts? CategoryCounts { get; init; }

	/// <summary>
	/// 1.9: the week's order of the categories (the first <see cref="CollectionLineup.CategoryRow"/>), as the lineup chose it:
	/// what is kept (<see cref="CategoryWeek"/>). The review's item 6: not the row as shown, whose first are This week's, which
	/// depend on the build's other collections (a Spotlight near a category kept it out of This week): the week kept by a build
	/// with the Spotlight and the one kept by a build without it were two weeks.
	/// </summary>
	public IReadOnlyList<string> WeekOrder { get; init; } = [];

	/// <summary>1.9: what this lineup's week holds of the categories (to keep: <see cref="ICategoryWeeks"/>); null for none.</summary>
	public CategoryWeek? CategoryWeek => WeekOrder.Count == 0 ? null : new CategoryWeek([.. WeekOrder], Explore);

	/// <summary>
	/// 1.9 (the owner, trying the test copy: "i dont think the categories should be alphabetical"): the categories of the
	/// Collections page's See all ("For you"), best first, without the row's one-a-family and weeks-apart rules. Ids. The
	/// critical review's cut 1 (the owner: the long list "isnt very useful"): only those past the gates that what the user
	/// watched lifts to <see cref="CollectionLineup.LiftShown"/> or more, and the week's explore pick, by fit (the lift's square
	/// root, 0.5 to 2) × their first posters' quality among their own kind's (the pre-release fix G), with no weekly draw;
	/// empty when nothing is lifted (no history: See all shows the row, <see cref="Categories"/>).
	/// </summary>
	public IReadOnlyList<string> CategoryRanking { get; init; } = [];
	// end 1.9

	/// <summary>
	/// 1.9 (Phase 3, R1; the critical review's option 5): the Collections page's More like row: one a title played in the last 30
	/// days (<see cref="Anchors.AnchorData.Lately"/>), the newest first, ids; none whose first posters a category of the row (or a
	/// newer More like) already opens on. Empty: the row as made.
	/// </summary>
	public IReadOnlyList<string> MoreLike { get; init; } = [];

	/// <summary>
	/// 1.9 (the Phase 3 review's Major): the row's categories of the user's taste carried over from the three weeks before, at
	/// most <see cref="CollectionLineup.CarriedMost"/>, ids; the rest of the row rotates.
	/// </summary>
	public IReadOnlyList<string> CarriedCategories { get; init; } = [];

	/// <summary>
	/// Home's rows (the report's "Where they show"): the best personal slot, then the seasonal row in its window, otherwise
	/// Trending, otherwise Spotlight; then the lineup's next ones, in its order, up to <paramref name="rows"/> (at most
	/// <see cref="CollectionLineup.HomeMost"/>). Unfinished series is never one of them: it is Home's own row, with its own switch.
	/// 1.9 (the review's item 5; the plan: "Home: unchanged"): nor a category: with no personal pick, the Category slot's prior
	/// (0.70) put one where 1.8.1 had a theme (0.68).
	/// </summary>
	/// <param name="shown">Whether Settings lets a collection on Home (its kind on, its row on).</param>
	public IReadOnlyList<SmartCollection> ForHome(int rows, Func<SmartCollection, bool>? shown = null)
	{
		rows = Math.Clamp(rows, 0, CollectionLineup.HomeMost);
		var open = Picks.Where(x => x.Slot is not (LineupSlot.Unfinished or LineupSlot.Category) && (shown?.Invoke(x.Collection) ?? true)).ToList();
		var home = new List<LineupPick>();
		if (open.FirstOrDefault(x => CollectionLineup.IsPersonal(x.Slot)) is { } personal)
		{
			home.Add(personal);
		}

		if ((open.FirstOrDefault(x => x.Slot == LineupSlot.Seasonal) ?? open.FirstOrDefault(x => x.Slot == LineupSlot.Trending)
			?? open.FirstOrDefault(x => x.Slot == LineupSlot.Spotlight)) is { } timely)
		{
			home.Add(timely);
		}

		home.AddRange(open.Where(x => !home.Contains(x)));
		return [.. home.Take(rows).Select(x => x.Collection)];
	}
}

/// <summary>
/// Nixie 1.8: what the lineup reads of the index: its titles with copies collapsed (each collection's titles are counted as
/// titles, not files), their scores, how many films, and the size of each outside list a collection comes from.
/// </summary>
public sealed record LineupData(CanonicalTitles Titles, QualityScore Quality, int Films)
{
	/// <summary>A collection's id → the size of the public list it was matched from ("award:…": the award's winners).</summary>
	public IReadOnlyDictionary<string, int> ListSizes { get; init; } = new Dictionary<string, int>();

	/// <summary>
	/// The share of the titles with a poster (0 to 1). Under <see cref="CollectionLineup.ArtworkKnown"/> the server keeps
	/// little artwork, and the artwork gate is off: missing data is not a decision (rule 4).
	/// </summary>
	public double ArtShare { get; init; } = 1;

	// 1.9 (Collections 2, stream lineup)
	/// <summary>1.9: how many shows (a show category's ceiling and share are of the shows).</summary>
	public int Shows { get; init; }

	/// <summary>1.9: what the user watched (<see cref="SmartCollections.Taste"/>): the categories' lift; none: every lift is 1.</summary>
	public Taste Taste { get; init; } = Taste.None;

	/// <summary>1.9: the categories' catalogue: each one's family and parent (the shipped one; tests give their own).</summary>
	public MicroGenreCatalogue Catalogue { get; init; } = MicroGenres.Shipped;

	/// <summary>1.9: the categories each week showed, kept on this PC ("2026-W48" → what it showed); none: a first week.</summary>
	public IReadOnlyDictionary<string, CategoryWeek> CategoryHistory { get; init; } = new Dictionary<string, CategoryWeek>(StringComparer.Ordinal);
	// end 1.9

	// 1.9 (Phase 3, "because you watched")
	/// <summary>
	/// 1.9 (Phase 3): the titles the user played (canonical ids; <see cref="Anchors.AnchoredSlots.PlayOf"/>: a show with an
	/// episode played, a film played to its end) and when last (null: not known). The categories are reached through those
	/// played lately (R1), and a category made only of filters needs two of them inside to be in the row (R3).
	/// </summary>
	public IReadOnlyDictionary<Guid, DateTimeOffset?> Played { get; init; } = new Dictionary<Guid, DateTimeOffset?>();

	/// <summary>1.9 (Phase 3): the played titles of <paramref name="titles"/> (copies are one title) and when each was last played.</summary>
	internal static Dictionary<Guid, DateTimeOffset?> PlayedOf(CanonicalTitles titles, IReadOnlyDictionary<Guid, SeriesPlays>? plays)
	{
		var played = new Dictionary<Guid, DateTimeOffset?>();
		var episodes = plays ?? new Dictionary<Guid, SeriesPlays>();
		foreach (var title in titles.Entries)
		{
			if (title is not null && Anchors.AnchoredSlots.PlayOf(title, titles, episodes, title.LastPlayed) is { Played: true } play)
			{
				played[title.Id] = play.Last;
			}
		}

		return played;
	}
	// end 1.9 (Phase 3)

	private static readonly ConditionalWeakTable<IndexSnapshot, Made> Kept = new();

	// what is made of an index once (its titles collapsed, its counts, what the user watched) and its score of the day: 1.9
	// (fix 9) the score reads the day (what is new), so it is made again on another day; the same day's is kept
	private sealed class Made(IndexSnapshot index)
	{
		private readonly Lock _gate = new();
		private (DateOnly Day, QualityScore Quality)? _scored;
		private QualityScore? _undated;

		public Lazy<(CanonicalTitles Titles, int Films, int Shows, double Art, bool Plain, Taste Taste, Dictionary<Guid, DateTimeOffset?> Played)> Base { get; } = new(() =>
		{
			// 1.9 (wire's review): the catalog's collapse of the index, not one of its own: one index is collapsed once, for the
			// pages' builds (their categories' posting lists are kept with the titles) and the lineup alike
			var collapsed = SmartCollectionCatalog.CollapsedOf(index);
			// as the builders' own score (BuildContext.Quality), without the outside lists: what backs a rating decides its mode
			var backing = RatingBacking.Of(collapsed.Entries, new HashSet<Guid>(), index.BackingKnown);
			return (collapsed, collapsed.Entries.Count(e => e.Kind == IndexKind.Movie), collapsed.Entries.Count(e => e.Kind == IndexKind.Series), ArtOf(collapsed), !backing.Any,
				Taste.Of(collapsed, index.SeriesPlays), PlayedOf(collapsed, index.SeriesPlays)); // 1.9 (Phase 3): what was played
		});

		public QualityScore QualityOn(DateTimeOffset? now)
		{
			var day = now is { } at ? DateOnly.FromDateTime(at.Date) : (DateOnly?)null;
			lock (_gate)
			{
				// the review's item 8: the day's score and the score without a day are kept apart (the ui's franchise order asks
				// without one): one evicted the other, and each page's build made the 30,000 titles' score again (16 ms)
				var held = day is null ? _undated : _scored is { } kept && kept.Day == day ? kept.Quality : null;
				if (held is not null)
				{
					return held;
				}
			}

			var (titles, _, _, _, plain, _, _) = Base.Value;
			var quality = QualityScore.Of(titles, plain: plain, now: now);
			lock (_gate)
			{
				if (day is { } dated)
				{
					_scored = (dated, quality);
				}
				else
				{
					_undated = quality;
				}
			}

			return quality;
		}
	}

	/// <summary>
	/// The index's: its titles collapsed once and kept while the index is (Home and the Collections page share them), the
	/// lists' sizes from <paramref name="outside"/>.
	/// </summary>
	/// <param name="now">1.9 (fix 9): the day the titles are ranked on (the builders' <see cref="BuildContext.Now"/>): what is new.</param>
	/// <param name="history">1.9: the categories each week showed (<see cref="ICategoryWeeks"/>); null: none kept.</param>
	public static LineupData Of(IndexSnapshot index, Outside.OutsideListsSnapshot? outside = null, DateTimeOffset? now = null, IReadOnlyDictionary<string, CategoryWeek>? history = null)
	{
		var made = Kept.GetValue(index, x => new Made(x));
		var (titles, films, shows, art, _, taste, played) = made.Base.Value;
		var quality = made.QualityOn(now);
		var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (var list in (outside ?? Outside.OutsideListsSnapshot.Empty).Lists.Where(l => l.Kind == Outside.OutsideListKind.Award))
		{
			sizes[$"award:{list.Id}"] = list.Items.Count;
		}

		return new LineupData(titles, quality, films)
		{
			ListSizes = sizes,
			ArtShare = art,
			Shows = shows,
			Taste = taste,
			CategoryHistory = history ?? new Dictionary<string, CategoryWeek>(StringComparer.Ordinal),
			Played = played, // 1.9 (Phase 3)
		};
	}

	/// <summary>Titles given as they are (tests): each its own title; 1.9: what is played or a favourite among them is the evidence.</summary>
	public static LineupData Of(IReadOnlyList<IndexEntry> entries, IReadOnlyDictionary<Guid, SeriesPlays>? plays = null)
	{
		var titles = CanonicalTitles.Of(entries);
		return new LineupData(titles, QualityScore.Of(titles), titles.Entries.Count(e => e.Kind == IndexKind.Movie))
		{
			ArtShare = ArtOf(titles),
			Shows = titles.Entries.Count(e => e.Kind == IndexKind.Series),
			Taste = Taste.Of(titles, plays),
			Played = PlayedOf(titles, plays), // 1.9 (Phase 3)
		};
	}

	// the share of the titles with a poster; an empty index counts as having them (nothing to gate either way)
	private static double ArtOf(CanonicalTitles titles) =>
		titles.Entries.Count == 0 ? 1 : titles.Entries.Count(x => x.PrimaryTag is not null) / (double)titles.Entries.Count;
}

/// <summary>Nixie 1.8: what the pages build: every published collection (their own rows) and the lineup made of them.</summary>
public sealed record LineupBuild(IReadOnlyList<SmartCollection> All, Lineup Lineup)
{
	/// <summary>
	/// Review (Minor 4): Home's own lineup, of the collections Home's switches let on Home only (<see cref="CollectionLineup.HomeLineup"/>);
	/// the lineup itself when no switch was given.
	/// </summary>
	public Lineup Home { get; init; } = Lineup;

	/// <summary>
	/// 1.9 (the pre-release fix A, BH-190-M2): the summaries' matches its categories were made of (<see cref="TextMatches.For"/>
	/// the index when the build began, and still when it ended); null when they changed during it, or not known (a lineup of
	/// collections made elsewhere). <see cref="CollectionLineup.AssembleAsync"/> reads its fallback's: a week is kept only of
	/// categories made of matches that cover the shows.
	/// </summary>
	internal TextMatches? MadeOf { get; init; }
}

/// <summary>
/// Nixie 1.8 (P1-1): the lineup assembler. All numbers are the report's starting values, to tune on the owner's own counts.
/// </summary>
public static class CollectionLineup
{
	/// <summary>The lineup's length: This week's tiles.</summary>
	public const int Slots = 10;

	/// <summary>Home's Nixie rows from the lineup: at most three, two by default (Unfinished series apart).</summary>
	public const int HomeMost = 3;

	public const int HomeDefault = 2;

	/// <summary>Unwatched titles a collection needs (the size floor): a row's worth.</summary>
	public const int Floor = 8;

	/// <summary>"Next in": owned films of the franchise, one or more of them unwatched.</summary>
	public const int FranchiseFloor = 2;

	/// <summary>A person's collection ("More from", the Spotlight): 5 or more unwatched titles.</summary>
	public const int PersonFloor = 5;

	/// <summary>A canon or award list: 10 matched or 10 % of the list, and 5 or more unwatched.</summary>
	public const int CanonMatched = 10;

	public const double CanonShare = 0.10;

	public const int CanonUnwatched = 5;

	/// <summary>A theme's unwatched pool above about 1 % of the films is a filter, not a theme (never under <see cref="ThemeCeilingLeast"/>).</summary>
	public const double ThemeCeilingShare = 0.01;

	/// <summary>The theme ceiling's least: a small library's 1 % would leave no theme at all (MUBI's shelf was about 30).</summary>
	public const int ThemeCeilingLeast = 30;

	/// <summary>The posters a row shows first: the artwork gate and the novelty rule count these.</summary>
	public const int Posters = 8;

	/// <summary>Of the first <see cref="Posters"/>, the share with a primary image.</summary>
	public const double ArtworkShare = 0.75;

	/// <summary>
	/// The share of the index's titles with a poster under which the artwork gate is off: a server that keeps little artwork
	/// (the throwaway 12.4: 140 posters of 25,736 films) would leave no collection at all (rule 4).
	/// </summary>
	public const double ArtworkKnown = 0.5;

	/// <summary>Of the first <see cref="Posters"/>, the most that may already be shown higher on the page.</summary>
	public const double MostShown = 0.5;

	/// <summary>Two pools that overlap more than this (Jaccard) are one: the better is kept.</summary>
	public const double MostOverlap = 0.5;

	/// <summary>The titles the coherence measure reads.</summary>
	public const int CoherenceTop = 12;

	// 1.9 (Collections 2, stream lineup; the plan: "one Categories row of about 12 on the Collections page, up to 2 in This week,
	// and Home unchanged")
	/// <summary>1.9: the categories This week takes at most (the Category slot's cap).</summary>
	public const int CategoriesInWeek = 2;

	/// <summary>1.9: the Collections page's Categories row: the week's first this many (<see cref="Lineup.Categories"/>).</summary>
	public const int CategoryRow = 12;

	/// <summary>1.9: no category again within this many weeks: the three weeks before this one are left out while others remain.</summary>
	public const int CategoryWeeksApart = 4;

	/// <summary>1.9: a category's lift (<see cref="Taste.Lift(double, double, double)"/>) counts up to this much, and down to its inverse.</summary>
	public const double LiftMost = 4;

	/// <summary>
	/// 1.9 (the critical review's cut 1): See all's "For you" holds a category whose lift is at least this (about one played show
	/// with a few episodes in it), and the week's explore pick (<see cref="Lineup.CategoryRanking"/>).
	/// </summary>
	public const double LiftShown = 1.5;
	// end 1.9

	// 1.9 (Phase 3, "because you watched")
	/// <summary>
	/// 1.9 (Phase 3, R3; the critical review's option 4): a category of filters alone (<see cref="FiltersOnly"/>) enters the row
	/// only with this many played titles inside: "Historical Mini-Series" with one, "Sci-Fi Series from Prime Video" with one,
	/// were filters that named where or when a show aired.
	/// </summary>
	public const int FilteredLeast = 2;

	/// <summary>
	/// 1.9 (Phase 3): the categories of one family the row holds at most: a category holding played titles may join one other
	/// of its family, whatever that one holds (<c>FamilyRoom</c>); every other category comes only into a family not taken yet
	/// (<c>FamilyFree</c>). One sitcom category a week left most of a sitcom viewer's shows out of every category shown.
	/// </summary>
	public const int FamilyMost = 2;

	/// <summary>
	/// 1.9 (Phase 3, R1): a category is reached through a title played lately when it fits: it holds that title and at least one
	/// more played title the row doesn't hold yet (this many in all). One holding the anchor alone is that show, not a taste:
	/// on a library of the owner's shape, the row's twelve places held 29 of their shows that way, 34 this way.
	/// </summary>
	public const int AnchoredLeast = 2;

	/// <summary>
	/// 1.9 (Phase 3; the coordinator, after the run on the owner's shape): a category holding this many of the user's played
	/// titles or more is their taste, and may be carried over past the four-week rotation (<see cref="CategoryWeeksApart"/>):
	/// at most <see cref="CarriedMost"/> of them. Fillers, a category of one played show and the explore pick rotate; the family
	/// rules still hold. On the owner's shape the row held 34 of their 87 shows, and 29 the week after with none carried.
	/// </summary>
	public const int TasteStays = 2;

	/// <summary>
	/// 1.9 (the Phase 3 review's Major; the coordinator's decision): of the taste categories shown in the three weeks before, at
	/// most this many are carried over (those holding a title played in the last 30 days first); the rest rotate with the week's
	/// draw, as the fillers do. Carrying every one froze the row: over 10 weeks of the same plays, 10 to 11 of its 12 tiles were
	/// the same each week.
	/// </summary>
	public const int CarriedMost = 6;
	// end 1.9 (Phase 3)

	/// <summary>The setting: how many of the lineup's rows Home shows (0 to <see cref="HomeMost"/>).</summary>
	public static Key<int> HomeRowsKey { get; } = new("CollectionsHomeLineupRows", HomeDefault);

	/// <summary>Home's rows from the lineup under the setting, 0 to <see cref="HomeMost"/>.</summary>
	public static int HomeRows(ILocalSettingsService? settings) => Math.Clamp(settings?.ReadSetting(HomeRowsKey) ?? HomeDefault, 0, HomeMost);

	/// <summary>How many of a slot the lineup takes (the report's caps; the seasonal row's 2 are the Collections page's, Home takes 1).</summary>
	public static int Cap(LineupSlot slot) => slot switch
	{
		LineupSlot.MoreLike or LineupSlot.Theme or LineupSlot.Seasonal => 2,
		LineupSlot.Category => CategoriesInWeek, // 1.9
		_ => 1,
	};

	/// <summary>How often a slot's choice changes.</summary>
	public static LineupRefresh Refresh(LineupSlot slot) => slot switch
	{
		LineupSlot.NextIn or LineupSlot.MoreLike or LineupSlot.MoreFrom => LineupRefresh.ThreeDays,
		LineupSlot.Spotlight or LineupSlot.Theme or LineupSlot.Category => LineupRefresh.Weekly, // 1.9: and the categories
		LineupSlot.Canon or LineupSlot.Seasonal => LineupRefresh.Monthly,
		LineupSlot.Unfinished => LineupRefresh.Live,
		_ => LineupRefresh.Daily, // Trending, Under 90 minutes, Acclaimed
	};

	/// <summary>
	/// A slot's prior: how much it is worth before its own titles count. Anchored to what the user watched first (labels that
	/// name the viewer's own titles beat those naming a genre or a date), then what is timely, then authority, then the rest.
	/// </summary>
	public static double Prior(LineupSlot slot) => slot switch
	{
		LineupSlot.NextIn => 1.00,
		LineupSlot.MoreLike => 0.96,
		LineupSlot.MoreFrom => 0.92,
		LineupSlot.Seasonal => 0.88,
		LineupSlot.Spotlight => 0.84,
		LineupSlot.Canon => 0.80,
		LineupSlot.Trending => 0.76,
		LineupSlot.Unfinished => 0.72,
		LineupSlot.Category => 0.70, // 1.9: a category names more than a theme's one keyword, so just above it
		LineupSlot.Theme => 0.68,
		LineupSlot.Acclaimed => 0.64,
		_ => 0.60, // Under 90 minutes
	};

	/// <summary>The slots anchored to what the user watched.</summary>
	public static bool IsPersonal(LineupSlot slot) => slot is LineupSlot.NextIn or LineupSlot.MoreLike or LineupSlot.MoreFrom;

	// the slots whose candidates are many of a kind made by a rule: the bottom third of each by coherence goes. Not the personal
	// slots (their builder keeps the anchor's evidence and the rare features they share) nor the Spotlight (its own gate)
	private static bool Coheres(LineupSlot slot) => slot is LineupSlot.Theme or LineupSlot.Canon;

	// the slots whose builder already chose their order (stream a's 3-day hold, fresh anchors first; stream s's week): the
	// lineup takes them as given, not by its own draw
	private static bool KeepsOrder(LineupSlot slot) => IsPersonal(slot) || slot == LineupSlot.Spotlight;

	// the daily slots whose titles' order turns each day ("the order rotated daily"); a list keeps its own order (Trending)
	private static bool TurnsDaily(LineupSlot slot) => slot is LineupSlot.Acclaimed or LineupSlot.UnderNinety;

	// the slots whose titles come best first, unwatched before watched (the report's "Themes … Q"): a keyword's builder gives
	// them A to Z, so a theme's row on Home and its tile's mosaic began with whatever sorted first, watched or not
	private static bool RanksByQuality(LineupSlot slot) => slot is LineupSlot.Theme;

	/// <summary>
	/// The slot a collection fills; null for one with no reason to be in the lineup (the evidence gate): a franchise not
	/// started, a saved filter, a public list other than Trending (they are the Lists), a kind made on demand.
	/// </summary>
	public static LineupSlot? SlotOf(SmartCollection collection) => collection.Kind switch
	{
		SmartCollectionKind.NextIn => LineupSlot.NextIn,
		SmartCollectionKind.MoreLike => LineupSlot.MoreLike,
		SmartCollectionKind.MoreFrom => LineupSlot.MoreFrom,
		SmartCollectionKind.Spotlight => LineupSlot.Spotlight,
		SmartCollectionKind.Keyword => LineupSlot.Theme,
		SmartCollectionKind.Award => LineupSlot.Canon,
		SmartCollectionKind.HighlyRated => LineupSlot.Acclaimed,
		SmartCollectionKind.ShortFilms => LineupSlot.UnderNinety,
		SmartCollectionKind.Seasonal => LineupSlot.Seasonal,
		SmartCollectionKind.UnfinishedSeries => LineupSlot.Unfinished,
		SmartCollectionKind.MicroGenre => LineupSlot.Category, // 1.9 (Collections 2)
		SmartCollectionKind.Trending when collection.Id == Outside.TrendingBuilder.HomeRowId => LineupSlot.Trending,
		_ => null,
	};

	/// <summary>The candidates among <paramref name="all"/>: each with its slot; those with none left out.</summary>
	public static IReadOnlyList<LineupCandidate> Candidates(IEnumerable<SmartCollection> all) =>
		[.. all.Select(x => SlotOf(x) is { } slot ? new LineupCandidate(x, slot) : null).OfType<LineupCandidate>()];

	// ---- seeds -------------------------------------------------------------------------------------------------------------

	/// <summary>The seed of a refresh on a local date: "d2026-11-24", "w2026-48", "m2026-11", "p20416"; "" for live.</summary>
	public static string Seed(LineupRefresh refresh, DateTimeOffset now)
	{
		var day = now.Date; // the local date: SmartCollectionCatalog.Now
		return refresh switch
		{
			LineupRefresh.Daily => "d" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
			LineupRefresh.ThreeDays => "p" + (day.Ticks / TimeSpan.TicksPerDay / 3).ToString(CultureInfo.InvariantCulture),
			LineupRefresh.Weekly => string.Create(CultureInfo.InvariantCulture, $"w{ISOWeek.GetYear(day)}-{ISOWeek.GetWeekOfYear(day):D2}"),
			LineupRefresh.Monthly => "m" + day.ToString("yyyy-MM", CultureInfo.InvariantCulture),
			_ => "",
		};
	}

	/// <summary>
	/// A number in [0, 1) drawn for <paramref name="id"/> under <paramref name="seed"/>: the same on every PC and every run
	/// (FNV-1a over UTF-8, not the runtime's string hash, which changes per process), so the TV can draw the same.
	/// </summary>
	public static double Draw(string seed, string id)
	{
		const ulong Offset = 14695981039346656037;
		const ulong Prime = 1099511628211;
		var hash = Offset;
		foreach (var b in Encoding.UTF8.GetBytes(seed + "|" + id))
		{
			hash = (hash ^ b) * Prime;
		}

		// a last mix, so ids that differ in their last character spread over the whole range
		hash ^= hash >> 33;
		hash *= 0xff51afd7ed558ccd;
		hash ^= hash >> 33;
		return (hash >> 11) * (1.0 / (1UL << 53));
	}

	// ---- coherence ---------------------------------------------------------------------------------------------------------

	/// <summary>
	/// How much the first <see cref="CoherenceTop"/> titles share beyond what defines them (the report's coherence gate, after
	/// Ziegler's intra-list similarity): the mean Jaccard similarity of each pair's genres, keywords, decade, country, studios,
	/// directors and first three actors, less the features every one of them has (the defining one). 0 to 1; 0 under two titles.
	/// A filmography dump shares nothing but its person; a theme shares more.
	/// </summary>
	public static double Coherence(IReadOnlyList<IndexEntry> titles)
	{
		var top = titles.Take(CoherenceTop).Select(Features).ToList();
		if (top.Count < 2)
		{
			return 0;
		}

		var common = new HashSet<string>(top[0], StringComparer.Ordinal);
		foreach (var set in top.Skip(1))
		{
			common.IntersectWith(set);
		}

		var own = top.Select(x => x.Where(f => !common.Contains(f)).ToHashSet(StringComparer.Ordinal)).ToList();
		double sum = 0;
		var pairs = 0;
		for (var i = 0; i < own.Count; i++)
		{
			for (var j = i + 1; j < own.Count; j++)
			{
				sum += Jaccard(own[i], own[j]);
				pairs++;
			}
		}

		return sum / pairs;
	}

	// a title's features; a damaged saved index's null name, genre or person is skipped (a throw here took the whole lineup)
	private static HashSet<string> Features(IndexEntry x)
	{
		var set = new HashSet<string>(StringComparer.Ordinal);
		void Add(string prefix, string? value)
		{
			if (!string.IsNullOrWhiteSpace(value))
			{
				set.Add(prefix + value.Trim().ToLowerInvariant());
			}
		}

		foreach (var g in x.Genres ?? [])
		{
			Add("g:", g);
		}

		foreach (var k in x.Tags ?? [])
		{
			Add("k:", k);
		}

		if (x.Year is { } year)
		{
			Add("d:", (year / 10 * 10).ToString(CultureInfo.InvariantCulture));
		}

		if (x.Countries is { Length: > 0 } countries)
		{
			Add("c:", countries[0]);
		}

		foreach (var s in x.Studios ?? [])
		{
			Add("s:", s is null ? null : Make.Key(s));
		}

		var people = (x.People ?? []).Where(p => p?.Name is not null).ToList();
		foreach (var p in people.Where(p => p.Kind == "Director"))
		{
			Add("p:", p.Name);
		}

		foreach (var p in people.Where(p => p.Kind == "Actor").Take(3)) // top-3 billing only
		{
			Add("p:", p.Name);
		}

		return set;
	}

	private static double Jaccard<T>(IReadOnlySet<T> a, IReadOnlySet<T> b)
	{
		if (a.Count == 0 && b.Count == 0)
		{
			return 0;
		}

		var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
		var both = small.Count(large.Contains);
		return both / (double)(a.Count + b.Count - both);
	}

	// ---- the assembler -----------------------------------------------------------------------------------------------------

	// a candidate as the gates read it
	private sealed class Seen
	{
		public required LineupCandidate Candidate { get; init; }

		public required SmartCollection Collection { get; init; }

		public required IReadOnlyList<IndexEntry> Titles { get; init; }

		public required HashSet<Guid> Pool { get; init; }

		public required IReadOnlyList<Guid> First { get; init; }

		public required int Unwatched { get; init; }

		public required double Art { get; init; }

		public required double Quality { get; init; }

		public double Coherence { get; set; }

		public double Order { get; set; }

		/// <summary>Its place among the candidates as given (its builder's preference).</summary>
		public required int Given { get; init; }

		public LineupSlot Slot => Candidate.Slot;
	}

	/// <summary>
	/// The lineup of <paramref name="candidates"/> on <paramref name="now"/>'s local date (see the class). Never throws for a
	/// candidate: one whose titles the index doesn't have is left out.
	/// </summary>
	public static Lineup Assemble(IReadOnlyList<LineupCandidate> candidates, LineupData data, DateTimeOffset now)
	{
		var counts = new Dictionary<LineupSlot, int[]>(); // candidates, floor, ceiling, artwork, coherence, novelty, picked
		int[] Of(LineupSlot slot) => counts.TryGetValue(slot, out var c) ? c : counts[slot] = new int[7];

		var ceiling = Math.Max(ThemeCeilingLeast, (int)Math.Ceiling(ThemeCeilingShare * data.Films));
		var passed = new List<Seen>();
		var given = 0;
		foreach (var candidate in candidates.DistinctBy(x => x.Collection.Id))
		{
			var c = Of(candidate.Slot);
			c[0]++;
			var seen = See(candidate, data, now, given++);
			if (!Floored(seen, data))
			{
				continue;
			}

			c[1]++;
			// 1.9: a category's ceiling is of its own kind (1 % of the shows for a show category: 50, not the films' 259)
			if ((seen.Slot == LineupSlot.Theme && seen.Unwatched > ceiling) || (seen.Slot == LineupSlot.Category && seen.Unwatched > CategoryCeiling(seen, data)))
			{
				continue;
			}

			c[2]++;
			// review (Minor 2): a row of posters only: one of fewer titles than a row's first eight ("Next in": two or three films,
			// one of them without its poster yet) isn't judged by its share
			if (data.ArtShare >= ArtworkKnown && seen.Slot != LineupSlot.Unfinished && seen.Titles.Count >= Posters && seen.Art < ArtworkShare)
			{
				continue;
			}

			c[3]++;
			passed.Add(seen);
		}

		// coherence: of each kind of many, the bottom third goes
		var kept = new List<Seen>();
		foreach (var group in passed.GroupBy(x => x.Slot))
		{
			var members = group.ToList();
			if (Coheres(group.Key) && members.Count >= 3)
			{
				foreach (var m in members)
				{
					m.Coherence = Coherence(m.Titles);
				}

				// equal coherence (titles with little to compare: often 0) is no reason to drop the same ones every time: the
				// slot's seed decides between them
				var seed = Seed(Refresh(group.Key), now);
				var drop = members.Count / 3;
				members = [.. members.OrderBy(x => x.Coherence).ThenBy(x => Draw(seed, x.Collection.Id)).ThenBy(x => x.Collection.Id, StringComparer.Ordinal).Skip(drop)];
			}

			kept.AddRange(members);
		}

		foreach (var group in kept.GroupBy(x => x.Slot))
		{
			Of(group.Key)[4] = Of(group.Key)[5] = group.Count();
		}

		// 1.9: the week's categories, in order (the Categories row; This week takes its first that pass): the coherence count is
		// theirs in the order (one a family, none of the last three weeks' while others are left)
		var categories = Categories([.. kept.Where(x => x.Slot == LineupSlot.Category)], data, now);
		if (categories.Counts is not null)
		{
			Of(LineupSlot.Category)[4] = Of(LineupSlot.Category)[5] = categories.Order.Count;
		}

		// each slot's own order: its titles' quality, turned by the slot's seed (the better still come up more often); the
		// personal slots and the Spotlight as their builders gave them
		var queues = new Dictionary<LineupSlot, Queue<Seen>>();
		foreach (var group in kept.GroupBy(x => x.Slot))
		{
			if (group.Key == LineupSlot.Category)
			{
				queues[group.Key] = new Queue<Seen>(categories.Order); // 1.9: the week's order
				continue;
			}

			if (KeepsOrder(group.Key))
			{
				// 1.8.1 (the owner's library, fix 1): a personal slot's picks by their anchor's recency first (fresh, dated, none),
				// then as their builder gave them
				queues[group.Key] = new Queue<Seen>(group.OrderBy(x => IsPersonal(x.Slot) ? Anchors.Hold.Recency(x.Collection.AnchoredAt, now) : 0).ThenBy(x => x.Given));
				continue;
			}

			var seed = Seed(Refresh(group.Key), now);
			foreach (var m in group)
			{
				m.Order = m.Quality * (Refresh(group.Key) == LineupRefresh.Live ? 1 : 0.6 + (0.4 * Draw(seed, m.Collection.Id)));
			}

			// 1.9 (wire's review): the seasonal rows by their end date, soonest first: in Thanksgiving's last week, beside Christmas,
			// Thanksgiving leads (it ends first); the slot's monthly draw ordered them by chance
			queues[group.Key] = new Queue<Seen>(group.OrderBy(x => group.Key == LineupSlot.Seasonal ? x.Collection.Reason?.Until ?? DateOnly.MaxValue : DateOnly.MaxValue)
				.ThenByDescending(x => x.Order).ThenBy(x => x.Collection.Id, StringComparer.Ordinal));
		}

		// greedy: the best of each slot's next one, scored against what is already taken. 1.8.1 (the owner's library, fix 1): the
		// personal slots' next ones are taken by their anchor's recency (a fresh anchor, then a dated one, then one with none),
		// their score breaking ties within a group; they take their place in the lineup by the best of their scores, as before.
		// On the owner's library "Next in Ant-Man" (a film with no play date) led Home over "More like What We Do in the
		// Shadows" (finished two days before): the slot's prior decided
		var picks = new List<(Seen Seen, double Score)>();
		var shown = new HashSet<Guid>();
		while (picks.Count < Slots)
		{
			var heads = new List<(Seen Seen, double Score)>();
			foreach (var (slot, queue) in queues.OrderBy(x => x.Key))
			{
				if (picks.Count(x => x.Seen.Slot == slot) >= Cap(slot))
				{
					continue;
				}

				// Acclaimed only when no canon list is on. Review (Minor 1): one that is taken, not one that passed the gates: a
				// canon list that then lost at novelty (a seasonal row of the same titles) left neither. While one may still be
				// taken, Acclaimed waits (Canon comes first in this loop); once one is, Acclaimed goes
				if (slot == LineupSlot.Acclaimed && queues.TryGetValue(LineupSlot.Canon, out var canon))
				{
					if (picks.Any(x => x.Seen.Slot == LineupSlot.Canon))
					{
						Of(slot)[5] -= queue.Count;
						queue.Clear();
						continue;
					}

					if (canon.Count > 0)
					{
						continue;
					}
				}

				while (queue.TryPeek(out var next))
				{
					var already = next.First.Count == 0 ? 0 : next.First.Count(shown.Contains) / (double)next.First.Count;
					if (already > MostShown || picks.Any(p => Jaccard(p.Seen.Pool, next.Pool) > MostOverlap) || BesideItsParent(next, picks, data))
					{
						queue.Dequeue(); // a near copy of one taken: the better (taken first) stays
						Of(slot)[5]--;
						continue;
					}

					heads.Add((next, Prior(slot) * next.Quality * (1 - already)));
					break;
				}
			}

			// the best of the others by score (an earlier slot wins a tie, as before); the personal ones by recency, then score
			(Seen Seen, double Score)? best = null;
			foreach (var head in heads.Where(x => !IsPersonal(x.Seen.Slot)))
			{
				if (best is not { } b || head.Score > b.Score)
				{
					best = head;
				}
			}

			var personal = heads.Where(x => IsPersonal(x.Seen.Slot)).ToList();
			if (personal.Count > 0 && (best is not { } other || personal.Max(x => x.Score) >= other.Score))
			{
				best = personal.OrderBy(x => Anchors.Hold.Recency(x.Seen.Collection.AnchoredAt, now)).ThenByDescending(x => x.Score).ThenBy(x => x.Seen.Slot).First();
			}

			if (best is not { } taken)
			{
				break;
			}

			queues[taken.Seen.Slot].Dequeue();
			picks.Add(taken);
			shown.UnionWith(taken.Seen.First);
			Of(taken.Seen.Slot)[6]++;
		}

		// 1.9: the row: This week's categories first (a category the novelty rules passed over stays in the row, after them)
		var row = picks.Where(x => x.Seen.Slot == LineupSlot.Category).Select(x => x.Seen).Concat(categories.Order).Distinct().Take(CategoryRow).ToList();
		return new Lineup([.. picks.Select(x => new LineupPick(x.Seen.Collection, x.Seen.Slot, Math.Round(x.Score, 4)))],
			counts.ToDictionary(x => x.Key, x => new LineupSlotCount(x.Value[0], x.Value[1], x.Value[2], x.Value[3], x.Value[4], x.Value[5], x.Value[6])),
			now)
		{
			Categories = [.. row.Select(x => x.Collection)],
			MoreLike = MoreLikeRow(kept, row, now), // 1.9 (Phase 3, R1)
			WeekOrder = [.. categories.Order.Take(CategoryRow).Select(x => x.Collection.Id)], // the review's item 6: kept as chosen
			Explore = categories.Explore,
			CategoryCounts = categories.Counts,
			CategoryRanking = categories.Ranking, // 1.9: See all's "For you"
			CarriedCategories = categories.Carried, // 1.9 (the Phase 3 review's Major)
		};
	}

	// 1.9 (Phase 3, R1): the More like row: the More likes past the gates, newest anchor first (by their anchor's recency, then
	// as their builder made them: the latest played first), less one whose first posters are more than MostShown among those a
	// category of the row or a newer More like opens on
	private static IReadOnlyList<string> MoreLikeRow(List<Seen> kept, List<Seen> row, DateTimeOffset now)
	{
		var shown = new HashSet<Guid>(row.SelectMany(x => x.First));
		var ids = new List<string>();
		foreach (var like in kept.Where(x => x.Slot == LineupSlot.MoreLike).OrderBy(x => Anchors.Hold.Recency(x.Collection.AnchoredAt, now)).ThenBy(x => x.Given))
		{
			var already = like.First.Count == 0 ? 0 : like.First.Count(shown.Contains) / (double)like.First.Count;
			if (already > MostShown)
			{
				continue;
			}

			ids.Add(like.Collection.Id);
			shown.UnionWith(like.First);
		}

		return ids;
	}

	// ---- 1.9 (Collections 2, stream lineup): the week's categories -------------------------------------------------------

	// the week's choice: the categories in order, the explore pick's id, and its counts (null when none passed)
	private sealed record CategoryChoice(List<Seen> Order, string? Explore, CategoryCounts? Counts)
	{
		public static CategoryChoice None { get; } = new([], null, null);

		/// <summary>1.9: every category past the gates by its order alone, best first (See all's "For you").</summary>
		public IReadOnlyList<string> Ranking { get; init; } = [];

		/// <summary>1.9 (the Phase 3 review's Major): the row's categories carried over (<see cref="Lineup.CarriedCategories"/>).</summary>
		public IReadOnlyList<string> Carried { get; init; } = [];
	}

	// a category as the week's choice reads it: its family, how far the user's evidence lifts it, its order and its explore order;
	// 1.9 (fix G, kept through cut 1): its quality among its own kind's (Footing), which See all's For you reads too
	private sealed record Ranked(Seen Seen, string Family, double Lift, double Order, double Explore, double Quality)
	{
		public string Id => Seen.Collection.Id;

		/// <summary>1.9 (Phase 3): the played titles inside it (canonical ids, <see cref="LineupData.Played"/>).</summary>
		public IReadOnlySet<Guid> Hits { get; init; } = new HashSet<Guid>();

		/// <summary>1.9 (Phase 3, R3): whether it may be in the row (filters alone need <see cref="FilteredLeast"/> played titles inside).</summary>
		public bool InRow { get; init; } = true;

		/// <summary>1.9 (Phase 3): whether it holds <paramref name="title"/>, a played title.</summary>
		public bool Holds(Guid title) => Hits.Contains(title);
	}

	/// <summary>A category's kind: films or shows (its titles' when it doesn't say).</summary>
	private static IndexKind KindOf(Seen seen) =>
		seen.Collection.Only ?? (seen.Titles.Count(x => x.Kind == IndexKind.Series) * 2 > seen.Titles.Count ? IndexKind.Series : IndexKind.Movie);

	// 1.9: 1 % of the category's own kind, at least 30 (the builder's ceiling: MicroGenreBuilder.CeilingOf)
	private static int CategoryCeiling(Seen seen, LineupData data) => MicroGenreBuilder.CeilingOf(KindOf(seen) == IndexKind.Series ? data.Shows : data.Films);

	// 1.9: a category is never beside its parent keyword collection in This week, either way round: Jaccard and the poster rule
	// let "1990s neo-noir" stand beside "Neo-noir" (0.30, the survey)
	private static bool BesideItsParent(Seen next, List<(Seen Seen, double Score)> picks, LineupData data) => next.Slot switch
	{
		LineupSlot.Category => data.Catalogue.Of(next.Collection.Id)?.Parent is { } parent && picks.Any(p => p.Seen.Collection.Id == parent),
		LineupSlot.Theme => picks.Any(p => p.Seen.Slot == LineupSlot.Category && data.Catalogue.Of(p.Seen.Collection.Id)?.Parent == next.Collection.Id),
		_ => false,
	};

	/// <summary>
	/// 1.9 (Collections 2; the plan's "How categories get picked"): the week's categories, in order, of those past the gates:
	/// <list type="number">
	/// <item>each one's lift: how much more of what the user watched it holds than its share of its kind would give, smoothed
	/// toward the library's mix (<see cref="Taste.Lift(double, double, double)"/>; shows with played episodes count most), its
	/// square root counting 0.5 to 2 (<see cref="LiftMost"/>);</item>
	/// <item>its order: lift × its first posters' quality × the week's draw (0.6 to 1), so the nearer and the better come up more
	/// often and the week changes on Monday. 1.9 (the pre-release fix G): the quality against the other categories of its own
	/// kind (<see cref="Footing"/>), so a kind the server gives no critics' ratings (the shows) isn't ranked below one it does;</item>
	/// <item>none of the three weeks before this one (<see cref="CategoryWeeksApart"/>, the weeks kept in
	/// <see cref="LineupData.CategoryHistory"/>) while others are left; then the least recently shown;</item>
	/// <item>one a family (the catalogue's: "1990s neo-noir" and "Neo-noir films of the 2010s" are one);</item>
	/// <item>the first by order, then one to explore: the best by quality and the draw of those nothing the user watched
	/// lifts (a lift of 1 or less), of another family; then the rest by order.</item>
	/// </list>
	/// A week already kept holds: its categories still past the gates come first, in its order, then the rest as above.
	/// <para>
	/// 1.9 (Phase 3, "because you watched": the critical review's options 4 and 5; on the owner's library 27 of the 87 shows
	/// they played were in a shown category): the row is made of what the user played first.
	/// <list type="number">
	/// <item>R3: a category of filters alone (genres, studios, years, a length or a rating: no keyword, summary words or country,
	/// <see cref="FiltersOnly"/>) is in the row only with <see cref="FilteredLeast"/> or more played titles inside; See all
	/// keeps cut 1's rule (<see cref="Lineup.CategoryRanking"/>).</item>
	/// <item>R1: through what was played lately: each title played in the last 30 days, newest first
	/// (<see cref="Anchors.AnchoredSlots.LatelyDays"/>), not held by a category already taken, brings the category that holds it
	/// and the most other played titles the row doesn't hold yet, where one fits (<see cref="AnchoredLeast"/>; their weight in
	/// <see cref="Taste"/>, its lift, then its id breaking ties). These follow the anchors, as More like does: the three weeks
	/// before don't leave them out.</item>
	/// <item>the explore pick second, as before (one the user played nothing of, so never a category of filters alone);</item>
	/// <item>then, while the row has room, the category holding the most of the rest of what the user played (the titles; then
	/// those played lately, their weight, its lift and its id);</item>
	/// <item>then the rest by order, one a family. One holding played titles may join one other of its family, whatever that one
	/// holds (<see cref="FamilyMost"/>): a viewer of sitcoms has NBC's and the workplace's.</item>
	/// </list>
	/// The three weeks before leave out what was shown then but <see cref="CarriedMost"/> categories of the user's taste (those
	/// holding <see cref="TasteStays"/> played titles or more, one played lately first), which are carried over.
	/// </para>
	/// </summary>
	private static CategoryChoice Categories(List<Seen> passed, LineupData data, DateTimeOffset now)
	{
		if (passed.Count == 0)
		{
			return CategoryChoice.None;
		}

		var week = Builders.SpotlightWeek.Of(now);
		var seed = Seed(LineupRefresh.Weekly, now);
		var footing = Footing(passed, data); // 1.9 (fix G)
		var ranked = passed.Select(x => Rank(x, data, seed, footing[x.Collection.Id])).ToList();
		var byId = ranked.ToDictionary(x => x.Id, StringComparer.Ordinal);
		var rowWorthy = ranked.Where(x => x.InRow).ToList(); // 1.9 (Phase 3, R3)

		// the weeks before this one: each category's most recent (1 = last week)
		var lastShown = new Dictionary<string, int>(StringComparer.Ordinal);
		for (var w = CategoryWeeksApart - 1; w >= 1; w--)
		{
			if (data.CategoryHistory.TryGetValue(week.Before(w).Seed, out var shown) && shown?.Ids is { } ids)
			{
				foreach (var id in ids)
				{
					lastShown[id] = w;
				}
			}
		}

		var order = new List<Ranked>();
		var families = new Dictionary<string, int>(StringComparer.Ordinal);
		var taken = new HashSet<string>(StringComparer.Ordinal);
		var covered = new HashSet<Guid>(); // 1.9 (Phase 3): the played titles the taken categories hold
		void Take(Ranked x, int at = -1)
		{
			if (at >= 0 && at < order.Count)
			{
				order.Insert(at, x);
			}
			else
			{
				order.Add(x);
			}

			families[x.Family] = families.GetValueOrDefault(x.Family) + 1;
			taken.Add(x.Id);
			covered.UnionWith(x.Hits);
		}

		bool FamilyFree(Ranked x) => !families.ContainsKey(x.Family);
		bool FamilyRoom(Ranked x) => families.GetValueOrDefault(x.Family) < FamilyMost; // 1.9 (Phase 3): one holding played titles

		// the week already kept: its categories still past the gates (1.9 Phase 3: and fit for the row), in its order, and its
		// explore pick while it is one of them
		var kept = data.CategoryHistory.GetValueOrDefault(week.Seed);
		foreach (var id in kept?.Ids ?? [])
		{
			if (id is not null && byId.TryGetValue(id, out var x) && x.InRow && !taken.Contains(x.Id) && (FamilyFree(x) || (x.Hits.Count > 0 && FamilyRoom(x))))
			{
				Take(x);
			}
		}

		var held = order.Count > 0;
		var explore = held && kept?.Explore is { } e && taken.Contains(e) ? e : null;
		var room = CategoryRow - (held ? 0 : 1); // a place kept for the explore pick

		// 1.9 (the Phase 3 review's Major): of the taste categories shown in the three weeks before, CarriedMost are carried over
		// (those holding a title played lately first, then the most played titles); every other one shown then rotates, as the
		// fillers do (Rotates)
		var carried = rowWorthy.Where(x => lastShown.ContainsKey(x.Id) && x.Hits.Count >= TasteStays)
			.Select(x => (Category: x, Lately: x.Hits.Count(id => data.Played.TryGetValue(id, out var at) && Anchors.AnchoredSlots.Lately(at, now))))
			.OrderByDescending(x => x.Lately).ThenByDescending(x => x.Category.Hits.Count).ThenByDescending(x => x.Category.Lift).ThenBy(x => x.Category.Id, StringComparer.Ordinal)
			.Take(CarriedMost).Select(x => x.Category.Id).ToHashSet(StringComparer.Ordinal);
		bool Rotates(Ranked x) => lastShown.ContainsKey(x.Id) && !carried.Contains(x.Id);

		// 1.9 (Phase 3, R1): through what was played lately, newest first (1.9's review: not one that rotates this week)
		var anchored = 0;
		foreach (var anchor in data.Played.Where(x => Anchors.AnchoredSlots.Lately(x.Value, now)).OrderByDescending(x => x.Value).ThenBy(x => x.Key).Select(x => x.Key))
		{
			if (order.Count >= room)
			{
				break;
			}

			if (covered.Contains(anchor))
			{
				continue;
			}

			if (rowWorthy.Where(x => !taken.Contains(x.Id) && !Rotates(x) && FamilyRoom(x) && x.Holds(anchor))
				.Select(x => (Category: x, Gain: Gain(x, covered, data, now)))
				.Where(x => x.Gain.Titles >= AnchoredLeast)
				.OrderByDescending(x => x.Gain.Titles).ThenByDescending(x => x.Gain.Weight).ThenByDescending(x => x.Category.Lift).ThenBy(x => x.Category.Id, StringComparer.Ordinal)
				.FirstOrDefault() is { Category: { } best })
			{
				Take(best);
				anchored++;
			}
		}

		var fresh = rowWorthy.Where(x => !Rotates(x)).OrderByDescending(x => x.Order).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
		if (!held)
		{
			// no history (or nothing played lately in any category): the first by order, as before
			if (order.Count == 0 && fresh.Count > 0)
			{
				Take(fresh[0]);
			}

			// one to explore, second: of those nothing the user watched lifts, in no family taken
			if (order.Count > 0 && fresh.Where(x => x.Lift <= 1 + 1e-9 && !taken.Contains(x.Id) && FamilyFree(x)).OrderByDescending(x => x.Explore).ThenBy(x => x.Id, StringComparer.Ordinal).FirstOrDefault() is { } other)
			{
				Take(other, 1);
				explore = other.Id;
			}
		}

		// 1.9 (Phase 3): then what holds the most of the rest of what the user played (these rotate with the weeks)
		while (order.Count < CategoryRow)
		{
			if (fresh.Where(x => !taken.Contains(x.Id) && FamilyRoom(x))
				.Select(x => (Category: x, Gain: Gain(x, covered, data, now)))
				.Where(x => x.Gain.Titles > 0)
				.OrderByDescending(x => x.Gain.Titles).ThenByDescending(x => x.Gain.Lately).ThenByDescending(x => x.Gain.Weight).ThenByDescending(x => x.Category.Lift).ThenBy(x => x.Category.Id, StringComparer.Ordinal)
				.FirstOrDefault() is not { Category: { } best })
			{
				break;
			}

			Take(best);
		}

		foreach (var x in fresh)
		{
			if (FamilyFree(x) && !taken.Contains(x.Id))
			{
				Take(x);
			}
		}

		// too few fresh ones for the row (a small library, a short catalogue): those shown before come back, the least recently
		// shown first
		var recent = ranked.Count(Rotates);
		if (order.Count < CategoryRow)
		{
			foreach (var x in rowWorthy.Where(Rotates).OrderByDescending(x => lastShown[x.Id]).ThenByDescending(x => x.Order).ThenBy(x => x.Id, StringComparer.Ordinal))
			{
				if (FamilyFree(x) && !taken.Contains(x.Id))
				{
					Take(x);
				}
			}
		}

		// 1.9: See all's "For you". The critical review's cut 1 (the owner: the long list "isnt very useful"): only the categories
		// the user's history lifts to LiftShown or more, and the week's explore pick, by fit × quality with no draw (the draw
		// decided most of the order where quality is flat), the quality among its own kind's (fix G: Footing, so a show category
		// isn't put below a film one for want of critics' ratings), the most lifted first among equals; none when nothing is lifted (no
		// history): See all then shows the row
		var lifted = ranked.Where(x => x.Lift >= LiftShown).ToList();
		return new CategoryChoice([.. order.Select(x => x.Seen)], explore, new CategoryCounts(passed.Count, order.Count, recent, held)
		{
			Anchored = anchored, // 1.9 (Phase 3)
			FiltersOnly = ranked.Count - rowWorthy.Count,
			PlayedInRow = order.Take(CategoryRow).SelectMany(x => x.Hits).Distinct().Count(),
			Carried = order.Take(CategoryRow).Count(x => carried.Contains(x.Id)), // 1.9 (the Phase 3 review)
		})
		{
			Carried = [.. order.Take(CategoryRow).Where(x => carried.Contains(x.Id)).Select(x => x.Id)],
			Ranking = lifted.Count == 0 ? [] : [.. lifted.Concat(ranked.Where(x => x.Id == explore && x.Lift < LiftShown))
				.OrderByDescending(x => Math.Sqrt(Math.Clamp(x.Lift, 1 / LiftMost, LiftMost)) * x.Quality).ThenByDescending(x => x.Lift).ThenBy(x => x.Id, StringComparer.Ordinal).Select(x => x.Id)],
		};
	}

	// 1.9 (Phase 3): how much of what the user played a category holds that the row doesn't yet: the titles, then their weight
	// (Taste's: a show binged counts more than one sampled), then how many of them were played lately
	private static (int Titles, double Weight, int Lately) Gain(Ranked category, HashSet<Guid> covered, LineupData data, DateTimeOffset now)
	{
		var (titles, weight, lately) = (0, 0.0, 0);
		foreach (var id in category.Hits)
		{
			if (!covered.Contains(id))
			{
				titles++;
				weight += data.Taste.WeightOf(id);
				lately += data.Played.TryGetValue(id, out var at) && Anchors.AnchoredSlots.Lately(at, now) ? 1 : 0;
			}
		}

		return (titles, weight, lately);
	}

	/// <summary>
	/// 1.9 (Phase 3, R3): whether a category's rule is made of filters alone: genres, studios, years, a length or a rating, with
	/// no keyword, summary words or country (the catalogue's; one it doesn't know isn't: missing data is no decision, rule 4).
	/// "Mystery Dramedies" (comedy, drama and mystery) and "Sci-Fi Series from Prime Video" are; "Mockumentary Sitcoms" (its
	/// summary words) isn't.
	/// </summary>
	internal static bool FiltersOnly(MicroGenre? category) =>
		category?.Rule is { } rule && rule.KeywordsAny.Count == 0 && rule.KeywordsAll.Count == 0 && rule.TextAny.Count == 0 && rule.CountriesAny.Count == 0;

	// a category's family (the catalogue's; one it doesn't know is its own), its lift, and its orders for the week, by its
	// quality among its own kind's (fix G); 1.9 (Phase 3): the played titles inside it, and whether it may be in the row (R3)
	private static Ranked Rank(Seen seen, LineupData data, string seed, double quality)
	{
		var kind = KindOf(seen);
		var lift = data.Taste.LiftOf(seen.Pool, kind, kind == IndexKind.Series ? data.Shows : data.Films);
		var near = Math.Sqrt(Math.Clamp(lift, 1 / LiftMost, LiftMost));
		var category = data.Catalogue.Of(seen.Collection.Id);
		var family = category?.Family is { Length: > 0 } f ? f : seen.Collection.Id;
		var hits = data.Played.Count == 0 ? [] : seen.Pool.Where(data.Played.ContainsKey).ToHashSet();
		return new Ranked(seen, family, lift, near * quality * (0.6 + (0.4 * Draw(seed, seen.Collection.Id))),
			quality * (0.6 + (0.4 * Draw(seed + "|explore", seen.Collection.Id))), quality)
		{
			Hits = hits,
			InRow = !FiltersOnly(category) || hits.Count >= FilteredLeast,
		};
	}

	/// <summary>
	/// 1.9 (the pre-release fix G, BH-190-M1): each category's quality for the week's order, 0.8 to 1 by its place among the
	/// categories of its own kind past the gates (the worst 0.8, the best 1, ties sharing their mean place; one alone 0.9). Its
	/// place is by its first posters' mean score (<see cref="QualityScore"/>), then their mean rating: on a server whose films
	/// have critics' ratings and whose shows have none, a show's score is its kind's mean (no confidence), so every show
	/// category stood at 0.9 against film categories up to 1, and 2 of the first 12 were shows though 40 % of those past the
	/// gates were. Ranked like with like, each kind comes up in proportion before the user's evidence lifts one.
	/// </summary>
	private static Dictionary<string, double> Footing(List<Seen> passed, LineupData data)
	{
		var footing = new Dictionary<string, double>(StringComparer.Ordinal);
		foreach (var kind in passed.GroupBy(KindOf))
		{
			var mean = data.Quality.MeanOf(kind.Key);
			var keyed = kind.Select(x => (Seen: x, Key: PosterScore(x, data, mean))).OrderBy(x => x.Key.Score).ThenBy(x => x.Key.Rating).ToList();
			for (var i = 0; i < keyed.Count;)
			{
				var j = i;
				while (j + 1 < keyed.Count && keyed[j + 1].Key == keyed[i].Key)
				{
					j++;
				}

				var place = keyed.Count == 1 ? 0.5 : (i + j) / 2.0 / (keyed.Count - 1);
				for (var k = i; k <= j; k++)
				{
					footing[keyed[k].Seen.Collection.Id] = 0.8 + (0.2 * place);
				}

				i = j + 1;
			}
		}

		return footing;
	}

	// a category's first posters' mean score and mean rating (none: its kind's mean), rounded so equal ones tie exactly
	private static (double Score, double Rating) PosterScore(Seen seen, LineupData data, double mean)
	{
		var first = seen.Titles.Take(Posters).ToList();
		var scores = first.Select(data.Quality.Of).OfType<double>().ToList();
		var ratings = first.Select(x => x.Rating).OfType<double>().Where(double.IsFinite).ToList();
		return (Math.Round(scores.Count == 0 ? mean : scores.Average(), 9), Math.Round(ratings.Count == 0 ? mean : ratings.Average(), 9));
	}
	// end 1.9

	// a candidate's titles (as titles: copies are one), what the gates read of them, and (the daily slots) its order turned
	private static Seen See(LineupCandidate candidate, LineupData data, DateTimeOffset now, int given)
	{
		var titles = new List<IndexEntry>(candidate.Collection.ItemIds.Count);
		var pool = new HashSet<Guid>();
		foreach (var id in candidate.Collection.ItemIds)
		{
			if (data.Titles.TitleOf(id) is { } title && pool.Add(title.Id))
			{
				titles.Add(title);
			}
		}

		var collection = candidate.Collection;
		List<IndexEntry>? reordered = TurnsDaily(candidate.Slot) ? Turned(titles, Seed(LineupRefresh.Daily, now))
			: RanksByQuality(candidate.Slot) ? [.. Best.Of(titles.Where(x => !x.Played), data.Quality), .. titles.Where(x => x.Played)]
			: null;
		if (reordered is not null && titles.Count > 1)
		{
			titles = reordered;
			collection = collection with
			{
				ItemIds = [.. titles.Select(x => x.Id)],
				Cover = [.. titles.Where(x => x.PrimaryTag is not null).Concat(titles.Where(x => x.PrimaryTag is null)).Take(4)],
			};
		}

		var first = titles.Take(Posters).ToList();
		var scored = first.Select(data.Quality.Of).OfType<double>().ToList();
		var mean = first.Count == 0 ? 0 : first.Average(x => data.Quality.MeanOf(x.Kind));
		// 0.8 to 1: a row of titles better than the server's mean up to a point above it (no score: the middle)
		var quality = scored.Count == 0 ? 0.9 : 0.8 + (0.2 * Math.Clamp(0.5 + ((scored.Average() - mean) / 2), 0, 1));
		return new Seen
		{
			Candidate = candidate,
			Collection = collection,
			Titles = titles,
			Pool = pool,
			First = [.. first.Select(x => x.Id)],
			Unwatched = titles.Count(x => !x.Played),
			Art = first.Count == 0 ? 0 : first.Count(x => x.PrimaryTag is not null) / (double)first.Count,
			Quality = quality,
			Given = given,
		};
	}

	// the size floor of the candidate's slot
	private static bool Floored(Seen seen, LineupData data) => seen.Slot switch
	{
		LineupSlot.Unfinished => seen.Titles.Count >= 1,
		LineupSlot.NextIn => seen.Titles.Count >= FranchiseFloor && seen.Unwatched >= 1,
		LineupSlot.MoreFrom or LineupSlot.Spotlight => seen.Unwatched >= PersonFloor,
		LineupSlot.Canon => (seen.Titles.Count >= CanonMatched
				|| (data.ListSizes.TryGetValue(seen.Collection.Id, out var size) && size > 0 && seen.Titles.Count >= CanonShare * size))
			&& seen.Unwatched >= CanonUnwatched,
		_ => seen.Unwatched >= Floor,
	};

	// the daily turn of a best-first list: each title drawn by the day's seed, weighted by its place (the first ones still
	// come first more often), so the first posters change each day and the best stay near the front
	private static List<IndexEntry> Turned(List<IndexEntry> titles, string seed) =>
		[.. titles.Select((x, i) => (Entry: x, Key: Math.Pow(Math.Max(Draw(seed, x.Id.ToString("N")), double.Epsilon), 1 + (i / 4.0))))
			.OrderByDescending(x => x.Key).ThenBy(x => x.Entry.Id).Select(x => x.Entry)];

	// ---- the pages' build --------------------------------------------------------------------------------------------------

	/// <summary>Where the lineup's line is said (the app's Made by Nixie log, <see cref="SmartCollectionCatalog.Log"/>).</summary>
	private static ILogger? Log => SmartCollectionCatalog.Log;

	private static readonly ConditionalWeakTable<ILogger, ConcurrentDictionary<string, byte>> Said = new();

	/// <summary>
	/// The pages' build, off the calling thread: every published collection of the index (as
	/// <see cref="SmartCollectionCatalog.BuildAllAsync"/>, its census said once), plus <paramref name="extra"/> (the trending row),
	/// and the lineup of them. The index's titles are collapsed once for both, and kept while the index is. Its line in the log
	/// (counts, slots and a duration; never a title) is said once an index and a lineup.
	/// </summary>
	/// <param name="homeShown">Review (Minor 4): Home's switches: <see cref="LineupBuild.Home"/> is the lineup of what they let on Home.</param>
	/// <param name="weeks">
	/// 1.9: where the weeks' categories are kept (<see cref="ICategoryWeeks"/>, the signed-in user's <see cref="SmartCollectionStore"/>
	/// by default): read before the lineup, and the week's kept after it (<see cref="KeepWeek"/>).
	/// </param>
	public static Task<LineupBuild> BuildAsync(IndexSnapshot index, DateTimeOffset now, IReadOnlySet<string>? boxedFranchises = null, IReadOnlySet<SmartCollectionKind>? kindsOff = null,
		IReadOnlyList<SmartCollection>? extra = null, Outside.OutsideListsSnapshot? outside = null, Func<SmartCollection, bool>? homeShown = null, ICategoryWeeks? weeks = null)
	{
		var lists = outside ?? Outside.OutsideListsSnapshot.Shared; // the lists as they are now, not when the task runs
		var kept = weeks ?? ViewModels.SmartCollectionServices.Store;
		return Task.Run(async () =>
		{
			var history = await WeeksOf(kept, index).ConfigureAwait(false);
			var text = TextMatches.For(index); // 1.9 (fix A): the matches the categories are made of (MicroGenreBuilder reads the same)
			var made = Build(index, now, boxedFranchises, kindsOff, extra ?? [], lists, homeShown, history);
			made = made with { MadeOf = ReferenceEquals(text, TextMatches.For(index)) ? text : null }; // changed during the build: unknown
			await KeepWeek(kept, index, made.Lineup, now, history, made.MadeOf).ConfigureAwait(false);
			return made;
		});
	}

	// 1.9 (Collections 2, stream lineup)
	// the weeks' categories of the index's own sign-in (BH-180-q1: never another's); none when they can't be read (the lineup is
	// then a first week's, rule 4)
	private static async Task<IReadOnlyDictionary<string, CategoryWeek>?> WeeksOf(ICategoryWeeks? weeks, IndexSnapshot index)
	{
		if (weeks is null || index.Key is null)
		{
			return null;
		}

		try
		{
			return await weeks.CategoryWeeksAsync(index.Key).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			Log?.LogWarning("Made by Nixie: the categories' weeks couldn't be read ({Error})", ex.GetType().Name);
			return null;
		}
	}

	/// <summary>
	/// 1.9: keeps the week's categories (<see cref="Lineup.CategoryWeek"/>) on this PC, for the index's own sign-in, once the
	/// index can say what the user watched: a whole index (not a first read's part) whose played episodes are in
	/// (<see cref="IndexSnapshot.PlaysDue"/>). A week chosen without them would hold all week against the evidence that came
	/// minutes later, as the Spotlight's first pick did before 1.8's re-review. The review's item 4: nor before the index keeps
	/// the matchable keywords (<see cref="IndexSnapshot.KeywordsKnown"/>): the first week after the update is chosen from 1.8.1's
	/// index, whose categories naming other keywords wait for the day's read; kept, that week would hold without them.
	/// 1.9 (the pre-release fix A, BH-190-M2): nor before the summaries' pass has read the shows (<see cref="SummariesRead"/>):
	/// until then every category with a word list waits, and a week kept without them held them out of the row until Monday,
	/// at a first start, after every sign-in, and after Categories off and on. Nor when the matches changed since the
	/// categories were made (<paramref name="madeOf"/>): the change makes the pages build again, and that build keeps it.
	/// </summary>
	/// <param name="madeOf">The summaries' matches the lineup's categories were made of (<see cref="LineupBuild.MadeOf"/>); null: not known, nothing kept.</param>
	internal static async Task KeepWeek(ICategoryWeeks? weeks, IndexSnapshot index, Lineup lineup, DateTimeOffset now, IReadOnlyDictionary<string, CategoryWeek>? read, TextMatches? madeOf)
	{
		// 1.9 (the index's load, the review's item 4): nor while the index says titles were removed that it still has (a read of
		// what changed, RemovalsDue): a week chosen with them would hold them all week
		if (weeks is null || index.Key is null || index.Reading is not null || index.PlaysDue || index.RemovalsDue || !index.KeywordsKnown || lineup.CategoryWeek is not { } shown
			|| madeOf is null || !ReferenceEquals(madeOf, TextMatches.For(index)) || !SummariesRead(index, madeOf))
		{
			return;
		}

		try
		{
			// the review's Major 1: written only if the user didn't sign out since the weeks were read
			await weeks.SaveCategoryWeek(SpotlightWeek.Of(now).Seed, shown, index.Key, (read as KeptCategoryWeeks)?.ReadAfter).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			Log?.LogWarning("Made by Nixie: the week's categories couldn't be kept ({Error})", ex.GetType().Name);
		}
	}

	/// <summary>
	/// 1.9 (the pre-release fix A): whether <paramref name="text"/> has read every kind the catalogue's word lists are for (the
	/// shipped one's: shows), so its categories with a word list are decided: the kind read whole once
	/// (<see cref="TextMatches.CompleteFor"/>, first: a server with no summaries is read whole too, and never holds the week back
	/// for good), or enough of it read (<see cref="MicroGenreMatcher.TextCovers"/>). A kind the index has no title of waits for
	/// nothing (the pass reads none). Off the calling thread: the index's matcher is the builders' (made once an index).
	/// </summary>
	internal static bool SummariesRead(IndexSnapshot index, TextMatches text, MicroGenreCatalogue? catalogue = null)
	{
		MicroGenreMatcher? matcher = null;
		foreach (var kind in (catalogue ?? MicroGenres.Shipped).Categories.Where(x => x.Rule.WaitsForText).Select(x => x.Kind).Distinct())
		{
			if (text.CompleteFor.Contains(kind))
			{
				continue;
			}

			matcher ??= MicroGenreMatcher.For(SmartCollectionCatalog.CollapsedOf(index));
			if (matcher.TitlesOf(kind) > 0 && !matcher.TextCovers(kind, text))
			{
				return false;
			}
		}

		return true;
	}
	// end 1.9

	/// <summary>
	/// Review (Minor 4): Home's lineup: of the candidates Home's switches let on Home only, so the ten slots aren't taken by rows
	/// Home won't show (a kind switched on was crowded out of them, and a canon list Home hides took Acclaimed's place).
	/// </summary>
	public static Lineup HomeLineup(IReadOnlyList<LineupCandidate> candidates, LineupData data, DateTimeOffset now, Func<SmartCollection, bool> shown) =>
		Assemble([.. candidates.Where(x => shown(x.Collection))], data, now);

	/// <summary>
	/// 1.8 (stream s's Major 2): the lineup again, off the calling thread, of collections already made (<paramref name="all"/>, a
	/// build's) and <paramref name="extra"/> (the trending row, the week's Spotlight once <see cref="SmartCollectionCatalog.BuildLaterAsync"/>
	/// has it): no builder runs again.
	/// </summary>
	/// <param name="fallback">
	/// The re-review: what a throw gives (the build made without the extra ones), not an empty lineup. 1.9 (fix A): the build
	/// <paramref name="all"/> came from; its <see cref="LineupBuild.MadeOf"/> says what their categories were made of.
	/// </param>
	/// <param name="weeks">1.9: where the weeks' categories are kept (<see cref="BuildAsync"/>'s).</param>
	public static Task<LineupBuild> AssembleAsync(IndexSnapshot index, IReadOnlyList<SmartCollection> all, IReadOnlyList<SmartCollection> extra, DateTimeOffset now,
		Func<SmartCollection, bool>? homeShown = null, Outside.OutsideListsSnapshot? outside = null, LineupBuild? fallback = null, ICategoryWeeks? weeks = null)
	{
		var lists = outside ?? Outside.OutsideListsSnapshot.Shared;
		var kept = weeks ?? ViewModels.SmartCollectionServices.Store;
		// 1.9 (fix A): the matches all's categories were made of: the build's they came from (the fallback's); with none given, the
		// matches as they are now (a caller with no build)
		var madeOf = fallback is null ? TextMatches.For(index) : fallback.MadeOf;
		return Task.Run(async () =>
		{
			var watch = Stopwatch.StartNew();
			try
			{
				var history = await WeeksOf(kept, index).ConfigureAwait(false);
				var data = LineupData.Of(index, lists, now, history); // 1.9 (fix 9): the day, as the builders'; the weeks' categories
				var candidates = Candidates([.. all, .. extra]);
				var lineup = Assemble(candidates, data, now);
				var home = homeShown is null ? lineup : HomeLineup(candidates, data, now, homeShown);
				Say(index, lineup, data.ArtShare, all.Count + extra.Count, 0, 0, watch.ElapsedMilliseconds);
				await KeepWeek(kept, index, lineup, now, history, madeOf).ConfigureAwait(false);
				return new LineupBuild(all, lineup) { Home = home, MadeOf = madeOf };
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				Log?.LogWarning("Made by Nixie: the lineup couldn't be made ({Error}); the collections still are", ex.GetType().Name);
				return fallback ?? new LineupBuild(all, Lineup.Empty) { Home = Lineup.Empty };
			}
		});
	}

	/// <param name="history">1.9: the weeks' categories kept (<see cref="ICategoryWeeks"/>); null: none (a first week).</param>
	internal static LineupBuild Build(IndexSnapshot index, DateTimeOffset now, IReadOnlySet<string>? boxedFranchises, IReadOnlySet<SmartCollectionKind>? kindsOff,
		IReadOnlyList<SmartCollection> extra, Outside.OutsideListsSnapshot lists, Func<SmartCollection, bool>? homeShown = null, IReadOnlyDictionary<string, CategoryWeek>? history = null)
	{
		var watch = Stopwatch.StartNew();
		var data = LineupData.Of(index, lists, now, history); // 1.9 (fix 9): the day, as the builders'; the weeks' categories
		var collapsed = watch.ElapsedMilliseconds;
		IReadOnlyList<SmartCollection> all = [.. SmartCollectionCatalog.BuildAll(SmartCollectionCatalog.PublishedBuilders, index, now, boxedFranchises, kindsOff, lists, Log, census: true, titles: data.Titles, publishing: true)
			.Where(x => SmartCollectionCatalog.Published.Contains(x.Kind))];
		var built = watch.ElapsedMilliseconds;
		Lineup lineup;
		var home = Lineup.Empty;
		try
		{
			var candidates = Candidates([.. all, .. extra]);
			lineup = Assemble(candidates, data, now);
			home = homeShown is null ? lineup : HomeLineup(candidates, data, now, homeShown); // review (Minor 4)
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			// the lineup is extra: a throw in it leaves the pages their own rows (the exception's type only)
			Log?.LogWarning("Made by Nixie: the lineup couldn't be made ({Error}); the collections still are", ex.GetType().Name);
			lineup = Lineup.Empty;
		}

		Say(index, lineup, data.ArtShare, all.Count + extra.Count, collapsed, built, watch.ElapsedMilliseconds);
		return new LineupBuild(all, lineup) { Home = home };
	}

	// counts, slots and durations only (1.8's "Report a problem" may attach the log); once an index and a lineup a log
	private static void Say(IndexSnapshot index, Lineup lineup, double art, int collections, long collapsed, long built, long took)
	{
		if (Log is not { } log || index.Reading is not null)
		{
			return;
		}

		var slots = string.Join(", ", lineup.Counts.OrderBy(x => x.Key).Select(x =>
			string.Create(CultureInfo.InvariantCulture, $"{x.Key} {x.Value.Candidates}/{x.Value.Floor}/{x.Value.Ceiling}/{x.Value.Artwork}/{x.Value.Coherence}/{x.Value.Novelty}/{x.Value.Picked}")));
		var order = string.Join(",", lineup.Picks.Select(x => x.Slot));
		// the artwork gate on, or off (rule 4) with the share of titles that have a poster
		var gate = art >= ArtworkKnown ? "on" : string.Create(CultureInfo.InvariantCulture, $"off, {art:P1} of the titles have a poster");
		// 1.9: the week's categories, counted (the Category slot's coherence count is the week's order)
		var categories = lineup.CategoryCounts is { } c
			? string.Create(CultureInfo.InvariantCulture, $"categories {c.Passed} past the gates, {c.Ranked} in the week's order (one a family), {c.Recent} shown in the 3 weeks before, explore {(lineup.Explore is null ? "none" : "1")}{(c.Kept ? ", the week's kept" : "")}, {c.Anchored} through titles played lately, {c.FiltersOnly} of filters alone out of the row, {c.PlayedInRow} played titles in the row")
			: "no categories";
		var key = string.Create(CultureInfo.InvariantCulture, $"{index.Key}|{index.ReadAt.UtcTicks}|{index.Entries.Count}|{Seed(LineupRefresh.Daily, lineup.Day)}|{slots}|{order}|{categories}");
		if (Said.GetOrCreateValue(log).TryAdd(key, 0))
		{
			log.LogInformation("Made by Nixie lineup: {Collections} collections; slot candidates/floor/ceiling/artwork/coherence/novelty/picked: {Slots}; picked {Order}; artwork gate {Gate}; {Categories}; in {Ms} ms (titles {CollapseMs} ms, collections {BuildMs} ms)",
				collections, slots, order, gate, categories, took, collapsed, built - collapsed);
		}
	}
}
