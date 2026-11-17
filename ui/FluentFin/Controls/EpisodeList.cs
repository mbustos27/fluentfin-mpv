using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace FluentFin.Controls;

/// <summary>
/// Seaglass scrolling: a season's episodes (<see cref="EpisodeRow"/>s), one Tab stop per episode: Up/Down move between them,
/// Home/End to the first and last, past either end to what is above or below; a focused row is scrolled clear of the page's
/// edges; Narrator reads a list (named by AutomationProperties.Name) of rows "n of m". An ItemsControl, not a virtualizing
/// ItemsRepeater: Tab never reached the episodes after the first.
/// </summary>
public sealed partial class EpisodeList : ItemsControl
{
	public EpisodeList()
	{
		IsTabStop = false;
		AddHandler(KeyDownEvent, new KeyEventHandler(List_KeyDown), false);
		BringIntoViewRequested += List_BringIntoViewRequested;
	}

	/// <summary>The row's "n of m" (0 of 0 when the row is not one of this list's).</summary>
	internal (int Position, int Size) PlaceOf(EpisodeRow row)
	{
		var index = row.Dto is { } dto ? Items.IndexOf(dto) : -1;
		return index < 0 ? (0, 0) : (index + 1, Items.Count);
	}

	internal static EpisodeList? Of(DependencyObject element)
	{
		for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
		{
			if (parent is EpisodeList list)
			{
				return list;
			}
		}

		return null;
	}

	protected override AutomationPeer OnCreateAutomationPeer() => new EpisodeListAutomationPeer(this);

	private EpisodeRow? RowAt(int index) =>
		ContainerFromIndex(index) is DependencyObject container ? FindRow(container) : null;

	private static EpisodeRow? FindRow(DependencyObject node)
	{
		if (node is EpisodeRow row)
		{
			return row;
		}

		for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
		{
			if (FindRow(VisualTreeHelper.GetChild(node, i)) is { } found)
			{
				return found;
			}
		}

		return null;
	}

	private static EpisodeRow? RowOf(DependencyObject? element)
	{
		for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (node is EpisodeRow row)
			{
				return row;
			}

			if (node is EpisodeList)
			{
				return null;
			}
		}

		return null;
	}

	private static bool IsDown(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

	private void List_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (RowOf(e.OriginalSource as DependencyObject) is not { } row || IsDown(VirtualKey.Menu) || IsDown(VirtualKey.Control))
		{
			return;
		}

		GridKey? key = e.Key switch
		{
			VirtualKey.Up or VirtualKey.GamepadDPadUp => GridKey.Up,
			VirtualKey.Down or VirtualKey.GamepadDPadDown => GridKey.Down,
			VirtualKey.Home => GridKey.Home,
			VirtualKey.End => GridKey.End,
			_ => null,
		};
		if (key is not { } listKey)
		{
			return;
		}

		var current = PlaceOf(row).Position - 1;
		if (current < 0)
		{
			return;
		}

		// a list is a grid of one column
		if (GridPaging.KeyTarget(listKey, current, Items.Count, columns: 1, pageLines: 1) is { } target)
		{
			if (target != current && RowAt(target) is { } next)
			{
				next.Focus(FocusState.Keyboard);
			}

			e.Handled = true; // the page does not also scroll by a line, or to its top or bottom
			return;
		}

		// past the first or the last episode: the season pills or the hero above, what follows below; else the page scrolls
		e.Handled = CardGrid.MoveFocusOut(this, listKey == GridKey.Up ? FocusNavigationDirection.Up : FocusNavigationDirection.Down);
	}

	/// <summary>A row scrolled into view (keyboard focus) keeps a margin clear above and below it: never flush under the title bar (WCAG 2.4.11).</summary>
	private void List_BringIntoViewRequested(UIElement sender, BringIntoViewRequestedEventArgs args)
	{
		if (RowOf(args.TargetElement) is null)
		{
			return;
		}

		var rect = args.TargetRect;
		args.TargetRect = new Rect(rect.X, rect.Y - CardGrid.RevealMargin, rect.Width, rect.Height + 2 * CardGrid.RevealMargin);
		args.AnimationDesired = args.AnimationDesired && SafeMotion.Allowed;
	}
}

/// <summary>Seaglass scrolling: the episode list is a List for Narrator; its rows are its items.</summary>
internal sealed partial class EpisodeListAutomationPeer(EpisodeList owner) : FrameworkElementAutomationPeer(owner)
{
	protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

	protected override string GetClassNameCore() => nameof(EpisodeList);

	protected override bool IsControlElementCore() => true;

	protected override bool IsContentElementCore() => true;

	protected override bool IsKeyboardFocusableCore() => false;
}
