﻿using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

/// <summary>Nixie 1.4.0: how many titles a library holds, and how many are not watched (null: no watched state, or not known).</summary>
public sealed record LibraryCounts(int Total, int? Unwatched)
{
	/// <summary>What a library's titles are, for counting them: films, shows (not episodes), box sets, playlists, albums, videos.</summary>
	public static BaseItemKind? KindOf(BaseItemDto_CollectionType? type) => type switch
	{
		BaseItemDto_CollectionType.Movies => BaseItemKind.Movie,
		BaseItemDto_CollectionType.Tvshows => BaseItemKind.Series,
		BaseItemDto_CollectionType.Boxsets => BaseItemKind.BoxSet,
		BaseItemDto_CollectionType.Playlists => BaseItemKind.Playlist,
		BaseItemDto_CollectionType.Music => BaseItemKind.MusicAlbum,
		BaseItemDto_CollectionType.Musicvideos => BaseItemKind.MusicVideo,
		BaseItemDto_CollectionType.Homevideos => BaseItemKind.Video,
		BaseItemDto_CollectionType.Books => BaseItemKind.Book,
		_ => null,
	};

	/// <summary>Titles that are watched or not (an "unwatched" count means something for them).</summary>
	public static bool HasWatchedState(BaseItemKind kind) => kind is BaseItemKind.Movie or BaseItemKind.Series or BaseItemKind.Video or BaseItemKind.MusicVideo;
}

/// <summary>
/// Nixie 1.4.0 (the UX review's plan item 10): a tile of the Libraries page: the library, and what it holds, as the tile shows
/// it ("Movies · 320 · 97 unwatched") and as Narrator hears it ("Movies, 320 movies, 97 unwatched").
/// </summary>
public sealed partial class LibraryTile(BaseItemDto library) : ObservableObject
{
	public BaseItemDto Library { get; } = library;

	public string Name => Library.Name ?? "";

	/// <summary>After the name: " · 320 · 97 unwatched" ("" until the counts are in, or when the server did not give them).</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(AutomationName))]
	public partial string Detail { get; private set; } = "";

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(AutomationName))]
	public partial LibraryCounts? Counts { get; private set; }

	public string AutomationName => SpokenName(Name, Library.CollectionType, Counts);

	public void SetCounts(LibraryCounts? counts)
	{
		Counts = counts;
		Detail = DetailText(counts);
	}

	public static string DetailText(LibraryCounts? counts) => counts is null ? ""
		: counts.Unwatched is { } unwatched
			? $" · {N(counts.Total)} · {N(unwatched)} unwatched"
			: $" · {N(counts.Total)}";

	/// <summary>"Movies, 320 movies, 97 unwatched"; "Collections, 12 collections"; the name alone until the counts are in.</summary>
	public static string SpokenName(string name, BaseItemDto_CollectionType? type, LibraryCounts? counts)
	{
		if (counts is null)
		{
			return name;
		}

		var (one, many) = type switch
		{
			BaseItemDto_CollectionType.Movies => ("movie", "movies"),
			BaseItemDto_CollectionType.Tvshows => ("show", "shows"),
			BaseItemDto_CollectionType.Boxsets => ("collection", "collections"),
			BaseItemDto_CollectionType.Playlists => ("playlist", "playlists"),
			BaseItemDto_CollectionType.Music => ("album", "albums"),
			BaseItemDto_CollectionType.Books => ("book", "books"),
			_ => ("video", "videos"),
		};
		var spoken = $"{name}, {N(counts.Total)} {(counts.Total == 1 ? one : many)}";
		return counts.Unwatched is { } unwatched ? $"{spoken}, {N(unwatched)} unwatched" : spoken;
	}

	private static string N(int n) => n.ToString("N0", CultureInfo.CurrentCulture);
}

/// <summary>
/// The Libraries page ("All libraries"). Nixie 1.4.0 (the UX review's plan item 10; X3, X11, CL-M5, CL-M13, CL-p5): the media
/// libraries first, then "Collections &amp; playlists" apart (they are not media libraries); each tile with what it holds; a
/// loading, empty and failed state with Retry (a failed read left a title over nothing).
/// </summary>
public partial class LibrariesLandingPageViewModel(IJellyfinClient jellyfinClient,
												   INavigationServiceCore navigationService) : ObservableObject, INavigationAware
{
	/// <summary>Every library, media and collections alike, in the server's order (kept for the harness and older callers).</summary>
	public ObservableCollection<BaseItemDto> Libraries { get; } = [];

	/// <summary>Movies, Shows, Music...: in the server's order.</summary>
	public ObservableCollection<LibraryTile> Media { get; } = [];

	/// <summary>Collections and Playlists: last, in a group of their own.</summary>
	public ObservableCollection<LibraryTile> Collections { get; } = [];

	[ObservableProperty]
	public partial bool HasCollections { get; private set; }

	public LoadState State { get; } = new("your libraries");

	public Task OnNavigatedFrom() => Task.CompletedTask;

	public Task OnNavigatedTo(object parameter) => State.RunAsync(Load);

	/// <summary>A collection or a playlist view: not a media library (the review's X3).</summary>
	public static bool IsCollectionsView(BaseItemDto view) =>
		view.CollectionType is BaseItemDto_CollectionType.Boxsets or BaseItemDto_CollectionType.Playlists;

	private async Task<LoadOutcome> Load()
	{
		var views = await jellyfinClient.GetUserViewsOrNull();
		if (views is null)
		{
			return LoadMessages.CouldNotLoad("your libraries");
		}

		Libraries.Clear();
		Media.Clear();
		Collections.Clear();
		var tiles = new List<LibraryTile>();
		foreach (var view in views)
		{
			Libraries.Add(view);
			var tile = new LibraryTile(view);
			(IsCollectionsView(view) ? Collections : Media).Add(tile);
			tiles.Add(tile);
		}

		HasCollections = Collections.Count > 0;
		if (tiles.Count == 0)
		{
			return LoadOutcome.Empty("No libraries yet", $"Libraries added on {LoadMessages.ServerName()} appear here.");
		}

		// the counts fill in as they come; the tiles do not wait for them
		_ = Task.WhenAll(tiles.Select(async tile => tile.SetCounts(await jellyfinClient.GetLibraryCounts(tile.Library))));
		return LoadOutcome.Loaded;
	}

	public void NavigateToLibrary(BaseItemDto library)
	{
		navigationService.NavigateTo<LibraryViewModel>(library);
	}
}
