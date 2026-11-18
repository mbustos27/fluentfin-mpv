using FluentFin.Core.Contracts.Services;
using FluentFin.Core.ViewModels;
using FluentFin.Dialogs;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Views.JellyfinSettings;

public sealed partial class LibrariesSettingsPage : Page
{
	public LibrariesSettingsViewModel ViewModel { get; } = App.GetService<LibrariesSettingsViewModel>();

	public LibrariesSettingsPage()
	{
		InitializeComponent();
		// Nixie 1.2.0 (admin): Delete asks here (Cancel the default); Add library opens the dialog that creates one
		ViewModel.Confirm = DestructiveConfirm.AskInMainWindow;
		ViewModel.AddLibraryDialog = App.Dialogs.CreateLibrary;
	}

	public static string SingleOrCount(IList<string> values)
	{
		return values.Count == 1 ? values[0] : $"{values.Count} folders";
	}

	public static bool IsOpen(ServerNotice? notice) => notice is not null;

	/// <summary>D20: the card's More button says which library.</summary>
	public static string MoreName(string? library) => string.IsNullOrWhiteSpace(library) ? "More options" : $"More options for {library}";

	private void NoticeBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.Notice = null;

	private async void OnDelete(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
	{
		if (sender is MenuFlyoutItem { Tag: VirtualFolderInfo info })
		{
			await ViewModel.DeleteLibraryCommand.ExecuteAsync(info);
		}
	}

	private async void OnManage(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
	{
		if (sender is MenuFlyoutItem { Tag: VirtualFolderInfo info } && await App.Dialogs.ManageLibrary(info))
		{
			await ViewModel.Reload();
		}
	}

	private async void OnRename(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
	{
		if (sender is not MenuFlyoutItem item)
		{
			return;
		}

		if (item.Tag is not VirtualFolderInfo info)
		{
			return;
		}

		var newName = await App.GetKeyedService<IUserInput<string>>(UserInputs.LibraryNewName).GetValue();

		if (!string.IsNullOrWhiteSpace(newName) && newName.Trim() != info.Name)
		{
			await App.GetService<IJellyfinClient>().RenameLibrary(info.Name ?? "", newName.Trim());
			await ViewModel.Reload();
		}
	}
}
