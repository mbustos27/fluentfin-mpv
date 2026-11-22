using FluentFin.Controls;
using FluentFin.Themes.Editorial.Controls;

namespace FluentFin.Tests;

/// <summary>
/// 1.6 colpage (LB1): a card's place in a set leads its caption ("1 · Wild Canyons"), and Narrator hears it first, in the
/// caption's order ("1, Wild Canyons, 1979"); no place, no change to the name every other page gives it.
/// </summary>
public sealed class CaptionPrefixTests
{
	[Fact]
	public void TheCaption_LeadsWithThePlace_AndNarratorHearsIt()
	{
		Assert.Equal("1 · ", BaseItemDtoCard.PrefixWords("1"));
		Assert.Equal("", BaseItemDtoCard.PrefixWords(""));
		Assert.Equal("", BaseItemDtoCard.PrefixWords(null));
		Assert.Equal("1, Wild Canyons, 1979", BaseItemDtoCard.NameWithPrefix("1", "Wild Canyons, 1979"));
		Assert.Equal("Wild Canyons, 1979", BaseItemDtoCard.NameWithPrefix("", "Wild Canyons, 1979"));
	}

	[Fact]
	public void EveryPosterTheCollectionPageCanMake_TakesAPlace()
	{
		Assert.True(typeof(ICaptionPrefixed).IsAssignableFrom(typeof(BaseItemDtoCard)));
		Assert.True(typeof(ICaptionPrefixed).IsAssignableFrom(typeof(EditorialPoster)));
		// Editorial numbers as its Home does ("01 … 05")
		Assert.Equal("01", EditorialPoster.Numbered("1"));
		Assert.Equal("12", EditorialPoster.Numbered("12"));
		Assert.Equal("", EditorialPoster.Numbered(""));
	}
}

/// <summary>
/// 1.6 colpage (review Major 2): on a page that runs under the 48 px top bar (Cinematic's Collection page), a card scrolled
/// into view (Back to a deep card, Up through the grid) keeps the bar's height and 16 above it, so its top and its focus ring
/// are never behind the bar; below the bar, 16 (then-160-colpage.ps1's 'deep' part checks it live).
/// </summary>
public sealed class CollectionRevealTests
{
	[Fact]
	public void ACardScrolledIntoView_KeepsClearOfTheBar()
	{
		Assert.Equal(FluentFin.Controls.TopBar.Height + 16, FluentFin.Views.CollectionPage.RevealTop(underBar: true));
		Assert.Equal(16, FluentFin.Views.CollectionPage.RevealTop(underBar: false));
		var code = File.ReadAllText(Path.Combine(FlyleafEngineTests.RepoRoot, "FluentFin", "Views", "CollectionPage.xaml.cs"));
		Assert.Contains("GridList.BringIntoViewRequested +=", code, StringComparison.Ordinal);
		Assert.Contains("RevealTop(Controls.TopBar.GetExtendsUnder(this))", code, StringComparison.Ordinal);
	}
}
