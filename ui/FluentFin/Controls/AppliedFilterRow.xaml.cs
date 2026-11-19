using System.ComponentModel;
using CommunityToolkit.WinUI;
using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Controls;

/// <summary>Nixie 1.4.0: the applied-filter row (see AppliedFilterRow.xaml); it shows itself only while a filter is on.</summary>
public sealed partial class AppliedFilterRow : UserControl
{
	[GeneratedDependencyProperty]
	public partial LibraryViewModel? ViewModel { get; set; }

	public AppliedFilterRow()
	{
		InitializeComponent();
		Visibility = Visibility.Collapsed;
	}

	private LibraryViewModel? _watched;

	partial void OnViewModelChanged(LibraryViewModel? newValue)
	{
		if (_watched is not null)
		{
			_watched.PropertyChanged -= ViewModel_PropertyChanged;
		}

		_watched = newValue;
		if (newValue is not null)
		{
			newValue.PropertyChanged += ViewModel_PropertyChanged;
		}

		Bindings.Update();
		Show();
	}

	private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		switch (e.PropertyName)
		{
			case nameof(LibraryViewModel.HasAppliedFilters) or nameof(LibraryViewModel.AppliedFilters):
				Show();
				break;
			case nameof(LibraryViewModel.ShowingText):
				// the count line is a polite live region: Narrator hears the new count
				if (Visibility == Visibility.Visible && FrameworkElementAutomationPeer.FromElement(ShowingLine) is { } peer)
				{
					peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
				}

				break;
		}
	}

	private void Show() => Visibility = ViewModel?.HasAppliedFilters == true ? Visibility.Visible : Visibility.Collapsed;

	private void Chip_Click(object sender, RoutedEventArgs e)
	{
		if (sender is Control { Tag: AppliedFilter filter } chip && ViewModel is { } vm)
		{
			// the focus goes to the next chip (or Clear all) rather than nowhere, the way it came (a ring for the keyboard)
			var index = vm.AppliedFilters.ToList().IndexOf(filter);
			var state = chip.FocusState is FocusState.Keyboard ? FocusState.Keyboard : FocusState.Programmatic;
			vm.RemoveFilter(filter);
			DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => FocusAfterRemove(index, state));
		}
	}

	private void FocusAfterRemove(int index, FocusState state)
	{
		if (Visibility != Visibility.Visible)
		{
			return; // the last filter is off: the row went; the page keeps the focus where WinUI puts it
		}

		var count = ViewModel?.AppliedFilters.Count ?? 0;
		if (count > 0 && Chips.ContainerFromIndex(Math.Min(index, count - 1)) is ContentPresenter { } presenter
			&& Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(presenter) > 0
			&& Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(presenter, 0) is Control chip)
		{
			chip.Focus(state);
			return;
		}

		ClearAllButton.Focus(state);
	}
}
