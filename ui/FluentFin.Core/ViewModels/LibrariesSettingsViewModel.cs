using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicData;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Admin;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

/// <remarks>
/// Nixie 1.2.0 (admin, button review D17 / D18 / D23 / D83): "Scan all libraries" scans (it was "Scall All Libraries" and did
/// nothing); "Add library" creates one and the list shows it; Delete asks first, naming the library, with Cancel the default,
/// and the card goes only when the server deleted it.
/// </remarks>
public partial class LibrariesSettingsViewModel(IJellyfinClient jellyfinClient) : ObservableObject, INavigationAware
{
	public ObservableCollection<VirtualFolderInfo> VirtualFolders { get; } = [];

	/// <summary>Asks before a destructive action; true only when the user confirmed. Unset (nobody to ask), nothing happens.</summary>
	public Func<ConfirmPrompt, Task<bool>>? Confirm { get; set; }

	/// <summary>The app's Add library dialog; true when it created a library.</summary>
	public Func<Task<bool>>? AddLibraryDialog { get; set; }

	/// <summary>What the last action here did, when there is something to say.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasNotice))]
	public partial ServerNotice? Notice { get; set; }

	/// <summary>The notice bar opens from this bool (x:Bind does not call a function whose argument is null, so a bar opened through one never closed).</summary>
	public bool HasNotice => Notice is not null;

	public Task OnNavigatedFrom() => Task.CompletedTask;

	public async Task OnNavigatedTo(object parameter) => await Reload();

	public async Task Reload()
	{
		var folders = await jellyfinClient.GetVirtualFolders();
		VirtualFolders.Clear();
		VirtualFolders.AddRange(folders);
	}

	[RelayCommand]
	private async Task ScanAll()
	{
		Notice = await jellyfinClient.ScanAllLibraries()
			? new ServerNotice(ServerPowerState.None, "Scan started", "The server is looking for new and changed files in every library.", NoticeKind.Success)
			: new ServerNotice(ServerPowerState.Failed, "The scan didn't start", Admin.ServerPower.NotTaken("The server didn't accept the request."), NoticeKind.Error);
	}

	[RelayCommand]
	private async Task AddLibrary()
	{
		if (AddLibraryDialog is not { } show || !await show())
		{
			return;
		}

		Notice = null;
		await Reload();
	}

	[RelayCommand]
	private async Task DeleteLibrary(VirtualFolderInfo? library)
	{
		if (library is not { Name: { Length: > 0 } name } || Confirm is not { } confirm || !await confirm(AdminPrompts.DeleteLibrary(name)))
		{
			return;
		}

		if (await jellyfinClient.DeleteLibrary(name))
		{
			VirtualFolders.Remove(library);
			Notice = null;
		}
		else
		{
			Notice = new ServerNotice(ServerPowerState.Failed, $"{name} wasn't deleted", Admin.ServerPower.NotTaken("The server didn't accept the request. Nothing changed."), NoticeKind.Error);
		}
	}
}
