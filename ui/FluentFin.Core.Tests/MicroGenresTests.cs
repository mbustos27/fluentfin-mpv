using System.Text;
using FluentFin.Core.SmartCollections;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (Collections 2, stream match): the micro-genre catalogue's loader and validator (<see cref="MicroGenres"/>). A
/// damaged file is an empty catalogue and never a throw; a category that fails a check is left out, with the problem said,
/// and its neighbours stay.
/// </summary>
public class MicroGenresTests
{
	// one valid category of the brief's shape; the cases below change one thing of it
	private const string Valid = """
		{
		  "id": "crime-drama-1970s", "name": "Gritty 1970s Crime Dramas", "kind": "films", "family": "crime", "parent": "keyword:neo-noir",
		  "rule": {
		    "genresAll": ["Crime", "Drama"], "genresAny": [], "genresNone": ["Animation", "Documentary"],
		    "keywordsAny": [], "keywordsAll": [], "keywordsNone": [],
		    "years": [1970, 1979], "countriesAny": [], "studiosAny": [], "lengthMinutes": [null, null],
		    "communityMin": null, "criticMin": null, "textAny": []
		  },
		  "reason": "Crime and drama, 1970–79"
		}
		""";

	// a second valid category that stays whatever happens to the first
	private const string Neighbour = """
		{ "id": "hong-kong-action", "name": "Hong Kong Action Movies", "kind": "films", "family": "action",
		  "rule": { "genresAll": ["Action"], "countriesAny": ["Hong Kong"] }, "reason": "Action, made in Hong Kong" }
		""";

	private static MicroGenreCatalogue Load(params string[] categories) => MicroGenres.Load(MicroGenreFixture.File(categories), MicroGenreFixture.TestMatchable());

