using FluentFin.Core.ViewModels;
using FluentFin.Dialogs;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Views;

public sealed partial class UserPasswordEditorPage : Page
{
	public UserPasswordEditorViewModel ViewModel { get; } = App.GetService<UserPasswordEditorViewModel>();

	public UserPasswordEditorPage()
	{
		InitializeComponent();
		// Nixie 1.2.0 (admin): Remove password asks here, Cancel the default
		ViewModel.Confirm = DestructiveConfirm.AskInMainWindow;
	}

	private async void ChangePasswordClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
	{
		if (await ViewModel.ChangePassword(CurrentPassword.Password, NewPassword.Password, ConfirmPassword.Password))
		{
			CurrentPassword.Password = NewPassword.Password = ConfirmPassword.Password = "";
		}
	}

	private async void RemovePasswordClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => await ViewModel.RemovePassword();

	private void NoticeBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.Notice = null;
}
