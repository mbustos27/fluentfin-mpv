using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevWinUI;
using FluentFin.Core;
using FluentFin.Core.Admin;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Dialogs.ViewModels;

/// <remarks>
/// Nixie 1.2.0 (admin, button review D62): Delete subtitle removed the row and deleted on the server at once; it now asks first
/// (<see cref="Confirm"/>, a flyout at the button: Cancel has the focus), and the row goes only when the server deleted it.
/// </remarks>
public partial class EditSubtitlesViewModel(IJellyfinClient jellyfinClient) : ObservableObject, IHandleClose, IBaseItemDialogViewModel
{
	public bool CanClose { get; set; }

	/// <summary>Asks before a destructive action; true only when the user confirmed. Unset (nobody to ask), nothing happens.</summary>
	public Func<ConfirmPrompt, Task<bool>>? Confirm { get; set; }

	/// <summary>Why the last change did not go through; empty otherwise.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasError))]
	public partial string Error { get; set; } = "";

	/// <summary>The error bar opens from this bool (no x:Bind function on IsOpen).</summary>
	public bool HasError => !string.IsNullOrEmpty(Error);

	[ObservableProperty]
	public partial BaseItemDto? Item { get; set; }

	[ObservableProperty]
	public partial CultureInfo? SelectedCulture { get; set; }

	[ObservableProperty]
	public partial List<RemoteSubtitleInfo> RemoteSubtitles { get; set; } = [];

	public ObservableCollection<MediaStream> Subtitles { get; } = [];

	public List<CultureInfo> Cultures { get; } = [.. CultureInfo.GetCultures(CultureTypes.NeutralCultures)];

	public async Task Initialize(BaseItemDto item)
	{
		if (item.Id is not { } id)
		{
			return;
		}

		Item = await jellyfinClient.GetItem(id);

		if (Item is null)
		{
			return;
		}

		Subtitles.AddRange(Item.MediaStreams?.Where(x => x.Type == MediaStream_Type.Subtitle).ToList() ?? []);
		SelectedCulture = Cultures.FirstOrDefault(x => x.ThreeLetterISOLanguageName.Equals("eng", StringComparison.OrdinalIgnoreCase));
	}

	[RelayCommand]
	public async Task SearchSubtitles()
	{
		if (Item is null || SelectedCulture is null)
		{
			return;
		}

		RemoteSubtitles = await jellyfinClient.SearchSubtitles(Item, SelectedCulture);
	}

	[RelayCommand]
	public async Task DownloadSubtitle(RemoteSubtitleInfo info)
	{
		if (Item is null)
		{
			return;
		}

		await jellyfinClient.DownloadSubtitle(Item, info);
	}

	[RelayCommand]
	public async Task DeleteSubtitle(MediaStream? info)
	{
		if (Item is null || info is null || info.IsExternal != true)
		{
			return;
		}

		if (Confirm is not { } confirm || !await confirm(AdminPrompts.DeleteSubtitle(Item.Name, info.DisplayTitle)))
		{
			return;
		}

		Error = "";
		if (await jellyfinClient.DeleteSubtitle(Item, info))
		{
			Subtitles.Remove(info);
		}
		else
		{
			Error = "The server didn't delete the subtitle. Nothing changed.";
		}
	}
}
