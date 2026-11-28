using Nixie.Collections;
using static Census.Tests.Fx;

namespace Census.Tests;

/// <summary>What the census's matcher says of one rule on one title: yes, no, or maybe (a dropped keyword or a summary).</summary>
public class RulesTests
{
	[Fact]
	public void Genres_All_Any_None()
	{
		var film = Film(genres: ["Crime", "Drama"]);
		Assert.Equal(Truth.Yes, Match(new Rule { GenresAll = ["Crime", "Drama"] }, film));
		Assert.Equal(Truth.No, Match(new Rule { GenresAll = ["Crime", "Thriller"] }, film));
		Assert.Equal(Truth.Yes, Match(new Rule { GenresAny = ["Thriller", "drama"] }, film)); // any case
		Assert.Equal(Truth.No, Match(new Rule { GenresAny = ["Thriller", "Horror"] }, film));
		Assert.Equal(Truth.No, Match(new Rule { GenresAll = ["Crime"], GenresNone = ["Drama"] }, film));
	}

	[Fact]
	public void Years_AreBothIncluded_AndATitleWithNoYearIsNoMatchForARuleThatBoundsThem()
	{
		var rule = new Rule { Years = [1970, 1979] };
		Assert.Equal(Truth.Yes, Match(rule, Film(year: 1970)));
		Assert.Equal(Truth.Yes, Match(rule, Film(year: 1979)));
		Assert.Equal(Truth.No, Match(rule, Film(year: 1980)));
		Assert.Equal(Truth.No, Match(rule, Film(year: null)));
		Assert.Equal(Truth.Yes, Match(new Rule { Years = [2000, null], GenresAny = ["Drama"] }, Film(year: 2026, genres: ["Drama"])));
		Assert.Equal(Truth.Yes, Match(new Rule { GenresAny = ["Drama"] }, Film(year: null, genres: ["Drama"]))); // unbounded: the year doesn't matter
	}

	[Fact]
	public void Lengths_AreWholeMinutesHalfUp_AsTheAppsMatcher()
	{
		var rule = new Rule { LengthMinutes = [null, 35] };
		Assert.Equal(Truth.Yes, Match(rule, Film(minutes: 35.4)));
		Assert.Equal(Truth.No, Match(rule, Film(minutes: 35.5))); // 36
		Assert.Equal(Truth.No, Match(rule, Film(minutes: null)));
		Assert.Equal(36, Rules.Minutes(Film(minutes: 35.5)));
		Assert.Equal(37, Rules.Minutes(Film(minutes: 36.5))); // half up; .NET's default (to even) would say 36
	}

	[Fact]
	public void Studios_MatchIgnoringCaseSpacesAndPunctuation()
	{
		var rule = new Rule { StudiosAny = ["Adult Swim", "ABC (US)"] };
		Assert.Equal(Truth.Yes, Match(rule, Film(studios: ["adult swim"])));
		Assert.Equal(Truth.Yes, Match(rule, Film(studios: ["Cartoon Network", "ABC US"])));
		Assert.Equal(Truth.No, Match(rule, Film(studios: ["ABC (JA)"])));
	}

	[Fact]
	public void Countries_AnyOfTheTitles()
	{
		var rule = new Rule { CountriesAny = ["Hong Kong"] };
		Assert.Equal(Truth.Yes, Match(rule, Film(countries: ["China", "Hong Kong"])));
		Assert.Equal(Truth.No, Match(rule, Film(countries: ["China"])));
	}

	[Fact]
	public void Ratings_AreLeasts_AndZeroIsNoRating()
	{
		Assert.Equal(Truth.Yes, Match(new Rule { CommunityMin = 7 }, Film(rating: 7.0)));
		Assert.Equal(Truth.No, Match(new Rule { CommunityMin = 7 }, Film(rating: 6.9)));
		Assert.Equal(Truth.No, Match(new Rule { CommunityMin = 0 }, Film(rating: 0)));
		Assert.Equal(Truth.Yes, Match(new Rule { CriticMin = 80 }, Film(critic: 80)));
		Assert.Equal(Truth.No, Match(new Rule { CriticMin = 80 }, Film(critic: null)));
	}

	[Fact]
	public void AKeptKeyword_IsAnswered()
	{
		var rule = new Rule { KeywordsAll = ["neo-noir"] };
		Assert.Equal(Truth.Yes, Match(rule, Film(tags: ["neo-noir"], tagCount: 12)));
		Assert.Equal(Truth.No, Match(rule, Film(tags: ["heist"], tagCount: 12)));
	}

	[Fact]
	public void AKeywordTheIndexDrops_IsMaybe_OnlyForATitleThatHadOtherTags()
	{
		var any = new Rule { KeywordsAny = ["small town"] };
		Assert.Equal(Truth.Maybe, Match(any, Film(tags: [], tagCount: 7)));
		Assert.Equal(Truth.No, Match(any, Film(tags: [], tagCount: 0))); // no tags at all: surely not
		Assert.Equal(Truth.No, Match(any, Film(tags: ["heist"], tagCount: 1))); // its one tag was kept: nothing else
		Assert.Equal(Truth.Yes, Match(new Rule { KeywordsAny = ["small town", "heist"] }, Film(tags: ["heist"], tagCount: 9))); // a kept one is enough

		var all = new Rule { KeywordsAll = ["heist", "small town"] };
		Assert.Equal(Truth.No, Match(all, Film(tags: [], tagCount: 9))); // the kept one is missing
		Assert.Equal(Truth.Maybe, Match(all, Film(tags: ["heist"], tagCount: 9)));

		var none = new Rule { GenresAny = ["Drama"], KeywordsNone = ["superhero", "small town"] };
		Assert.Equal(Truth.No, Match(none, Film(genres: ["Drama"], tags: ["superhero"], tagCount: 3)));
		Assert.Equal(Truth.Maybe, Match(none, Film(genres: ["Drama"], tags: [], tagCount: 3)));
		Assert.Equal(Truth.Yes, Match(none, Film(genres: ["Drama"], tags: [], tagCount: 0)));
	}

	[Fact]
	public void SummaryWords_AreMaybe_ForAShowThatPassesTheRest()
	{
		var rule = new Rule { GenresAll = ["Comedy"], TextAny = ["office"] };
		Assert.Equal(Truth.Maybe, Match(rule, Show(genres: ["Comedy"]), kind: "shows"));
		Assert.Equal(Truth.No, Match(rule, Show(genres: ["Drama"]), kind: "shows"));
	}

	[Fact]
	public void FilmsAndShows_AreKeptApart()
	{
		var rule = new Rule { GenresAll = ["Comedy"] };
		Assert.Equal(Truth.No, Match(rule, Show(genres: ["Comedy"]), kind: "films"));
		Assert.Equal(Truth.No, Match(rule, Film(genres: ["Comedy"]), kind: "shows"));
		Assert.Equal(Truth.Yes, Match(rule, Show(genres: ["Comedy"]), kind: "shows"));
	}
}
