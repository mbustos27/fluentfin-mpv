using System.Collections.ObjectModel;
using System.Globalization;
using FluentFin.Core.Services;
using Jellyfin.Sdk.Generated.Items.Item.Refresh;
using Jellyfin.Sdk.Generated.Library.VirtualFolders;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Contracts.Services
{
	public interface IJellyfinClient
	{
		string BaseUrl { get; }

		Guid UserId { get; }

		Task Initialize(string baseUrl, AuthenticationResult authResult);

		Task<EndPointInfo?> EndpointInfo();

		Task<BaseItemDtoQueryResult?> GetContinueWatching();

		Task<BaseItemDtoQueryResult?> GetNextUp();

		Task<BaseItemDtoQueryResult?> GetNextUp(BaseItemDto dto);

		/// <summary>
		/// Every child of <paramref name="parent"/> in one request: for the small ones (a season's episodes, a series' seasons,
		/// a box set). A library is read a page at a time (<see cref="GetLibraryItems"/>, <see cref="FluentFin.Core.Services.LibraryPageReader"/>).
		/// </summary>
		Task<BaseItemDtoQueryResult?> GetItems(BaseItemDto parent, bool recursive = false);

		/// <summary>
		/// Nixie 1.4.1: one page of a library's titles, by sort name: <paramref name="limit"/> from <paramref name="startIndex"/>,
		/// with the fields the library page shows and filters by, and the library's size when <paramref name="withTotal"/>.
		/// Null when it failed or was cancelled (logged). A whole library in one request took over 100 s on a server with
		/// 25,842 movies and timed out, so a library is never asked for at once.
		/// </summary>
		Task<BaseItemDtoQueryResult?> GetLibraryItems(BaseItemDto library, int startIndex, int limit, bool withTotal = false, CancellationToken cancellationToken = default);

		Task ResetProgress(Guid id);

		/// <summary>
		/// Seaglass 1.1.4: sets the user's resume point of an item (<c>POST /UserItems/{id}/UserData</c>, the user-data
		/// endpoint any user may call; nothing else of the user's data changes). 0 takes the item out of Continue Watching,
		/// which is the server's list of items with a resume point; the old value puts it back. False when it failed (logged).
		/// </summary>
		Task<bool> SetResumePosition(Guid id, long positionTicks);

		Task<BaseItemDtoQueryResult?> GetMediaFolders();

		Task<List<FileSystemEntryInfo>> GetDirectoryContents(string path);

		Task<List<VirtualFolderInfo>> GetVirtualFolders();

		Task DeleteItem(BaseItemDto dto);

		/// <summary>Nixie 1.2.0 (admin): <c>POST /Library/VirtualFolders</c> (the folders in <c>options.PathInfos</c>); false when it failed (logged).</summary>
		Task<bool> CreateLibrary(string name, CollectionTypeOptions collectionType, LibraryOptions options);

		/// <summary>Nixie 1.2.0 (admin): false when it failed (logged).</summary>
		Task<bool> DeleteLibrary(string name);

		Task RenameLibrary(string name, string newName);

		Task<BaseItemDtoQueryResult?> GetSimilarItems(BaseItemDto dto);

		/// <summary>Nixie 1.3.1: a playlist's items in the playlist's order (/Playlists/{id}/Items), null on a failure.</summary>
		Task<BaseItemDtoQueryResult?> GetPlaylistItems(Guid playlistId);

		/// <summary>Nixie 1.4.0: the collections an item is part of (none on a server without the request).</summary>
		Task<IReadOnlyList<BaseItemDto>> GetItemCollections(Guid itemId);

		/// <summary>Nixie 1.3.1: the item's trailers on the server (/Items/{id}/LocalTrailers), empty when none or on a failure.</summary>
		Task<List<BaseItemDto>> GetLocalTrailers(Guid id);

		Task<BaseItemDtoQueryResult?> Search(string searchTerm);

		/// <summary>Seaglass 1.1.4: tells the server the selected player's profile and commands now (Settings > Player). Never throws.</summary>
		Task<bool> UpdateCapabilities();

		/// <summary>Seaglass 1.1.4: the matches of these types only (the search results page, one request per type).</summary>
		Task<BaseItemDtoQueryResult?> Search(string searchTerm, IReadOnlyList<BaseItemKind> kinds, int limit = 100);

		// 1.5 search (JellyfinClient_Search.cs; docs/research/search.md): the other search shapes the server offers, and a
		// person's titles. Each is null on a failure and never throws.
		/// <summary>Nixie 1.5: /Search/Hints (every kind, people included, under one limit; the server's own order).</summary>
		Task<SearchHintResult?> SearchHints(string searchTerm, int limit);

		/// <summary>Nixie 1.5: /Persons?searchTerm= (people, with what the cards need).</summary>
		Task<BaseItemDtoQueryResult?> SearchPeople(string searchTerm, int limit);

		/// <summary>Nixie 1.5: these items by id, with the titles search ranks on (the hints' items).</summary>
		Task<BaseItemDtoQueryResult?> GetSearchItems(IReadOnlyList<Guid> ids);

		/// <summary>Nixie 1.5: the films and shows a person is in, newest first ("With Ava Abara").</summary>
		Task<BaseItemDtoQueryResult?> GetPersonItems(Guid personId);
		// end 1.5 search

		// 1.6 collections (JellyfinClient_Index.cs; docs/research/smart-collections-data.md): the library index's pages, the
		// played episodes, items by id, the box sets and playlists, and "Save as playlist". Each is null on a failure.
		/// <summary>Nixie 1.6: a page of a library's index (films or shows with ProviderIds, Tags, Genres, Studios, ProductionLocations).</summary>
		Task<BaseItemDtoQueryResult?> GetLibraryIndexPage(BaseItemDto library, int startIndex, int limit, bool withTotal, CancellationToken cancellationToken);

		/// <summary>Nixie 1.6: the same page with People only (its own pass: People cost about 3.5 times the index).</summary>
		Task<BaseItemDtoQueryResult?> GetLibraryPeoplePage(BaseItemDto library, int startIndex, int limit, bool withTotal, CancellationToken cancellationToken);

		/// <summary>Nixie 1.6: the user's played episodes, latest first (each with its show and when it was played).</summary>
		Task<BaseItemDtoQueryResult?> GetPlayedEpisodesPage(int startIndex, int limit, bool withTotal, CancellationToken cancellationToken);

		/// <summary>Nixie 1.6: these items in this order (a Nixie collection's cards).</summary>
		Task<IReadOnlyList<BaseItemDto>?> GetItemsByIds(IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default);

		/// <summary>Nixie 1.6: the user's box sets, with their TMDb id and size.</summary>
		Task<BaseItemDtoQueryResult?> GetBoxSets(CancellationToken cancellationToken = default);

		/// <summary>Nixie 1.6: the playlists the user can see.</summary>
		Task<BaseItemDtoQueryResult?> GetPlaylists(CancellationToken cancellationToken = default);

		/// <summary>Nixie 1.6: "Save as playlist": a playlist of the user's own with these items; the server's no as a status.</summary>
		Task<PlaylistCreation> CreatePlaylist(string name, IReadOnlyList<Guid> ids);
		// end 1.6 collections

		Task<BaseItemDto?> GetItem(Guid id);

		/// <summary>Nixie 1.4.0: the folders an item is in, nearest first (its library among them); empty on failure. Never throws.</summary>
		Task<IReadOnlyList<BaseItemDto>> GetAncestors(Guid id);

		/// <summary>
		/// fluentfin-mpv: every episode of a series in play order (seasons in order, specials where the server places them),
		/// in one request with the user data (played, resume point) and nothing else: no images, no extra fields. The mpv
		/// player's queue (Mpv.MpvQueue). Null when the request failed (logged).
		/// </summary>
		Task<BaseItemDtoQueryResult?> GetSeriesEpisodes(Guid seriesId);

		/// <summary>
		/// fluentfin-mpv: the user's views (libraries) in the server's order, read once and shared by sign-in, Home and the
		/// Libraries page; read again after a new sign-in, a library added, removed or renamed here, a server message that
		/// this user changed (UserUpdated: library access, view order) or that a library changed (LibraryChanged, which the
		/// server sends only while the websocket is connected), a failed read, or a few minutes. Empty when they could not
		/// be read (logged).
		/// </summary>
		Task<IReadOnlyList<BaseItemDto>> GetUserViews();

		/// <summary>Nixie 1.4.0: the user's views as <see cref="GetUserViews"/>, but null when they could not be read (the Libraries page says so, with Retry).</summary>
		Task<IReadOnlyList<BaseItemDto>?> GetUserViewsOrNull();

		/// <summary>
		/// Nixie 1.4.0: how many titles a library holds, and how many of them are not watched (null for a library whose items have
		/// no watched state, a collection or a playlist); null when the server did not say.
		/// </summary>
		Task<FluentFin.Core.ViewModels.LibraryCounts?> GetLibraryCounts(BaseItemDto library);

		/// <summary>
		/// 1.2.0 (review): the views read before are out of date (a library added, removed or renamed here, or the server's
		/// LibraryChanged or UserUpdated): the top bar reads them again. Raised on any thread.
		/// </summary>
		event EventHandler? UserViewsChanged;

		/// <summary>fluentfin-mpv: one library's "Recently added" row (Home asks for every row at once). Null when the request failed (logged).</summary>
		Task<List<BaseItemDto>?> GetLatestItems(BaseItemDto library);

		IAsyncEnumerable<BaseItemDto> GetUserLibraries();

		Task<QueryFiltersLegacy?> GetFilters(BaseItemDto library);

		/// <returns>1.2.0: the item's user data as the server has it now; null when the request failed.</returns>
		Task<UserItemDataDto?> SetIsFavorite(BaseItemDto dto, bool isFavorite);

		/// <returns>1.2.0: the item's user data as the server has it now; null when the request failed.</returns>
		Task<UserItemDataDto?> SetPlayed(BaseItemDto dto, bool played);

		/// <summary>
		/// 1.2.0: puts an item's watched state and resume point back as they were (an Undo of marking a whole series or season;
		/// <c>POST /UserItems/{id}/UserData</c>). The server's answer; null when the request failed.
		/// </summary>
		Task<UserItemDataDto?> SetUserData(Guid id, bool played, long positionTicks);

		Task<bool> Authenticate(string code);

		/// <summary>Seaglass 1.1.4: approves another device's Quick Connect code, and how it went.</summary>
		Task<QuickConnectApproval> ApproveQuickConnect(string code);

		Task<MediaResponse?> GetMediaUrl(BaseItemDto dto, PlaybackSelection? selection = null);

		Uri GetImage(BaseItemDto item, ImageInfo info);

		Task<List<SessionInfoDto>> GetControllableSessions();

		Uri GetTrickplayImage(BaseItemDto dto, int index, int resolution);

		Task Playing(BaseItemDto dto);

		Task Progress(PlaybackProgressInfo info);

		Task Stop(PlaybackStopInfo dto);

		Task Stop();

		Task Logout();

		Task<int> BitrateTest();

		Task<List<RemoteSearchResult>> IdentifySeries(BaseItemDto dto, SeriesInfo info);

		Task<List<RemoteSearchResult>> IdentifyMovie(BaseItemDto dto, MovieInfo info);

		Task ApplyRemoteResult(BaseItemDto dto, RemoteSearchResult remoteResult);

		/// <summary>Nixie 1.2.0 (admin): <c>POST /Items/{id}</c>, which replaces every field it reads with the one sent. False when it failed (logged).</summary>
		Task<bool> UpdateMetadata(BaseItemDto dto);

		Uri? GetStreamUrl(BaseItemDto dto);

		Task<List<ExternalIdInfo>> GetExternalIdInfo(BaseItemDto dto);

		Task<MetadataEditorInfo?> GetMetadataEditorInfo(BaseItemDto dto);

		Task<List<ParentalRating>> GetParentalRatings();

		Task<List<ImageInfo>> GetImages(BaseItemDto dto);

		/// <summary>Nixie 1.2.0 (admin): false when it failed (logged).</summary>
		Task<bool> DeleteImage(BaseItemDto dto, ImageInfo info);

		Task<List<ImageProviderInfo>> GetImageProviders(BaseItemDto dto);

		Task<RemoteImageResult?> SearchImages(BaseItemDto dto, ImageType type, string? providerName = null, bool includeAllLanguages = false);

		Task UpdateImage(BaseItemDto dto, RemoteImageInfo info);

		Task<List<RemoteSubtitleInfo>> SearchSubtitles(BaseItemDto dto, CultureInfo culture);

		Task DownloadSubtitle(BaseItemDto dto, RemoteSubtitleInfo info);

		/// <summary>Nixie 1.2.0 (admin): false when it failed (logged).</summary>
		Task<bool> DeleteSubtitle(BaseItemDto dto, MediaStream stream);

		Task RefreshMetadata(Guid id, RefreshMetadataInfo info);

		Task<List<CountryInfo>> GetCountries();

		Task<List<CultureDto>> GetCultures();

		Task<MediaSegmentDtoQueryResult?> GetMediaSegments(BaseItemDto dto, MediaSegmentType[]? types = null);

		/// <summary>Nixie 1.2.0 (admin): false when it failed (logged).</summary>
		Task<bool> CreateMediaSegment(MediaSegmentDto segmentDto);

		/// <summary>Nixie 1.2.0 (admin): false when it failed (logged).</summary>
		Task<bool> DeleteMediaSegment(Guid segmentId);

		Task<SystemInfo?> GetSystemInfo();

		Task<SystemStorageDto?> GetSystemStorage();

		Task<List<SessionInfoDto>> GetActiveSessions();

		Task<ActivityLogEntryQueryResult?> GetActivities(DateTimeOffset minDate, bool hasUserId);

		Task<ActivityLogEntryQueryResult?> GetAllActivities();

		Task<List<UserDto>> GetUsers();

		Task<UserDto?> GetUser(Guid id);

		/// <summary>fluentfin-mpv: the signed-in user, fresh from the server (<c>GET /Users/Me</c>); null when it did not answer.</summary>
		Task<UserDto?> GetCurrentUser();

		/// <summary>
		/// fluentfin-mpv: saves <paramref name="userId"/>'s configuration on the server (<c>POST /Users/Configuration</c>), as
		/// jellyfin-web's Playback settings do; the whole configuration, as the server replaces it. The user is the caller's,
		/// not whoever is signed in when the request goes out. False when it failed.
		/// </summary>
		Task<bool> SaveUserConfiguration(Guid userId, UserConfiguration configuration);

		Task<UserDto?> CreateUser(string username, string password);

		/// <summary>Nixie 1.2.0 (admin): false when it failed (logged).</summary>
		Task<bool> DeleteUser(UserDto user);

		/// <summary>Nixie 1.2.0 (admin): false when it failed (logged).</summary>
		Task<bool> UpdatePolicy(UserDto user, UserPolicy policy);

		/// <summary>Nixie 1.2.0 (admin): false when the server did not take it (logged).</summary>
		Task<bool> ChangePassword(UserDto user, string currentPassword, string newPassword);

		/// <summary>Nixie 1.2.0 (admin): removes the user's password (<c>ResetPassword</c>: they sign in with none); false when it failed (logged).</summary>
		Task<bool> ResetPassword(UserDto user);

		Task<List<TaskInfo>> GetScheduledTasks(bool? isEnabled);

		Task RunTask(string? taskId);

		/// <summary>Nixie 1.2.0 (admin): <c>POST /System/Restart</c>; false when the server did not take it (logged).</summary>
		Task<bool> Restart();

		/// <summary>Nixie 1.2.0 (admin): <c>POST /System/Shutdown</c>; false when the server did not take it (logged).</summary>
		Task<bool> Shutdown();

		/// <summary>Nixie 1.2.0 (admin): scans every library for new and changed files (<c>POST /Library/Refresh</c>, the web dashboard's "Scan all libraries"); false when it failed (logged).</summary>
		Task<bool> ScanAllLibraries();

		Task<ServerConfiguration?> GetConfiguration();

		Task<XbmcMetadata> GetXbmcMetadata();

		Task SaveXbmcMetadata(XbmcMetadata metadata);

		Task<Metadata> GetMetadata();

		Task SaveMetadata(Metadata metadata);

		Task SaveConfiguration(ServerConfiguration configuration);

		// 1.5 fixes
		/// <summary>1.5 (the button review's S22): <see cref="SaveConfiguration"/> that says whether the server took it.</summary>
		Task<bool> TrySaveConfiguration(ServerConfiguration configuration);
		// END 1.5 fixes

		Task<TranscodingSettings?> GetTranscodeOptions();

		Task SaveTranscodeOptions(TranscodingSettings options);

		Task<LibraryOptionsResultDto?> GetAvailableInfo(Jellyfin.Sdk.Generated.Libraries.AvailableOptions.CollectionType libraryContentType, bool isNewLibrary);

		/// <summary>Nixie 1.2.0 (admin): false when it failed (logged).</summary>
		Task<bool> SaveLibraryOptions(Guid folderId, LibraryOptions options);

		Task<List<TaskInfo>> GetScheduledTasks();

		/// <summary>Nixie 1.2.0 (admin): false when the server did not take it (logged).</summary>
		Task<bool> RunScheduledTask(string id);

		/// <summary>Nixie 1.2.0 (admin): false when the server did not take it (logged).</summary>
		Task<bool> PlayOnSession(string sessionId, params IEnumerable<Guid?> itemIds);

		Task TogglePlayPause(string sessionId);

		Task Stop(string sessionId);

		IObservable<T> ObserveWebSocket<T>() where T : class, Jellyfin.Sdk.WebSockets.IOutboundWebSocketMessage, new();

		// SyncPlay
		Task<List<GroupInfoDto>> GetSyncPlayGroups();

		Task CreateSyncPlayGroup();

		/// <summary>Seaglass 1.1.4: starts a SyncPlay group; null when it started, else why not (the server's refusal said).</summary>
		Task<string?> StartSyncPlayGroup();

		Task JoinSyncPlayGroup(Guid groupId);

		Task LeaveSyncPlayGroup();

		Task SignalReadyForSyncPlay(ReadyRequestDto request);

		Task SignalPauseForSyncPlay();

		Task SignalUnpauseForSyncPlay();

		Task SignalSeekForSyncPlay(TimeSpan position);

		Task SignalNewPlaylist(PlayRequestDto request);

		Task<DateTimeOffset> SyncTime();

		Task SendMessage(string sessionId, MessageCommand command);
	}

	public record RefreshMetadataInfo(MetadataRefreshMode ImageRefreshMode, MetadataRefreshMode MetadataRefreshMode, bool ReplaceAllImages, bool RegenerateTrickplay, bool ReplaceAllMetadata);

	/// <summary>Audio/subtitle streams chosen before playback starts. A null index means "use the server default"; a subtitle index of -1 means no subtitles.</summary>
	public record PlaybackSelection(int? AudioStreamIndex, int? SubtitleStreamIndex)
	{
		public const int NoSubtitles = -1;

		/// <summary>Builds a selection from the page pickers, where the "None" subtitle entry is a stream without an index.</summary>
		public static PlaybackSelection From(MediaStream? audio, MediaStream? subtitle) =>
			new(audio?.Index, subtitle is null ? null : subtitle.Index ?? NoSubtitles);
	}

	/// <param name="Selection">Streams picked on the item page; null plays with the defaults.</param>
	/// <param name="FromBeginning">fluentfin-mpv: "Play from beginning" on the item page: the item starts at 0, not at its resume point.</param>
	public record PlaybackRequest(BaseItemDto Dto, PlaybackSelection? Selection, bool FromBeginning = false);

	public record MediaResponse(Uri Uri, PlaybackProgressInfo_PlayMethod PlayMethod, string PlaybackSessionId, string MediaSourceId, MediaSourceInfo MediaSourceInfo);

	public class XbmcMetadata
	{
		public string? ReleaseDateFormat { get; set; }
		public bool SaveImagePathsInNfo { get; set; }
		public bool EnablePathSubstitution { get; set; }
		public bool EnableExtraThumbsDuplication { get; set; }
		public Guid? UserId { get; set; }
	}

	public class Metadata
	{
		public bool UseFileCreationTimeForDateAdded { get; set; }
	}

	/// <summary>Seaglass 1.1.4: how approving a Quick Connect code went (IJellyfinClient.ApproveQuickConnect).</summary>
	public enum QuickConnectApproval
	{
		Approved,

		/// <summary>No request is waiting with that code (mistyped, expired).</summary>
		NoSuchCode,

		/// <summary>Quick Connect is turned off on the server.</summary>
		Disabled,

		/// <summary>No answer (the connection, the server).</summary>
		Failed,
	}
}
