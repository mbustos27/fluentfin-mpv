using System.Reflection;
using FluentFin.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace FluentFin.Tests;

/// <summary>
/// fluentfin-mpv: the parts of the card, row and overview fixes that run without a XAML host (docs/research/ux-review.md A1, I1).
/// The XAML side is checked in FluentFin.Core.Tests (AccessibilityXamlTests).
/// </summary>
public sealed class CardAndRowAccessibilityTests
{
	[Fact]
	public void Card_HasItsOwnAutomationPeer_ThatCanBeInvoked()
	{
		var create = typeof(BaseItemDtoCard).GetMethod("OnCreateAutomationPeer", BindingFlags.Instance | BindingFlags.NonPublic);

		Assert.Equal(typeof(BaseItemDtoCard), create!.DeclaringType);
		Assert.True(typeof(FrameworkElementAutomationPeer).IsAssignableFrom(typeof(BaseItemDtoCardAutomationPeer)));
		Assert.True(typeof(IInvokeProvider).IsAssignableFrom(typeof(BaseItemDtoCardAutomationPeer)));
	}

	[Theory]
	[InlineData(false, false, 4, false, "Show more")] // short text: no toggle
	[InlineData(false, true, 4, true, "Show more")]   // cut: "Show more"
	[InlineData(true, false, 0, true, "Show less")]   // expanded: all lines, "Show less"
	public void Overview_ShowsMoreOnlyWhenCut(bool expanded, bool trimmed, int maxLines, bool showToggle, string text)
	{
		Assert.Equal((maxLines, showToggle, text), ExpandableTextLayout.For(expanded, trimmed, collapsedLines: 4));
	}

	[Theory]
	[InlineData(0, 1000, 200, 225, null)]      // in view
	[InlineData(500, 1000, 200, 225, 200.0)]   // left of the view: its left edge
	[InlineData(0, 1000, 900, 225, 125.0)]     // past the right edge: its right edge at the view's
	[InlineData(0, 1000, 1000, 1200, 1000.0)]  // wider than the view: its start
	[InlineData(0, 1000, 775, 225, null)]      // exactly at the right edge
	public void Row_ScrollsAFocusedCardIntoView_WithTheLeastMove(double offset, double viewport, double left, double width, double? expected)
	{
		Assert.Equal(expected, RowScroll.OffsetToShow(offset, viewport, left, width));
	}
}
