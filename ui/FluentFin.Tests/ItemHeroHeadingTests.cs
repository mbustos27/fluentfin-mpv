using FluentFin.Controls;
using Microsoft.UI.Xaml;

namespace FluentFin.Tests;

/// <summary>
/// 1.2.0 (the states stream's Narrator pass): an item page that failed to load had an empty top heading (the hero's title,
/// a Level 1 heading with no text). The hero's heading is the logo or the title, and none at all without a title.
/// </summary>
public sealed class ItemHeroHeadingTests
{
	[Theory]
	[InlineData(false, "A Quiet Harbor", Visibility.Collapsed, Visibility.Visible)]   // no logo: the title is the heading
	[InlineData(true, "A Quiet Harbor", Visibility.Visible, Visibility.Collapsed)]    // the logo is, named for the item
	[InlineData(false, "", Visibility.Collapsed, Visibility.Collapsed)]               // failed to load: no heading at all
	[InlineData(false, "   ", Visibility.Collapsed, Visibility.Collapsed)]
	[InlineData(true, "", Visibility.Collapsed, Visibility.Collapsed)]                // a logo with no name to read: none either
	public void TheHerosHeading_IsTheLogoOrTheTitle_AndNoneWithoutATitle(bool hasLogo, string title, Visibility logo, Visibility text)
	{
		Assert.Equal(logo, ItemHero.HeadingVisibility(hasLogo, title, forLogo: true));
		Assert.Equal(text, ItemHero.HeadingVisibility(hasLogo, title, forLogo: false));
	}
}
