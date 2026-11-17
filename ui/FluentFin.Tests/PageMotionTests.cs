using FluentFin.Services;
using Microsoft.UI.Xaml.Navigation;

namespace FluentFin.Tests;

/// <summary>
/// Seaglass 1.1.4 (interaction audit, item 16): a page change's motion says which way you went, as Windows' page transitions
/// do: a rail destination comes in with the refresh (slide up), opening something drills in (grows into place), Back drills out
/// (settles down into place). It used to be the same rise-and-grow for every change, Back included.
/// </summary>
public sealed class PageMotionTests
{
	[Theory]
	[InlineData(NavigationMode.New, true, PageMotion.Refresh)]
	[InlineData(NavigationMode.New, false, PageMotion.DrillIn)]
	[InlineData(NavigationMode.Forward, false, PageMotion.DrillIn)]
	[InlineData(NavigationMode.Forward, true, PageMotion.DrillIn)]
	[InlineData(NavigationMode.Back, false, PageMotion.DrillOut)]
	[InlineData(NavigationMode.Back, true, PageMotion.DrillOut)]
	[InlineData(NavigationMode.Refresh, true, PageMotion.Refresh)]
	public void TheMotion_FollowsTheDirection(NavigationMode mode, bool railPage, PageMotion expected)
	{
		Assert.Equal(expected, PageTransition.MotionFor(mode, railPage));
	}

	[Fact]
	public void EachMotion_StartsWhereItsDirectionSays()
	{
		// the refresh slides up into place, no scale
		Assert.Equal((1f, 28f), PageTransition.StartOf(PageMotion.Refresh));
		// in: smaller to full size; out: larger to full size
		Assert.True(PageTransition.StartOf(PageMotion.DrillIn).Scale < 1f);
		Assert.True(PageTransition.StartOf(PageMotion.DrillOut).Scale > 1f);
		Assert.Equal(0f, PageTransition.StartOf(PageMotion.DrillIn).OffsetY);
		Assert.Equal(0f, PageTransition.StartOf(PageMotion.DrillOut).OffsetY);
	}

	[Fact]
	public void TheNavigation_PassesItsDirection_AndTheShellNamesItsRailPages()
	{
		var root = Path.GetDirectoryName(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "FluentFin.csproj")))!;

		Assert.Contains("PageTransition.Play(e.Content, e.NavigationMode);", File.ReadAllText(Path.Combine(root, "Services", "NavigationService.cs")));
		Assert.Contains("PageTransition.IsTopLevel = IsRailPage;", File.ReadAllText(Path.Combine(root, "Views", "ShellPage.xaml.cs")));
	}
}
