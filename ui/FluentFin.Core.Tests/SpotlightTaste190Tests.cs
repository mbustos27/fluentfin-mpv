using FluentFin.Core.SmartCollections;
using FluentFin.Core.SmartCollections.Builders;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (the owner's library, fix 8: "Give the Spotlight some taste"): the week's draw weighted by how near each
/// candidate is to what the user watched. On the owner's library the draw took Ealing (British films of the 1940s and 50s)
/// over ufotable, whose show they had watched 46 episodes of. With no history the draw is 1.8's.
/// </summary>
public sealed class SpotlightTaste190Tests
{
	// Wednesday 25 November 2026: ISO week 48
	private static readonly DateTimeOffset Now = new(2026, 11, 25, 20, 0, 0, TimeSpan.FromHours(-8));
	private static int s_next;

	private static IndexEntry Title(string name, double rating, int year, string[] genres, string country, string studio, IndexKind kind = IndexKind.Movie, bool played = false,
		string[]? tags = null) => new()
		{
			Id = Guid.Parse($"{Interlocked.Increment(ref s_next):D8}-0000-0000-0000-000000001908"),
			Name = name,
			Kind = kind,
			Year = year,
			Rating = rating,
			Critic = 80,
			TagCount = 10,
			Played = played,
			Unplayed = kind == IndexKind.Series ? 10 : null,
			Genres = genres,
			Tags = tags ?? [],
			Countries = [country],
			Studios = [studio],
			PrimaryTag = "p",
		};

	// a studio's eight films of one kind: its genres, decade and country, rated 8.6 down
	private static List<IndexEntry> Studio(string studio, string[] genres, int decade, string country, string[]? tags = null) =>
		[.. Enumerable.Range(0, 8).Select(i => Title($"{studio} film {i + 1}", 8.6 - (i * 0.05), decade + i, genres, country, studio, tags: tags))];

	// a curated studio whose films share nothing but the name: coherence drops these first
	private static List<IndexEntry> Scattered(string studio)
	{
		string[] genres = ["Western", "Musical", "War", "Romance", "Documentary", "Sport", "History", "Mystery"];
		string[] countries = ["Brazil", "Norway", "Egypt", "Peru", "Iceland", "India", "Chile", "Kenya"];
		return [.. Enumerable.Range(0, 8).Select(i => Title($"{studio} film {i + 1}", 8.6 - (i * 0.05), 1930 + (i * 11), [genres[i]], countries[i], studio))];
	}

	private static List<IndexEntry> Filler() =>
		[.. Enumerable.Range(0, 200).Select(i => Title($"Filler {i:D3}", 3.0 + (i % 40 * 0.1), 1980 + (i % 30), [i % 2 == 0 ? "Drama" : "Comedy"], "United States of America", $"Filler studio {i}"))];

	private sealed record Library(List<IndexEntry> Titles, Dictionary<Guid, SeriesPlays> Plays);

	// the owner's shape in small: anime shows binged (one of them ufotable's), two horror films played; six curated studios
	// that pass every gate, three that coherence drops
	private static Library Make(bool history)
	{
		var demonSlayer = Title("Demon Slayer", 8.7, 2019, ["Animation", "Action", "Fantasy"], "Japan", "ufotable", IndexKind.Series, tags: ["anime"]);
		var otherAnime = Enumerable.Range(0, 3).Select(i => Title($"Anime show {i}", 7.6, 2018 + i, ["Animation", "Action", "Fantasy"], "Japan", $"Anime studio {i}", IndexKind.Series, tags: ["anime"])).ToList();
		var horror = Enumerable.Range(0, 2).Select(i => Title($"Horror film {i}", 6.5, 2015 + i, ["Horror"], "United States of America", $"Horror studio {i}", played: history)).ToList();
		List<IndexEntry> titles =
		[
			demonSlayer,
			.. otherAnime,
			.. horror,
			.. Filler(),
			.. Studio("ufotable", ["Animation", "Action", "Fantasy"], 2010, "Japan", ["anime"]),
			.. Studio("Studio Ghibli", ["Animation", "Fantasy", "Family"], 1990, "Japan", ["anime"]),
			.. Studio("Ealing Studios", ["Comedy", "Crime"], 1945, "United Kingdom"),
			.. Studio("Aardman", ["Animation", "Comedy", "Family"], 2000, "United Kingdom"),
			.. Studio("Hammer Films", ["Horror"], 1958, "United Kingdom"),
			.. Studio("Merchant Ivory Productions", ["Drama", "Romance"], 1983, "United Kingdom"),
			.. Scattered("Troma Entertainment"),
			.. Scattered("Golden Harvest"),
			.. Scattered("Shaw Brothers"),
		];
		var plays = new Dictionary<Guid, SeriesPlays>();
		if (history)
		{
			plays[demonSlayer.Id] = new SeriesPlays(46, Now.AddDays(-3));
			foreach (var (show, i) in otherAnime.Select((x, i) => (x, i)))
			{
				plays[show.Id] = new SeriesPlays(20 + (i * 10), Now.AddDays(-10 - i));
			}
		}

		return new Library(titles, plays);
	}

	private static BuildContext Context(Library library) =>
		new(new IndexSnapshot([], library.Titles, library.Plays, HasPeople: false, Now) { Key = "taste" }, Now, new HashSet<string>());

	private static string Studio(SpotlightCandidate candidate) => candidate.Id["spotlight:studio:".Length..];

	[Fact]
	public void EachCandidatesTaste_IsHowNearItIsToWhatTheUserWatched_UfotableFirst_EalingLast()
	{
		var candidates = SpotlightBuilder.Candidates(Context(Make(history: true)));
		var taste = candidates.ToDictionary(Studio, x => x.Taste);

		Assert.Equal(["aardman", "ealingstudios", "hammerfilms", "merchantivoryproductions", "studioghibli", "ufotable"], taste.Keys.Order(StringComparer.Ordinal));
		Assert.Equal("ufotable", taste.MaxBy(x => x.Value).Key); // the studio of the show they watched 46 episodes of
		Assert.True(taste["studioghibli"] > 1); // anime, Japan
		Assert.True(taste["ufotable"] > taste["studioghibli"]);
		Assert.True(taste["ealingstudios"] < 1); // nothing near
		Assert.All(taste.Values, x => Assert.InRange(x, SpotlightBuilder.TasteLeast, SpotlightBuilder.TasteMost));
	}

	// the weeks each studio is drawn in, over ten years: as the app does (each week's pick kept on this PC, never last week's
	// again), and with nothing kept (a first week, the TV)
	private static (Dictionary<string, int> Kept, Dictionary<string, int> Plain) Drawn(IReadOnlyList<SpotlightCandidate> candidates)
	{
		var plain = new Dictionary<string, int>();
		var keptDrawn = new Dictionary<string, int>();
		var kept = new Dictionary<string, string>();
		string? last = null;
		for (var w = 0; w < Weeks; w++)
		{
			var week = SpotlightWeek.Of(Now.AddDays(7 * w));
			var pick = Studio(SpotlightBuilder.Pick(candidates, week)!);
			plain[pick] = plain.GetValueOrDefault(pick) + 1;

			var keptPick = SpotlightBuilder.Pick(candidates, week, kept)!;
			kept[week.Seed] = keptPick.Id;
			Assert.NotEqual(last, keptPick.Id); // never the same two weeks running
			last = keptPick.Id;
			keptDrawn[Studio(keptPick)] = keptDrawn.GetValueOrDefault(Studio(keptPick)) + 1;
		}

		return (keptDrawn, plain);
	}

	private const int Weeks = 520;

	[Fact]
	public void OverTenYears_TheDrawTakesTheNearerOftener_NeverTheSameTwoWeeksRunning()
	{
		var withHistory = SpotlightBuilder.Candidates(Context(Make(history: true)));
		var (kept, plain) = Drawn(withHistory);
		var (keptBefore, plainBefore) = Drawn(SpotlightBuilder.Candidates(Context(Make(history: false))));
		string Say() => $"kept {string.Join(", ", kept)}; plain {string.Join(", ", plain)}; with no history: kept {string.Join(", ", keptBefore)}; plain {string.Join(", ", plainBefore)}; "
			+ string.Join(", ", withHistory.Select(x => $"{Studio(x)} {x.Taste:0.00}"));

		// measured: kept ufotable 165 of 520 weeks, Ealing 55 (with no history 79 and 94); nothing kept 137 and 62
		Assert.True(kept["ufotable"] >= 2 * kept["ealingstudios"], Say());
		Assert.True(kept["ufotable"] >= 1.5 * keptBefore["ufotable"], Say());
		Assert.True(kept["ealingstudios"] < keptBefore["ealingstudios"], Say());
		Assert.True(plain["ufotable"] >= 1.5 * plain["ealingstudios"], Say());
		Assert.All(keptBefore.Values, x => Assert.InRange(x, 60, 110)); // with no history, each about a sixth
	}

	[Fact]
	public void WithNoHistory_EveryTasteIsOne_AndTheDrawIs18s()
	{
		var candidates = SpotlightBuilder.Candidates(Context(Make(history: false)));

		Assert.All(candidates, x => Assert.Equal(1, x.Taste));
		for (var w = 0; w < 104; w++)
		{
			var week = SpotlightWeek.Of(Now.AddDays(7 * w));
			Assert.Equal(Draw18(candidates, week, SpotlightBuilder.Rounds).Id, SpotlightBuilder.Pick(candidates, week)!.Id);
		}
	}

	// 1.8.1's draw, as it was: the highest weight by the week's seed, leaving out what last week's rounds could have drawn
	private static SpotlightCandidate Draw18(IReadOnlyList<SpotlightCandidate> candidates, SpotlightWeek week, int round)
	{
		var left = candidates;
		if (round > 0)
		{
			var before = week.Before(1);
			var out_ = Enumerable.Range(0, round).Select(r => Draw18(candidates, before, r).Id).ToHashSet(StringComparer.Ordinal);
			var fresh = candidates.Where(x => !out_.Contains(x.Id)).ToList();
			left = fresh.Count > 0 ? fresh : candidates;
		}

		return left.OrderByDescending(x => SpotlightBuilder.Weight(week.Seed, x.Id)).ThenBy(x => x.Id, StringComparer.Ordinal).First();
	}
}
