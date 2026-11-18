using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FluentFin.Themes.Editorial.Controls;

/// <summary>Editorial's season grid (see EditorialEpisodeGrid.xaml).</summary>
public sealed partial class EditorialEpisodeGrid : UserControl
{
	public static readonly DependencyProperty EpisodesProperty =
		DependencyProperty.Register(nameof(Episodes), typeof(IReadOnlyList<BaseItemDto>), typeof(EditorialEpisodeGrid), new PropertyMetadata(null, (d, _) => ((EditorialEpisodeGrid)d).Show()));

	public static readonly DependencyProperty TitleProperty =
		DependencyProperty.Register(nameof(Title), typeof(string), typeof(EditorialEpisodeGrid), new PropertyMetadata("", (d, _) => ((EditorialEpisodeGrid)d).ShowTitle()));

	private bool _hideWatched;
	private bool _newestFirst;
	private IReadOnlyList<BaseItemDto> _shown = [];

	public EditorialEpisodeGrid()
	{
		InitializeComponent();
		Repeater.ElementPrepared += (_, e) =>
		{
			AutomationProperties.SetPositionInSet(e.Element, e.Index + 1);
			AutomationProperties.SetSizeOfSet(e.Element, _shown.Count);
		};
		List.GettingFocus += List_GettingFocus;
		List.SizeChanged += (_, e) => Fit(e.NewSize.Width);
	}

	/// <summary>The season's episodes, in order.</summary>
	public IReadOnlyList<BaseItemDto>? Episodes
	{
		get => GetValue(EpisodesProperty) as IReadOnlyList<BaseItemDto>;
		set => SetValue(EpisodesProperty, value);
	}

	/// <summary>"Season 1".</summary>
	public string Title
	{
		get => (string)GetValue(TitleProperty);
		set => SetValue(TitleProperty, value);
	}

	private void ShowTitle()
	{
		TitleText.Text = Title;
		AutomationProperties.SetName(List, string.IsNullOrEmpty(Title) ? "Episodes" : $"Episodes of {Title}");
	}

	private void Show()
	{
		var all = Episodes ?? [];
		_shown = EditorialSeries.Shown(all, _hideWatched, _newestFirst);
		Repeater.ItemsSource = _shown;
		CaptionText.Text = EditorialSeries.SeasonCaption(all);
		EmptyText.Visibility = all.Count > 0 && _shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		HideWatchedButton.Visibility = all.Any(e => e.UserData?.Played == true) || _hideWatched ? Visibility.Visible : Visibility.Collapsed;
		OrderButton.Visibility = all.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
		Visibility = all.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
	}

	/// <summary>Four across from 1500 px of grid, three from 1000, else two; the stills share the width.</summary>
	private void Fit(double width)
	{
		if (width <= 0)
		{
			return;
		}

		var (columns, spacing) = EditorialEpisodeColumns.For(width);
		GridLayout.Columns = columns;
		GridLayout.ColumnSpacing = spacing;
	}

	private void HideWatchedButton_Click(object sender, RoutedEventArgs e)
	{
		_hideWatched = !_hideWatched;
		HideWatchedButton.Content = _hideWatched ? "Show watched" : "Hide watched";
		AutomationProperties.SetName(HideWatchedButton, _hideWatched ? "Show watched episodes" : "Hide watched episodes");
		Show();
	}

	private void OrderButton_Click(object sender, RoutedEventArgs e)
	{
		_newestFirst = !_newestFirst;
		OrderText.Text = _newestFirst ? "Newest first" : "Oldest first";
		AutomationProperties.SetName(OrderButton, _newestFirst ? "Order: newest first, switch to oldest first" : "Order: oldest first, switch to newest first");
		Show();
	}

	/// <summary>The keyboard comes into the grid: to the first episode not watched (the one to watch), not the grid's first.</summary>
	private void List_GettingFocus(UIElement sender, GettingFocusEventArgs args)
	{
		if (args.FocusState != FocusState.Keyboard || _shown.Count == 0 || args.OldFocusedElement is DependencyObject old && IsInside(old, List))
		{
			return;
		}

		var index = EditorialSeries.FocusIndex(_shown);
		if (Repeater.GetOrCreateElement(index) is UIElement target && !ReferenceEquals(target, args.NewFocusedElement))
		{
			args.TrySetNewFocusedElement(target);
		}
	}

	/// <summary>Gives the keyboard to the episode to watch (a season was just picked with the keyboard).</summary>
	public bool FocusFirst()
	{
		if (_shown.Count == 0)
		{
			return false;
		}

		Repeater.UpdateLayout();
		return Repeater.GetOrCreateElement(EditorialSeries.FocusIndex(_shown)) is Control still && still.Focus(FocusState.Keyboard);
	}

	private static bool IsInside(DependencyObject element, DependencyObject ancestor)
	{
		for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (ReferenceEquals(node, ancestor))
			{
				return true;
			}
		}

		return false;
	}
}
