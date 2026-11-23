using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFin.Core.Services;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.SmartCollections.Builders;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

/// <summary>Nixie 1.6: the library index and the saved collections, for pages whose view models the colpage stream owns (set by the app).</summary>
public static class SmartCollectionServices
{
	public static LibraryIndex? Index { get; set; }

	public static SmartCollectionStore? Store { get; set; }

	/// <summary>Settings → Collections (the kinds turned off).</summary>
	public static Contracts.Services.ILocalSettingsService? Settings { get; set; }
}

/// <summary>
/// Nixie 1.6 (plan 4.3; the collections stream's part of the shared Collection page, kept apart from colpage's file): a
/// Nixie collection opens here through its source (<see cref="NixieCollection"/>), with the eyebrow "Made by Nixie ·
/// Franchise", the "why" line, the 2×2 mosaic for its art, Favourite kept on this PC, and "Save as playlist"; an admin's
/// box set with a TMDb id shows "Also in your library": the owned films of that franchise it leaves out.
/// </summary>
public partial class CollectionViewModel
{
	/// <summary>The Nixie collection shown; null on a server's box set or playlist.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Eyebrow), nameof(Why), nameof(IsNixie), nameof(IsSavedFilter), nameof(CanMarkWatched))]
	public partial NixieCollection? Nixie { get; private set; }

	public bool IsNixie => Nixie is not null;

	/// <summary>Over the title: "Made by Nixie · Franchise" for a Nixie collection, else "Collection" or "Playlist".</summary>
	public string Eyebrow => Nixie?.Eyebrow ?? Kind;

	/// <summary>Under the summary: where a Nixie collection came from ("" for the server's own).</summary>
	public string Why => Nixie?.Why ?? "";

	/// <summary>On an admin's box set: the owned films of its TMDb collection it leaves out, in release order.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(AlsoInLibraryTitle))]
	public partial IReadOnlyList<BaseItemViewModel> AlsoInLibrary { get; private set; } = [];

	/// <summary>"Also in your library · 1 film" (the section's heading and its list's name).</summary>
	public string AlsoInLibraryTitle => AlsoInLibrary.Count == 0 ? "" : $"Also in your library · {SmartCollectionKinds.CountText(AlsoInLibrary.Count, IndexKind.Movie)}";

