using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.SmartCollections;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

/// <summary>Nixie 1.6: the Collections page's sections (where the one Collections destination opens it).</summary>
public enum CollectionsSection
{
	All,
	Server,
	Playlists,
	MadeByNixie,
	Saved,
}

/// <summary>Where the Collections page opens: a section, or (See all) every collection of one Nixie kind or section.</summary>
public sealed record CollectionsPlace(CollectionsSection Section, SmartCollectionKind? Kind = null, bool SeeAll = false);

/// <summary>
/// Nixie 1.6: one tile of the Collections page: a server's box set or playlist (its poster, "6 films · 3 left") or a Nixie
/// collection (a 2×2 mosaic of its titles, a small Nixie mark, "12 films"), as Narrator hears it.
/// </summary>
public sealed class CollectionTile
{
	private CollectionTile(string title, string detail, string automationName, BaseItemDto? set, SmartCollection? nixie, IReadOnlyList<BaseItemDto> mosaic)
	{
		Title = title;
		Detail = detail;
		AutomationName = automationName;
		Set = set;
		Nixie = nixie;
		Mosaic = mosaic;
	}

	public string Title { get; }

	/// <summary>Under the title: "6 films · 3 left", "12 items", "12 films".</summary>
	public string Detail { get; }

	public string AutomationName { get; }

	/// <summary>The server's box set or playlist; null for a Nixie collection.</summary>
	public BaseItemDto? Set { get; }

	/// <summary>The Nixie collection; null for the server's.</summary>
	public SmartCollection? Nixie { get; }

	public bool IsNixie => Nixie is not null;

	/// <summary>The art: the set's own poster, or up to four titles' posters for the mosaic.</summary>
	public IReadOnlyList<BaseItemDto> Mosaic { get; }

	public static CollectionTile Server(BaseItemDto set, string shownName)
	{
		var playlist = set.Type == BaseItemDto_Type.Playlist;
		var detail = playlist ? Items(set.ChildCount) : BoxSetDetail(set.ChildCount, set.UserData?.UnplayedItemCount);
		var kind = playlist ? "playlist" : "collection";
		var spoken = string.IsNullOrEmpty(detail) ? $"{shownName}, {kind}" : $"{shownName}, {kind}, {detail.Replace(" · ", ", ", StringComparison.Ordinal)}";
		return new CollectionTile(shownName, detail, spoken, set, null, [set]);
	}

	public static CollectionTile Made(SmartCollection collection) =>
		new(collection.Title, collection.CountText, collection.AutomationName, null, collection, [.. collection.Cover.Select(x => x.ToDto())]);

	/// <summary>"6 films · 3 left", "6 films · all watched", "6 films"; "" when the server gave no count.</summary>
	public static string BoxSetDetail(int? count, int? unplayed)
	{
		if (count is not { } n)
		{
			return "";
		}

		var films = $"{n.ToString("N0", CultureInfo.CurrentCulture)} {(n == 1 ? "film" : "films")}";
		return unplayed switch
		{
			null => films,
			0 when n > 0 => $"{films} · all watched",
			> 0 and var left => $"{films} · {left.ToString("N0", CultureInfo.CurrentCulture)} left",
			_ => films,
		};
	}

	private static string Items(int? count) => count is { } n ? $"{n.ToString("N0", CultureInfo.CurrentCulture)} {(n == 1 ? "item" : "items")}" : "";
}

/// <summary>One row of the Collections page: its title, its tiles, and whether it ends in "See all".</summary>
public sealed class CollectionsRow(string title, CollectionsSection section, SmartCollectionKind? kind, IReadOnlyList<CollectionTile> tiles, bool hasSeeAll)
{
	public string Title { get; } = title;

	public CollectionsSection Section { get; } = section;

	public SmartCollectionKind? Kind { get; } = kind;

	public IReadOnlyList<CollectionTile> Tiles { get; } = tiles;

	public bool HasSeeAll { get; } = hasSeeAll;

	/// <summary>The first row of the Made by Nixie section carries its heading.</summary>
	public string Heading { get; init; } = "";

	public bool HasHeading => Heading.Length > 0;
}

