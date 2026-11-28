using System.Runtime.CompilerServices;
using FluentFin.Core.SmartCollections.Builders;

namespace FluentFin.Core.SmartCollections.Anchors;

/// <summary>
/// Nixie 1.8 (P1-2): the three personal slots, made from the user's anchors (<see cref="AnchorFinder"/>): "Next in
/// &lt;franchise&gt;", "More like &lt;title&gt;" (the report's "Because you watched") and "More from &lt;person&gt;". Each
/// builder makes its candidates in the order it prefers them (the anchors' <see cref="Hold"/> order: the same for three
/// days unless an anchor changes; 1.9: More like follows the titles played lately, newest first, so it reorders after any
/// episode played, <see cref="AnchorData.Lately"/>); the lineup (<c>CollectionLineup</c>, stream l) takes them in that order,
/// within its caps (0–1, 0–2, 0–1). No anchors, no candidates: a user who has finished nothing sees none of these.
/// </summary>
public static class AnchoredSlots
{
	/// <summary>
	/// The candidates a kind makes at most, its best in its order: the lineup takes one or two, and the rest are its fallbacks
	/// when one overlaps another slot. Every candidate counts in the pages' "Made by Nixie · N" (57 people on 27,000 titles).
	/// </summary>
	public const int MostCandidates = AnchorFinder.Recent;

	/// <summary>The three builders, from the index's own facts (<see cref="IndexAnchorSource"/>).</summary>
	public static IReadOnlyList<ISmartCollectionBuilder> Builders { get; } = [new NextInFranchiseBuilder(), new MoreLikeBuilder(), new MoreFromBuilder()];

	/// <summary>The ids each makes ("next:franchise:2344", "like:…", "more:denisvilleneuve"), for the catalog's kind of an id.</summary>
	public static IReadOnlyList<(string Prefix, SmartCollectionKind Kind)> IdPrefixes { get; } =
	[
		(NextInFranchiseBuilder.IdPrefix, SmartCollectionKind.NextIn), (MoreLikeBuilder.IdPrefix, SmartCollectionKind.MoreLike), (MoreFromBuilder.IdPrefix, SmartCollectionKind.MoreFrom),
	];

	private static readonly ConditionalWeakTable<BuildContext, ConditionalWeakTable<IAnchorSource, AnchorData>> Made = new();

	/// <summary>What the three builders share of one build: made once a context and source.</summary>
	internal static AnchorData DataOf(BuildContext context, IAnchorSource source) =>
		Made.GetOrCreateValue(context).GetValue(source, x => new AnchorData(context, x, x is IndexAnchorSource ? Kept.GetOrCreateValue(context.Index) : new KeptOfIndex()));

	// the people and the similarity of an index read, from the index's own facts: the same for every build of it (Home, the
	// Collections and Libraries pages each build), so made once a read, not once a build (about 150 ms on 27,000 entries)
	private static readonly ConditionalWeakTable<IndexSnapshot, KeptOfIndex> Kept = new();

	/// <summary>The people and similarity made of one index (one build's when the facts are a test's own).</summary>
	internal sealed class KeptOfIndex
	{
		private readonly Lock _lock = new();
		private PeopleIndex? _people;
		private TitleSimilarity? _similarity;
		// 1.8 (P1-1, stream l: Home's time): the anchors and each anchor's alike titles are the index's too (its titles, their
		// plays, their features): made once a read, not once a build. Home's R made them again each time (25,000 titles: about
		// 10 ms for the anchors, 30 to 75 ms for six anchors' alike titles)
		private IReadOnlyList<Anchor>? _anchors;
		private IReadOnlyList<Anchor>? _played; // 1.9 (Phase 3): every title played, the latest first (any day: the day filters them)
		private readonly Dictionary<Guid, IReadOnlyList<(IndexEntry Title, double Similarity)>> _likes = [];

		public IReadOnlyList<Anchor> Anchors(Func<IReadOnlyList<Anchor>> make)
		{
			lock (_lock)
			{
				return _anchors ??= make();
			}
		}

		public IReadOnlyList<Anchor> Played(Func<IReadOnlyList<Anchor>> make)
		{
			lock (_lock)
			{
				return _played ??= make();
			}
		}

		public IReadOnlyList<(IndexEntry Title, double Similarity)> Like(Guid anchor, Func<IReadOnlyList<(IndexEntry Title, double Similarity)>> make)
		{
			lock (_lock)
			{
				if (!_likes.TryGetValue(anchor, out var like))
				{
					_likes[anchor] = like = make();
				}

				return like;
			}
		}
		// end 1.8 (stream l)

		public PeopleIndex People(Func<PeopleIndex> make)
		{
			lock (_lock)
			{
				return _people ??= make();
			}
		}

		public TitleSimilarity Similarity(Func<TitleSimilarity> make)
		{
			lock (_lock)
			{
				return _similarity ??= make();
			}
		}
	}

