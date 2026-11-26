using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.SmartCollections;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

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
/// 1.7.1 (review M1): what a tile holds changes in place (<see cref="Update"/>): a first read's rows stay on the page (and the
/// keyboard on their tiles) while its pages come, and their collections grow with them.
public sealed partial class CollectionTile : ObservableObject
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
	[ObservableProperty]
	public partial string Detail { get; private set; }

	[ObservableProperty]
	public partial string AutomationName { get; private set; }

	/// <summary>The server's box set or playlist; null for a Nixie collection.</summary>
	[ObservableProperty]
	public partial BaseItemDto? Set { get; private set; }

	/// <summary>The Nixie collection; null for the server's. What the tile opens: 1.7.1, the newest the page has.</summary>
	[ObservableProperty]
	public partial SmartCollection? Nixie { get; private set; }

	public bool IsNixie => Nixie is not null;

	/// <summary>The art: the set's own poster, or up to four titles' posters for the mosaic.</summary>
	[ObservableProperty]
	public partial IReadOnlyList<BaseItemDto> Mosaic { get; private set; }

	/// <summary>
	/// 1.7.1 (review M1): the same tile, made again of a newer index: its collection, count, spoken name and art become the
	/// newer one's. The mosaic changes only when its titles do (each change loads its posters again).
	/// </summary>
	internal void Update(CollectionTile newer)
	{
		Detail = newer.Detail;
		AutomationName = newer.AutomationName;
		Set = newer.Set;
		Nixie = newer.Nixie;
		if (!Mosaic.Select(x => x.Id).SequenceEqual(newer.Mosaic.Select(x => x.Id)))
		{
			Mosaic = newer.Mosaic;
		}
	}

	public static CollectionTile Server(BaseItemDto set, string shownName)
	{
		var playlist = set.Type == BaseItemDto_Type.Playlist;
		var detail = playlist ? Items(set.ChildCount) : BoxSetDetail(set.ChildCount, set.UserData?.UnplayedItemCount);
		var kind = playlist ? "playlist" : "collection";
		var spoken = string.IsNullOrEmpty(detail) ? $"{shownName}, {kind}" : $"{shownName}, {kind}, {detail.Replace(" · ", ", ", StringComparison.Ordinal)}";
		return new CollectionTile(shownName, detail, spoken, set, null, [set]);
	}

	public static CollectionTile Made(SmartCollection collection) => collection.Ended
		? new(collection.Title, EndedText, $"{collection.Title}, made by Nixie, {EndedSpoken}", null, collection, []) // 1.8 (BH-180-m1)
		: new(collection.Title, collection.CountText, collection.AutomationName, null, collection, [.. collection.Cover.Select(x => x.ToDto())]);

	/// <summary>1.8 (BH-180-m1): what a pinned collection Nixie no longer makes says under its title in Saved.</summary>
	public const string EndedText = "Ended";

	/// <summary>1.8 (BH-180-m1): and what Narrator says of it.</summary>
	public const string EndedSpoken = "ended: Nixie no longer makes it, unpin it to take it out of Saved";

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

	// 1.8 (BH-180-M1): observable, so a row takes a newer one's tiles in place (Take) and the keyboard stays on its tile
	private readonly ObservableCollection<CollectionTile> _tiles = [.. tiles];

	public IReadOnlyList<CollectionTile> Tiles => _tiles;

	public bool HasSeeAll { get; } = hasSeeAll;

	/// <summary>The first row of the Made by Nixie section carries its heading.</summary>
	public string Heading { get; init; } = "";

	public bool HasHeading => Heading.Length > 0;

	/// <summary>1.7.1 (review M1): the same row (its tiles' titles), made of a newer index: each tile takes the newer one's contents.</summary>
	internal void Update(CollectionsRow newer)
	{
		for (var i = 0; i < Tiles.Count && i < newer.Tiles.Count; i++)
		{
			Tiles[i].Update(newer.Tiles[i]);
		}
	}

	/// <summary>
	/// 1.8 (BH-180-M1): the same row (its title, section, kind, heading and See all) whose tiles changed: This week when the
	/// people come in, a first read's rows growing. In place, by what each tile is: the tile of a collection (or set) still in
	/// it stays (its contents the newer one's) and moves to its place, a new one is put in, a gone one taken out. The keyboard
	/// stays on its tile; one whose tile went goes to its neighbour (ShelfRow). Making the row again sent it to Back.
	/// </summary>
	internal void Take(CollectionsRow newer)
	{
		for (var i = 0; i < newer.Tiles.Count; i++)
		{
			var key = KeyOf(newer.Tiles[i]);
			var at = -1;
			for (var k = i; k < _tiles.Count && at < 0; k++)
			{
				at = KeyOf(_tiles[k]) == key ? k : -1;
			}

			if (at < 0)
			{
				_tiles.Insert(i, newer.Tiles[i]);
				continue;
			}

			if (at != i)
			{
				_tiles.Move(at, i);
			}

			if (_tiles[i].Title != newer.Tiles[i].Title)
			{
				_tiles[i] = newer.Tiles[i]; // renamed (TMDb's franchise names came): replaced where it is (ShelfRow keeps the keyboard on it)
			}
			else
			{
				_tiles[i].Update(newer.Tiles[i]);
			}
		}

		while (_tiles.Count > newer.Tiles.Count)
		{
			_tiles.RemoveAt(_tiles.Count - 1);
		}
	}

	/// <summary>Whether <paramref name="newer"/> is this row (made again): its tiles may differ (<see cref="Take"/>).</summary>
	internal bool IsRow(CollectionsRow newer) => Title == newer.Title && Section == newer.Section && Kind == newer.Kind && Heading == newer.Heading && HasSeeAll == newer.HasSeeAll;

	// a tile is its collection (a Nixie one's id), or the server's set, or its title
	private static string KeyOf(CollectionTile tile) => tile.Nixie?.Id ?? (tile.Set?.Id is { } id ? $"set:{id:N}" : $"title:{tile.Title}");
}