	/// <summary>"Save as playlist" can run: a Nixie collection with titles, not saving now, not saved yet.</summary>
	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(SaveAsPlaylistCommand))]
	public partial bool CanSaveAsPlaylist { get; private set; }

	/// <summary>
	/// Nixie 1.6.1 (bug hunt BH-160-m2): "Save as playlist" is offered: a Nixie collection holding something a playlist from
	/// Nixie holds (films, episodes, videos). A collection of shows only ("Unfinished series") never offers it: pressed, it
	/// could only say it can't.
	/// </summary>
	[ObservableProperty]
	public partial bool OffersSaveAsPlaylist { get; private set; }

	/// <summary>What "Save as playlist" did, or why the server said no ("" before it is pressed).</summary>
	[ObservableProperty]
	public partial string PlaylistMessage { get; private set; } = "";

	/// <summary>The playlist "Save as playlist" made (its Open link), or null.</summary>
	[ObservableProperty]
	public partial BaseItemDto? SavedPlaylist { get; private set; }

	private async Task OpenNixie(NixieCollection nixie)
	{
		Nixie = nixie;
		var favorite = SmartCollectionServices.Store is { } store && (await store.FavoritesAsync()).Contains(nixie.Collection.Id);
		Collection = nixie.AsItem(favorite);
		IsPlaylist = false;
		UseHeaderHook(nixie);
		await State.RunAsync(() => LoadNixie(nixie));
	}

	/// <summary>
	/// The colpage stream's "// 1.6 collections" hook in CollectionViewModel (wip/150-colpage): the header's eyebrow ("Made by
	/// Nixie · Franchise"), its why line, and whether the cards are numbered (a franchise yes, a decade no); its header makes the
	/// 2×2 mosaic from the titles when there is no backdrop (1.6 integration: on since the two branches merged).
	/// </summary>
	private void UseHeaderHook(NixieCollection nixie)
	{
		HeaderEyebrow = nixie.Eyebrow;
		HeaderWhy = nixie.Why;
		PositionsOverride = nixie.HasOwnOrder;
	}

	private async Task<LoadOutcome> LoadNixie(NixieCollection nixie)
	{
		var items = await nixie.LoadItems(JellyfinClient);
		if (items is null)
		{
			CanSaveAsPlaylist = false;
			OffersSaveAsPlaylist = false;
			return LoadMessages.CouldNotLoad("this collection");
		}

		_serverOrder = [.. items];
		_placeKnown = true;
		Update();

		OffersSaveAsPlaylist = _serverOrder.Any(IsPlaylistItem); // 1.6.1 (BH-160-m2): never on shows only
		CanSaveAsPlaylist = OffersSaveAsPlaylist && SavedPlaylist is null;
		return _serverOrder.Count == 0 ? EmptyOutcome(nixie.Collection) : LoadOutcome.Loaded;
	}

	/// <summary>
	/// What an empty Nixie collection says. 1.7.3 fix round (BH-173-M2, rule 11): a saved filter is Nixie's own filter over a
	/// library, so it says the filter matches nothing now, not that the titles left the server (which answered).
	/// </summary>
	public static LoadOutcome EmptyOutcome(SmartCollection collection) => collection.Filter is { } filter
		? LoadOutcome.Empty("Nothing matches now", $"No titles in {(string.IsNullOrWhiteSpace(filter.LibraryName) ? "this library" : filter.LibraryName)} match this filter now.")
		: LoadOutcome.Empty("Nothing here now", $"None of the titles in this collection are on {LoadMessages.ServerName()} any more.");

	/// <summary>After a server's box set is in: "Also in your library", from the library index (none without a TMDb id).</summary>
	private async Task AfterServerLoad()
	{
		if (Nixie is not null || Collection is not { Type: BaseItemDto_Type.BoxSet } set || IndexEntry.ProviderId(set, "Tmdb") is not { } tmdb
			|| SmartCollectionServices.Index is not { } index || await index.GetAsync() is not { } snapshot)
		{
			return;
		}

		var missing = FranchiseBuilder.AlsoInLibrary(snapshot, tmdb, _serverOrder.Select(x => x.Id).OfType<Guid>());
		if (missing.Count == 0)
		{
			return;
		}

		var items = await JellyfinClient.GetItemsByIds([.. missing.Select(x => x.Id)]);
		AlsoInLibrary = [.. (items ?? [.. missing.Select(x => x.ToDto())]).Select(BaseItemViewModel.FromDto)];
	}

	/// <summary>
	/// "Save as playlist": the collection, in its order, as a playlist of the user's own on the server (the user's explicit
	/// action; nothing else Nixie makes is written to the server). The server's no is said in Nixie's words.
	/// </summary>
	[RelayCommand(CanExecute = nameof(CanSaveAsPlaylist))]
	public async Task SaveAsPlaylist()
	{
		if (Nixie is null || _serverOrder.Count == 0 || !CanSaveAsPlaylist)
		{
			return;
		}

		// review Major 3: films and episodes only (the server adds every episode of a show sent to it), and at most
		// PlaylistMost of them, the first in the collection's order
		var ordered = CollectionBrowse.Ordered(_serverOrder, Order, IsPlaylist, OwnDisplayOrder);
		var playable = ordered.Where(IsPlaylistItem).ToList();
		var shows = ordered.Count - playable.Count;
		if (playable.Count == 0)
		{
			CanSaveAsPlaylist = false;
			OffersSaveAsPlaylist = false;
			PlaylistMessage = ShowsOnlyText;
			return;
		}

		CanSaveAsPlaylist = false;
		PlaylistMessage = "Saving to your playlists…";
		// 1.6.1 (BH-160-m2): saved before (this page left and opened again, another start, a 200 that named no playlist): the
		// user's playlist of that name is the one, not a second copy
		if (await SavedBefore(Title) is { } before)
		{
			SavedPlaylist = before;
			PlaylistMessage = $"Already in your playlists as “{before.Name}”.";
			return;
		}

		var ids = playable.Take(PlaylistMost).Select(x => x.Id).OfType<Guid>().ToList();
		var made = await JellyfinClient.CreatePlaylist(Title, ids);
		if (made.Created)
		{
			SavedPlaylist = new BaseItemDto { Id = made.Id, Name = Title, Type = BaseItemDto_Type.Playlist };
			PlaylistMessage = SavedText(Title, ids.Count, playable.Count, shows);
			return;
		}

		PlaylistMessage = PlaylistRefusal(made.Status);
		CanSaveAsPlaylist = true;
	}

	/// <summary>What a collection of shows only says if Save is pressed anyway (it isn't offered: <see cref="OffersSaveAsPlaylist"/>).</summary>
	public const string ShowsOnlyText = "This collection holds shows only. A playlist from Nixie holds films and episodes: a show would add every one of its episodes.";

	/// <summary>
	/// The user's playlist named <paramref name="title"/> (as the server's list of playlists has it, ignoring case and the
	/// edges' spaces), or null: none, or the list could not be read (then Save makes it, as asked).
	/// </summary>
	private async Task<BaseItemDto?> SavedBefore(string title)
	{
		var name = title.Trim();
		var playlists = await JellyfinClient.GetPlaylists();
		return playlists?.Items?.FirstOrDefault(x => x is { Id: not null, Type: BaseItemDto_Type.Playlist } && string.Equals(x.Name?.Trim(), name, StringComparison.CurrentCultureIgnoreCase));
	}

	/// <summary>The most titles "Save as playlist" sends: the first ones in the collection's order (a "Highly rated" of thousands is not one request).</summary>
	public const int PlaylistMost = 200;

	/// <summary>What a playlist holds from Nixie: films and episodes (a show sent to the server would bring every episode).</summary>
	public static bool IsPlaylistItem(BaseItemDto item) =>
		item.Id is not null && item.Type is BaseItemDto_Type.Movie or BaseItemDto_Type.Episode or BaseItemDto_Type.Video or BaseItemDto_Type.MusicVideo;

	/// <summary>
	/// What "Save as playlist" did, plainly: "Saved to your playlists as “Heists”"; when the collection was too big, the
	/// first N of how many; and the shows left out.
	/// </summary>
	public static string SavedText(string title, int saved, int films, int shows)
	{
		var text = saved < films
			? $"Saved the first {saved.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} of {films.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} titles to your playlists as “{title}”: a playlist from Nixie holds {PlaylistMost.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} at most."
			: $"Saved to your playlists as “{title}”.";
		return shows == 0 ? text
			: $"{text} {(shows == 1 ? "1 show was" : $"{shows.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} shows were")} left out: a show would add every one of its episodes.";
	}

	/// <summary>Why a playlist wasn't made, in Nixie's words (never the server's error page).</summary>
	public static string PlaylistRefusal(int status) => status switch
	{
		401 => "Your sign-in has ended. Sign in again, then save the playlist.",
		403 => $"{LoadMessages.ServerName()} doesn't let your account make playlists. Only the server's owner can change that.",
		// 1.6.1 (BH-160-m2): an answer that names no playlist: it may have been made (Save again finds it by its name)
		200 => $"{LoadMessages.ServerName()} answered without saying which playlist it made. Look in your playlists before saving again.",
		0 => $"Couldn't reach {LoadMessages.ServerName()}. Check the connection, then try again.",
		_ => $"{LoadMessages.ServerName()} didn't make the playlist (error {status}). Try again later.",
	};

	/// <summary>A saved filter ("Save as collection") can be removed from its own page: the way out of keeping one.</summary>
	public bool IsSavedFilter => Nixie?.Collection.Kind == SmartCollectionKind.Saved;

	/// <summary>Removes a saved filter from Saved (on this PC), and goes back to where it was opened from.</summary>
	[RelayCommand]
	public async Task RemoveSaved()
	{
		if (Nixie is not { Collection.Kind: SmartCollectionKind.Saved } nixie || SmartCollectionServices.Store is not { } store)
		{
			return;
		}

		await store.RemoveSaved(nixie.Collection.Id);
		commands.GoBack();
	}

	/// <summary>Opens the playlist "Save as playlist" made.</summary>
	[RelayCommand]
	public void OpenSavedPlaylist()
	{
		if (SavedPlaylist is { } playlist)
		{
			commands.DisplayDto(playlist);
		}
	}

	// a Nixie collection's Favourite is kept on this PC (it isn't a server item); true when it was one
	private async Task<bool> SetNixieFavorite(bool favorite)
	{
		if (Nixie is not { } nixie)
		{
			return false;
		}

		if (SmartCollectionServices.Store is { } store)
		{
			await store.SetFavorite(nixie.Collection.Id, favorite);
		}

		Collection = nixie.AsItem(favorite);
		OnPropertyChanged(nameof(IsFavorite));
		return true;
	}

	/// <summary>
	/// Review Major 4: the whole set's Watched toggle shows on a server's box set or playlist only. A Nixie collection writes
	/// nothing to the server but "Save as playlist" (the plan): its toggle is hidden, and a call from anywhere else changes
	/// nothing (true: it was a Nixie collection, and nothing was sent).
	/// </summary>
	public bool CanMarkWatched => Nixie is null;

	private Task<bool> SetNixieWatched(bool played)
	{
		_ = played;
		return Task.FromResult(Nixie is not null);
	}
}
