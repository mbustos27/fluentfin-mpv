using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace FluentFin.Views;

/// <summary>Seaglass 1.1.4: the search results page (<see cref="SearchResultsViewModel"/>), opened by Enter in the title bar's search.</summary>
public sealed partial class SearchResultsPage : Page
{
	public SearchResultsViewModel ViewModel { get; } = App.GetService<SearchResultsViewModel>();

	public SearchResultsPage()
	{
		InitializeComponent();
	}

	/// <summary>
	/// Nixie 1.5: Left and Right (Home, End) move the keyboard between the chips; Space or Enter lights the one it is on. The
	/// arrows stop at either end (no wrap), and anything else goes on to the page.
	/// </summary>
	private void KindChips_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Key is not (VirtualKey.Left or VirtualKey.Right or VirtualKey.Home or VirtualKey.End))
		{
			return;
		}

		var chips = Descendants<RadioButton>(KindChips).ToList();
		var at = chips.FindIndex(c => c.FocusState != FocusState.Unfocused);
		if (chips.Count == 0 || at < 0)
		{
			return;
		}

		var to = e.Key switch
		{
			VirtualKey.Left => Math.Max(0, at - 1),
			VirtualKey.Right => Math.Min(chips.Count - 1, at + 1),
			VirtualKey.Home => 0,
			_ => chips.Count - 1,
		};
		e.Handled = true;
		if (to != at)
		{
			chips[to].Focus(FocusState.Keyboard);
		}
	}

	private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
	{
		for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
		{
			var child = VisualTreeHelper.GetChild(root, i);
			if (child is T match)
			{
				yield return match;
			}

			foreach (var inner in Descendants<T>(child))
			{
				yield return inner;
			}
		}
	}

	/// <summary>Nixie 1.5: a line of the top result's details shows only with words in it.</summary>
	public Microsoft.UI.Xaml.Visibility Shows(string? text) => string.IsNullOrEmpty(text) ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

	/// <summary>Nixie 1.5: the top result as its row's one card (none when there is no top result).</summary>
	public IReadOnlyList<BaseItemViewModel> One(BaseItemViewModel? item) => item is null ? [] : [item];
}
