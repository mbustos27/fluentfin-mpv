using FluentFin.Core.SmartCollections;
using Nixie.Collections;
using static Census.Tests.Fx;

namespace Census.Tests;

/// <summary>Pools against the floor and each kind's ceiling, copies once, overlap within a family, the parent, the watched shows.</summary>
public class CensusTests
{
	[Theory]
	[InlineData(25_877, 259)] // the owner's films
	[InlineData(4_975, 50)] // the owner's shows: 1 % of shows, not of films
	[InlineData(1_000, 30)] // a small library keeps the least
	[InlineData(0, 30)]
	public void TheCeiling_IsOnePercentOfTheKind_AtLeastThirty(int ofKind, int ceiling) => Assert.Equal(ceiling, Nixie.Collections.Census.CeilingFor(ofKind));

	[Fact]
	public void AShowsCategory_IsHeldToTheShowsCeiling_NotTheFilms()
	{
		// 6,000 films (ceiling 60) and 100 shows (ceiling 30): 40 unwatched comedies is a filter among shows, a theme among films
		var films = Enumerable.Range(0, 6_000).Select(i => Film(genres: i < 40 ? ["Comedy"] : ["Drama"])).ToList();
		var shows = Enumerable.Range(0, 100).Select(i => Show(genres: i < 40 ? ["Comedy"] : ["Drama"])).ToList();
		var census = CensusOf([.. films, .. shows],
		[
			Cat(new Rule { GenresAll = ["Comedy"] }, id: "film-comedies"),
			Cat(new Rule { GenresAll = ["Comedy"] }, id: "show-comedies", kind: "shows"),
		]);
		Assert.Equal((60, 30), (census.CeilingOf(census["film-comedies"].Category), census.CeilingOf(census["show-comedies"].Category)));
		Assert.Equal(Verdict.Fits, census.VerdictOf(census["film-comedies"]));
		Assert.Equal(Verdict.OverCeiling, census.VerdictOf(census["show-comedies"]));
	}

	[Fact]
	public void Copies_CountOnce_AsTheBuildersCountThem()
	{
		var titles = Enumerable.Range(0, 8).Select(i => Film($"Film {i}", genres: ["War"])).ToList();
		titles.Add(titles[0] with { Id = Guid.NewGuid() }); // a second file of the same film (same TMDb id)
		var census = CensusOf(titles, [Cat(new Rule { GenresAll = ["War"] })]);
		Assert.Equal(8, census["a-category"].Least);
	}

	[Fact]
	public void TheVerdicts_FollowTheFloorTheCeilingAndWhatTheIndexCanSay()
	{
		var titles = new List<IndexEntry>();
		titles.AddRange(Enumerable.Range(0, 10).Select(i => Film(genres: ["Western"])));
		titles.AddRange(Enumerable.Range(0, 3).Select(i => Film(genres: ["Western"], played: true)));
		titles.AddRange(Enumerable.Range(0, 7).Select(i => Film(genres: ["War"])));
		titles.AddRange(Enumerable.Range(0, 31).Select(i => Film(genres: ["Music"])));
		titles.AddRange(Enumerable.Range(0, 12).Select(i => Film(genres: ["Drama"], tags: [], tagCount: 6)));
		titles.AddRange(Enumerable.Range(0, 5).Select(i => Film(genres: ["History"], tags: [], tagCount: 6)));
		var census = CensusOf(titles,
		[
			Cat(new Rule { GenresAll = ["Western"] }, id: "westerns"),
			Cat(new Rule { GenresAll = ["War"] }, id: "war"),
			Cat(new Rule { GenresAll = ["Music"] }, id: "music"),
			Cat(new Rule { GenresAll = ["Drama"], KeywordsAny = ["small town"] }, id: "small-town"),
			Cat(new Rule { GenresAll = ["History"], KeywordsAny = ["ancient rome"] }, id: "rome"),
		]);

		Assert.Equal((13, 10), (census["westerns"].Least, census["westerns"].LeastUnwatched)); // the watched aren't the floor's
		Assert.Equal(Verdict.Fits, census.VerdictOf(census["westerns"]));
		Assert.Equal(Verdict.UnderFloor, census.VerdictOf(census["war"])); // 7 unwatched
		Assert.Equal(Verdict.OverCeiling, census.VerdictOf(census["music"])); // 31 > max(30, 1 % of 68)
		Assert.False(census["small-town"].Known);
		Assert.Equal((0, 12), (census["small-town"].Least, census["small-town"].Most));
		Assert.Equal(Verdict.Unknown, census.VerdictOf(census["small-town"]));
		Assert.Equal(Verdict.UnderFloor, census.VerdictOf(census["rome"])); // unknown, but 5 at most: surely under the floor
	}

	[Fact]
	public void TheClosest_IsOfTheSameFamilyAndKindOnly()
	{
		var crime70s = Enumerable.Range(0, 10).Select(i => Film(year: 1975, genres: ["Crime", "Drama"])).ToList();
		var thrillers = Enumerable.Range(0, 10).Select(i => Film(year: 1975, genres: ["Crime", "Thriller"])).ToList();
		var census = CensusOf([.. crime70s, .. thrillers],
		[
			Cat(new Rule { GenresAll = ["Crime"], Years = [1970, 1979] }, id: "crime-1970s", family: "crime"),
			Cat(new Rule { GenresAll = ["Crime", "Drama"] }, id: "crime-dramas", family: "crime"),
			Cat(new Rule { GenresAll = ["Crime", "Drama"] }, id: "same-elsewhere", family: "other"),
		]);

		var closest = census.Closest(census["crime-1970s"]);
		Assert.Equal("crime-dramas", closest?.Id);
		Assert.Equal(0.5, closest!.Value.Jaccard, 3); // 10 shared of 20
		Assert.Equal("crime-1970s", census.Closest(census["crime-dramas"])?.Id); // not "same-elsewhere" (1.0, another family)
		Assert.Null(census.Closest(census["same-elsewhere"])); // alone in its family
	}

