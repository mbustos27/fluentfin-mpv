using CommunityToolkit.Mvvm.Input;
using FluentFin.Core.Contracts.Services;
using FluentFin.Dialogs.ViewModels;
using FluentFin.Services;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Dialogs;

public partial class DialogCommands(IContentDialogService dialogService)
{
	[RelayCommand]
	private async Task IdentifyDialog(BaseItemDto dto)
	{
		var vm = App.GetService<IdentifyViewModel>();
		await vm.Initialize(dto);

		var result = await dialogService.ShowDialog(vm, x =>
		{
			x.Closing += (_, e) =>
			{
				if (!vm.CanClose)
				{
					e.Cancel = true;
				}
			};
			x.CloseButtonClick += (_, _) => { vm.CanClose = true; };
			x.PrimaryButtonClick += (_, _) => { vm.CanClose = vm.ViewState == IdentifyViewModelState.Result; };
		});
	}

	[RelayCommand]
	private async Task EditMetadataDialog(BaseItemDto dto)
	{
		var vm = App.GetService<EditMetadataViewModel>();
		await vm.Initialize(dto);
		await dialogService.ShowDialog(vm, dialog => CloseOnCancelOrSaved(dialog, vm, vm.Save));
	}

	[RelayCommand]
	private async Task AddUserDialog()
	{
		var vm = App.GetService<AddUserViewModel>();
		await vm.Initialize();
		// Nixie 1.2.0 (admin, D46): closes only once the user was created (it closed on an empty form and created nothing)
		await dialogService.ShowDialog(vm, dialog => CloseOnCancelOrSaved(dialog, vm, vm.AddUser));
	}

	// Nixie 1.2.0 (admin, D81): Delete user asks in UsersViewModel.DeleteUser (DestructiveConfirm: "Delete user", Cancel the
	// default; it was an accent "Yes" as the Enter key, "Are you sure you wan to delete the user : {name}?")

	[RelayCommand]
	private async Task MediaInfoDialog(BaseItemDto dto) => await ShowBaseItemDialog<MediaInfoViewModel>(dto);

	[RelayCommand]
	private async Task EditSubtitlesDialog(BaseItemDto dto) => await ShowBaseItemDialog<EditSubtitlesViewModel>(dto);

	[RelayCommand]
	private async Task RefreshMetadataDialog(Guid id) => await ShowBaseItemDialog<RefreshMetadataViewModel>(id);


	[RelayCommand]
	private async Task EditImagesDialog(BaseItemDto dto)
	{
		var vm = App.GetService<EditImagesViewModel>();
		await vm.Initialize(dto);
		await dialogService.ShowDialog(vm, dialog => CloseOnlyOnCloseButtonClick(dialog, vm));
	}

	// Seaglass 1.1.3: no "Copy Download URL" (CopyUrlToClipboard): the URL carries the access token (api_key), and the
	// clipboard is readable by every app (Core\ItemMenu)

	[RelayCommand]
	private async Task ManageLibraryDialog(VirtualFolderInfo info) => await ManageLibrary(info);

	/// <summary>Nixie 1.2.0 (admin): the library's options; true when they were saved (the dialog closes only then).</summary>
	public async Task<bool> ManageLibrary(VirtualFolderInfo info)
	{
		var vm = App.GetService<ManageLibraryViewModel>();
		await vm.Initialize(info);
		return await dialogService.ShowDialog(vm, dialog => CloseOnCancelOrSaved(dialog, vm, vm.Save)) == ContentDialogResult.Primary;
	}

	[RelayCommand]
	private async Task CreateLibraryDialog() => await CreateLibrary();

	/// <summary>Nixie 1.2.0 (admin, D18 / D69): Add library; true when the server created it (it never could: Save returned at once).</summary>
	public async Task<bool> CreateLibrary()
	{
		var vm = App.GetService<ManageLibraryViewModel>();
		await vm.Initialize();
		return await dialogService.ShowDialog(vm, dialog => CloseOnCancelOrSaved(dialog, vm, vm.Save)) == ContentDialogResult.Primary;
	}

	[RelayCommand]
	private async Task QuickConnectAuth()
	{
		var vm = App.GetService<QuickConnectViewModel>();
		await dialogService.ShowDialog(vm, dialog => CloseOnlyOnCloseButtonClick(dialog, vm));
	}