/// <summary>
/// Nixie 1.6 (plan 4.1): the Collections page: every collection in one place: the server's box sets (tidied, "6 films · 3
/// left"), Playlists, Made by Nixie by kind (each row ending in "See all"), and Saved (saved filters and pinned Nixie
/// collections). A franchise the admin boxed shows once, as the box set. The index may take a while on a first visit: the
/// server's rows show at once and "Made by Nixie" says it is reading.
/// </summary>
public partial class CollectionsViewModel(IJellyfinClient jellyfinClient, GlobalCommands commands, LibraryIndex index, SmartCollectionStore store,
	ILocalSettingsService? settings = null, TimeProvider? time = null, ILogger<CollectionsViewModel>? logger = null) : ObservableObject, INavigationAware, IKeptOnBack
{
	/// <summary>How many tiles a row shows before its "See all".</summary>
	public const int RowLength = 12;

	private readonly TimeProvider _time = time ?? TimeProvider.System;
	private readonly ILogger _log = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance; // 1.7.1: durations
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

	// ---- 1.8: the grid's reverse toggle (stream s's sort-direction setting) ----------------------------------------------------

	/// <summary>
	/// 1.8 (the owner, 2026-11-23: "we should be able to reverse the direction of the sort in collections"): See all's grid runs
	/// the other way. Kept like Order and Hide watched: for the session, per user and grid (<see cref="SortDirections"/>), so Back
	/// (the kept view model), Forward and R (a new one, which recalls it) show it the same; a grid opened anew runs as its order does.
	/// </summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(GridDirectionName), nameof(GridDirectionTip), nameof(GridDirectionGlyph), nameof(GridDirectionWords))]
	public partial bool GridReversed { get; private set; }

	/// <summary>What the grid's order runs by: names A to Z (the server's sets, playlists, franchises), else the list's own sequence.</summary>
	public OrderRuns GridRuns => Place.Section switch
	{
		CollectionsSection.Server or CollectionsSection.Playlists => OrderRuns.AToZ,
		CollectionsSection.MadeByNixie when Place.Kind == SmartCollectionKind.Franchise => OrderRuns.AToZ,
		_ => OrderRuns.FirstToLast,
	};

	/// <summary>The direction button's Narrator name: "A to Z, switch to Z to A".</summary>
	public string GridDirectionName => SortDirections.ButtonName(GridRuns, GridReversed);

	/// <summary>Its tooltip: "Switch to Z to A".</summary>
	public string GridDirectionTip => SortDirections.ToolTip(GridRuns, GridReversed);

	/// <summary>Its arrow, the way the tiles run (the Library's glyphs, one width: no jump when it flips).</summary>
	public string GridDirectionGlyph => SortDirections.Glyph(GridRuns, GridReversed);

	/// <summary>The words beside it: "A to Z", "Z to A", "First to last", "Last to first".</summary>
	public string GridDirectionWords => SortDirections.Words(GridRuns, GridReversed);

	private string GridView => SortDirections.ForGrid(Place.Section, Place.Kind);

	/// <summary>The direction button: the grid runs the other way, kept for this grid (the user's press only).</summary>
	public void ReverseGrid()
	{
		if (!Place.SeeAll)
		{
			return;
		}

		GridReversed = !GridReversed;
		SortDirections.Remember(JellyfinClient.UserId, GridView, GridReversed);
		GridTiles = [.. GridTiles.Reverse()]; // the same tiles, the other way (no read, no build)
	}
	// end 1.8

	/// <summary>The index is being read for the first time: "Made by Nixie" says so.</summary>
	[ObservableProperty]
	public partial bool IsReadingIndex { get; private set; }

	/// <summary>
	/// 1.7.1 (BH-171-m1): a first read stopped part way: its rows so far stay, and the line says they are not all (they looked
	/// final, with no line). Gone with the next index in, or the next visit.
	/// </summary>
	[ObservableProperty]
	public partial bool IsReadStopped { get; private set; }

	public const string ReadStoppedText = "Nixie couldn't finish reading your libraries. It will try again next time.";

	/// <summary>
	/// 1.7.1 (BH-171-m1): the line under the title: while the first read runs, or when it stopped part way. 1.7.3 fix round
	/// (BH-173-m1): or when a whole index made nothing (<see cref="IsNixieEmpty"/>).
	/// </summary>
	public bool ShowsReadingLine => IsReadingIndex || IsReadStopped || IsNixieEmpty;

	partial void OnIsReadingIndexChanged(bool value)
	{
		SayNixieEmpty();
		OnPropertyChanged(nameof(ShowsReadingLine));
	}

	partial void OnIsReadStoppedChanged(bool value)
	{
		SayNixieEmpty();
		OnPropertyChanged(nameof(ShowsReadingLine));
	}

	// 1.7.3 fix round (BH-173-m1)
	/// <summary>
	/// A whole index made no Made by Nixie collection (the throwaway's 25,736 films with no tags made none, and the page showed
	/// the server's row alone, with no word): the line says so, and what Nixie looks for. Never during a read.
	/// </summary>
	[ObservableProperty]
	public partial bool IsNixieEmpty { get; private set; }

	partial void OnIsNixieEmptyChanged(bool value)
	{
		SayNixieEmpty();
		OnPropertyChanged(nameof(ShowsReadingLine));
	}

	/// <summary>What the line says when Made by Nixie made nothing (rule 11: what Nixie looks for, nothing about the server).</summary>
	public const string NixieEmptyText = "Made by Nixie has nothing to show yet. It makes collections of films in one franchise, titles that share a keyword, "
		+ "and well-rated titles you haven't watched, and found too few of them in your libraries.";

	/// <summary>When every kind of Made by Nixie collection is turned off.</summary>
	public const string NixieOffText = "Every kind of Made by Nixie collection is turned off in Settings > Collections.";

	private string _nixieEmptyText = NixieEmptyText;

	// the reading line's words: a read running or stopped says so first
	private void SayNixieEmpty()
	{
		if (IsNixieEmpty && !IsReadingIndex && !IsReadStopped)
		{
			ReadingText = _nixieEmptyText;
		}
	}

	// whether every kind a switch turns off is off
	private bool EveryKindOff()
	{
		try
		{
			var off = CollectionSettings.KindsOff(settings);
			return SmartCollectionKinds.Switched.All(off.Contains); // 1.8 (BH-180-M2): 1.8's kinds too
		}
		catch (Exception)
		{
			return false;
		}
	}
	// end 1.7.3 fix round

	public const string ReadingStart = "Nixie is reading your libraries to make its collections…";

	/// <summary>1.7.1: the reading line, with how far the first read has got ("…: 12,000 of 25,736 films").</summary>
	[ObservableProperty]
	public partial string ReadingText { get; private set; } = ReadingStart;

	/// <summary>How many Nixie collections there are (the Libraries page's "Made by Nixie · 48").</summary>
	[ObservableProperty]
	public partial int MadeByNixieCount { get; private set; }

	/// <summary>The row the page should bring into view and focus when it is in (its section's), or null.</summary>
	[ObservableProperty]
	public partial CollectionsRow? FocusRow { get; private set; }

	public LoadState State { get; } = new("your collections");

	public Task OnNavigatedFrom()
	{
		_kindsOffLeft = KindsOffNow(); // Nixie 1.7.5 (review Major 2): Back sees a kind turned off meanwhile
		index.Changed -= OnIndexChanged;
		index.Progress -= OnIndexProgress; // 1.7.1
		Outside.OutsideListsSnapshot.SharedChanged -= OnOutsideChanged; // 1.7 keyless
		UnwatchTrending(); // 1.7 trending
		return Task.CompletedTask;
	}

	// ---- Nixie 1.7.4: Back shows the Collections page as it was (IKeptOnBack) ------------------------------------------------

	public bool CanKeep => _loaded && State.Status is LoadStatus.Loaded or LoadStatus.Empty;

	/// <summary>
	/// Back or Forward shows the page as it was (its rows, its tiles). It listens again to the index, the outside lists and the
	/// trending lists; an index that came in while it was away (a first read finished) is shown now, from what is in memory
	/// (never a read).
	/// </summary>
	public async Task Returned(object parameter, bool stale)
	{
		var returnedAt = _time.GetTimestamp(); // 1.8.1
		_ui = SynchronizationContext.Current;
		index.Changed -= OnIndexChanged;
		index.Changed += OnIndexChanged;
		index.Progress -= OnIndexProgress;
		index.Progress += OnIndexProgress;
		Outside.OutsideListsSnapshot.SharedChanged -= OnOutsideChanged;
		Outside.OutsideListsSnapshot.SharedChanged += OnOutsideChanged;
		WatchTrending();

		// Nixie 1.7.5 (review Major 2): what the page left may have changed of the page's own: a saved filter removed (its page goes
		// Back by itself), a pin, a kind turned off in Settings > Collections. They are read again from this PC, and when the
		// server's may have changed (stale), its box sets and playlists behind the page (a playlist saved); the rows are shown
		// again from the index in memory when anything differs (the same rows keep their tiles). A read that fails keeps the page.
		var changed = false;
		try
		{
			var (saved, favorites) = await PinsAndFilters();
			if (!saved.Select(x => (x.Id, x.Title)).SequenceEqual(_saved.Select(x => (x.Id, x.Title))) || !favorites.SetEquals(_favorites))
			{
				(_saved, _favorites) = (saved, favorites);
				changed = true;
			}
		}
		catch (Exception)
		{
			// the page keeps what it shows
		}

		changed |= KindsOffNow() != _kindsOffLeft;
		if (stale)
		{
			var boxSets = JellyfinClient.GetBoxSets();
			var playlists = JellyfinClient.GetPlaylists();
			try
			{
				await Task.WhenAll(boxSets, playlists);
			}
			catch (Exception)
			{
				// (either failed: what came is compared below, the rest stays)
			}

			static bool Same(IReadOnlyList<BaseItemDto> a, IReadOnlyList<BaseItemDto> b) =>
				a.Select(x => (x.Id, x.Name, x.ChildCount, x.UserData?.UnplayedItemCount)).SequenceEqual(b.Select(x => (x.Id, x.Name, x.ChildCount, x.UserData?.UnplayedItemCount)));
			if (boxSets.IsCompletedSuccessfully && boxSets.Result?.Items is { } sets && !Same(sets, _boxSets))
			{
				_boxSets = sets;
				changed = true;
			}

			if (playlists.IsCompletedSuccessfully && playlists.Result?.Items is { } lists && !Same(lists, _playlists))
			{
				_playlists = lists;
				changed = true;
			}
		}

		// 1.8 (P1-1): This week's lineup is of a day (its daily picks turn, its themes each ISO week): shown again on another day,
		// the rows are made again
		changed |= _lineupDate is { } made && SmartCollectionCatalog.Now(_time).Date != made;
		var what = "nothing changed";
		if (changed)
		{
			what = "the rows made again";
			try
			{
				await ShowAsync(index.Current ?? _latest);
			}
			catch (Exception)
			{
				// (Made by Nixie couldn't be made again: the rows shown stay)
			}
		}
		else if (index.Current is { } current && !ReferenceEquals(current, _latest))
		{
			what = "a newer index shown";
			OnIndexChanged(this, current);
		}

		// 1.8.1 (rule 10): what Back's check behind did, and how long it took (durations and words only)
		_log.LogInformation("Collections: Back's check behind in {Ms} ms: {What}{Stale}", (long)_time.GetElapsedTime(returnedAt).TotalMilliseconds, what,
			stale ? " (the server's sets asked again)" : "");
	}

	// Nixie 1.7.5 (review Major 2): the kinds turned off when the page was left (OnNavigatedFrom), against the ones now
	private string _kindsOffLeft = "";

	private string KindsOffNow()
	{
		try
		{
			return string.Join(",", CollectionSettings.KindsOff(settings).Order());
		}
		catch (Exception)
		{
			return _kindsOffLeft;
		}
	}

	public Task OnNavigatedTo(object parameter)
	{
		_ui = SynchronizationContext.Current;
		Place = parameter as CollectionsPlace ?? new CollectionsPlace(parameter is CollectionsSection section ? section : CollectionsSection.All);
		OnPropertyChanged(nameof(IsSeeAll));
		// 1.8: this grid's direction as the user left it this session (R makes a new page: it reads it back here)
		GridReversed = Place.SeeAll && SortDirections.Recall(JellyfinClient.UserId, GridView) == true;
		OnPropertyChanged(nameof(GridRuns));
		OnPropertyChanged(nameof(GridDirectionName));
		OnPropertyChanged(nameof(GridDirectionTip));
		OnPropertyChanged(nameof(GridDirectionGlyph));
		OnPropertyChanged(nameof(GridDirectionWords));
		Title = Place.SeeAll ? Place.Kind is { } kind ? SmartCollectionKinds.Plural(kind) : SectionTitle(Place.Section) : "Collections";
		index.Changed -= OnIndexChanged;
		index.Changed += OnIndexChanged;
		index.Progress -= OnIndexProgress; // 1.7.1: how far a first read has got
		index.Progress += OnIndexProgress;
		_opened = System.Diagnostics.Stopwatch.StartNew();
		_loaded = false;
		_nixieRowsLogged = false;
		IsReadStopped = false;
		IsNixieEmpty = false; // 1.7.3 fix round (BH-173-m1): said again by this visit's rows
		_firstRows = new(TaskCreationOptions.RunContinuationsAsynchronously);
		// 1.7 keyless: the outside lists (awards, TMDb's names) as they arrive, and a refresh of the stale ones
		Outside.OutsideListsSnapshot.SharedChanged -= OnOutsideChanged;
		Outside.OutsideListsSnapshot.SharedChanged += OnOutsideChanged;
		_ = Outside.OutsideLists.App?.RefreshInBackground();
		// end 1.7 keyless
		WatchTrending(); // 1.7 trending
		return State.RunAsync(Load);
	}

	// 1.7 keyless: new outside lists: the collections are made again from the index the page has (1.7.1: never a read; during
	// a first read, the pages it has so far)
	private void OnOutsideChanged(Outside.OutsideListsSnapshot lists)
	{
		if (Rematchable() is { } current)
		{
			OnIndexChanged(this, current);
		}
	}

	// 1.7.1: what new lists are matched against: the whole index; during a first read, its pages so far. Review (Minor): never
	// the pages of a first read that stopped, which brought back their reading line with nothing reading
	private IndexSnapshot? Rematchable() => index.Current ?? index.ReadingSoFar ?? (_latest is { Reading: null } whole ? whole : null);

	public static string SectionTitle(CollectionsSection section) => section switch
	{
		CollectionsSection.Server => "The server's collections", // 1.7.2: "the server" (the user may not be its admin)
		CollectionsSection.Playlists => "Playlists",
		CollectionsSection.MadeByNixie => "Made by Nixie",
		CollectionsSection.Saved => "Saved",
		_ => "Collections",
	};

	private System.Diagnostics.Stopwatch _opened = new();

	// 1.7.1: the load showed its rows (an index that comes in before that is shown by the load itself, with the server's sets)
	private bool _loaded;

	// BH-171-q2: the first Made by Nixie rows of this visit were logged; this visit began with no index (a first read)
	private bool _nixieRowsLogged;
	private bool _firstVisit;

	// 1.7.1: the page has a row (a first read's first pages may be the first)
	private TaskCompletionSource _firstRows = new(TaskCreationOptions.RunContinuationsAsynchronously);

	private async Task<LoadOutcome> Load()
	{
		var boxSets = JellyfinClient.GetBoxSets();
		var playlists = JellyfinClient.GetPlaylists();
		var mine = PinsAndFilters(); // 1.7.3: after the retired kinds' pins and filters are removed
		var known = index.SavedAsync(); // 1.7.1: the index in memory, else the saved one (never the server's: that comes behind it)
		// 1.7.1: its collections made while the server's sets come (the franchises an admin boxed are left out once they are in)
		var building = BuildSoon(known);
		await Task.WhenAll(boxSets, playlists, mine, building);
		var current = known.Result;
		if (boxSets.Result is null && playlists.Result is null && current is null)
		{
			return LoadMessages.CouldNotLoad("your collections");
		}

		_boxSets = boxSets.Result?.Items ?? [];
		_playlists = playlists.Result?.Items ?? [];
		(_saved, _favorites) = mine.Result;
		IsReadingIndex = current is null;
		_firstVisit = current is null;
		ReadingText = ReadingStart;
		// a newer one that came in meanwhile; or, opened again during a first read, its titles so far
		var latest = index.Current ?? current ?? index.ReadingSoFar;
		if (latest?.Reading is { } progress)
		{
			ReadingText = Reading_(progress);
		}

		try
		{
			await ShowAsync(latest, ReferenceEquals(latest, current) ? building.Result : null);
		}
		catch (Exception ex) when (latest is not null)
		{
			// 1.7.2: a failed Made by Nixie never fails the load: the server's rows without it (as 1.7.0 showed them).
			// 1.7.3: logged as what failed: Made by Nixie's making (ShowAsync says so), or anything else in showing the rows,
			// with its own type and place (it was all "Made by Nixie couldn't be made")
			if (ex is MadeByNixieException made)
			{
				MadeByNixieFailed(made.InnerException!);
			}
			else
			{
				_log.LogWarning("Collections: the rows couldn't be shown ({Error} in {Where}); shown again without Made by Nixie", ex.GetType().Name, Where(ex));
			}

			await ShowAsync(null);
		}

		_loaded = true;
		// 1.7.1: how long the page took to its rows, and from what (durations and counts only)
		_log.LogInformation("Collections: {Rows} rows in {Ms} ms ({From})", Rows.Count + (GridTiles.Count > 0 ? 1 : 0), _opened.ElapsedMilliseconds,
			current is null ? "Made by Nixie waits for the index's first read" : "Made by Nixie from the saved index");

		if (current is null)
		{
			// the first visit: the server's rows now, and the page is loaded; Made by Nixie once the index is in (OnIndexChanged
			// shows it), which on a big library takes minutes. Only a page with nothing else to show waits for it (1.7.1: for its
			// first rows, as the first read's pages come)
			var reading = Reading = ReadIndex();
			if (Rows.Count == 0 && GridTiles.Count == 0)
			{
				await Task.WhenAny(reading, _firstRows.Task);
			}
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
		IndexSnapshot? read = null;
		try
		{
			if ((read = await index.GetAsync()) is not null)
			{
				// 1.7.1: not made twice: Changed may be showing this very index already. 1.7.1 blocker: then the read waits for
				// that show, so that Reading is done when Made by Nixie's rows are in (it skipped it and was done before them)
				await (_pending is { } pending && ReferenceEquals(pending.Snapshot, read) ? pending.Shown : ShowAsync(read));
			}
		}
		catch (Exception)
		{
			// the read failed: the server's rows stay, without Made by Nixie
		}
		finally
		{
			// Nixie 1.7.5: a read that stopped waits for its pages' rows still being made (a show pending: the pages came just
			// before the stop, and a cold first build takes a while), so that the line saying they are not all never shows
			// before them, with no Made by Nixie rows under it. ShowAsync's Shown never throws
			if (read is null && _pending is { Snapshot.Reading: not null } pending)
			{
				await pending.Shown;
			}

			IsReadingIndex = false;
			if (read is null && _latest is { Reading: not null } && index.Current is null)
			{
				// BH-171-m1: it stopped part way: the rows made of its pages so far stay, said to be what they are
				ReadingText = ReadStoppedText;
				IsReadStopped = true;
			}
		}
	}

	private void OnIndexChanged(object? sender, IndexSnapshot snapshot)
	{
		async void Apply()
		{
			try
			{
				if (!_loaded)
				{
					return; // the load shows the newest itself
				}

				// 1.7.1: a first read's pages so far keep the reading line; a whole index takes it away
				IsReadingIndex = snapshot.Reading is not null;
				IsReadStopped = false; // BH-171-m1: a newer index came
				if (snapshot.Reading is { } progress)
				{
					ReadingText = Reading_(progress);
				}

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

	// 1.7.1: the reading line says how far the first read has got
	private static string Reading_(IndexProgress progress) => $"Nixie is reading your libraries to make its collections: {progress.Text}";

	private void OnIndexProgress(object? sender, IndexProgress progress)
	{
		void Apply()
		{
			if (IsReadingIndex)
			{
				ReadingText = Reading_(progress);
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
	public async Task<IReadOnlyList<SmartCollection>> MadeByNixie(IndexSnapshot snapshot) => Unboxed((await Build(snapshot, waitForLater: true)).All);

	/// <summary>1.8 (P1-1): This week's row's title: the week's lineup (<see cref="CollectionLineup"/>), ten tiles at most.</summary>
	public const string ThisWeekTitle = "This week";

	/// <summary>1.8: the collections made after the first rows (stream s's <see cref="SmartCollectionCatalog.BuildLaterAsync"/>); tests give their own.</summary>
	internal Func<IndexSnapshot, DateTimeOffset, IReadOnlySet<SmartCollectionKind>, Task<IReadOnlyList<SmartCollection>>> LaterBuild { get; set; } =
		(index, now, kindsOff) => SmartCollectionCatalog.BuildLaterAsync(index, now, kindsOff);

	// 1.8 (P1-1): the local date of the lineup This week shows (null: none yet)
	private DateTime? _lineupDate;

	// 1.8 (P1-1): what the page makes of an index: every collection (its own rows) and the week's lineup (This week)
	private sealed record Built(IReadOnlyList<SmartCollection> All, Lineup Lineup)
	{
		public static Built None { get; } = new([], Lineup.Empty);

		/// <summary>The re-review: the week's Spotlight still being made (the page shows it after: ShowSpotlightLater).</summary>
		public Task<IReadOnlyList<SmartCollection>>? Later { get; init; }
	}

	// 1.7.1: every collection of this index, the boxed franchises too (Unboxed leaves them out), so it can be made before the
	// server's sets are in. 1.8 (P1-1): and This week's lineup, made with them off the UI thread, of the same candidates as
	// Home's (Home's rows are its first ones)
	/// <param name="waitForLater">The Spotlight waited for (a count: the Libraries page's), not shown after (the page's own rows).</param>
	private async Task<Built> Build(IndexSnapshot snapshot, bool waitForLater = false)
	{
		// 1.7 trending: the public lists' collections (Trending, Popular, Most anticipated, Box office), matched on this PC.
		// 1.7.3: in a catch of their own, as each kind's builder is: a throw there took every Made by Nixie row away. 1.8: and
		// the merged trending row, the lineup's Trending candidate
		IReadOnlyList<SmartCollection> lists = [];
		SmartCollection? trendingRow = null;
		if (Outside.TrendingServices.Collections is { } trending)
		{
			lists = await TrendingMatch.Safely(() => trending.For(snapshot), [], _log);
			trendingRow = await TrendingMatch.Safely(() => trending.HomeRowFor(snapshot), null, _log);
		}
		// end 1.7 trending
		IReadOnlyList<SmartCollection> extra = trendingRow is null ? [] : [trendingRow];
		var kindsOff = CollectionSettings.KindsOff(settings);
		// 1.8 (stream s's Major 2): the week's Spotlight is made apart (BuildLaterAsync, once an index), beside the build. The
		// re-review: the page doesn't wait for it: in when it is made already, else the page shows and This week takes it after
		var later = LaterBuild(snapshot, SmartCollectionCatalog.Now(_time), kindsOff);
		var made = await CollectionLineup.BuildAsync(snapshot, SmartCollectionCatalog.Now(_time), null, kindsOff, // 1.6.1 (BH-160-m1): the local date
			extra);
		IReadOnlyList<SmartCollection> spotlight = [];
		Task<IReadOnlyList<SmartCollection>>? pending = null;
		if (later.IsCompleted || waitForLater)
		{
			spotlight = await later;
		}
		else
		{
			pending = later;
		}

		if (spotlight.Count > 0)
		{
			// one of the page's collections, as when it was a published builder's (the Libraries page's "Made by Nixie · N")
			made = await CollectionLineup.AssembleAsync(snapshot, [.. made.All, .. spotlight], extra, SmartCollectionCatalog.Now(_time), fallback: made);
		}

		return new Built([.. made.All.Where(x => SmartCollectionCatalog.Published.Contains(x.Kind)), .. lists], made.Lineup) { Later = pending }; // 1.7.3 (P0-3): the published kinds only
	}

	// 1.8 (the re-review): the Spotlight made after the page showed: the page again (This week with it; its later build is in, so
	// Build takes it at once) while it shows that index. Called on the page's thread, after its show
	private async Task ShowSpotlightLater(IndexSnapshot snapshot, Task<IReadOnlyList<SmartCollection>> later)
	{
		try
		{
			if ((await later).Count > 0 && ReferenceEquals(_latest, snapshot))
			{
				await ShowAsync(snapshot);
			}
		}
		catch (Exception)
		{
			// the page keeps what it shows
		}
	}

	/// <summary>Tests: the week's Spotlight shown after the page (done when there is none).</summary>
	internal Task SpotlightLater { get; private set; } = Task.CompletedTask;

	// 1.7.3 (the owner's answer 2): the user's saved filters and pins, once the pins and filters of the kinds that went
	// (directors, actors, studios, networks) are taken out; a filter of theirs whose removal failed isn't listed either
	private async Task<(IReadOnlyList<SmartCollection> Saved, IReadOnlySet<string> Favorites)> PinsAndFilters()
	{
		await RetiredCollections.ForgetAsync(store, _log);
		var saved = await store.SavedAsync();
		return ([.. saved.Where(x => !RetiredCollections.IsGone(x.Filter?.PresetId))], await store.FavoritesAsync());
	}

	// 1.7.3 (P0-3): the pinned collections of the kinds the presets strip still makes but Nixie no longer publishes (a decade,
	// a genre mix, a country), each made by its id; none when there are no such pins. Off the UI thread.
	private async Task<IReadOnlyList<SmartCollection>> PinnedOnDemand(IndexSnapshot snapshot, IReadOnlyList<SmartCollection> made)
	{
		// 1.8 (BH-180-m1): every pin of a kind Nixie makes that this build didn't (last week's Spotlight, a pick from what the
		// user watched that moved on), not the presets strip's kinds alone: they went from Saved without a word
		var titles = await store.PinnedTitlesAsync();
		foreach (var pinned in made.Where(x => _favorites.Contains(x.Id) && titles.GetValueOrDefault(x.Id) != x.Title))
		{
			await store.SetFavorite(pinned.Id, true, pinned.Title); // a pin made before this kept no title: named from now on
		}

		var ids = _favorites.Where(x => SmartCollectionCatalog.KindOf(x) is not null && !RetiredCollections.IsGone(x)).Except(made.Select(x => x.Id)).ToList();
		if (ids.Count == 0)
		{
			return [];
		}

		var now = SmartCollectionCatalog.Now(_time);
		// fix round (BH-173-M2): by its id, every title of its rule (a genre mix past the strip's twelve is still found); the
		// Spotlight's by its candidates (any week's person or studio). 1.8 (BH-180-m1): one not made at all stays, by its title,
		// said to have ended (opened, it says so and offers to unpin), until the user unpins it
		return await Task.Run(() => (IReadOnlyList<SmartCollection>)[.. ids.Select(id =>
			SmartCollectionCatalog.Find(snapshot, id, now, whole: NixiePresets.IsStripOnly(id))
			?? new SmartCollection(id, SmartCollectionCatalog.KindOf(id)!.Value, titles.GetValueOrDefault(id) ?? EndedTitle, "", false, []) { Ended = true })]);
	}

	/// <summary>1.8 (BH-180-m1): an ended pin's name when it was pinned before Nixie kept pins' titles.</summary>
	public const string EndedTitle = "A pinned collection";

	// 1.7.3: a throw's place for the log: its type and method, never its message (which could name a title)
	private static string Where(Exception ex) => ex.TargetSite is { } site ? $"{site.DeclaringType?.Name}.{site.Name}" : "an unknown place";

	// 1.7.3: Made by Nixie's own making failed (not the page around it): ShowAsync throws this, and the load says so
	private sealed class MadeByNixieException(Exception inner) : Exception("Made by Nixie couldn't be made", inner);

	// 1.7.1: a franchise the admin boxed shows once, as the box set
	private IReadOnlyList<SmartCollection> Unboxed(IReadOnlyList<SmartCollection> made)
	{
		var boxed = _boxSets.Select(x => IndexEntry.ProviderId(x, "Tmdb")).OfType<string>().ToHashSet();
		return boxed.Count == 0 ? made : [.. made.Where(x => !(x.Kind == SmartCollectionKind.Franchise && x.TmdbCollection is { } id && boxed.Contains(id)))];
	}

	// 1.7.1: the saved index's collections, made as soon as it is read from this PC; none without one. 1.7.2: none when they
	// couldn't be made (the load awaits this beside the server's sets: a throw here failed the whole page)
	private async Task<Built?> BuildSoon(Task<IndexSnapshot?> known)
	{
		if (await known is not { } snapshot)
		{
			return null;
		}

		try
		{
			return await Build(snapshot);
		}
		catch (Exception ex)
		{
			MadeByNixieFailed(ex);
			return Built.None;
		}
	}

	// 1.7.2: Made by Nixie couldn't be made: the page shows the server's collections and playlists without it (the exception's
	// type only)
	private void MadeByNixieFailed(Exception ex) =>
		_log.LogWarning("Collections: Made by Nixie couldn't be made ({Error}); the server's collections and playlists show without it", ex.GetType().Name);

	// 1.7 trending: the lists change (a refresh came in, a source was turned on or off): the page shows its rows again
	private Outside.TrendingCollections? _trending;

	private void WatchTrending()
	{
		UnwatchTrending();
		if (Outside.TrendingServices.Collections is { } trending)
		{
			_trending = trending;
			trending.Changed += OnTrendingChanged;
			_ = trending.RefreshAsync(); // the lists that aren't fresh, in the background
		}
	}

	private void UnwatchTrending()
	{
		if (_trending is { } trending)
		{
			trending.Changed -= OnTrendingChanged;
			_trending = null;
		}
	}

	private void OnTrendingChanged()
	{
		if (Rematchable() is { } snapshot) // 1.7.1
		{
			OnIndexChanged(this, snapshot);
		}
	}
	// end 1.7 trending

	// a new index can come in on another thread while a load shows the first one (the app posts it to the UI thread; a test
	// has no UI thread): one Show at a time
	private readonly Lock _showing = new();

	// 1.7.1: the latest asked for wins: a first read's pages come every few seconds, and an older one's collections can be
	// made after a newer one's
	private int _asked;
	private int _shown;
	private IndexSnapshot? _latest;

	// 1.7.1 blocker: the newest show, of which index, and done when it is on the page (or a newer one is)
	private sealed record PendingShow(IndexSnapshot Snapshot, Task Shown);

	private volatile PendingShow? _pending;

	/// <summary>Tests: awaited before the collections of an index are made (a test holds one show there).</summary>
	internal Func<IndexSnapshot, Task>? Making { get; init; }

	// the collections are made off the UI thread (BuildAllAsync), then shown on it
	private async Task ShowAsync(IndexSnapshot? snapshot, Built? built = null)
	{
		var asked = Interlocked.Increment(ref _asked);
		_latest = snapshot ?? _latest;
		var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		if (snapshot is not null)
		{
			_pending = new PendingShow(snapshot, shown.Task);
		}

		try
		{
			if (snapshot is not null && Making is { } making)
			{
				await making(snapshot);
			}

			IReadOnlyList<SmartCollection> made = [];
			IReadOnlyList<SmartCollection> pinned = [];
			var lineup = Lineup.Empty; // 1.8 (P1-1)
			Task<IReadOnlyList<SmartCollection>>? later = null; // the re-review
			if (snapshot is not null)
			{
				try
				{
					var both = built ?? await Build(snapshot);
					later = both.Later;
					made = Unboxed(both.All);
					lineup = both.Lineup;
					pinned = await PinnedOnDemand(snapshot, made); // 1.7.3
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					throw new MadeByNixieException(ex); // 1.7.3: said as Made by Nixie's (the load's catch)
				}
			}

			lock (_showing)
			{
				if (asked < _shown)
				{
					return;
				}

				_shown = asked;
				ShowLocked(made, pinned, lineup);
				// 1.7.3 fix round (BH-173-m1): a whole index that made nothing says so (none of a first read's pages, nor a show
				// without an index: a read that failed)
				_nixieEmptyText = EveryKindOff() ? NixieOffText : NixieEmptyText;
				IsNixieEmpty = snapshot is { Reading: null } && made.Count == 0 && !Place.SeeAll;
				SayNixieEmpty();
				if (Rows.Count > 0 || GridTiles.Count > 0)
				{
					_firstRows.TrySetResult();
				}

				// BH-171-q2: how long the page took to its first Made by Nixie rows (a duration only)
				if (!_nixieRowsLogged && (Rows.Any(r => r.Section == CollectionsSection.MadeByNixie) || GridTiles.Any(t => t.IsNixie)))
				{
					_nixieRowsLogged = true;
					_log.LogInformation("Collections: first Made by Nixie rows in {Ms} ms ({From})", _opened.ElapsedMilliseconds,
						snapshot?.Reading is not null ? "first read, its pages so far" : _firstVisit ? "first read" : "the saved index");
				}
			}

			if (later is not null && snapshot is not null)
			{
				SpotlightLater = ShowSpotlightLater(snapshot, later); // the re-review: the page first, the Spotlight after
			}
		}
		finally
		{
			shown.TrySetResult();
		}
	}

	/// <param name="pinnedOnDemand">1.7.3: the pinned collections of kinds no longer published (Saved shows them).</param>
	/// <param name="lineup">1.8 (P1-1): the week's lineup: This week's row, the first of Made by Nixie.</param>
	private void ShowLocked(IReadOnlyList<SmartCollection> made, IReadOnlyList<SmartCollection> pinnedOnDemand, Lineup lineup)
	{
		MadeByNixieCount = made.Count;
		var rows = new List<CollectionsRow>();
		var names = CollectionNames.DisplayAll(_boxSets);
		var server = _boxSets.Select((x, i) => CollectionTile.Server(x, names[i])).ToList();
		var playlists = _playlists.Select(x => CollectionTile.Server(x, x.Name ?? "")).ToList();
		// 1.8 (P1-1): a collection This week holds is the lineup's everywhere on the page (its titles turned for the day or best
		// first: one mosaic for one collection, in This week and in its own row)
		_lineupDate = lineup == Lineup.Empty ? _lineupDate : lineup.Day.Date;
		var chosen = lineup.Collections.ToDictionary(x => x.Id);
		made = [.. made.Select(x => chosen.GetValueOrDefault(x.Id) ?? x)];
		var nixie = made.Select(CollectionTile.Made).ToList();
		var pinned = made.Where(x => _favorites.Contains(x.Id)).Concat(pinnedOnDemand).Select(CollectionTile.Made);
		var saved = _saved.Select(CollectionTile.Made).Concat(pinned).ToList();

		if (Place.SeeAll)
		{
			List<CollectionTile> grid = Place.Section switch
			{
				CollectionsSection.Server => server,
				CollectionsSection.Playlists => playlists,
				CollectionsSection.Saved => saved,
				_ when Place.Kind is { } kind => [.. nixie.Where(x => x.Nixie!.Kind == kind)],
				_ => nixie,
			};
			grid = [.. SortDirections.Apply(grid, GridReversed)]; // 1.8: the grid's direction (its reverse toggle)
			// 1.7.1 (review M1): the same tiles take the newer contents in place (the keyboard stays on its tile)
			if (grid.Count == GridTiles.Count && grid.Select(t => t.Title).SequenceEqual(GridTiles.Select(t => t.Title)))
			{
				for (var i = 0; i < grid.Count; i++)
				{
					GridTiles[i].Update(grid[i]);
				}
			}
			else
			{
				GridTiles = grid;
			}

			Rows.Clear();
			return;
		}

		void Add(string title, CollectionsSection section, SmartCollectionKind? kind, List<CollectionTile> tiles, string heading = "")
		{
			if (tiles.Count > 0)
			{
				// 1.7 keyless: the Awards row always ends in See all (at most nine awards, so never "more", but its grid shows them together)
				rows.Add(new CollectionsRow(title, section, kind, [.. tiles.Take(RowLength)], tiles.Count > RowLength || kind == SmartCollectionKind.Award) { Heading = heading });
			}
		}

		Add(SectionTitle(CollectionsSection.Server), CollectionsSection.Server, null, server);
		Add(SectionTitle(CollectionsSection.Playlists), CollectionsSection.Playlists, null, playlists);
		var heading = "Made by Nixie";
		// 1.8 (P1-1): This week first: the week's lineup (ten at most, best first), Home's rows among them. Never a See all: it
		// is the whole lineup
		var week = lineup.Collections.Select(CollectionTile.Made).ToList();
		if (week.Count > 0)
		{
			Add(ThisWeekTitle, CollectionsSection.MadeByNixie, null, week, heading);
			heading = "";
		}

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

		// 1.7 trending: Trending, Popular, Most anticipated and Box office this week (a row only when it has a collection)
		foreach (var kind in Outside.TrendingBuilder.PageOrder)
		{
			var ofKind = nixie.Where(x => x.Nixie!.Kind == kind).ToList();
			if (ofKind.Count > 0)
			{
				Add(SmartCollectionKinds.Plural(kind), CollectionsSection.MadeByNixie, kind, ofKind, heading);
				heading = "";
			}
		}
		// end 1.7 trending

		Add(SectionTitle(CollectionsSection.Saved), CollectionsSection.Saved, null, saved);

		// 1.7.1: row by row: a row that is the same stays (with its tiles, and the keyboard on one of them), a new or changed
		// one goes in its place. A first read's pages, then its people pass, add rows while the page shows; making them all
		// again sent the keyboard to Back
		// 1.8 (BH-180-M1): the same row is its title, section, kind, heading and See all, whatever its tiles: it stays and takes
		// the newer tiles in place (CollectionsRow.Take). This week changes whenever the lineup does (the people step, the day's
		// read, the Spotlight after the page), and a row made again for it sent the keyboard on its tile to Back
		for (var i = 0; i < rows.Count; i++)
		{
			if (i < Rows.Count && Rows[i].IsRow(rows[i]))
			{
				Rows[i].Take(rows[i]);
				continue;
			}

			var later = -1;
			for (var k = i + 1; k < Rows.Count && later < 0; k++)
			{
				later = Rows[k].IsRow(rows[i]) ? k : -1;
			}

			if (later >= 0)
			{
				Rows.Move(later, i);
				Rows[i].Take(rows[i]);
			}
			else if (i < Rows.Count && !rows.Skip(i + 1).Any(r => Rows[i].IsRow(r)))
			{
				Rows[i] = rows[i];
			}
			else
			{
				Rows.Insert(i, rows[i]);
			}
		}

		while (Rows.Count > rows.Count)
		{
			Rows.RemoveAt(Rows.Count - 1);
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

// 1.7.3
/// <summary>
/// The trending lists' collections matched against the index, off the UI thread, inside a catch of their own, as each kind's
/// builder is (<see cref="SmartCollectionCatalog"/>'s per-kind catch): a throw there leaves out the trending collections
/// only, and is said once in the log (the exception's type: no titles). The Collections page and Home took every Made by
/// Nixie row away with it.
/// </summary>
internal static class TrendingMatch
{
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ILogger, object> Said = new();

	public static async Task<T> Safely<T>(Func<T> match, T none, ILogger? log)
	{
		try
		{
			return await Task.Run(match);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			if (log is not null && Said.TryAdd(log, Said))
			{
				log.LogWarning("Made by Nixie: the trending collections couldn't be matched ({Error}); the other kinds still are", ex.GetType().Name);
			}

			return none;
		}
	}
}
// end 1.7.3
