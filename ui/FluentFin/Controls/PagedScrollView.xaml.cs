using System.Collections.ObjectModel;
using CommunityToolkit.WinUI;
using FluentFin.Contracts.Services;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Controls;

public sealed partial class PagedScrollView : UserControl
{

	[GeneratedDependencyProperty(DefaultValue = "")]
	public partial string Header { get; set; }

	[GeneratedDependencyProperty(DefaultValueCallback = nameof(Empty))]
	public partial ObservableCollection<BaseItemViewModel> Items { get; set; }

	[GeneratedDependencyProperty]
	public partial IJellyfinClient? JellyfinClient { get; set; }

	public PagedScrollView()
	{
		InitializeComponent();

		// fluentfin-mpv: the title opens the row's library, so it is a keyboard stop only when there is one (ux-review A1)
		RegisterPropertyChangedCallback(TagProperty, (_, _) => HeaderButton.IsTabStop = Tag is not null);
		HeaderButton.IsTabStop = Tag is not null;
	}

	private bool _pointerIn;
	private bool _focusIn;

	/// <summary>Seaglass: the scroll pills show while the pointer or the focus is in the row (and stay reachable by Tab).</summary>
	private void UpdatePills()
	{
		var opacity = _pointerIn || _focusIn ? 1.0 : 0.0;
		ScrollLeftBtn.Opacity = opacity;
		ScrollRightBtn.Opacity = opacity;
	}

	private void RowRoot_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) { _pointerIn = true; UpdatePills(); }

	private void RowRoot_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) { _pointerIn = false; UpdatePills(); }

	private void RowRoot_GotFocus(object sender, RoutedEventArgs e) { _focusIn = true; UpdatePills(); }

	private void RowRoot_LostFocus(object sender, RoutedEventArgs e)
	{
		var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
		_focusIn = false;
		for (var node = focused; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
		{
			if (ReferenceEquals(node, RowRoot))
			{
				_focusIn = true;
				break;
			}
		}
		UpdatePills();
	}

	private void HeaderButton_Click(object sender, RoutedEventArgs e)
	{
		if (Tag is null)
		{
			return;
		}

		App.GetService<INavigationService>().NavigateTo<LibraryViewModel>(Tag);
	}

	/// <summary>
	/// fluentfin-mpv: a card that gets keyboard focus (Tab, or the arrow keys along the row) is scrolled into view; the row only
	/// scrolls through its buttons (HorizontalScrollMode is Disabled), so it does not follow the focus by itself.
	/// </summary>
	private void Scroller_GotFocus(object sender, RoutedEventArgs e)
	{
		if (e.OriginalSource is not FrameworkElement focused || ReferenceEquals(focused, Scroller))
		{
			return;
		}

		// a card's own button: the whole card
		FrameworkElement element = focused;
		for (DependencyObject? parent = focused; parent is not null && !ReferenceEquals(parent, ContentGrid); parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent))
		{
			if (parent is BaseItemDtoCard card)
			{
				element = card;
				break;
			}
		}

		if (element.ActualWidth <= 0)
		{
			return;
		}

		var left = element.TransformToVisual(ContentGrid).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
		if (RowScroll.OffsetToShow(Scroller.HorizontalOffset, Scroller.ViewportWidth, left, element.ActualWidth) is { } offset)
		{
			Scroller.ChangeView(offset, null, null);
		}
	}

	private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		UpdateScrollButtonsVisibility();
	}

	private void Scroller_ViewChanging(object sender, ScrollViewerViewChangingEventArgs e)
	{
		if (e.FinalView.HorizontalOffset < 1)
		{
			Hide(ScrollLeftBtn, ScrollRightBtn);
		}
		else if (e.FinalView.HorizontalOffset > 1)
		{
			ScrollLeftBtn.Visibility = Visibility.Visible;
		}

		if (e.FinalView.HorizontalOffset > Scroller.ScrollableWidth - 1)
		{
			Hide(ScrollRightBtn, ScrollLeftBtn);
		}
		else if (e.FinalView.HorizontalOffset < Scroller.ScrollableWidth - 1)
		{
			ScrollRightBtn.Visibility = Visibility.Visible;
		}
	}

	// fluentfin-mpv: the focus stays on the arrow that was pressed, so Enter can be pressed again; it moves to the other arrow
	// only when the row's end hides this one (upstream moved it to the other arrow on every click)
	private void ScrollLeftBtn_Click(object sender, RoutedEventArgs e)
	{
		Scroller.ChangeView(Scroller.HorizontalOffset - Scroller.ViewportWidth, null, null);
	}

	private void ScrollRightBtn_Click(object sender, RoutedEventArgs e)
	{
		Scroller.ChangeView(Scroller.HorizontalOffset + Scroller.ViewportWidth, null, null);
	}

	private static void Hide(Button button, Button other)
	{
		if (button.Visibility == Visibility.Collapsed)
		{
			return;
		}

		var hadFocus = button.FocusState != FocusState.Unfocused;
		button.Visibility = Visibility.Collapsed;
		if (hadFocus && other.Visibility == Visibility.Visible)
		{
			other.Focus(FocusState.Programmatic);
		}
	}

	private void UpdateScrollButtonsVisibility()
	{
		if (Scroller.ScrollableWidth > 0)
		{
			if (Scroller.HorizontalOffset > 1)
			{
				ScrollLeftBtn.Visibility = Visibility.Visible;
			}
		}
		else
		{
			ScrollRightBtn.Visibility = Visibility.Collapsed;
		}
	}

	private static ObservableCollection<BaseItemViewModel> Empty() => [];

	private void TextBlock_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
	{
		if (Tag is null)
		{
			return;
		}

		var textBlock = (TextBlock)sender;
		textBlock.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
	}

	private void TextBlock_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
	{
		if (Tag is null)
		{
			return;
		}

		var textBlock = (TextBlock)sender;
		textBlock.TextDecorations = Windows.UI.Text.TextDecorations.None;
	}

}

/// <summary>fluentfin-mpv: how far a row scrolls to show an element that got focus (none when it is already in view).</summary>
internal static class RowScroll
{
	/// <returns>The horizontal offset that shows the element with the least scrolling, or null when it is in view.</returns>
	public static double? OffsetToShow(double offset, double viewport, double left, double width)
	{
		var right = left + width;
		if (left < offset)
		{
			return Math.Max(0, left);
		}

		if (right > offset + viewport)
		{
			// wider than the view: its start
			return width >= viewport ? left : right - viewport;
		}

		return null;
	}
}
