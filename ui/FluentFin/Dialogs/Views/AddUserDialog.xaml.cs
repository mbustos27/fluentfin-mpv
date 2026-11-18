using FluentFin.Dialogs.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using ReactiveUI;

namespace FluentFin.Dialogs.Views;

public sealed partial class AddUserDialog : IViewFor<AddUserViewModel>
{
	public AddUserViewModel? ViewModel { get; set; } = App.GetService<AddUserViewModel>();

	object? IViewFor.ViewModel { get => ViewModel; set => ViewModel = (AddUserViewModel?)value; }

	public AddUserDialog()
	{
		InitializeComponent();
	}

	private void PasswordBox_PasswordChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
	{
		if (ViewModel is null)
		{
			return;
		}

		ViewModel.Password = PasswordBox.Password;
	}

	private void ListView_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
	{
		if (ViewModel is null)
		{
			return;
		}

		// Nixie 1.2.0 (admin): SelectedItems is a list of objects, never an IEnumerable<BaseItemDto>: the picked libraries were never kept
		ViewModel.SelectedItems = [.. ListView.SelectedItems.OfType<BaseItemDto>()];
	}
}
