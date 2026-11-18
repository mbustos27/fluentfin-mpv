using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using FluentFin.ViewModels;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Dialogs.ViewModels;

/// <remarks>
/// Nixie 1.2.0 (admin, button review A71 / A72, worst offender 5): "Select" was always enabled and threw when nothing was
/// selected. Now the primary ("Play there", or "Use this device" when choosing the device to control) is enabled only once a
/// device is picked, and an empty list says so. This app's own session is not offered.
/// </remarks>
public partial class SessionPickerViewModel(IJellyfinClient jellyfinClient) : ObservableObject, IHandleClose
{
	private IEnumerable<Guid?> _itemIds = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsEmpty))]
	public partial List<SessionInfoDto> Sessions { get; set; } = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(CanPick))]
	public partial SessionInfoDto? SelectedSesion { get; set; }

	/// <summary>Playing an item there (true), or choosing the device to control (false).</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(PickText))]
	public partial bool PlaysItem { get; set; }

	/// <summary>The primary waits for a device.</summary>
	public bool CanPick => SelectedSesion is not null;

	public string PickText => PlaysItem ? "Play there" : "Use this device";

	/// <summary>A72: no other device to offer.</summary>
	public bool IsEmpty => Sessions.Count == 0;

	public bool CanClose { get; set; }

	public async Task Initialize(BaseItemDto dto)
	{
		PlaysItem = true;
		Sessions = await OtherSessions();
		_itemIds = await GetItemIds(dto);
	}

	public async Task Initialize()
	{
		PlaysItem = false;
		Sessions = await OtherSessions();
		SelectedSesion = Sessions.FirstOrDefault(x => x.Id == SessionInfo.RemoteSessionId);
	}

	private async Task<List<SessionInfoDto>> OtherSessions() =>
		[.. (await jellyfinClient.GetControllableSessions()).Where(x => string.IsNullOrEmpty(SessionInfo.SessionId) || x.Id != SessionInfo.SessionId)];

	/// <summary>Plays the item on the picked device, or makes it the one to control; nothing without a pick.</summary>
	[RelayCommand]
	public async Task PlayOnSession()
	{
		if (SelectedSesion is not { Id: { Length: > 0 } id })
		{
			return;
		}

		if (PlaysItem)
		{
			if (_itemIds.Any())
			{
				await jellyfinClient.PlayOnSession(id, _itemIds);
			}
		}
		else
		{
			SessionInfo.RemoteSessionId = id;
		}
	}

	/// <summary>How a device reads in the list and to Narrator: "Jellyfin Web on Living room TV, alice".</summary>
	public static string Describe(SessionInfoDto? session)
	{
		if (session is null)
		{
			return "";
		}

		var where = string.IsNullOrWhiteSpace(session.DeviceName) ? "" : $" on {session.DeviceName}";
		var who = string.IsNullOrWhiteSpace(session.UserName) ? "" : $", {session.UserName}";
		return $"{session.Client}{where}{who}";
	}

	private async Task<List<Guid?>> GetItemIds(BaseItemDto dto)
	{
		var playlist = dto.Type switch
		{
			BaseItemDto_Type.Movie => PlaylistViewModel.FromMovie(dto),
			BaseItemDto_Type.Episode => await PlaylistViewModel.FromEpisode(jellyfinClient, dto),
			BaseItemDto_Type.Series => await PlaylistViewModel.FromSeries(jellyfinClient, dto),
			BaseItemDto_Type.Season => await PlaylistViewModel.FromSeason(jellyfinClient, dto),
			_ => new PlaylistViewModel()
		};

		playlist.AutoSelect();

		if (playlist.SelectedItem is null)
		{
			return [];
		}

		var index = playlist.Items.IndexOf(playlist.SelectedItem);
		return [.. playlist.Items.Skip(index).Select(x => x.Dto.Id)];
	}
}