/// <summary>
/// Nixie 1.6 (plan 4.1): the Collections page: every collection in one place: the server's box sets (tidied, "6 films · 3
/// left"), Playlists, Made by Nixie by kind (each row ending in "See all"), and Saved (saved filters and pinned Nixie
/// collections). A franchise the admin boxed shows once, as the box set. The index may take a while on a first visit: the
/// server's rows show at once and "Made by Nixie" says it is reading.
/// </summary>
public partial class CollectionsViewModel(IJellyfinClient jellyfinClient, GlobalCommands commands, LibraryIndex index, SmartCollectionStore store,
	ILocalSettingsService? settings = null, TimeProvider? time = null) : ObservableObject, INavigationAware
{
	/// <summary>How many tiles a row shows before its "See all".</summary>
	public const int RowLength = 12;

	private readonly TimeProvider _time = time ?? TimeProvider.System;
	private SynchronizationContext? _ui;
	private IReadOnlyList<BaseItemDto> _boxSets = [];
	private IReadOnlyList<BaseItemDto> _playlists = [];
	private IReadOnlyList<SmartCollection> _saved = [];
	private IReadOnlySet<string> _favorites = new HashSet<string>();

	public IJellyfinClient JellyfinClient { get; } = jellyfinClient;

	public CollectionsPlace Place { get; private set; } = new(CollectionsSection.All);

	/// <summary>"Collections", or a See all's kind ("Franchises").</summary>
	[ObservableProperty]
	public partial string Title { get; private set; } = "Collections";

	public ObservableCollection<CollectionsRow> Rows { get; } = [];

	/// <summary>See all: every tile of one row, as a grid.</summary>
	[ObservableProperty]
	public partial IReadOnlyList<CollectionTile> GridTiles { get; private set; } = [];

	public bool IsSeeAll => Place.SeeAll;

	/// <summary>The index is being read for the first time: "Made by Nixie" says so.</summary>
	[ObservableProperty]
	public partial bool IsReadingIndex { get; private set; }

	public string ReadingText => "Nixie is reading your libraries to make its collections…";

	/// <summary>How many Nixie collections there are (the Libraries page's "Made by Nixie · 48").</summary>
	[ObservableProperty]
	public partial int MadeByNixieCount { get; private set; }

	/// <summary>The row the page should bring into view and focus when it is in (its section's), or null.</summary>
	[ObservableProperty]
	public partial CollectionsRow? FocusRow { get; private set; }

	public LoadState State { get; } = new("your collections");

	public Task OnNavigatedFrom()
	{
		index.Changed -= OnIndexChanged;
		return Task.CompletedTask;
	}

	public Task OnNavigatedTo(object parameter)
	{
		_ui = SynchronizationContext.Current;
		Place = parameter as CollectionsPlace ?? new CollectionsPlace(parameter is CollectionsSection section ? section : CollectionsSection.All);
		OnPropertyChanged(nameof(IsSeeAll));
		Title = Place.SeeAll ? Place.Kind is { } kind ? SmartCollectionKinds.Plural(kind) : SectionTitle(Place.Section) : "Collections";
		index.Changed -= OnIndexChanged;
		index.Changed += OnIndexChanged;
		return State.RunAsync(Load);
	}

	public static string SectionTitle(CollectionsSection section) => section switch
	{
		CollectionsSection.Server => "Your server's collections",
		CollectionsSection.Playlists => "Playlists",
		CollectionsSection.MadeByNixie => "Made by Nixie",
		CollectionsSection.Saved => "Saved",
		_ => "Collections",
	};

	private async Task<LoadOutcome> Load()
	{
		var boxSets = JellyfinClient.GetBoxSets();
		var playlists = JellyfinClient.GetPlaylists();
		var saved = store.SavedAsync();
		var favorites = store.FavoritesAsync();
		var current = index.Current;
		await Task.WhenAll(boxSets, playlists, saved, favorites);
		if (boxSets.Result is null && playlists.Result is null && current is null)
		{
			return LoadMessages.CouldNotLoad("your collections");
		}

		_boxSets = boxSets.Result?.Items ?? [];
		_playlists = playlists.Result?.Items ?? [];
		_saved = saved.Result;
		_favorites = favorites.Result;
		IsReadingIndex = current is null;
		await ShowAsync(current);

		if (current is null)
		{
			// the first visit: the server's rows now, and the page is loaded; Made by Nixie once the index is in (OnIndexChanged
			// shows it), which on a big library takes minutes. Only a page with nothing else to show waits for it
			var reading = Reading = ReadIndex();
			if (Rows.Count == 0 && GridTiles.Count == 0)
			{
				await reading;
			}
		}
		else
		{
			_ = index.GetAsync(); // read again in the background when stale; OnIndexChanged shows the new one
		}

		return Rows.Count == 0 && GridTiles.Count == 0
			? LoadOutcome.Empty("No collections yet", $"Box sets and playlists on {LoadMessages.ServerName()}, and the collections Nixie makes from your libraries, appear here.")
			: LoadOutcome.Loaded;
	}

	/// <summary>The first read of the index while the page shows (tests wait for it); done when there is none.</summary>
	internal Task Reading { get; private set; } = Task.CompletedTask;

	// the first read of the index: its rows when it is in (also through OnIndexChanged), and the "reading" line gone either way
	private async Task ReadIndex()
	{
		try
		{
			if (await index.GetAsync() is { } read)
			{
				await ShowAsync(read);
			}
		}
		catch (Exception)
		{
			// the read failed: the server's rows stay, without Made by Nixie
		}
		finally
		{
			IsReadingIndex = false;
		}
	}

	private void OnIndexChanged(object? sender, IndexSnapshot snapshot)
	{
		async void Apply()
		{
			try
			{
				IsReadingIndex = false;
				await ShowAsync(snapshot);
			}
			catch (Exception)
			{
				// a page left meanwhile, a collection that failed to build: the page keeps what it shows
			}
		}

		if (_ui is { } ui)
		{
			ui.Post(_ => Apply(), null);
		}
		else
		{
			Apply();
		}
	}

	/// <summary>Every collection Nixie makes of this index, less the franchises the admin boxed and the kinds turned off.</summary>
	public Task<IReadOnlyList<SmartCollection>> MadeByNixie(IndexSnapshot snapshot)
	{
		var boxed = _boxSets.Select(x => IndexEntry.ProviderId(x, "Tmdb")).OfType<string>().ToHashSet();
		return SmartCollectionCatalog.BuildAllAsync(snapshot, _time.GetUtcNow(), boxed, CollectionSettings.KindsOff(settings));
	}

	// a new index can come in on another thread while a load shows the first one (the app posts it to the UI thread; a test
	// has no UI thread): one Show at a time
	private readonly Lock _showing = new();

	// the collections are made off the UI thread (BuildAllAsync), then shown on it
	private async Task ShowAsync(IndexSnapshot? snapshot)
	{
		var made = snapshot is null ? [] : await MadeByNixie(snapshot);
		lock (_showing)
		{
			ShowLocked(made);
		}
	}

	private void ShowLocked(IReadOnlyList<SmartCollection> made)
	{
		MadeByNixieCount = made.Count;
		var rows = new List<CollectionsRow>();
		var names = CollectionNames.DisplayAll(_boxSets);
		var server = _boxSets.Select((x, i) => CollectionTile.Server(x, names[i])).ToList();
		var playlists = _playlists.Select(x => CollectionTile.Server(x, x.Name ?? "")).ToList();
		var nixie = made.Select(CollectionTile.Made).ToList();
		var pinned = made.Where(x => _favorites.Contains(x.Id)).Select(CollectionTile.Made);
		var saved = _saved.Select(CollectionTile.Made).Concat(pinned).ToList();

		if (Place.SeeAll)
		{
			GridTiles = Place.Section switch
			{
				CollectionsSection.Server => server,
				CollectionsSection.Playlists => playlists,
				CollectionsSection.Saved => saved,
				_ when Place.Kind is { } kind => [.. nixie.Where(x => x.Nixie!.Kind == kind)],
				_ => nixie,
			};
			Rows.Clear();
			return;
		}

		void Add(string title, CollectionsSection section, SmartCollectionKind? kind, List<CollectionTile> tiles, string heading = "")
		{
			if (tiles.Count > 0)
			{
				rows.Add(new CollectionsRow(title, section, kind, [.. tiles.Take(RowLength)], tiles.Count > RowLength) { Heading = heading });
			}
		}

		Add(SectionTitle(CollectionsSection.Server), CollectionsSection.Server, null, server);
		Add(SectionTitle(CollectionsSection.Playlists), CollectionsSection.Playlists, null, playlists);
		var heading = "Made by Nixie";
		var picks = nixie.Where(x => SmartCollectionKinds.Picks.Contains(x.Nixie!.Kind)).ToList();
		if (picks.Count > 0)
		{
			Add("Picks", CollectionsSection.MadeByNixie, null, picks, heading);
			heading = "";
		}

		foreach (var kind in SmartCollectionKinds.PageOrder)
		{
			var ofKind = nixie.Where(x => x.Nixie!.Kind == kind).ToList();
			if (ofKind.Count > 0)
			{
				Add(SmartCollectionKinds.Plural(kind), CollectionsSection.MadeByNixie, kind, ofKind, heading);
				heading = "";
			}
		}

		Add(SectionTitle(CollectionsSection.Saved), CollectionsSection.Saved, null, saved);

		if (!rows.Select(x => (x.Title, x.Tiles.Count, x.HasSeeAll)).SequenceEqual(Rows.Select(x => (x.Title, x.Tiles.Count, x.HasSeeAll)))
			|| !rows.SelectMany(x => x.Tiles.Select(t => t.Title)).SequenceEqual(Rows.SelectMany(x => x.Tiles.Select(t => t.Title))))
		{
			Rows.Clear();
			foreach (var row in rows)
			{
				Rows.Add(row);
			}
		}

		FocusRow = Place.Section == CollectionsSection.All ? Rows.FirstOrDefault() : Rows.FirstOrDefault(x => x.Section == Place.Section) ?? Rows.FirstOrDefault();
	}

	/// <summary>A tile opened: a server's set on the Collection page (or a playlist), a Nixie collection on it too.</summary>
	public void Open(CollectionTile tile)
	{
		if (tile.Nixie is { } nixie)
		{
			commands.OpenSmartCollection(nixie);
		}
		else if (tile.Set is { } set)
		{
			commands.DisplayDto(set);
		}
	}

	/// <summary>A row's "See all": the Collections page with every tile of that row.</summary>
	public void SeeAll(CollectionsRow row) => commands.OpenCollectionsAt(new CollectionsPlace(row.Section, row.Kind, SeeAll: true));
}
