using FluentFin.Controls;
using FluentFin.Plugins.Journal.Controls;

namespace FluentFin.Tests;

/// <summary>
/// Seaglass 1.1.4 (Seaglass UX review, fix 7): the Home hero's dots and the item pages' rating stars are targets of at least
/// 24 px (WCAG 2.5.8). Their XAML and code use these values (FluentFin.Core.Tests, ItemButtonsXamlTests).
/// </summary>
public sealed class TargetSizeTests
{
	private const double WcagMinimum = 24;

	[Fact]
	public void HeroDots_AreAtLeast24Px_AndKeepTheirOldSpacing()
	{
		Assert.True(HeroDots.TargetSize >= WcagMinimum, $"a dot's target is {HeroDots.TargetSize} px");
		// the dots touch (no gap), so their centres sit 24 px apart: 22 px and a 2 px gap before
		Assert.Equal(22 + 2, HeroDots.TargetSize);
	}

	[Fact]
	public void RatingStars_AreAtLeast24PxWideAndHigh()
	{
		// HalfStarRating gives each star a cell 1.25 times its size
		Assert.True(JournalRatingPanel.StarSize * 1.25 >= WcagMinimum, $"a star's cell is {JournalRatingPanel.StarSize * 1.25} px wide");
		Assert.True(JournalRatingPanel.TargetHeight >= WcagMinimum, $"the stars are {JournalRatingPanel.TargetHeight} px high");
	}
}
