using FluentFin.Core.SmartCollections;

namespace Nixie.Collections;

/// <summary>What the index can say of a rule on one title: no, yes, or "maybe" (it turns on a keyword the index drops, or a summary).</summary>
public enum Truth
{
	No,
	Maybe,
	Yes,
}

/// <summary>Where a category's pool stands against the lineup's gates.</summary>
public enum Verdict
{
	/// <summary>Between the floor and the ceiling.</summary>
	Fits,

	/// <summary>Fewer than <see cref="Census.Floor"/> unwatched (or, for an unknown pool, fewer even at most).</summary>
	UnderFloor,

	/// <summary>More unwatched than its kind's ceiling (or, for an unknown pool, more even at least): a filter, not a theme.</summary>
	OverCeiling,

	/// <summary>It turns on keywords the index doesn't keep, or on a summary: unknown until the app's census.</summary>
	Unknown,
}

/// <summary>
/// The rule matcher of the census. The app's own (stream match's MicroGenreMatcher) is the one that ships; this one says what
/// a saved index can tell: the index keeps only the allowlisted and seasonal keywords and no summary, so a condition on
/// anything else is "maybe", unless the title has no other tags (its TagCount says so).
/// </summary>
public static class Rules
{
	/// <summary>Whether a keyword is on the allowlist or the seasonal list (the lint: a matchable keyword may be neither).</summary>
	public static bool Listed(string keyword) => Keywords.Allowed.ContainsKey(keyword.Trim()) || Keywords.Seasonal.Contains(keyword.Trim());

	/// <summary>
	/// What a saved index of revision 3 keeps of a title's tags (the allowlist and the six seasonal ones before 1.9): the census's
	/// default. An index of revision 4 keeps every matchable keyword (<see cref="IndexFile.Kept"/>).
	/// </summary>
	public static bool Kept(string keyword) => Keywords.KeptByRevision3(keyword);

	/// <summary>The index's kind of a category ("films" → Movie).</summary>
	public static IndexKind KindOf(Category category) => category.Films ? IndexKind.Movie : IndexKind.Series;

