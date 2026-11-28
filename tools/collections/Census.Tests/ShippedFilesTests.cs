using System.Text.Json;
using Nixie.Collections;
using static Census.Tests.Fx;

namespace Census.Tests;

/// <summary>
/// The shipped data files (ui\FluentFin.Core\SmartCollections\Data) and the draft they are cut from (tools\collections): no lint
/// problem, the catalogue is exactly the draft cut by kept.txt, and the matchable list has the app's loader's shape.
/// </summary>
public class ShippedFilesTests
{
	private static Catalogue Shipped => Catalogue.Read(Path.Combine(Data, "micro-genres.json"));

	private static Catalogue Draft => Catalogue.Read(Path.Combine(Tools, "draft.json"));

	private static Matchable List => Matchable.Read(Path.Combine(Data, "keywords-matchable.json"));

	[Fact]
	public void TheShippedCatalogueAndList_HaveNoProblems()
	{
		Assert.Empty(Lint.Check(Shipped, List));
		Assert.InRange(Shipped.Categories.Count, 200, 300); // the brief's cut
		Assert.Contains(Shipped.Categories, c => c.Films);
		Assert.Contains(Shipped.Categories, c => !c.Films);
	}

	[Fact]
	public void TheDraft_HasNoProblems() => Assert.Empty(Lint.Check(Draft, List));

	[Fact]
	public void TheShippedCatalogue_IsTheDraftCutByTheKeepList()
	{
		var keep = File.ReadAllLines(Path.Combine(Tools, "kept.txt")).Select(x => x.Split('#')[0].Trim()).Where(x => x.Length > 0).ToHashSet(StringComparer.Ordinal);
		Assert.Equal(keep.Count, Shipped.Categories.Count);
		Assert.True(keep.SetEquals(Draft.Categories.Where(c => keep.Contains(c.Id)).Select(c => c.Id)), "kept.txt names an id the draft hasn't");
		Assert.Equal(Draft.Format(keep), File.ReadAllText(Path.Combine(Data, "micro-genres.json")).ReplaceLineEndings("\n"));
	}

	[Fact]
	public void EveryMatchableKeyword_IsUsedByTheDraft_SoNoneIsKeptForNothing()
	{
		// the list is the draft's keywords, not only the shipped ones: a drafted category the owner's review brings back needs no
		// new index revision (every change to the list reads every title again)
		var used = Draft.Categories.SelectMany(c => c.Rule.AllKeywords).ToHashSet(StringComparer.Ordinal);
		Assert.DoesNotContain(List.Keywords, k => !used.Contains(k));
	}

	[Fact]
	public void TheMatchableList_HasTheShapeTheAppsLoaderReads()
	{
		// MicroGenres.LoadMatchable reads "keywords": strings, or objects with a "keyword" string; anything else is its authors' notes
		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Data, "keywords-matchable.json")));
		var keywords = doc.RootElement.GetProperty("keywords");
		Assert.Equal(JsonValueKind.Array, keywords.ValueKind);
		Assert.All(keywords.EnumerateArray(), x => Assert.Equal(JsonValueKind.String, x.GetProperty("keyword").ValueKind));
		Assert.Equal(List.Keywords.Count(), keywords.GetArrayLength());
		Assert.InRange(keywords.GetArrayLength(), 250, 350); // the brief's "about 300"
	}

	[Fact]
	public void EveryParent_IsAKeywordCollectionsId_AndEveryFamilyIsShort()
	{
		Assert.All(Shipped.Categories.Where(c => c.Parent is not null), c => Assert.NotNull(Nixie.Collections.Census.ParentKeyword(c.Parent)));
		Assert.All(Shipped.Categories, c => Assert.InRange(c.Family.Length, 3, 24));
	}
}
