using CommunityToolkit.Mvvm.Input;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using FluentFin.ViewModels;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core;

public partial class GlobalCommands(INavigationServiceCore navigationService,
									IJellyfinClient jellyfinClient,
									INavigationViewServiceCore navigationViewService,
									ISettings settings)
{
	[RelayCommand]
	public Task PlayDto(BaseItemDto dto) => Play(dto, null);

	/// <param name="streamsPicked">fluentfin-mpv: the user changed a stream picker on the item page (only the mpv player uses it).</param>
	/// <param name="fromBeginning">fluentfin-mpv: "Play from beginning": start at 0 rather than at the item's resume point.</param>
	public async Task Play(BaseItemDto dto, PlaybackSelection? selection, bool streamsPicked = true, bool fromBeginning = false)
	{
		// fluentfin-mpv (startup-perf): the play timeline in the log counts from here (MpvPlayTimeline)
		PerfLog.PlayPressed();
		// SyncPlay is not supported with the mpv player (docs/BRIDGE.md §6): play here even if a group was joined before
		if (SessionInfo.GroupId is not null && settings.MediaPlayer != MediaPlayerType.Mpv)
		{
			var ids = await GetItemIds(dto);
			var info = await jellyfinClient.GetItem(ids[0]!.Value);

			var request = new PlayRequestDto
			{
				PlayingItemPosition = 0,
				PlayingQueue = ids,
				StartPositionTicks = fromBeginning ? 0 : info?.UserData?.PlaybackPositionTicks ?? 0,
			};
			await jellyfinClient.SignalNewPlaylist(request);
		}
		else if (!string.IsNullOrEmpty(SessionInfo.RemoteSessionId) && SessionInfo.SessionId != SessionInfo.RemoteSessionId)
		{
			await jellyfinClient.PlayOnSession(SessionInfo.RemoteSessionId, await GetItemIds(dto));
		}
		else
		{
			// fluentfin-mpv: with the mpv player only a pick the user made is sent (docs/BRIDGE.md §4)
			if (settings.MediaPlayer == MediaPlayerType.Mpv)
			{
				selection = Mpv.MpvTrackPick.ForPlay(dto, selection, streamsPicked);
			}

			navigationService.NavigateTo("FluentFin.ViewModels.VideoPlayerViewModel", selection is null && !fromBeginning ? dto : new PlaybackRequest(dto, selection, fromBeginning));
		}
	}

	[RelayCommand]
	public async Task ResetWatchProgress(Guid id)
	{
		await jellyfinClient.ResetProgress(id);
	}

	// 1.2.0: through ItemUserData, which puts the server's answer into the item's copy (so the next toggle goes the
	// other way and the menu says the right thing) and tells every card, hero and row; before, a second click sent the first
	// one's request again. The More menu uses these; a toggle button sends the state it shows (SetWatched, SetFavorite).
	[RelayCommand]
	public Task ToggleWatched(BaseItemDto dto) => SetWatched(dto, !(dto.UserData?.Played ?? false));

	[RelayCommand]
	public Task ToggleFavorite(BaseItemDto dto) => SetFavorite(dto, !(dto.UserData?.IsFavorite ?? false));

	/// <summary>1.2.0: a Watched toggle: the state it shows after the click (two quick clicks end where the second one left it).</summary>
	public Task<bool> SetWatched(BaseItemDto dto, bool played) => ItemUserData.SetPlayedAsync(jellyfinClient, dto, played);

	/// <summary>1.2.0: a Favourite toggle: the state it shows after the click.</summary>
	public Task<bool> SetFavorite(BaseItemDto dto, bool favorite) => ItemUserData.SetFavoriteAsync(jellyfinClient, dto, favorite);

	/// <summary>
	/// Nixie 1.4.0 (the bug hunt's m5, the rest of the UX review's X2): an item Nixie has no page for was opened (a song, a photo,
	/// a book, a person): what to tell the user (the app shows it in a message). Nothing is opened silently any more.
	/// </summary>
	public event EventHandler<(string Title, string Message)>? CannotOpen;

	/// <summary>Where <see cref="DisplayDto"/> takes an item of each type: its page's view model, or null (<see cref="CannotOpen"/>).</summary>
	public static Type? PageFor(BaseItemDto_Type? type) => type switch
	{
		BaseItemDto_Type.Movie => typeof(MovieViewModel),
		// a trailer: the item page, whose Play plays it alone (MpvQueue). A home video or a music video has no player queue yet
		// (MpvQueue answers nothing for them): it says so rather than opening a page whose Play would do nothing
		BaseItemDto_Type.Trailer => typeof(MovieViewModel),
		BaseItemDto_Type.Series => typeof(SeriesViewModel),
		BaseItemDto_Type.Season => typeof(SeasonViewModel),
		BaseItemDto_Type.Episode => typeof(EpisodeViewModel),
		// Nixie 1.4.0 (the UX review's plan item 1, X1): a box set opens the shared Collection page, in its own order; a
		// playlist too, as an ordered list (1.3.1 opened both as the library page); an album as the list of what it holds
		BaseItemDto_Type.BoxSet or BaseItemDto_Type.Playlist or BaseItemDto_Type.MusicAlbum or BaseItemDto_Type.PhotoAlbum => typeof(CollectionViewModel),
		BaseItemDto_Type.Folder or BaseItemDto_Type.CollectionFolder or BaseItemDto_Type.UserView or BaseItemDto_Type.AggregateFolder
			or BaseItemDto_Type.ManualPlaylistsFolder or BaseItemDto_Type.PlaylistsFolder or BaseItemDto_Type.ChannelFolderItem
			or BaseItemDto_Type.Channel or BaseItemDto_Type.BasePluginFolder => typeof(LibraryViewModel),
		_ => null,
	};

	/// <summary>The message for an item with no page: "Nixie can't open Song name here".</summary>
	public static (string Title, string Message) CannotOpenText(BaseItemDto dto) =>
		($"{AppIdentity.Name} can't open {(string.IsNullOrWhiteSpace(dto.Name) ? "this item" : dto.Name.Trim())} here",
		 $"{AppIdentity.Name} has pages for films, shows, collections and playlists. Open this one in Jellyfin's web app on {LoadMessages.ServerName()}.");

	[RelayCommand]
	public void DisplayDto(BaseItemDto dto)
	{
		if (PageFor(dto.Type) is { } page)
		{
			navigationService.NavigateTo(page.FullName!, dto);
			return;
		}

		CannotOpen?.Invoke(this, CannotOpenText(dto));
	}

	[RelayCommand]
	public void NavigateToSegmentsEditor(BaseItemDto dto)
	{
		navigationService.NavigateTo("FluentFin.ViewModels.MediaSegmentsEditorViewModel", dto);
	}

	[RelayCommand]
	public async Task DeleteItem(BaseItemDto dto)
	{
		await jellyfinClient.DeleteItem(dto);
	}

	[RelayCommand]
	private void PinToSideBar(BaseItemDto library)
	{
		if (library is not { Id: not null, CollectionType: not null })
		{
			return;
		}

		var folder = new CustomNavigationViewItem
		{
			Key = typeof(LibraryViewModel).FullName!,
			Name = library.Name!,
			Parameter = library.Id.Value,
			Glyph = library.CollectionType.Value switch
			{
				BaseItemDto_CollectionType.Tvshows => "\uE7F4",
				BaseItemDto_CollectionType.Movies => "\uE8B2",
				_ => null
			},
			Commands = [new CommandModel() { Name = "Unpin", DisplayName = "Unpin", Glyph = "\uE77A" }],
			Persistent = true,
		};

		navigationViewService.AddNavigationItem(folder);
		navigationViewService.SaveCustomViews();
	}

	[RelayCommand]
	private void UnPinFromSideBar(CustomNavigationViewItem item)
	{
		navigationViewService.RemoveNavigationItem(item);
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
