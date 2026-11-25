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
}

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

	/// <summary>
	/// Home's rows (the report's "Where they show"): the best personal slot, then the seasonal row in its window, otherwise
	/// Trending, otherwise Spotlight; then the lineup's next ones, in its order, up to <paramref name="rows"/> (at most
	/// <see cref="CollectionLineup.HomeMost"/>). Unfinished series is never one of them: it is Home's own row, with its own switch.
	/// </summary>
	/// <param name="shown">Whether Settings lets a collection on Home (its kind on, its row on).</param>
	public IReadOnlyList<SmartCollection> ForHome(int rows, Func<SmartCollection, bool>? shown = null)
	{
		rows = Math.Clamp(rows, 0, CollectionLineup.HomeMost);
		var open = Picks.Where(x => x.Slot != LineupSlot.Unfinished && (shown?.Invoke(x.Collection) ?? true)).ToList();
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

	private static readonly ConditionalWeakTable<IndexSnapshot, Lazy<(CanonicalTitles Titles, QualityScore Quality, int Films, double Art)>> Made = new();

	/// <summary>
	/// The index's: its titles collapsed once and kept while the index is (Home and the Collections page share them), the
	/// lists' sizes from <paramref name="outside"/>.
	/// </summary>
	public static LineupData Of(IndexSnapshot index, Outside.OutsideListsSnapshot? outside = null)
	{
		var (titles, quality, films, art) = Made.GetValue(index, x => new Lazy<(CanonicalTitles, QualityScore, int, double)>(() =>
		{
			var collapsed = BuildContext.Collapse(x.Entries);
			// as the builders' own score (BuildContext.Quality), without the outside lists: what backs a rating decides its mode
			var backing = RatingBacking.Of(collapsed.Entries, new HashSet<Guid>(), x.BackingKnown);
			return (collapsed, QualityScore.Of(collapsed, plain: !backing.Any), collapsed.Entries.Count(e => e.Kind == IndexKind.Movie), ArtOf(collapsed));
		})).Value;
		var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (var list in (outside ?? Outside.OutsideListsSnapshot.Empty).Lists.Where(l => l.Kind == Outside.OutsideListKind.Award))
		{
			sizes[$"award:{list.Id}"] = list.Items.Count;
		}

		return new LineupData(titles, quality, films) { ListSizes = sizes, ArtShare = art };
	}

	/// <summary>Titles given as they are (tests): each its own title.</summary>
	public static LineupData Of(IReadOnlyList<IndexEntry> entries)
	{
		var titles = CanonicalTitles.Of(entries);
		return new LineupData(titles, QualityScore.Of(titles), titles.Entries.Count(e => e.Kind == IndexKind.Movie)) { ArtShare = ArtOf(titles) };
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

	/// <summary>The setting: how many of the lineup's rows Home shows (0 to <see cref="HomeMost"/>).</summary>
	public static Key<int> HomeRowsKey { get; } = new("CollectionsHomeLineupRows", HomeDefault);

	/// <summary>Home's rows from the lineup under the setting, 0 to <see cref="HomeMost"/>.</summary>
	public static int HomeRows(ILocalSettingsService? settings) => Math.Clamp(settings?.ReadSetting(HomeRowsKey) ?? HomeDefault, 0, HomeMost);

	/// <summary>How many of a slot the lineup takes (the report's caps; the seasonal row's 2 are the Collections page's, Home takes 1).</summary>
	public static int Cap(LineupSlot slot) => slot switch
	{
		LineupSlot.MoreLike or LineupSlot.Theme or LineupSlot.Seasonal => 2,
		_ => 1,
	};

	/// <summary>How often a slot's choice changes.</summary>
	public static LineupRefresh Refresh(LineupSlot slot) => slot switch
	{
		LineupSlot.NextIn or LineupSlot.MoreLike or LineupSlot.MoreFrom => LineupRefresh.ThreeDays,
		LineupSlot.Spotlight or LineupSlot.Theme => LineupRefresh.Weekly,
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
			if (seen.Slot == LineupSlot.Theme && seen.Unwatched > ceiling)
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

		// each slot's own order: its titles' quality, turned by the slot's seed (the better still come up more often); the
		// personal slots and the Spotlight as their builders gave them
		var queues = new Dictionary<LineupSlot, Queue<Seen>>();
		foreach (var group in kept.GroupBy(x => x.Slot))
		{
			if (KeepsOrder(group.Key))
			{
				queues[group.Key] = new Queue<Seen>(group.OrderBy(x => x.Given));
				continue;
			}

			var seed = Seed(Refresh(group.Key), now);
			foreach (var m in group)
			{
				m.Order = m.Quality * (Refresh(group.Key) == LineupRefresh.Live ? 1 : 0.6 + (0.4 * Draw(seed, m.Collection.Id)));
			}

			queues[group.Key] = new Queue<Seen>(group.OrderByDescending(x => x.Order).ThenBy(x => x.Collection.Id, StringComparer.Ordinal));
		}

		// greedy: the best of each slot's next one, scored against what is already taken
		var picks = new List<(Seen Seen, double Score)>();
		var shown = new HashSet<Guid>();
		while (picks.Count < Slots)
		{
			(Seen Seen, double Score)? best = null;
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
					if (already > MostShown || picks.Any(p => Jaccard(p.Seen.Pool, next.Pool) > MostOverlap))
					{
						queue.Dequeue(); // a near copy of one taken: the better (taken first) stays
						Of(slot)[5]--;
						continue;
					}

					var score = Prior(slot) * next.Quality * (1 - already);
					if (best is not { } b || score > b.Score)
					{
						best = (next, score);
					}

					break;
				}
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

		return new Lineup([.. picks.Select(x => new LineupPick(x.Seen.Collection, x.Seen.Slot, Math.Round(x.Score, 4)))],
			counts.ToDictionary(x => x.Key, x => new LineupSlotCount(x.Value[0], x.Value[1], x.Value[2], x.Value[3], x.Value[4], x.Value[5], x.Value[6])),
			now);
	}

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
	public static Task<LineupBuild> BuildAsync(IndexSnapshot index, DateTimeOffset now, IReadOnlySet<string>? boxedFranchises = null, IReadOnlySet<SmartCollectionKind>? kindsOff = null,
		IReadOnlyList<SmartCollection>? extra = null, Outside.OutsideListsSnapshot? outside = null, Func<SmartCollection, bool>? homeShown = null)
	{
		var lists = outside ?? Outside.OutsideListsSnapshot.Shared; // the lists as they are now, not when the task runs
		return Task.Run(() => Build(index, now, boxedFranchises, kindsOff, extra ?? [], lists, homeShown));
	}

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
	/// <param name="fallback">The re-review: what a throw gives (the build made without the extra ones), not an empty lineup.</param>
	public static Task<LineupBuild> AssembleAsync(IndexSnapshot index, IReadOnlyList<SmartCollection> all, IReadOnlyList<SmartCollection> extra, DateTimeOffset now,
		Func<SmartCollection, bool>? homeShown = null, Outside.OutsideListsSnapshot? outside = null, LineupBuild? fallback = null)
	{
		var lists = outside ?? Outside.OutsideListsSnapshot.Shared;
		return Task.Run(() =>
		{
			var watch = Stopwatch.StartNew();
			try
			{
				var data = LineupData.Of(index, lists);
				var candidates = Candidates([.. all, .. extra]);
				var lineup = Assemble(candidates, data, now);
				var home = homeShown is null ? lineup : HomeLineup(candidates, data, now, homeShown);
				Say(index, lineup, data.ArtShare, all.Count + extra.Count, 0, 0, watch.ElapsedMilliseconds);
				return new LineupBuild(all, lineup) { Home = home };
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				Log?.LogWarning("Made by Nixie: the lineup couldn't be made ({Error}); the collections still are", ex.GetType().Name);
				return fallback ?? new LineupBuild(all, Lineup.Empty) { Home = Lineup.Empty };
			}
		});
	}

	internal static LineupBuild Build(IndexSnapshot index, DateTimeOffset now, IReadOnlySet<string>? boxedFranchises, IReadOnlySet<SmartCollectionKind>? kindsOff,
		IReadOnlyList<SmartCollection> extra, Outside.OutsideListsSnapshot lists, Func<SmartCollection, bool>? homeShown = null)
	{
		var watch = Stopwatch.StartNew();
		var data = LineupData.Of(index, lists);
		var collapsed = watch.ElapsedMilliseconds;
		IReadOnlyList<SmartCollection> all = [.. SmartCollectionCatalog.BuildAll(SmartCollectionCatalog.PublishedBuilders, index, now, boxedFranchises, kindsOff, lists, Log, census: true, titles: data.Titles)
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
		var key = string.Create(CultureInfo.InvariantCulture, $"{index.Key}|{index.ReadAt.UtcTicks}|{index.Entries.Count}|{Seed(LineupRefresh.Daily, lineup.Day)}|{slots}|{order}");
		if (Said.GetOrCreateValue(log).TryAdd(key, 0))
		{
			log.LogInformation("Made by Nixie lineup: {Collections} collections; slot candidates/floor/ceiling/artwork/coherence/novelty/picked: {Slots}; picked {Order}; artwork gate {Gate}; in {Ms} ms (titles {CollapseMs} ms, collections {BuildMs} ms)",
				collections, slots, order, gate, took, collapsed, built - collapsed);
		}
	}
}