	/// <summary>"A, B and C".</summary>
	internal static string Listed(IReadOnlyList<string> words) => words.Count switch
	{
		0 => "",
		1 => words[0],
		_ => $"{string.Join(", ", words.Take(words.Count - 1))} and {words[^1]}",
	};

	// 1.9 (Phase 3, "because you watched": the critical review's option 5, the owner's "hold for the rebuild")
	/// <summary>
	/// 1.9 (Phase 3, R1): "played lately" is played on one of the last this many calendar days (as <see cref="Hold.DaysBetween"/>
	/// counts them): More like is made for each such title, and the lineup reaches a category through each.
	/// </summary>
	public const int LatelyDays = 30;

	/// <summary>
	/// 1.9 (Phase 3): whether the user played <paramref name="title"/> (a canonical one), and when last: a show with an episode
	/// played (any copy's) or played to its end; a film played to its end (the server marks it at 90 %), not one begun. Its last
	/// play: a show's latest episode or its own last play, whichever is later; a film's own (<paramref name="own"/>, the anchors'
	/// source: <see cref="IndexEntry.LastPlayed"/> in the app); null when not known. The one rule for More like's anchors, the
	/// lineup's categories and their reason lines.
	/// </summary>
	public static (bool Played, DateTimeOffset? Last) PlayOf(IndexEntry title, CanonicalTitles titles, IReadOnlyDictionary<Guid, SeriesPlays> plays, DateTimeOffset? own)
	{
		if (title.Kind != IndexKind.Series)
		{
			return (title.Played, title.Played ? own : null);
		}

		var episodes = 0;
		DateTimeOffset? last = own;
		foreach (var id in titles.CopyIds(title.Id).DefaultIfEmpty(title.Id))
		{
			if (plays.TryGetValue(id, out var p) && p.Played > 0)
			{
				episodes = Math.Max(episodes, p.Played);
				if (p.LastPlayed > DateTimeOffset.MinValue && (last is null || p.LastPlayed > last))
				{
					last = p.LastPlayed;
				}
			}
		}

		return episodes > 0 || title.Played ? (true, last) : (false, null);
	}

	/// <summary>
	/// 1.9 (Phase 3): whether <paramref name="when"/> is on one of the last <see cref="LatelyDays"/> calendar days before <paramref name="now"/>;
	/// up to <see cref="Hold.ClockAhead"/> after it is now (the Phase 3 review: a server's clock running ahead).
	/// </summary>
	public static bool Lately(DateTimeOffset? when, DateTimeOffset now) => Hold.AsOf(when, now) is { } at && at <= now && Hold.DaysBetween(at, now) < LatelyDays; // 1.9: a clock ahead

	/// <summary>
	/// 1.9 (the rebuild's blocker 2): a show played lately anchors More like only with this many episodes played (any copy's),
	/// or finished. On the owner's library a More like of a show with one episode played was This week's second tile.
	/// </summary>
	public const int AnchorEpisodes = 3;

	/// <summary>
	/// 1.9 (the rebuild's blocker 2): whether a title played lately is enough of what the user watched to make More like of: a
	/// film finished; a show with <see cref="AnchorEpisodes"/> or more episodes played, or finished (the 90 % rule). A show
	/// tried once still brings its categories to the row (the lineup reads every played title), not a More like.
	/// </summary>
	public static bool AnchorsMoreLike(IndexEntry title, CanonicalTitles titles, IReadOnlyDictionary<Guid, SeriesPlays> plays)
	{
		if (title.Played || AnchorFinder.Finished(title, titles, plays))
		{
			return true;
		}

		if (title.Kind != IndexKind.Series)
		{
			return false;
		}

		var episodes = 0;
		foreach (var id in titles.CopyIds(title.Id).DefaultIfEmpty(title.Id))
		{
			if (plays.TryGetValue(id, out var p))
			{
				episodes = Math.Max(episodes, p.Played);
			}
		}

		return episodes >= AnchorEpisodes;
	}
	// end 1.9 (Phase 3)
}

/// <summary>Nixie 1.8 (P1-2): one build's anchors, in the order the slots take them, and the people and similarity behind them.</summary>
internal sealed class AnchorData(BuildContext context, IAnchorSource source, AnchoredSlots.KeptOfIndex kept)
{
	private IReadOnlyList<Anchor>? _anchors;
	private IReadOnlyList<Anchor>? _ordered;
	private QualityScore? _quality;

	public IAnchorSource Source => source;

	/// <summary>Every anchor, the latest played first (made once an index read, 1.8 stream l).</summary>
	public IReadOnlyList<Anchor> Anchors => _anchors ??= kept.Anchors(() => AnchorFinder.Of(context, source));