	private static MicroGenreCatalogue LoadRaw(string json) => MicroGenres.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)), MicroGenreFixture.TestMatchable());

	[Fact]
	public void TheBriefsExample_ReadsAsWritten()
	{
		var catalogue = Load(Valid);

		Assert.Equal(Array.Empty<string>(), catalogue.Problems);
		var category = Assert.Single(catalogue.Categories);
		Assert.Equal("crime-drama-1970s", category.Id);
		Assert.Equal("micro:crime-drama-1970s", category.CollectionId);
		Assert.Equal("Gritty 1970s Crime Dramas", category.Name);
		Assert.Equal(IndexKind.Movie, category.Kind);
		Assert.Equal("crime", category.Family);
		Assert.Equal("keyword:neonoir", category.Parent); // the keyword builder's id of "neo-noir"
		Assert.Equal("Crime and drama, 1970–79", category.Reason);
		Assert.Equal(["Crime", "Drama"], category.Rule.GenresAll);
		Assert.Equal(["Animation", "Documentary"], category.Rule.GenresNone);
		Assert.Equal(new MicroRange(1970, 1979), category.Rule.Years);
		Assert.True(category.Rule.LengthMinutes.IsOpen);
		Assert.Null(category.Rule.CommunityMin);
		Assert.False(category.Rule.WaitsForText);
		Assert.Same(category, catalogue.Of("crime-drama-1970s"));
		Assert.Same(category, catalogue.Of("micro:crime-drama-1970s"));
		Assert.Null(catalogue.Of("micro:nothing"));
	}

	[Fact]
	public void TheTestCatalogue_LoadsEveryCategory_EveryRuleFieldUsed()
	{
		var catalogue = MicroGenreFixture.TestCatalogue();

		Assert.Equal(Array.Empty<string>(), catalogue.Problems);
		Assert.Equal(12, catalogue.Categories.Count);
		Assert.Equal(8, catalogue.Categories.Count(x => x.Kind == IndexKind.Movie));
		Assert.Equal(4, catalogue.Categories.Count(x => x.Kind == IndexKind.Series));
		var rules = catalogue.Categories.Select(x => x.Rule).ToList();
		Assert.Contains(rules, x => x.GenresAll.Count > 0);
		Assert.Contains(rules, x => x.GenresAny.Count > 0);
		Assert.Contains(rules, x => x.GenresNone.Count > 0);
		Assert.Contains(rules, x => x.KeywordsAny.Count > 0);
		Assert.Contains(rules, x => x.KeywordsAll.Count > 0);
		Assert.Contains(rules, x => x.KeywordsNone.Count > 0);
		Assert.Contains(rules, x => !x.Years.IsOpen);
		Assert.Contains(rules, x => x.CountriesAny.Count > 0);
		Assert.Contains(rules, x => x.StudiosAny.Count > 0);
		Assert.Contains(rules, x => !x.LengthMinutes.IsOpen);
		Assert.Contains(rules, x => x.CommunityMin is not null);
		Assert.Contains(rules, x => x.CriticMin is not null);
		Assert.Contains(rules, x => x.WaitsForText);
		// the matchable tier: "small town" is on the test list, not the allowlist
		Assert.Equal(["small town"], catalogue.Of("small-town-mysteries")!.Rule.KeywordsAll);
		Assert.Equal(new MicroRange(null, 35), catalogue.Of("sitcoms-2000s")!.Rule.LengthMinutes);
		Assert.Equal("keyword:martialarts", catalogue.Of("hong-kong-action")!.Parent);
	}

	[Theory]
	[InlineData("name over 50", "the name is over 50 characters (51)")]
	[InlineData("name your", "the name starts with “Your”")]
	[InlineData("name viewer", "the name speaks of the viewer (“you”)")]
	[InlineData("name filler", "the name has the filler word “Gems”")]
	[InlineData("name filler hyphen", "the name has the filler word “Must-Watch”")]
	[InlineData("name missing", "no name")]
	[InlineData("unknown field", "field “mood” is not in the format")]
	[InlineData("unknown rule field", "the rule's field “genreAll” is not in the format")]
	[InlineData("field twice", "field “name” is given twice")]
	[InlineData("empty rule", "the rule is empty")]
	[InlineData("empty rule of empty lists", "the rule is empty")]
	[InlineData("no rule", "the rule is empty")]
	[InlineData("years backwards", "the rule's years run backwards (1979 to 1970)")]
	[InlineData("length backwards", "the rule's lengthMinutes run backwards (120 to 90)")]
	[InlineData("length below 0", "the rule's lengthMinutes go below 0")]
	[InlineData("years not whole", "the rule's years has an end that is not a whole number")]
	[InlineData("keyword not matchable", "the keyword “duringcreditsstinger” is not matchable")]
	[InlineData("keyword none not matchable", "the keyword “suicide” is not matchable")]
	[InlineData("keyword all not matchable", "the keyword “twist ending” is not matchable")]
	[InlineData("kind", "the kind “movies” is not films or shows")]
	[InlineData("id", "the id is not lower case words and hyphens")]
	[InlineData("parent", "the parent “keyword:office” is not a keyword collection")]
	[InlineData("community scale", "the rule's communityMin is outside 0 to 10")]
	[InlineData("critic scale", "the rule's criticMin is outside 0 to 100")]
	[InlineData("genre both", "the genre “Crime” is both wanted and refused")]
	[InlineData("reason viewer", "the reason speaks of the viewer (“you”)")]
	[InlineData("no family", "no family")]
	[InlineData("list not a list", "the rule's genresAll is not a list")]
	public void ACategoryFailingACheck_IsLeftOut_WithItsProblemSaid_ItsNeighbourStays(string change, string problem)
	{
		var bad = change switch
		{
			"name over 50" => Valid.Replace("Gritty 1970s Crime Dramas", new string('x', 26) + " " + new string('y', 24)),
			"name your" => Valid.Replace("Gritty 1970s Crime Dramas", "Your Gritty Crime Dramas"),
			"name viewer" => Valid.Replace("Gritty 1970s Crime Dramas", "Crime Dramas you missed"),
			"name filler" => Valid.Replace("Gritty 1970s Crime Dramas", "Hidden Gems: 1970s Crime Dramas"),
			"name filler hyphen" => Valid.Replace("Gritty 1970s Crime Dramas", "Must-Watch 1970s Crime Dramas"),
			"name missing" => Valid.Replace("\"name\": \"Gritty 1970s Crime Dramas\",", ""),
			"unknown field" => Valid.Replace("\"family\": \"crime\"", "\"family\": \"crime\", \"mood\": \"dark\""),
			"unknown rule field" => Valid.Replace("\"genresAny\": []", "\"genreAll\": [\"Crime\"]"),
			"field twice" => Valid.Replace("\"family\": \"crime\"", "\"family\": \"crime\", \"name\": \"Other\""),
			"empty rule" => """{ "id": "crime-drama-1970s", "name": "Gritty 1970s Crime Dramas", "kind": "films", "family": "crime", "rule": {}, "reason": "Crime" }""",
			"empty rule of empty lists" => Valid.Replace("[\"Crime\", \"Drama\"]", "[]").Replace("[\"Animation\", \"Documentary\"]", "[]").Replace("[1970, 1979]", "[null, null]"),
			"no rule" => """{ "id": "crime-drama-1970s", "name": "Gritty 1970s Crime Dramas", "kind": "films", "family": "crime", "reason": "Crime" }""",
			"years backwards" => Valid.Replace("[1970, 1979]", "[1979, 1970]"),
			"length backwards" => Valid.Replace("\"lengthMinutes\": [null, null]", "\"lengthMinutes\": [120, 90]"),
			"length below 0" => Valid.Replace("\"lengthMinutes\": [null, null]", "\"lengthMinutes\": [-5, 90]"),
			"years not whole" => Valid.Replace("[1970, 1979]", "[1970.5, 1979]"),
			"keyword not matchable" => Valid.Replace("\"keywordsAny\": []", "\"keywordsAny\": [\"heist\", \"duringcreditsstinger\"]"),
			"keyword none not matchable" => Valid.Replace("\"keywordsNone\": []", "\"keywordsNone\": [\"suicide\"]"),
			"keyword all not matchable" => Valid.Replace("\"keywordsAll\": []", "\"keywordsAll\": [\"Twist Ending\"]"),
			"kind" => Valid.Replace("\"films\"", "\"movies\""),
			"id" => Valid.Replace("\"crime-drama-1970s\"", "\"Crime Drama\""),
			"parent" => Valid.Replace("keyword:neo-noir", "keyword:office"),
			"community scale" => Valid.Replace("\"communityMin\": null", "\"communityMin\": 75"),
			"critic scale" => Valid.Replace("\"criticMin\": null", "\"criticMin\": 101"),
			"genre both" => Valid.Replace("[\"Animation\", \"Documentary\"]", "[\"crime\"]"),
			"reason viewer" => Valid.Replace("Crime and drama, 1970–79", "Crime dramas for you"),
			"no family" => Valid.Replace("\"family\": \"crime\",", ""),
			"list not a list" => Valid.Replace("[\"Crime\", \"Drama\"]", "\"Crime\""),
			_ => throw new ArgumentOutOfRangeException(nameof(change)),
		};
		Assert.NotEqual(Valid, bad); // the change took

		var catalogue = Load(bad, Neighbour);

		Assert.Equal(["hong-kong-action"], catalogue.Categories.Select(x => x.Id));
		Assert.Contains(catalogue.Problems, x => x.EndsWith(": " + problem, StringComparison.Ordinal) && x.StartsWith("#1", StringComparison.Ordinal));
	}

	[Fact]
	public void ANameOfExactly50Characters_Passes()
	{
		var name = new string('x', 25) + " " + new string('y', 24);
		Assert.Equal(50, name.Length);

		var catalogue = Load(Valid.Replace("Gritty 1970s Crime Dramas", name));

		Assert.Equal(Array.Empty<string>(), catalogue.Problems);
		Assert.Equal(name, Assert.Single(catalogue.Categories).Name);
	}

	[Fact]
	public void AnIdUsedTwice_TheSecondIsRefused_AndANameUsedTwiceToo()
	{
		var sameId = Neighbour.Replace("hong-kong-action", "crime-drama-1970s").Replace("Hong Kong Action Movies", "Other Action Movies");
		var sameName = Neighbour.Replace("\"hong-kong-action\"", "\"hong-kong-action-2\"").Replace("Hong Kong Action Movies", "gritty 1970S crime dramas");

		var catalogue = Load(Valid, sameId, sameName, Neighbour);

		Assert.Equal(["crime-drama-1970s", "hong-kong-action"], catalogue.Categories.Select(x => x.Id));
		Assert.Contains("#2 “crime-drama-1970s”: the id is used before", catalogue.Problems);
		Assert.Contains("#3 “hong-kong-action-2”: the name is used before", catalogue.Problems);
	}

	[Fact]
	public void AParent_IsTheKeywordCollectionsId_WrittenEitherWay()
	{
		Assert.Equal("keyword:neonoir", MicroGenres.ParentId("keyword:neo-noir"));
		Assert.Equal("keyword:neonoir", MicroGenres.ParentId("keyword:neonoir"));
		Assert.Equal("keyword:artificialintelligenceai", MicroGenres.ParentId("keyword:artificial intelligence (a.i.)"));
		Assert.Null(MicroGenres.ParentId("keyword:office")); // matchable, but no keyword collection
		Assert.Null(MicroGenres.ParentId("neo-noir"));
		Assert.Null(MicroGenres.ParentId("franchise:2344"));
	}

	[Theory]
	[InlineData("")]
	[InlineData("{")]
	[InlineData("not json")]
	[InlineData("null")]
	[InlineData("[]")]
	[InlineData("""{ "categories": [] }""")]
	[InlineData("""{ "version": 2, "categories": [ { "id": "a" } ] }""")]
	[InlineData("""{ "version": "1", "categories": [] }""")]
	[InlineData("""{ "version": 1 }""")]
	[InlineData("""{ "version": 1, "categories": {} }""")]
	[InlineData("""{ "version": 1, "categories": [ 1, "two", null, [] ] }""")]
	[InlineData("""{ "version": 1, "categories": [ { "id": "a", "name": "A Films", "kind": "films", "family": "a", "rule": { "genresAll": ["Drama"] }, "reason": "x" } """)]
	public void ADamagedFile_IsAnEmptyCatalogue_NeverAThrow(string json)
	{
		var catalogue = LoadRaw(json);

		Assert.Empty(catalogue.Categories);
	}

	[Fact]
	public void NoFile_IsTheEmptyCatalogue()
	{
		Assert.Same(MicroGenreCatalogue.Empty, MicroGenres.Load(null, MicroGenreFixture.TestMatchable()));
		Assert.Empty(MicroGenreCatalogue.Empty.Categories);
		Assert.Empty(MicroGenreCatalogue.Empty.Problems);
	}

	[Fact]
	public void AnUnknownFieldOfTheFile_IsSaid_TheCategoriesStillRead()
	{
		var catalogue = LoadRaw($$"""{ "version": 1, "notes": "draft 3", "categories": [ {{Valid}} ] }""");

		Assert.Single(catalogue.Categories);
		Assert.Equal(["the file: field “notes” is not in the format"], catalogue.Problems);
	}

	[Fact]
	public void TheMatchableKeywords_AreTheAllowlistTheSeasonalOnesAndTheFilesOwn()
	{
		var none = MicroGenres.LoadMatchable(null);
		var test = MicroGenreFixture.TestMatchable();
		var array = MicroGenres.LoadMatchable(new MemoryStream(Encoding.UTF8.GetBytes("""[ "Small Town", 3, null ]""")));
		var damaged = MicroGenres.LoadMatchable(new MemoryStream(Encoding.UTF8.GetBytes("""{ "keywords": [ "small town", """)));

		Assert.Equal(Keywords.Allowed.Count + Keywords.Seasonal.Count, none.Count);
		Assert.Contains("neo-noir", none);
		Assert.Contains("christmas", none);
		Assert.DoesNotContain("small town", none);
		Assert.Equal(none.Count + 3, test.Count);
		Assert.Contains("small town", test);
		Assert.Contains("boarding school", test); // an object's keyword
		Assert.Contains("office", test); // trimmed, lower case
		Assert.Contains("small town", array);
		Assert.Equal(none.Count + 1, array.Count);
		Assert.Equal(none.Count, damaged.Count); // a damaged file adds none, and none of it half
	}

	[Fact]
	public void TheShippedCatalogue_HasNoProblems_AndEveryNameKeepsTheRules()
	{
		// until the cat stream's micro-genres.json lands it is missing, and the catalogue is empty; once it lands, every one of
		// its categories must pass every check
		var shipped = MicroGenres.Shipped;
		var present = typeof(MicroGenres).Assembly.GetManifestResourceNames().Contains(MicroGenres.Resource);

		Assert.Empty(shipped.Problems);
		Assert.Equal(present, shipped.Categories.Count > 0);
		Assert.All(shipped.Categories, x =>
		{
			Assert.InRange(x.Name.Length, 1, MicroGenres.NameMost);
			Assert.Null(NixieTitles.Check(x.Name));
			Assert.False(x.Rule.IsEmpty);
		});
		Assert.True(Keywords.Allowed.Keys.Select(x => x.ToLowerInvariant()).All(MicroGenres.Matchable.Contains), "every allowlisted keyword is matchable");
	}

	// the pre-release fix D: the offline run on the owner's library found documentaries, talk shows, reality shows and news in
	// show categories they made untrue (This week's "Historical Mini-Series" was a documentary shelf; a horror-host talk show
	// was in "Mystery Dramedies"). Each still takes the show it is for
	[Theory]
	[InlineData("tv-historical-mini-series", new[] { "Mini-Series", "History" }, "Documentary")]
	[InlineData("tv-mystery-dramedies", new[] { "Comedy", "Drama", "Mystery" }, "Talk Show")]
	[InlineData("tv-mystery-dramedies", new[] { "Comedy", "Drama", "Mystery" }, "Talk")]
	[InlineData("tv-mystery-dramedies", new[] { "Comedy", "Drama", "Mystery" }, "Reality")]
	[InlineData("tv-horror-comedies", new[] { "Comedy", "Horror" }, "Talk Show")]
	[InlineData("tv-horror-comedies", new[] { "Comedy", "Horror" }, "Reality")]
	[InlineData("tv-sci-fi-dramas-1990s", new[] { "Drama", "Science Fiction" }, "Talk")]
	[InlineData("tv-sci-fi-dramas-1990s", new[] { "Drama", "Science Fiction" }, "Reality")]
	[InlineData("tv-rated-mini-series", new[] { "Mini-Series" }, "Talk Show")]
	[InlineData("tv-sports-dramas", new[] { "Drama", "Sport" }, "Documentary")]
	[InlineData("tv-sports-dramas", new[] { "Drama", "Sport" }, "Reality")]
	[InlineData("tv-sports-comedies", new[] { "Comedy", "Sport" }, "News")]
	[InlineData("tv-sports-comedies", new[] { "Comedy", "Sport" }, "Talk Show")]
	public void TheShippedShowCategories_LeaveOutWhatMadeThemUntrue(string id, string[] genres, string refused)
	{
		var category = MicroGenres.Shipped.Of(id);
		Assert.NotNull(category);
		IndexEntry Show(string[] its) => new() { Id = Guid.NewGuid(), Name = "A show", Kind = IndexKind.Series, Genres = its, Year = 1995, Rating = 8.5, RunTimeTicks = TimeSpan.FromMinutes(45).Ticks };

		Assert.True(MicroGenreMatcher.Fits(category, Show(genres)), $"{id} takes the show it is for");
		Assert.False(MicroGenreMatcher.Fits(category, Show([.. genres, refused])), $"{id} takes a show of \"{refused}\"");
	}
}
