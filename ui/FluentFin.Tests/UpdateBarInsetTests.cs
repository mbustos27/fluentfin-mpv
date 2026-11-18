using FluentFin.Controls;
using FluentFin.Views;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.3.0: the update bar takes room only while it is open. Closed, it kept its margin (8, plus the top bar's height over
/// a hero), so every page meant to run under the top bar started that much lower: the heroes never reached under it.
/// </summary>
public sealed class UpdateBarInsetTests
{
	[Theory]
	[InlineData(true, false)]
	[InlineData(true, true)]
	[InlineData(false, false)]
	[InlineData(false, true)]
	public void A_closed_update_bar_takes_no_room(bool barVisible, bool extendsUnder) =>
		Assert.Null(ShellPage.UpdateBarTop(open: false, barVisible, extendsUnder));

	[Fact]
	public void An_open_update_bar_sits_below_the_top_bar()
	{
		Assert.Equal(8, ShellPage.UpdateBarTop(open: true, barVisible: true, extendsUnder: false));
		Assert.Equal(8 + TopBar.Height, ShellPage.UpdateBarTop(open: true, barVisible: true, extendsUnder: true));
		Assert.Equal(8, ShellPage.UpdateBarTop(open: true, barVisible: false, extendsUnder: true));
	}
}
