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

	/// <summary>1.7.1 (review M1): the same row (its tiles' titles), made of a newer index: each tile takes the newer one's contents.</summary>
	internal void Update(CollectionsRow newer)
	{
		for (var i = 0; i < Tiles.Count && i < newer.Tiles.Count; i++)
		{
			Tiles[i].Update(newer.Tiles[i]);
		}
	}
}

/// <summary>
/// Nixie 1.6 (plan 4.1): the Collections page: every collection in one place: the server's box sets (tidied, "6 films · 3
/// left"), Playlists, Made by Nixie by kind (each row ending in "See all"), and Saved (saved filters and pinned Nixie
/// collections). A franchise the admin boxed shows once, as the box set. The index may take a while on a first visit: the
/// server's rows show at once and "Made by Nixie" says it is reading.
/// </summary>
public partial class CollectionsViewModel(IJellyfinClient jellyfinClient, GlobalCommands commands, LibraryIndex index, SmartCollectionStore store,
	ILocalSettingsService? settings = null, TimeProvider? time = null, ILogger<CollectionsViewModel>? logger = null) : ObservableObject, INavigationAware
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

	/// <summary>1.7.1 (BH-171-m1): the line under the title: while the first read runs, or when it stopped part way.</summary>
	public bool ShowsReadingLine => IsReadingIndex || IsReadStopped;

	partial void OnIsReadingIndexChanged(bool value) => OnPropertyChanged(nameof(ShowsReadingLine));

	partial void OnIsReadStoppedChanged(bool value) => OnPropertyChanged(nameof(ShowsReadingLine));

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
		index.Changed -= OnIndexChanged;
		index.Progress -= OnIndexProgress; // 1.7.1
		Outside.OutsideListsSnapshot.SharedChanged -= OnOutsideChanged; // 1.7 keyless
		UnwatchTrending(); // 1.7 trending
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
		index.Progress -= OnIndexProgress; // 1.7.1: how far a first read has got
		index.Progress += OnIndexProgress;
		_opened = System.Diagnostics.Stopwatch.StartNew();
		_loaded = false;
		_nixieRowsLogged = false;
		IsReadStopped = false;
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
		CollectionsSection.Server => "Your server's collections",
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
		var saved = store.SavedAsync();
		var favorites = store.FavoritesAsync();
		var known = index.SavedAsync(); // 1.7.1: the index in memory, else the saved one (never the server's: that comes behind it)
		// 1.7.1: its collections made while the server's sets come (the franchises an admin boxed are left out once they are in)
		var building = BuildSoon(known);
		await Task.WhenAll(boxSets, playlists, saved, favorites, building);
		var current = known.Result;
		if (boxSets.Result is null && playlists.Result is null && current is null)
		{
			return LoadMessages.CouldNotLoad("your collections");
		}

		_boxSets = boxSets.Result?.Items ?? [];
		_playlists = playlists.Result?.Items ?? [];
		_saved = saved.Result;
		_favorites = favorites.Result;
		IsReadingIndex = current is null;
		_firstVisit = current is null;
		ReadingText = ReadingStart;
		// a newer one that came in meanwhile; or, opened again during a first read, its titles so far
		var latest = index.Current ?? current ?? index.ReadingSoFar;
		if (latest?.Reading is { } progress)
		{
			ReadingText = Reading_(progress);
		}

		await ShowAsync(latest, ReferenceEquals(latest, current) ? building.Result : null);
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
	public async Task<IReadOnlyList<SmartCollection>> MadeByNixie(IndexSnapshot snapshot) => Unboxed(await Build(snapshot));

	// 1.7.1: every collection of this index, the boxed franchises too (Unboxed leaves them out), so it can be made before the
	// server's sets are in
	private async Task<IReadOnlyList<SmartCollection>> Build(IndexSnapshot snapshot)
	{
		var made = await SmartCollectionCatalog.BuildAllAsync(snapshot, SmartCollectionCatalog.Now(_time), null, CollectionSettings.KindsOff(settings)); // 1.6.1 (BH-160-m1): the local date
		// 1.7 trending: the public lists' collections (Trending, Popular, Most anticipated, Box office), matched on this PC
		if (Outside.TrendingServices.Collections is { } trending)
		{
			var lists = await Task.Run(() => trending.For(snapshot));
			made = [.. made, .. lists];
		}
		// end 1.7 trending
		return made;
	}

	// 1.7.1: a franchise the admin boxed shows once, as the box set
	private IReadOnlyList<SmartCollection> Unboxed(IReadOnlyList<SmartCollection> made)
	{
		var boxed = _boxSets.Select(x => IndexEntry.ProviderId(x, "Tmdb")).OfType<string>().ToHashSet();
		return boxed.Count == 0 ? made : [.. made.Where(x => !(x.Kind == SmartCollectionKind.Franchise && x.TmdbCollection is { } id && boxed.Contains(id)))];
	}

	// 1.7.1: the saved index's collections, made as soon as it is read from this PC; none without one
	private async Task<IReadOnlyList<SmartCollection>?> BuildSoon(Task<IndexSnapshot?> known) =>
		await known is { } snapshot ? await Build(snapshot) : null;

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
	private async Task ShowAsync(IndexSnapshot? snapshot, IReadOnlyList<SmartCollection>? built = null)
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

			var made = snapshot is null ? [] : Unboxed(built ?? await Build(snapshot));
			lock (_showing)
			{
				if (asked < _shown)
				{
					return;
				}

				_shown = asked;
				ShowLocked(made);
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
		}
		finally
		{
			shown.TrySetResult();
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
			List<CollectionTile> grid = Place.Section switch
			{
				CollectionsSection.Server => server,
				CollectionsSection.Playlists => playlists,
				CollectionsSection.Saved => saved,
				_ when Place.Kind is { } kind => [.. nixie.Where(x => x.Nixie!.Kind == kind)],
				_ => nixie,
			};
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
		static bool Same(CollectionsRow a, CollectionsRow b) => a.Title == b.Title && a.HasSeeAll == b.HasSeeAll && a.Heading == b.Heading
			&& a.Tiles.Select(t => t.Title).SequenceEqual(b.Tiles.Select(t => t.Title));
		for (var i = 0; i < rows.Count; i++)
		{
			if (i < Rows.Count && Same(Rows[i], rows[i]))
			{
				continue;
			}

			var later = -1;
			for (var k = i + 1; k < Rows.Count && later < 0; k++)
			{
				later = Same(Rows[k], rows[i]) ? k : -1;
			}

			if (later >= 0)
			{
				Rows.Move(later, i);
			}
			else if (i < Rows.Count && !rows.Skip(i + 1).Any(r => Same(Rows[i], r)))
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

		// 1.7.1 (review M1): a row that stayed takes the newer contents of its tiles (a first read's "Highly rated" kept its first
		// page's 80 films through the whole read, in its count and when opened)
		for (var i = 0; i < rows.Count; i++)
		{
			if (!ReferenceEquals(Rows[i], rows[i]))
			{
				Rows[i].Update(rows[i]);
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
