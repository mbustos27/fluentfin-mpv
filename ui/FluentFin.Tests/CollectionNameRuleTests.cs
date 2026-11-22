using FluentFin.Converters;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.6.1 (bug hunt BH-160-M2): the app's own places that tidy a collection's name follow the one rule
/// (<see cref="CollectionNames.Display"/>): two box sets that would read the same keep their server names. The view model
/// places are FluentFin.Core.Tests' CollectionNameRuleTests. Alone: the rule's list is static.
/// </summary>
[Collection(FluentFin.Tests.Mpv.AloneCollection.Name)]
public sealed class CollectionNameRuleTests : IDisposable
{
	private static BaseItemDto BoxSet(string name) => new() { Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.BoxSet };

	public void Dispose() => CollectionNames.RememberBoxSets([]);

	/// <summary>Cards in a library grid and the server's box-set view (JellyfinTitleConverter).</summary>
	[Fact]
	public void ACardsTitle_SaysTheServersName_ForTwoThatWouldReadTheSame()
	{
		var winter = BoxSet("Winter Films");
		var winter4K = BoxSet("Winter Films (4K)");
		var marvel = BoxSet("[HUB] Marvel");
		CollectionNames.RememberBoxSets([winter, winter4K, marvel]);
		var title = new JellyfinTitleConverter();

		Assert.Equal("Winter Films", title.Convert(winter, typeof(string), null!, ""));
		Assert.Equal("Winter Films (4K)", title.Convert(winter4K, typeof(string), null!, ""));
		Assert.Equal("Marvel", title.Convert(marvel, typeof(string), null!, ""));
	}

	/// <summary>
	/// Review of 1.6.1 (F2): until the user's box sets are read, names that would be tidied stay the server's; with nothing else
	/// reading them (Home's Nixie rows off, a film opened at once), the shell reads them once, a few seconds after sign-in.
	/// </summary>
	[Fact]
	public void TheShell_ReadsTheBoxSetsOnce_WhenNothingElseHas()
	{
		var shell = File.ReadAllText(Path.Combine(FlyleafEngineTests.RepoRoot, "FluentFin", "Views", "ShellPage.xaml.cs"));

		Assert.Contains("_ = ReadBoxSetsSoonAsync();", shell);
		Assert.Contains("if (CollectionNames.Tidy && !CollectionNames.BoxSetsKnown)", shell);
		Assert.Contains("await _client.GetBoxSets();", shell);
	}

	/// <summary>A film page's "Part of:" links say what ItemMeta says (its Narrator name already did).</summary>
	[Fact]
	public void PartOfLinks_SayTheRulesName()
	{
		var link = File.ReadAllText(Path.Combine(FlyleafEngineTests.RepoRoot, "FluentFin", "Controls", "PartOfLink.cs"));

		Assert.Contains("var shown = ItemMeta.PartOfShown(collection);", link);
		Assert.Contains("AutomationProperties.SetName(link, ItemMeta.PartOfName(collection));", link);
		Assert.DoesNotContain("CollectionNames.Display(", link);
	}
}