	[RelayCommand]
	private async Task PlayOnSession(BaseItemDto dto)
	{
		var vm = App.GetService<SessionPickerViewModel>();
		await vm.Initialize(dto);
		await dialogService.ShowDialog(vm, dialog => CloseOnlyOnCloseAndPrimaryButtonClick(dialog, vm));
	}

	[RelayCommand]
	private async Task ChangeDefaultSession()
	{
		var vm = App.GetService<SessionPickerViewModel>();
		await vm.Initialize();
		await dialogService.ShowDialog(vm, dialog => CloseOnlyOnCloseAndPrimaryButtonClick(dialog, vm));
	}

	[RelayCommand]
	private async Task SelectSyncPlayGroup()
	{
		if (App.GetService<Core.Settings.ISettings>().MediaPlayer == MediaPlayerType.Mpv)
		{
			dialogService.Growl("SyncPlay", "SyncPlay is not available with the MPV player. Choose another player in Settings to use it.", TimeSpan.FromSeconds(6));
			return;
		}

		var vm = App.GetService<SyncPlayGroupPickerViewModel>();
		await vm.Initialize();
		await dialogService.ShowDialog(vm, dialog => CloseOnAllButtonClick(dialog, vm));
	}


	// Nixie 1.2.0 (admin, D83): the library's Delete asks in LibrariesSettingsViewModel.DeleteLibrary (DestructiveConfirm:
	// "Delete library", Cancel the default; it was an accent "Yes" as the Enter key)


	private async Task ShowBaseItemDialog<TViewModel>(BaseItemDto dto)
		where TViewModel : class, IBaseItemDialogViewModel
	{
		var vm = App.GetService<TViewModel>();
		await vm.Initialize(dto);
		await dialogService.ShowDialog(vm, null!);
	}

	private async Task ShowBaseItemDialog<TViewModel>(Guid? id)
	where TViewModel : class, IItemDialogViewModel
	{
		var vm = App.GetService<TViewModel>();
		await vm.Initialize(id);
		await dialogService.ShowDialog(vm, null!);
	}

	private static void CloseOnlyOnCloseButtonClick(ContentDialog dialog, IHandleClose vm)
	{
		dialog.Closing += (_, e) =>
		{
			if (!vm.CanClose)
			{
				e.Cancel = true;
			}
		};
		dialog.CloseButtonClick += (_, _) => { vm.CanClose = true; };
	}
	private static void CloseOnlyOnCloseAndPrimaryButtonClick(ContentDialog dialog, IHandleClose vm)
	{
		dialog.Closing += (_, e) =>
		{
			if (!vm.CanClose)
			{
				e.Cancel = true;
			}
		};
		dialog.CloseButtonClick += (_, _) => { vm.CanClose = true; };
		dialog.PrimaryButtonClick += (_, _) => { vm.CanClose = true; };
	}

	/// <summary>
	/// Nixie 1.2.0 (admin): the dialog closes on Cancel, and on its primary (Save) only once <paramref name="save"/> says the
	/// server took it; otherwise it stays open with the edits, and the view model says why. Escape does not close it.
	/// </summary>
	private static void CloseOnCancelOrSaved(ContentDialog dialog, IHandleClose vm, Func<Task<bool>> save)
	{
		dialog.Closing += (_, e) =>
		{
			if (!vm.CanClose)
			{
				e.Cancel = true;
			}
		};
		dialog.CloseButtonClick += (_, _) => { vm.CanClose = true; };
		dialog.PrimaryButtonClick += async (_, e) =>
		{
			var deferral = e.GetDeferral();
			try
			{
				vm.CanClose = await save();
				e.Cancel = !vm.CanClose;
			}
			finally
			{
				deferral.Complete();
			}
		};
	}

	private static void CloseOnAllButtonClick(ContentDialog dialog, IHandleClose vm)
	{
		dialog.Closing += (_, e) =>
		{
			if (!vm.CanClose)
			{
				e.Cancel = true;
			}
		};
		dialog.CloseButtonClick += (_, _) => { vm.CanClose = true; };
		dialog.PrimaryButtonClick += (_, _) => { vm.CanClose = true; };
		dialog.SecondaryButtonClick += (_, _) => { vm.CanClose = true; };
	}
}
