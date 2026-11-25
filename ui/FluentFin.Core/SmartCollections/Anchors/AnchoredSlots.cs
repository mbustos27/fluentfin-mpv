using System.Runtime.CompilerServices;
using FluentFin.Core.SmartCollections.Builders;

namespace FluentFin.Core.SmartCollections.Anchors;

/// <summary>
/// Nixie 1.8 (P1-2): the three personal slots, made from the user's anchors (<see cref="AnchorFinder"/>): "Next in
/// &lt;franchise&gt;", "More like &lt;title&gt;" (the report's "Because you watched") and "More from &lt;person&gt;". Each
/// builder makes its candidates in the order it prefers them (the anchors' <see cref="Hold"/> order: the same for three
/// days unless an anchor changes); the lineup (<c>CollectionLineup</c>, stream l) takes them in that order, within its caps
/// (0–1, 0–2, 0–1). No anchors, no candidates: a user who has finished nothing sees none of these.
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
		private readonly Dictionary<Guid, IReadOnlyList<(IndexEntry Title, double Similarity)>> _likes = [];

		public IReadOnlyList<Anchor> Anchors(Func<IReadOnlyList<Anchor>> make)
		{
			lock (_lock)
			{
				return _anchors ??= make();
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

	/// <summary>The people of the titles (made once an index read: their credits are by title id).</summary>
	public PeopleIndex People => kept.People(() => PeopleIndex.Of(context.Titles.Entries, source));

	/// <summary>The titles' similarity (made once an index read: it keeps titles by id).</summary>
	public TitleSimilarity Similarity => kept.Similarity(() => TitleSimilarity.Of(context.Titles.Entries, source));

	/// <summary>
	/// 1.8 (stream l): the titles like <paramref name="anchor"/> that "More like" may show (not watched, not of its own franchise),
	/// most alike first; made once an index read (they are of its titles alone).
	/// </summary>
	public IReadOnlyList<(IndexEntry Title, double Similarity)> LikeOf(IndexEntry anchor)
	{
		var franchise = anchor.TmdbCollection;
		return kept.Like(anchor.Id, () => Similarity.Like(anchor, x => !Finished(x) && (string.IsNullOrEmpty(franchise) || !string.Equals(x.TmdbCollection, franchise, StringComparison.Ordinal))));
	}

	public QualityScore Quality => _quality ??= context.Quality;

	/// <summary>Whether the user finished the title, by the anchors' own rule (a show at 90 % of its episodes is finished).</summary>
	public bool Finished(IndexEntry title) => AnchorFinder.Finished(title, context.Titles, context.Index.SeriesPlays);

	/// <summary>A title's score for ordering: its own, else its kind's mean, else 5.</summary>
	public double ScoreOf(IndexEntry title) => Quality.Of(title) ?? (Quality.MeanOf(title.Kind) is > 0 and var mean ? mean : 5);
}