	[Theory]
	[InlineData("keyword:neonoir", "neo-noir")]
	[InlineData("keyword:artificialintelligenceai", "artificial intelligence (a.i.)")]
	[InlineData("keyword:basedonnovelorbook", "based on novel or book")]
	[InlineData("keyword:neo-noir", null)] // the brief's example form isn't the keyword builder's id
	[InlineData("keyword:smalltown", null)] // not allowlisted: no keyword collection
	[InlineData("neonoir", null)]
	public void AParent_IsTheKeywordBuildersId(string parent, string? keyword) => Assert.Equal(keyword, Nixie.Collections.Census.ParentKeyword(parent));

	[Fact]
	public void TheParentOverlap_IsAgainstTheKeywordsTitlesOfTheSameKind()
	{
		var noir70s = Enumerable.Range(0, 4).Select(i => Film(year: 1974, tags: ["neo-noir"])).ToList();
		var noir90s = Enumerable.Range(0, 4).Select(i => Film(year: 1994, tags: ["neo-noir"])).ToList();
		var census = CensusOf([.. noir70s, .. noir90s], [Cat(new Rule { KeywordsAll = ["neo-noir"], Years = [1970, 1979] }, parent: "keyword:neonoir")]);
		Assert.Equal(0.5, census.ParentOverlap(census["a-category"])!.Value, 3);
	}

	[Fact]
	public void TheWatchedShows_AreThoseWithPlayedEpisodes_AnyCopy()
	{
		var watched = Show("Watched", genres: ["Comedy"], tmdb: "42");
		var copy = watched with { Id = Guid.NewGuid() };
		var other = Show("Other", genres: ["Comedy"]);
		var plays = new Dictionary<Guid, SeriesPlays> { [copy.Id] = new(12, DateTimeOffset.UnixEpoch) };
		var census = CensusOf([watched, copy, other], [Cat(new Rule { GenresAll = ["Comedy"] }, kind: "shows")], plays);
		Assert.Single(census.WatchedShows);
		Assert.Equal(1, census["a-category"].WatchedShows);
		Assert.Equal(2, census["a-category"].Least);
	}

	[Fact]
	public void Examples_AreUnwatchedFirst_ThenBestScored_AndNoMoreThanAsked()
	{
		var seen = Film("Seen", genres: ["War"], rating: 9.5, played: true);
		var good = Film("Good", genres: ["War"], rating: 8.0);
		var fair = Film("Fair", genres: ["War"], rating: 6.0);
		var weak = Film("Weak", genres: ["War"], rating: 4.0);
		var census = CensusOf([seen, weak, fair, good], [Cat(new Rule { GenresAll = ["War"] })]);
		Assert.Equal(["Good", "Fair", "Weak"], census.Examples(census["a-category"], 3).Select(x => x.Name));
	}
}

/// <summary>1.9: what a saved index kept depends on its revision; and the authoring patch.</summary>
public class RevisionAndPatchTests
{
	[Fact]
	public void ARevision4Index_KeptEveryMatchableKeyword_ARevision3OneOnlyTheAllowlist()
	{
		Assert.True(IndexFile.Of([], revision: 4).Kept("small town"));
		Assert.False(IndexFile.Of([], revision: 3).Kept("small town"));
		Assert.True(IndexFile.Of([], revision: 3).Kept("heist"));
		Assert.False(IndexFile.Of([], revision: 3).Kept("thanksgiving")); // seasonal only since revision 4
		Assert.True(IndexFile.Of([], revision: 4).Kept("thanksgiving"));

		var titles = Enumerable.Range(0, 9).Select(i => Fx.Film(genres: ["Drama"], tags: ["small town"], tagCount: 3)).ToList();
		var rule = new Rule { GenresAll = ["Drama"], KeywordsAny = ["small town"] };
		var four = IndexFile.Of(titles, revision: 4);
		var census = Nixie.Collections.Census.Of(four.Titles, null, new Catalogue { Version = 1, Categories = [Fx.Cat(rule)] }, four.Kept);
		Assert.True(census["a-category"].Known);
		Assert.Equal(9, census["a-category"].Least);
	}

	[Fact]
	public void APatch_ReplacesRulesAndReasons_NeverNames()
	{
		var catalogue = new Catalogue { Version = 1, Categories = [Fx.Cat(new Rule { GenresAll = ["War"] }, id: "one", name: "War Movies on File")] };
		var patched = catalogue.With(new CataloguePatch { Patches = [new() { Id = "one", Rule = new Rule { GenresAll = ["Western"] }, Reason = "Western" }] });
		Assert.Equal(["Western"], patched.Categories[0].Rule.GenresAll);
		Assert.Equal("Western", patched.Categories[0].Reason);
		Assert.Equal("War Movies on File", patched.Categories[0].Name);
		Assert.Throws<InvalidOperationException>(() => catalogue.With(new CataloguePatch { Patches = [new() { Id = "two", Reason = "x" }] }));
	}
}