	/// <summary>The anchors in the order the personal slots take them (<see cref="Hold.Order"/>).</summary>
	public IReadOnlyList<Anchor> Ordered => _ordered ??= Hold.Order(Anchors, context.Now);

	// 1.9 (Phase 3, R1)
	private IReadOnlyList<Anchor>? _lately;

	/// <summary>
	/// 1.9 (Phase 3, R1): every title the user played (<see cref="AnchoredSlots.PlayOf"/>: a show with an episode played, a film
	/// played to its end), the latest played first, then those with no date by name; each an anchor (its own, when
	/// <see cref="AnchorFinder"/> makes it one: finished or a favourite; else a show under way, <see cref="AnchorWhy.None"/>).
	/// Made once an index read.
	/// </summary>
	public IReadOnlyList<Anchor> Played => kept.Played(() =>
	{
		var own = Anchors.ToDictionary(x => x.Title.Id);
		var played = new List<Anchor>();
		foreach (var title in context.Titles.Entries)
		{
			if (title is null || string.IsNullOrWhiteSpace(title.Name))
			{
				continue;
			}

			var plays = source.PlaysOf(title);
			var (isPlayed, last) = AnchoredSlots.PlayOf(title, context.Titles, context.Index.SeriesPlays, plays.Last);
			if (isPlayed)
			{
				played.Add(own.TryGetValue(title.Id, out var anchor) ? anchor with { When = last ?? anchor.When } : new Anchor(title, AnchorWhy.None, last, plays.Count));
			}
		}

		return [.. played.OrderBy(x => x.When is null).ThenByDescending(x => x.When ?? DateTimeOffset.MinValue)
			.ThenBy(x => x.Title.OrderName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Title.Id)];
	});

	/// <summary>
	/// 1.9 (Phase 3, R1; the critical review's option 5): the anchors More like is made of: each title played lately (on one of
	/// the last <see cref="AnchoredSlots.LatelyDays"/> days: <see cref="Played"/>; 1.9's rebuild blocker 2: a film finished, a
	/// show with three episodes played or finished, <see cref="AnchoredSlots.AnchorsMoreLike"/>), newest first, at most
	/// <see cref="AnchorFinder.Recent"/>; while there are fewer, the anchors' own turns after them (<see cref="Ordered"/>: 1.8's,
	/// so a user who played nothing lately keeps 1.8's More like). One of each title.
	/// </summary>
	public IReadOnlyList<Anchor> Lately => _lately ??= [.. Played.Where(x => AnchoredSlots.Lately(x.When, context.Now)
			&& AnchoredSlots.AnchorsMoreLike(x.Title, context.Titles, context.Index.SeriesPlays)) // 1.9 (the rebuild's blocker 2)
		.Concat(Ordered).DistinctBy(x => x.Title.Id).Take(AnchorFinder.Recent)];
	// end 1.9 (Phase 3)

	/// <summary>The people of the titles (made once an index read: their credits are by title id).</summary>
	public PeopleIndex People => kept.People(() => PeopleIndex.Of(context.Titles.Entries, source));

	/// <summary>The titles' similarity (made once an index read: it keeps titles by id).</summary>
	public TitleSimilarity Similarity => kept.Similarity(() => TitleSimilarity.Of(context.Titles.Entries, source));

	/// <summary>
	/// 1.8 (stream l): the titles like <paramref name="anchor"/> that "More like" may show (not watched, not of its own franchise),
	/// most alike first; made once an index read (they are of its titles alone). 1.8.1 (the owner's library, fix 2): not started
	/// either (<see cref="AnchorFinder.Started"/>: a show with any episode played, a film begun), not only not finished.
	/// </summary>
	public IReadOnlyList<(IndexEntry Title, double Similarity)> LikeOf(IndexEntry anchor)
	{
		var franchise = anchor.TmdbCollection;
		return kept.Like(anchor.Id, () => Similarity.Like(anchor, x => !Started(x) && (string.IsNullOrEmpty(franchise) || !string.Equals(x.TmdbCollection, franchise, StringComparison.Ordinal))));
	}

	/// <summary>1.8.1 (fix 2): whether the user started the title (<see cref="AnchorFinder.Started"/>).</summary>
	public bool Started(IndexEntry title) => AnchorFinder.Started(title, context.Titles, context.Index.SeriesPlays, source);

	public QualityScore Quality => _quality ??= context.Quality;

	/// <summary>Whether the user finished the title, by the anchors' own rule (a show at 90 % of its episodes is finished).</summary>
	public bool Finished(IndexEntry title) => AnchorFinder.Finished(title, context.Titles, context.Index.SeriesPlays);

	/// <summary>A title's score for ordering: its own, else its kind's mean, else 5.</summary>
	public double ScoreOf(IndexEntry title) => Quality.Of(title) ?? (Quality.MeanOf(title.Kind) is > 0 and var mean ? mean : 5);
}
