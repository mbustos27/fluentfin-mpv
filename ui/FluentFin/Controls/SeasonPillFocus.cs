using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FluentFin.Controls;

/// <summary>
/// Nixie 1.4.1: a series page's season pills are an ItemsControl made anew for each season shown (Classic's, Drive-in's): the
/// pill pressed with the keyboard is gone with the old list, and the keyboard fell to the next thing in the page ("Season 2
/// details"). The pill of the season now shown gets it back, the way the pressed one had it (a ring only for the keyboard).
/// </summary>
internal static class SeasonPillFocus
{
	public static void Keep(Page page, SeriesViewModel viewModel, ItemsControl picker)
	{
		var had = FocusState.Unfocused;
		viewModel.PropertyChanging += (_, e) =>
		{
			if (e.PropertyName == nameof(SeriesViewModel.SeasonPills))
			{
				had = FocusedIn(page, picker);
			}
		};
		viewModel.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(SeriesViewModel.SeasonPills) && had is not FocusState.Unfocused)
			{
				var state = had;
				had = FocusState.Unfocused;
				page.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => FocusCurrent(viewModel, picker, state));
			}
		};
	}

	/// <summary>How the pill with the keyboard got it, or Unfocused when no pill has it.</summary>
	private static FocusState FocusedIn(Page page, ItemsControl picker)
	{
		if (page.XamlRoot is not { } root || FocusManager.GetFocusedElement(root) is not Control focused)
		{
			return FocusState.Unfocused;
		}

		for (DependencyObject? node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (ReferenceEquals(node, picker))
			{
				return focused.FocusState is FocusState.Unfocused ? FocusState.Programmatic : focused.FocusState;
			}
		}

		return FocusState.Unfocused;
	}

	private static void FocusCurrent(SeriesViewModel viewModel, ItemsControl picker, FocusState state)
	{
		var index = viewModel.SeasonPills.FindIndex(p => p.IsCurrent);
		picker.UpdateLayout();
		if (index >= 0 && picker.ContainerFromIndex(index) is DependencyObject container
			&& VisualTreeHelper.GetChildrenCount(container) > 0 && VisualTreeHelper.GetChild(container, 0) is Control pill)
		{
			pill.Focus(state);
		}
	}
}
