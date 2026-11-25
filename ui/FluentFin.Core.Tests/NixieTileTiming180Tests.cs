using System.Diagnostics;
using FluentFin.Core.Outside;
using FluentFin.Core.SmartCollections;
using Xunit.Abstractions;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.8 (P1-4, stream t): the reason lines and the award badges at real shape: every builder over 26,000 titles (the
/// throwaway 12.4's 25,736 films and some shows), with Wikidata's nine award lists of 600 titles each, as Home and the
/// Collections page build them (<see cref="SmartCollectionCatalog.BuildAll(IndexSnapshot, DateTimeOffset, IReadOnlySet{string}?, IReadOnlySet{SmartCollectionKind}?, OutsideListsSnapshot?)"/>).
/// The time is written to the test's output (Home's first Nixie row waits for it); the bound only catches a runaway.
/// </summary>
public sealed class NixieTileTiming180Tests(ITestOutputHelper output)
{
	private static readonly DateTimeOffset November = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

	/// <summary>A build's bound, as a ratio to the reference work timed beside it: 4x the ratio measured (see the test).</summary>
	private const double MostRatio = 4 * MeasuredRatio;

	/// <summary>The larger of the two builds' ratios to the reference work, measured on the owner's PC (stream t, 2026-11-24).</summary>
	private const double MeasuredRatio = 1.8; // 1.4-1.8 in six runs, other streams' runs and builds beside them

	private static IndexSnapshot Big(int titles)
	{
		string[] genres = ["Comedy", "Drama", "Horror", "Action", "Romance", "Thriller"];
		string[] tags = ["heist", "time travel", "zombie", "spy", "revenge", "robot", "dystopia", "biography"];
		var entries = Enumerable.Range(0, titles).Select(i => new IndexEntry
		{
			Id = Guid.Parse($"{i:D8}-0180-0000-0000-000000000001"),
			Name = $"Title {i:D5}",
			Kind = i % 12 == 0 ? IndexKind.Series : IndexKind.Movie,
			Year = 1950 + (i % 76),
			Genres = [genres[i % 6], genres[(i / 6) % 6]],
			Tags = i % 7 == 0 ? [tags[i % 8]] : [],
			TagCount = i % 15,
			Countries = [i % 3 == 0 ? "France" : "United States of America"],
			Rating = 5 + (i % 50) / 10.0,
			Critic = i % 4 == 0 ? 70 + i % 30 : null,
			RunTimeTicks = TimeSpan.FromMinutes(70 + (i % 70)).Ticks,
			TmdbCollection = i % 40 < 3 ? $"c{i / 40}" : null,
			Tmdb = $"{i}",
			Played = i % 9 == 0,
			PrimaryTag = "p",
		}).ToList();
		return new IndexSnapshot([], entries, new Dictionary<Guid, SeriesPlays>(), HasPeople: false, November);
	}

	private static OutsideListsSnapshot Awards(int titles) => OutsideListsSnapshot.Empty with
	{
		Lists = [.. WikidataLists.Awards.Select((query, n) => new OutsideList(query.Id, OutsideSourceId.Wikidata, OutsideListKind.Award, query.Title, query.Why,
			[.. Enumerable.Range(0, 600).Select(i => new OutsideListItem(new OutsideRef(Tmdb: $"{(i * 37 + n * 101) % titles}", Kind: IndexKind.Movie), 1960 + i % 60))]))],
	};

	[Fact]
	public void Every_builder_with_reasons_and_badges_over_26000_titles_stays_quick()
	{
		var index = Big(26_000);
		var outside = Awards(26_000);
		_ = SmartCollectionCatalog.BuildAll(Big(500), November, outside: Awards(500)); // the JIT warmed

		// five rounds, each timing a build without the lists, one with them, and the machine's speed now: a fixed piece of the
		// same kind of work (grouping and sorting the titles). Load from anything else slows all three alike, so the bounds are
		// on the builds' ratios to it (the review: an absolute 20 s let a 10x slowdown pass; an absolute 400 ms flakes under load)
		IReadOnlyList<SmartCollection> plain = [], made = [];
		var offs = new List<double>();
		var ons = new List<double>();
		var speeds = new List<double>();
		for (var i = 0; i < 5; i++)
		{
			speeds.Add(Timed(() => Calibrate(index)));
			offs.Add(Timed(() => plain = SmartCollectionCatalog.BuildAll(index with { }, November, outside: OutsideListsSnapshot.Empty)));
			ons.Add(Timed(() => made = SmartCollectionCatalog.BuildAll(index with { }, November, outside: outside)));
		}

		static double Median(IEnumerable<double> values) => values.Order().ElementAt(2);
		var (off, on, speed) = (Median(offs), Median(ons), Median(speeds));
		var (offRatio, onRatio) = (Median(offs.Zip(speeds, (a, b) => a / b)), Median(ons.Zip(speeds, (a, b) => a / b)));
		output.WriteLine($"BuildAll on {index.Entries.Count:N0} titles, the median of 5: {off:F0} ms without Wikidata's lists ({plain.Count} collections), "
			+ $"{on:F0} ms with its 9 award lists ({made.Count} collections, {made.Count(x => x.Badges.Count > 0)} with badges); the reference work "
			+ $"{speed:F0} ms; ratios {offRatio:F1} and {onRatio:F1}");
		Assert.All(made, x => Assert.NotNull(x.Reason));
		Assert.Contains(made, x => x.Badges.Count > 0);
		Assert.True(offRatio <= MostRatio, $"without the lists: {offRatio:F1}x the reference work, over {MostRatio}x (4x the measured)");
		Assert.True(onRatio <= MostRatio, $"with the lists: {onRatio:F1}x the reference work, over {MostRatio}x (4x the measured)");
	}

	private static double Timed(Action act)
	{
		var watch = Stopwatch.StartNew();
		act();
		return Math.Max(watch.Elapsed.TotalMilliseconds, 0.01);
	}

	// the reference work: the titles grouped by first genre and decade, each group sorted by name, three times over
	private static int Calibrate(IndexSnapshot index)
	{
		var total = 0;
		for (var i = 0; i < 3; i++)
		{
			total += index.Entries.GroupBy(x => (x.Genres.FirstOrDefault(), x.Year / 10))
				.ToDictionary(g => g.Key, g => g.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).Select(x => x.Id).ToHashSet())
				.Sum(x => x.Value.Count);
		}

		return total;
	}
}
