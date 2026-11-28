namespace FluentFin.Core.SmartCollections;

/// <summary>
/// Nixie 1.9 (Collections 2; the owner's library, fix 8): what the user watched, as the evidence the categories and the
/// Spotlight are weighted by, kept on this PC like everything else of the index (never sent anywhere, counts only in the log):
/// <list type="bullet">
/// <item>a film played, or a favourite: 1; a film started and not finished: <see cref="Started"/>;</item>
/// <item>a show with episodes played (any copy's): 1 and 1 more each <see cref="EpisodesPerStep"/> episodes, up to
/// <see cref="ShowMost"/> (the plan: "shows with played episodes count most"); a show played to its end with no episode count:
/// <see cref="ShowMost"/>; a favourite show: at least 1.</item>
/// </list>
/// A pool of titles (a category, a Spotlight's subject) is then measured by its <see cref="Lift(double, double, double)"/>:
/// how much more of the evidence falls in it than its share of the library would give, smoothed toward the library's own mix
/// (<see cref="Prior"/>), so a user with no history has no lift either way.
/// </summary>
public sealed class Taste
{
	/// <summary>
	/// The Spotlight's shrink toward the candidates' mean (<c>evidence / (evidence + this)</c>): evidence of this much weight moves
	/// it half way.
	/// </summary>
	public const double Smoothing = 10;

	/// <summary>
	/// The weight both sides of the lift start from (β, <see cref="Lift(double, double, double)"/>): a few titles' worth, so one
	/// title watched moves a category a little and a binge a lot.
	/// </summary>
	public const double Prior = 2;

	/// <summary>
	/// A film started and not finished. The review: a quarter, so the shows with episodes played count most (the plan) and a
	/// film sampled for a minute doesn't lift a category.
	/// </summary>
	public const double Started = 0.25;

	/// <summary>A show's weight at most.</summary>
	public const double ShowMost = 3;

	/// <summary>A show's weight grows by 1 for each this many episodes played.</summary>
	public const double EpisodesPerStep = 10;

	private readonly Dictionary<Guid, double> _weights;
	private readonly Dictionary<IndexKind, double> _totals;

	private Taste(Dictionary<Guid, double> weights)
	{
		_weights = weights;
		_totals = [];
		foreach (var (_, weight) in weights)
		{
			Total += weight;
		}
	}

	/// <summary>No evidence: every lift is 1.</summary>
	public static Taste None { get; } = new([]);

	/// <summary>The evidence's titles (canonical ids) and their weights.</summary>
	public IReadOnlyDictionary<Guid, double> Weights => _weights;

	/// <summary>The evidence's whole weight.</summary>
	public double Total { get; }

	/// <summary>Whether there is any evidence.</summary>
	public bool Any => Total > 0;

	/// <summary>The evidence's weight of one kind (films or shows).</summary>
	public double TotalOf(IndexKind kind) => _totals.TryGetValue(kind, out var total) ? total : 0;

	/// <summary>A title's weight (its canonical id); 0 for one not watched.</summary>
	public double WeightOf(Guid title) => _weights.TryGetValue(title, out var weight) ? weight : 0;

	/// <summary>The evidence of <paramref name="titles"/> (copies are one title) and the played episodes of each show.</summary>
	public static Taste Of(CanonicalTitles titles, IReadOnlyDictionary<Guid, SeriesPlays>? plays)
	{
		var weights = new Dictionary<Guid, double>();
		foreach (var title in titles.Entries)
		{
			if (title is null)
			{
				continue;
			}

			var weight = title.Kind == IndexKind.Series ? ShowWeight(title, titles, plays) : FilmWeight(title);
			if (weight > 0)
			{
				weights[title.Id] = weight;
			}
		}

		var taste = new Taste(weights);
		foreach (var title in titles.Entries)
		{
			if (title is not null && weights.TryGetValue(title.Id, out var weight))
			{
				taste._totals[title.Kind] = taste._totals.GetValueOrDefault(title.Kind) + weight;
			}
		}

		return taste;
	}

	private static double FilmWeight(IndexEntry film) =>
		film.Played || film.Favorite ? 1
		: film.PlayCount > 0 || film.LastPlayed is not null ? Started
		: 0;

	private static double ShowWeight(IndexEntry show, CanonicalTitles titles, IReadOnlyDictionary<Guid, SeriesPlays>? plays)
	{
		var episodes = 0;
		foreach (var id in titles.CopyIds(show.Id).DefaultIfEmpty(show.Id))
		{
			if (plays is not null && plays.TryGetValue(id, out var p) && p.Played > episodes)
			{
				episodes = p.Played;
			}
		}

		var weight = episodes > 0 ? Math.Min(ShowMost, 1 + (episodes / EpisodesPerStep)) : show.Played ? ShowMost : 0;
		return show.Favorite ? Math.Max(1, weight) : weight;
	}

	/// <summary>
	/// The lift of a pool: the evidence that falls in it against what its share of the library would give, both with
	/// <see cref="Prior"/> titles' weight added (β): <c>(hits + β) / (evidence·p + β)</c>, where p is the pool's share of the titles
	/// it is drawn from. 1 with no evidence; above 1 when the user watched more of it than the library's mix would give; never
	/// negative. The review of wip/190-lineup: 1.9's first form, <c>((hits + α·p) / (evidence + α)) / p</c>, smoothed almost
	/// nothing at a category's size (p of a thousandth): one film started in a category of 30 lifted it 7 times, to the most.
	/// </summary>
	/// <param name="hits">The evidence's weight that falls in the pool.</param>
	/// <param name="evidence">The evidence's whole weight (of the pool's kind).</param>
	/// <param name="share">The pool's share of the library (of its kind), 0 to 1; 0 or less is no lift (1).</param>
	public static double Lift(double hits, double evidence, double share)
	{
		if (share <= 0 || double.IsNaN(share))
		{
			return 1;
		}

		share = Math.Min(share, 1);
		return (Math.Max(0, hits) + Prior) / ((Math.Max(0, evidence) * share) + Prior);
	}

	/// <summary>
	/// The lift of the titles <paramref name="pool"/> (canonical ids) among <paramref name="titlesOfKind"/> titles of
	/// <paramref name="kind"/> (null: of every kind, against <paramref name="titlesOfKind"/> titles in all).
	/// </summary>
	public double LiftOf(IEnumerable<Guid> pool, IndexKind? kind, int titlesOfKind)
	{
		var size = 0;
		var hits = 0.0;
		foreach (var id in pool)
		{
			size++;
			hits += WeightOf(id);
		}

		return titlesOfKind <= 0 || size == 0 ? 1 : Lift(hits, kind is { } k ? TotalOf(k) : Total, size / (double)titlesOfKind);
	}
}
