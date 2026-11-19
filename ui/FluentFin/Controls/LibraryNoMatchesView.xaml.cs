using System.ComponentModel;
using CommunityToolkit.WinUI;
using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Controls;

/// <summary>Nixie 1.4.0: the no-matches state (see LibraryNoMatchesView.xaml); it shows itself only while nothing matches, and Narrator hears it.</summary>
public sealed partial class LibraryNoMatchesView : UserControl
{
	[GeneratedDependencyProperty]
	public partial LibraryViewModel? ViewModel { get; set; }

	public LibraryNoMatchesView()
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
		Show(announce: false);
	}

	private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(LibraryViewModel.NoMatches) or nameof(LibraryViewModel.NoMatchesText))
		{
			Show(announce: true);
		}
	}

	private void Show(bool announce)
	{
		var show = ViewModel?.NoMatches == true;
		Visibility = show ? Visibility.Visible : Visibility.Collapsed;
		if (!show || !announce || ViewModel is not { } vm)
		{
			return;
		}

		// Narrator hears it as it appears (the grid went blank under the focus)
		var peer = FrameworkElementAutomationPeer.FromElement(this) ?? FrameworkElementAutomationPeer.CreatePeerForElement(this);
		peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent,
			$"{vm.NoMatchesText}. {vm.NoMatchesMessage}", "LibraryNoMatches");
	}
}
