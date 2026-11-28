using FluentFin.Core.SmartCollections;
using Nixie.Collections;
using static Census.Tests.Fx;

namespace Census.Tests;

/// <summary>The census's words: counts and catalogue ids on the console and in its JSON, never a title or an id of the library.</summary>
public class ReportTests
{
	private static (Nixie.Collections.Census Census, IndexEntry Title) Library()
	{
		var titles = Enumerable.Range(0, 9).Select(i => Film($"Zqx Title {i}", genres: ["War"])).ToList();
		titles.AddRange(Enumerable.Range(0, 9).Select(i => Show($"Vbn Show {i}", genres: ["Comedy"], studios: ["Qwk Network"])));
		var census = CensusOf(titles,
		[
			Cat(new Rule { GenresAll = ["War"] }, id: "war-films"),
			Cat(new Rule { GenresAll = ["Comedy"], StudiosAny = ["Qwk Network"] }, id: "network-comedies", kind: "shows"),
		]);
		return (census, titles[0]);
	}

	[Fact]
	public void TheTableAndTheJson_SayCountsOnly()
	{
		var (census, title) = Library();
		foreach (var text in new[] { Report.Table(census), Report.Json(census) })
		{
			Assert.DoesNotContain("Zqx", text, StringComparison.Ordinal);
			Assert.DoesNotContain("Vbn", text, StringComparison.Ordinal);
			Assert.DoesNotContain("Qwk", text, StringComparison.Ordinal); // a network is in the rule, not in the census's words
			Assert.DoesNotContain(title.Id.ToString(), text, StringComparison.OrdinalIgnoreCase);
			Assert.Contains("war-films", text, StringComparison.Ordinal);
		}

		Assert.Contains("fits", Report.Table(census), StringComparison.Ordinal);
	}

	[Fact]
	public void Examples_GoOnlyToTheirOwnText_WithTitles()
	{
		var (census, _) = Library();
		var examples = Report.Examples(census, 5);
		Assert.Contains("Zqx Title", examples, StringComparison.Ordinal);
		Assert.Contains("local only", examples, StringComparison.Ordinal);
		Assert.Equal(5, examples.Split('\n').Single(x => x.StartsWith("- war-films:", StringComparison.Ordinal)).Split(';').Length);
	}

	[Fact]
	public void TheNamesList_SaysEachPool_OrWhyItIsUnknown_ShowsFirst()
	{
		var titles = Enumerable.Range(0, 9).Select(i => Film($"Zqx Title {i}", genres: ["War"], tags: [], tagCount: 4)).ToList();
		titles.AddRange(Enumerable.Range(0, 9).Select(i => Show($"Vbn Show {i}", genres: ["Comedy"])));
		var census = CensusOf(titles,
		[
			Cat(new Rule { GenresAll = ["War"] }, id: "war-films", name: "War Movies on File"),
			Cat(new Rule { GenresAll = ["War"], KeywordsAny = ["small town"] }, id: "small-town-war", name: "Small-Town War Movies"),
			Cat(new Rule { GenresAll = ["Comedy"], TextAny = ["office"] }, id: "office", kind: "shows", name: "Workplace TV Comedies"),
		]);
		var names = Report.Names(census, 5);
		Assert.True(names.IndexOf("## Shows", StringComparison.Ordinal) < names.IndexOf("## Films", StringComparison.Ordinal));
		Assert.Contains("**War Movies on File**. Facts of the titles. Pool: 9 films, none watched.", names, StringComparison.Ordinal);
		Assert.Contains("**Small-Town War Movies**. Facts of the titles. Pool unknown until the app's census.", names, StringComparison.Ordinal);
		Assert.Contains("**Workplace TV Comedies**. Facts of the titles. Pool unknown until the summary pass (step 1b).", names, StringComparison.Ordinal);
		Assert.Single(names.Split('\n'), x => x.StartsWith("  - Zqx Title", StringComparison.Ordinal)); // examples for the known pool only
	}

	[Theory]
	[InlineData("Louie", 2010, "Louie (2010)")]
	[InlineData("Louie (2010)", 2010, "Louie (2010)")] // the server's name already says it
	[InlineData("Battlestar Galactica (2003)", 2005, "Battlestar Galactica (2003) (2005)")]
	[InlineData("Louie", null, "Louie")]
	public void AnExampleTitle_SaysItsYearOnce(string name, int? year, string said) => Assert.Equal(said, Report.Titled(Film(name, year: year)));

	[Fact]
	public void TheFormat_ReadsBackAsTheSameCatalogue()
	{
		var catalogue = new Catalogue
		{
			Version = 1,
			Categories =
			[
				Cat(new Rule { GenresAll = ["Crime", "Drama"], Years = [1970, 1979], KeywordsNone = ["superhero"], LengthMinutes = [null, 35], CommunityMin = 7.5 },
					id: "one", parent: "keyword:neonoir", reason: "TMDb keyword “neo-noir”, 1970–79"),
				Cat(new Rule { TextAny = ["office"], StudiosAny = ["ABC (US)"] }, id: "two", kind: "shows", name: "Workplace TV Comedies"),
			],
		};
		var text = catalogue.Format();
		Assert.Contains("“neo-noir”, 1970–79", text, StringComparison.Ordinal); // typography kept, not \u escapes
		var back = Catalogue.Parse(text);
		Assert.Equal(text, back.Format());
		Assert.Equal([null, 35], back.Categories[0].Rule.LengthMinutes);
		Assert.Null(back.Categories[1].Parent);
		Assert.DoesNotContain("\"one\"", catalogue.Format(new HashSet<string> { "two" }), StringComparison.Ordinal); // the cut
	}
}
