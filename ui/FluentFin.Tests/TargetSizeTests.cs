using FluentFin.Controls;
using FluentFin.Plugins.Journal.Controls;

namespace FluentFin.Tests;

/// <summary>
/// 1.2.0 (Seaglass UX review, fix 7): the Home hero's dots and the item pages' rating stars are targets of at least
/// 24 px (WCAG 2.5.8). Their XAML and code use these values (FluentFin.Core.Tests, ItemButtonsXamlTests).
/// </summary>
public sealed class TargetSizeTests
{
	private const double WcagMinimum = 24;

	[Fact]
	public void HeroDots_Are32PxTargets()
	{
		// 1.2.0 (Nixie button review, I20): the "Compact" target, above the WCAG floor (it was 24, 22 before)
		Assert.True(HeroDots.TargetSize >= WcagMinimum, $"a dot's target is {HeroDots.TargetSize} px");
		Assert.Equal(32, HeroDots.TargetSize);
	}

	[Fact]
	public void RatingStars_AreAtLeast24PxWideAndHigh()
	{
		// HalfStarRating gives each star a cell 1.25 times its size
		Assert.True(JournalRatingPanel.StarSize * 1.25 >= WcagMinimum, $"a star's cell is {JournalRatingPanel.StarSize * 1.25} px wide");
		Assert.True(JournalRatingPanel.TargetHeight >= WcagMinimum, $"the stars are {JournalRatingPanel.TargetHeight} px high");
	}
}
