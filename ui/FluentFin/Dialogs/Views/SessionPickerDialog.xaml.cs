using FluentFin.Dialogs.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using ReactiveUI;

namespace FluentFin.Dialogs.Views;

public sealed partial class SessionPickerDialog : IViewFor<SessionPickerViewModel>
{
	public SessionPickerViewModel? ViewModel { get; set; } = App.GetService<SessionPickerViewModel>();

	object? IViewFor.ViewModel { get => ViewModel; set => ViewModel = (SessionPickerViewModel?)value; }

	public SessionPickerDialog()
	{
		InitializeComponent();
	}

	// Nixie 1.2.0 (admin): each device reads as itself to Narrator (it read "Jellyfin.Sdk.Generated.Models.SessionInfoDto")
	private void ItemsView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args) =>
		AutomationProperties.SetName(args.ItemContainer, SessionPickerViewModel.Describe(args.Item as SessionInfoDto));
}