	/// <summary>A studio's or network's name for matching: lower case, letters and digits only ("Adult Swim" = "adult swim" = "AdultSwim").</summary>
	public static string Key(string name) => new([.. name.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

	/// <summary>A title's length in whole minutes, half up as the app's matcher (a film's runtime, a show's episode); null when none.</summary>
	public static int? Minutes(IndexEntry title) => title.RunTimeTicks is > 0 and var ticks ? (int)Math.Round(ticks / (double)TimeSpan.TicksPerMinute, MidpointRounding.AwayFromZero) : null;

	public static Truth Match(Category category, IndexEntry title, Func<string, bool>? kept = null)
	{
		if (title.Kind != KindOf(category))
		{
			return Truth.No;
		}

		var r = category.Rule;
		if (!r.GenresAll.All(g => Has(title.Genres, g)) || (r.GenresAny.Length > 0 && !r.GenresAny.Any(g => Has(title.Genres, g)))
			|| r.GenresNone.Any(g => Has(title.Genres, g)))
		{
			return Truth.No;
		}

		if (!InRange(title.Year, r.Years) || !InRange(Minutes(title), r.LengthMinutes))
		{
			return Truth.No;
		}

		if (r.CountriesAny.Length > 0 && !r.CountriesAny.Any(c => Has(title.Countries, c)))
		{
			return Truth.No;
		}

		if (r.StudiosAny.Length > 0 && !r.StudiosAny.Select(Key).Any(s => title.Studios.Any(x => Key(x) == s)))
		{
			return Truth.No;
		}

		if ((r.CommunityMin is { } least && !(title.Rating is > 0 and var rating && rating >= least))
			|| (r.CriticMin is { } critics && !(title.Critic is > 0 and var critic && critic >= critics)))
		{
			return Truth.No;
		}

		kept ??= Kept;
		var truth = And(Truth.Yes, AnyOf(title, r.KeywordsAny, kept));
		truth = And(truth, AllOf(title, r.KeywordsAll, kept));
		truth = And(truth, NoneOf(title, r.KeywordsNone, kept));
		return r.TextAny.Length > 0 ? And(truth, Truth.Maybe) : truth;
	}

	// the title's tags beyond those the index kept: none when its count before the allowlist is no more than those kept
	private static bool MayHaveOthers(IndexEntry title) => title.TagCount > title.Tags.Length;

	private static Truth AnyOf(IndexEntry title, string[] keywords, Func<string, bool> kept)
	{
		if (keywords.Length == 0 || keywords.Any(k => kept(k) && Has(title.Tags, k)))
		{
			return Truth.Yes;
		}

		return keywords.Any(k => !kept(k)) && MayHaveOthers(title) ? Truth.Maybe : Truth.No;
	}

	private static Truth AllOf(IndexEntry title, string[] keywords, Func<string, bool> kept)
	{
		if (keywords.Any(k => kept(k) && !Has(title.Tags, k)))
		{
			return Truth.No;
		}

		return !keywords.Any(k => !kept(k)) ? Truth.Yes : MayHaveOthers(title) ? Truth.Maybe : Truth.No;
	}

	private static Truth NoneOf(IndexEntry title, string[] keywords, Func<string, bool> kept)
	{
		if (keywords.Any(k => kept(k) && Has(title.Tags, k)))
		{
			return Truth.No;
		}

		return keywords.Any(k => !kept(k)) && MayHaveOthers(title) ? Truth.Maybe : Truth.Yes;
	}

	private static Truth And(Truth a, Truth b) => (Truth)Math.Min((int)a, (int)b);

	private static bool Has(string[] values, string value) => Array.Exists(values, x => string.Equals(x.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase));

	private static bool InRange(int? value, int?[]? range)
	{
		if (range is not [var from, var to] || (from is null && to is null))
		{
			return true;
		}

		return value is { } v && (from is null || v >= from) && (to is null || v <= to);
	}
}

/// <summary>A category's pool on one index: the titles it surely holds, and how many more it may hold.</summary>
/// <param name="Sure">The titles the rule surely matches (unwatched first is not implied: index order).</param>
/// <param name="Most">The titles it may match: the sure ones and the "maybe" ones.</param>
/// <param name="MostUnwatched">Of <paramref name="Most"/>, those not watched.</param>
/// <param name="WatchedShows">For a shows category: how many shows the user has played episodes of are surely in it.</param>
public sealed record Pool(Category Category, IReadOnlyList<IndexEntry> Sure, int Most, int MostUnwatched, int WatchedShows)
{
	public int Least => Sure.Count;

	public int LeastUnwatched => Sure.Count(x => !x.Played);

	/// <summary>The index says it all: no title turns on a dropped keyword or a summary.</summary>
	public bool Known => Most == Least;
}

/// <summary>
/// The census of a catalogue on one saved index (copies collapsed, as the app counts): each category's pool, all and unwatched,
/// against the floor (8 unwatched) and its kind's ceiling (1 % of that kind, at least 30), and the overlap between categories
/// of one family. Counts only: nothing here says a title, unless <see cref="Examples"/> is asked for.
/// </summary>
public sealed class Census
{
	/// <summary>The lineup's floor: unwatched titles a collection needs (CollectionLineup.Floor).</summary>
	public const int Floor = CollectionLineup.Floor;

	/// <summary>The ceiling's share of its kind (CollectionLineup.ThemeCeilingShare), per kind for 1.9.</summary>
	public const double CeilingShare = CollectionLineup.ThemeCeilingShare;

	/// <summary>The ceiling's least (CollectionLineup.ThemeCeilingLeast).</summary>
	public const int CeilingLeast = CollectionLineup.ThemeCeilingLeast;

	private readonly Dictionary<string, Pool> _pools;

	private Census(CanonicalTitles titles, IReadOnlySet<Guid> watchedShows, IReadOnlyList<Pool> pools)
	{
		Titles = titles;
		WatchedShows = watchedShows;
		Pools = pools;
		Films = titles.Entries.Count(x => x.Kind == IndexKind.Movie);
		Shows = titles.Entries.Count(x => x.Kind == IndexKind.Series);
		_pools = pools.ToDictionary(x => x.Category.Id, StringComparer.Ordinal);
	}

	public CanonicalTitles Titles { get; }

	/// <summary>The canonical ids of the shows the user has played episodes of (the played-episodes pass), or has played.</summary>
	public IReadOnlySet<Guid> WatchedShows { get; }

	public IReadOnlyList<Pool> Pools { get; }

	public int Films { get; }

	public int Shows { get; }

	public Pool this[string id] => _pools[id];

	/// <summary>A kind's ceiling: 1 % of the titles of that kind, at least 30.</summary>
	public static int CeilingFor(int ofKind) => Math.Max(CeilingLeast, (int)Math.Ceiling(CeilingShare * ofKind));

	public int CeilingOf(Category category) => CeilingFor(category.Films ? Films : Shows);

	public static Census Of(CanonicalTitles titles, IReadOnlyDictionary<Guid, SeriesPlays>? plays, Catalogue catalogue, Func<string, bool>? kept = null)
	{
		var watched = new HashSet<Guid>();
		foreach (var (id, p) in plays ?? new Dictionary<Guid, SeriesPlays>())
		{
			if (p.Played > 0 && titles.TitleOf(id) is { Kind: IndexKind.Series } show)
			{
				watched.Add(show.Id);
			}
		}

		foreach (var show in titles.Entries.Where(x => x.Kind == IndexKind.Series && x.Played))
		{
			watched.Add(show.Id);
		}

		var pools = new List<Pool>(catalogue.Categories.Count);
		foreach (var category in catalogue.Categories)
		{
			var sure = new List<IndexEntry>();
			int most = 0, mostUnwatched = 0;
			foreach (var title in titles.Entries)
			{
				var truth = Rules.Match(category, title, kept);
				if (truth == Truth.No)
				{
					continue;
				}

				most++;
				mostUnwatched += title.Played ? 0 : 1;
				if (truth == Truth.Yes)
				{
					sure.Add(title);
				}
			}

			pools.Add(new Pool(category, sure, most, mostUnwatched, category.Films ? 0 : sure.Count(x => watched.Contains(x.Id))));
		}

		return new Census(titles, watched, pools);
	}

	public Verdict VerdictOf(Pool pool)
	{
		var ceiling = CeilingOf(pool.Category);
		if (pool.MostUnwatched < Floor)
		{
			return Verdict.UnderFloor;
		}

		if (pool.LeastUnwatched > ceiling)
		{
			return Verdict.OverCeiling;
		}

		return pool.Known ? Verdict.Fits : Verdict.Unknown;
	}

	/// <summary>The category of the same family and kind whose known pool overlaps this one's most (Jaccard), or null.</summary>
	public (string Id, double Jaccard)? Closest(Pool pool)
	{
		if (!pool.Known || pool.Least == 0)
		{
			return null;
		}

		var mine = pool.Sure.Select(x => x.Id).ToHashSet();
		(string, double)? best = null;
		foreach (var other in Pools)
		{
			if (ReferenceEquals(other, pool) || !other.Known || other.Least == 0 || other.Category.Family != pool.Category.Family || other.Category.Kind != pool.Category.Kind)
			{
				continue;
			}

			var j = Jaccard(mine, other.Sure);
			if (best is null || j > best.Value.Item2)
			{
				best = (other.Category.Id, j);
			}
		}

		return best;
	}

	/// <summary>The overlap (Jaccard) of a known pool with its parent keyword collection's titles of its kind; null without a parent.</summary>
	public double? ParentOverlap(Pool pool)
	{
		if (!pool.Known || ParentKeyword(pool.Category.Parent) is not { } keyword)
		{
			return null;
		}

		var kind = Rules.KindOf(pool.Category);
		var parent = Titles.Entries.Where(x => x.Kind == kind && x.Tags.Contains(keyword, StringComparer.OrdinalIgnoreCase)).Select(x => x.Id).ToHashSet();
		return parent.Count == 0 && pool.Least == 0 ? 0 : Jaccard(parent, pool.Sure);
	}

	/// <summary>The allowlisted keyword a parent id names ("keyword:neonoir" → "neo-noir"), as the keyword builder makes ids; null when none.</summary>
	public static string? ParentKeyword(string? parent) =>
		parent is { } p && p.StartsWith("keyword:", StringComparison.Ordinal)
			? Keywords.Allowed.Keys.FirstOrDefault(k => "keyword:" + Rules.Key(k) == p)
			: null;

	/// <summary>Up to <paramref name="count"/> titles of a pool, unwatched first, then best scored (the app's Best.Of order).</summary>
	public IEnumerable<IndexEntry> Examples(Pool pool, int count)
	{
		var quality = QualityScore.Of(Titles);
		return pool.Sure.Select(x => (Entry: x, Q: quality.Of(x)))
			.OrderBy(x => x.Entry.Played)
			.ThenBy(x => x.Q is null)
			.ThenByDescending(x => x.Q ?? 0)
			.ThenByDescending(x => x.Entry.Rating ?? 0)
			.ThenBy(x => x.Entry.OrderName, StringComparer.OrdinalIgnoreCase)
			.Take(count)
			.Select(x => x.Entry);
	}

	private static double Jaccard(HashSet<Guid> a, IEnumerable<IndexEntry> b)
	{
		var bs = b.Select(x => x.Id).ToHashSet();
		var both = bs.Count(a.Contains);
		var either = a.Count + bs.Count - both;
		return either == 0 ? 0 : both / (double)either;
	}
}
