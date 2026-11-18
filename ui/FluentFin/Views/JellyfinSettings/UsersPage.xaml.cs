using FluentFin.Core.ViewModels;
using FluentFin.Dialogs;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Views.JellyfinSettings;

public sealed partial class UsersPage : Page
{
	public UsersViewModel ViewModel { get; } = App.GetService<UsersViewModel>();

	public UsersPage()
	{
		InitializeComponent();
		// Nixie 1.2.0 (admin): Delete user asks here, Cancel the default
		ViewModel.Confirm = DestructiveConfirm.AskInMainWindow;
	}

	/// <summary>D27: the card's More button says which user.</summary>
	public static string MoreName(string? user) => string.IsNullOrWhiteSpace(user) ? "More options" : $"More options for {user}";

	private void NoticeBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.Notice = null;

	private async void DeleteUser_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
	{
		if (sender is MenuFlyoutItem { CommandParameter: UserDto user })
		{
			await ViewModel.DeleteUserCommand.ExecuteAsync(user);
		}
	}

	private void MenuFlyoutItem_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
	{
		if (ViewModel is null)
		{
			return;
		}

		if (sender is not MenuFlyoutItem item)
		{
			return;
		}

		if (item.CommandParameter is not UserDto user)
		{
			return;
		}

		if (item.Tag is not UserEditorSection section)
		{
			return;
		}

		ViewModel.Navigate(user, section);
	}
}
