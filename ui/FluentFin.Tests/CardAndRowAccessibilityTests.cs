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

	[Fact]
	public void EpisodeRow_IsOneListItem_ThatPlaysOnInvoke_InAList()
	{
		// Seaglass scrolling: the row (not its five buttons) is what Narrator and the keyboard reach; Invoke is Enter's Play
		var row = typeof(EpisodeRow).GetMethod("OnCreateAutomationPeer", BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.Equal(typeof(EpisodeRow), row!.DeclaringType);
		Assert.True(typeof(FrameworkElementAutomationPeer).IsAssignableFrom(typeof(EpisodeRowAutomationPeer)));
		Assert.True(typeof(IInvokeProvider).IsAssignableFrom(typeof(EpisodeRowAutomationPeer)));

		var list = typeof(EpisodeList).GetMethod("OnCreateAutomationPeer", BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.Equal(typeof(EpisodeList), list!.DeclaringType);
		Assert.True(typeof(FrameworkElementAutomationPeer).IsAssignableFrom(typeof(EpisodeListAutomationPeer)));
	}

	[Fact]
	public void EpisodeRow_OnItsOwn_IsAButton_NotAListItemWithoutAList()
	{
		// the episode page's "Up next": then-season.ps1 checks the real one
		Assert.Equal(AutomationControlType.ListItem, EpisodeRowAutomationPeer.ControlTypeFor(inList: true));
		Assert.Equal(AutomationControlType.Button, EpisodeRowAutomationPeer.ControlTypeFor(inList: false));
	}

	[Fact]
	public void CardButtons_ShowOnKeyboardFocus_AndOutliveThePointerLeaving()
	{
		var card = new CardActions();

		Assert.True(card.GotFocus(byPointer: false));
		Assert.True(card.Shown);

		card.PointerEntered();
		card.PointerExited();
		Assert.True(card.Shown); // the focused button stays

		Assert.True(card.LostFocus(() => false));
		Assert.False(card.Shown);
	}

	[Fact]
	public void CardButtons_FocusFromAClick_LeavesThemToThePointer()
	{
		var card = new CardActions();

		card.PointerEntered();
		card.GotFocus(byPointer: true);
		card.PointerExited();

		Assert.False(card.Shown);
	}

	[Fact]
	public void CardButtons_FocusMovingToTheCardsOwnButton_KeepsThemShown()
	{
		var card = new CardActions();
		card.GotFocus(byPointer: false);

		card.LostFocus(() => true);
		card.GotFocus(byPointer: false);

		Assert.True(card.Shown);
	}

	[Fact]
	public void Focus_OnTheCardOrItsButton_StaysWithTheCard()
	{
		var (card, content, windowScroller) = (new object(), new object(), new object());

		Assert.True(CardActions.FocusStaysWithCard([new object(), new object(), card, new object(), content, windowScroller], card, content, []));
	}

	[Fact]
	public void Focus_ElsewhereInThePage_LeavesTheCard_WhateverPopupIsOpen()
	{
		// the walk goes past the window's content (to its scroll viewer), and a popup is open: a tooltip, or anything else
		var (card, content, windowScroller) = (new object(), new object(), new object());
		object[] elsewhere = [new object(), new object(), content, windowScroller];

		Assert.False(CardActions.FocusStaysWithCard(elsewhere, card, content, [ToolTip()]));
		Assert.False(CardActions.FocusStaysWithCard(elsewhere, card, content, [new object()]));
	}

	[Fact]
	public void Focus_InAMenuOrDialog_StaysWithTheCard_ButATooltipIsNotOne()
	{
		var (card, content) = (new object(), new object());
		object[] inAPopup = [new object(), new object(), new object()];

		Assert.True(CardActions.FocusStaysWithCard(inAPopup, card, content, [new object()]));
		Assert.False(CardActions.FocusStaysWithCard(inAPopup, card, content, [ToolTip()]));
		Assert.False(CardActions.FocusStaysWithCard(inAPopup, card, content, []));
	}

	/// <summary>A ToolTip for its type only: made without its constructor, which needs a running XAML.</summary>
	private static object ToolTip()
	{
		var tip = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Microsoft.UI.Xaml.Controls.ToolTip));
		GC.SuppressFinalize(tip);
		return tip;
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
