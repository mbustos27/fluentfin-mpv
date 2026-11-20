using System.Collections.ObjectModel;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicData;
using DynamicData.Binding;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;
using ReactiveUI;

namespace FluentFin.Core.ViewModels;

public partial class LibraryViewModel : ObservableObject, INavigationAware
{
	private readonly SourceCache<BaseItemViewModel, Guid> _itemsCache = new(x => x.Id ?? Guid.Empty);
	private readonly ReadOnlyObservableCollection<BaseItemViewModel> _items;


	public LibraryViewModel(IJellyfinClient jellyfinClient, ILocalSettingsService? localSettings = null)
	{
		JellyfinClient = jellyfinClient;
		_localSettings = localSettings;

		// tie-break so equal keys (e.g. unrated, never played) keep a stable order
		// Nixie 1.3.1: a playlist keeps its own order until a sort is picked (InPlaylistOrder)
		var comparer = this.WhenAnyValue(x => x.SortBy, x => x.Order, x => x.InPlaylistOrder)
						   .Select(x => (IComparer<BaseItemViewModel>)(x.Item3 ? PlaylistComparer() : GetComparer((x.Item1, x.Item2))).ThenByAscending(LibraryBrowse.SortKey).ThenByAscending(i => i.Id ?? Guid.Empty))
						   .Do(x =>
						   {
							   _comparer = x; // Seaglass 1.1.4: the A-Z jump orders the library the same way
							   UpdateBrowse();
						   });

		var filter = this.WhenAnyValue(x => x.Filter)
			.WhereNotNull()
			.SelectMany(x => x.WhenAnyPropertyChanged())
			.Where(x => x is not null)
			.Select(x => (Func<BaseItemViewModel, bool>)x!.IsVisible);

		Filter.WhenAnyPropertyChanged()
			.Subscribe(x =>
			{
				_itemsCache.Refresh();
				FiltersChanged();
			});

		// Seaglass 1.1.4 (layout audit, item 6): one continuously scrolling grid, as every streaming app has, not pages of 100
		// under a numbered pager: the library is one request already (every title, sorted and filtered here), and the grid
		// (an ItemsRepeater) draws only the cards near the view
		_itemsCache
			.Connect()
			.RefCount()
			.Filter(Filter.IsVisible)
			.SortAndBind(out _items, comparer)
			.Subscribe(_ =>
			{
				// Seaglass scrolling: the cards in the (filtered) library, for Narrator's "n of m"
				TotalCount = _items.Count;
				UpdateBrowse();
			});
	}

	/// <summary>Nixie 1.3.1: each item's place in the playlist (the server's order), while a playlist is shown.</summary>
	private Dictionary<Guid, int> _playlistPlaces = [];

	private SortExpressionComparer<BaseItemViewModel> PlaylistComparer()
	{
		var places = _playlistPlaces;
		return SortExpressionComparer<BaseItemViewModel>.Ascending(x => places.TryGetValue(x.Id ?? Guid.Empty, out var place) ? place : int.MaxValue);
	}

	/// <summary>
	/// Nixie 1.3.1: a playlist is shown in its own order (the user's, as the server keeps it), until a sort is picked. The
	/// Sort button then says "Playlist order", and there is no A-Z jump.
	/// </summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(SortName), nameof(SortButtonName))]
	public partial bool InPlaylistOrder { get; private set; }

	/// <summary>The Sort button's name for the playlist's own order.</summary>
	public const string PlaylistOrderName = "Playlist order";

	private SortExpressionComparer<BaseItemViewModel> GetComparer((ItemSortBy, SortOrder) sortDescription)
	{
		return sortDescription switch
		{
			// Nixie 1.3.1: by the sort name, so "The Matrix" files under M (LibraryBrowse.SortKey)
			(ItemSortBy.Name, SortOrder.Ascending) => SortExpressionComparer<BaseItemViewModel>.Ascending(LibraryBrowse.SortKey),
			(ItemSortBy.CommunityRating, SortOrder.Ascending) => SortExpressionComparer<BaseItemViewModel>.Ascending(x => x.CommunityRating ?? 0),
			(ItemSortBy.CriticRating, SortOrder.Ascending) => SortExpressionComparer<BaseItemViewModel>.Ascending(x => x.CriticRating ?? 0),
			(ItemSortBy.DateCreated, SortOrder.Ascending) => SortExpressionComparer<BaseItemViewModel>.Ascending(x => x.DateCreated ?? new DateTimeOffset()),
			(ItemSortBy.DatePlayed, SortOrder.Ascending) => SortExpressionComparer<BaseItemViewModel>.Ascending(x => x.UserData?.LastPlayedDate ?? new DateTimeOffset()),
			(ItemSortBy.PlayCount, SortOrder.Ascending) => SortExpressionComparer<BaseItemViewModel>.Ascending(x => x.UserData?.PlayCount ?? 0),
			(ItemSortBy.PremiereDate, SortOrder.Ascending) => SortExpressionComparer<BaseItemViewModel>.Ascending(x => x.PremiereDate ?? new DateTimeOffset()),
			(ItemSortBy.Runtime, SortOrder.Ascending) => SortExpressionComparer<BaseItemViewModel>.Ascending(x => x.RunTimeTicks ?? 0),

			(ItemSortBy.Name, SortOrder.Descending) => SortExpressionComparer<BaseItemViewModel>.Descending(LibraryBrowse.SortKey),
			(ItemSortBy.CommunityRating, SortOrder.Descending) => SortExpressionComparer<BaseItemViewModel>.Descending(x => x.CommunityRating ?? 0),
			(ItemSortBy.CriticRating, SortOrder.Descending) => SortExpressionComparer<BaseItemViewModel>.Descending(x => x.CriticRating ?? 0),
			(ItemSortBy.DateCreated, SortOrder.Descending) => SortExpressionComparer<BaseItemViewModel>.Descending(x => x.DateCreated ?? new DateTimeOffset()),
			(ItemSortBy.DatePlayed, SortOrder.Descending) => SortExpressionComparer<BaseItemViewModel>.Descending(x => x.UserData?.LastPlayedDate ?? new DateTimeOffset()),
			(ItemSortBy.PlayCount, SortOrder.Descending) => SortExpressionComparer<BaseItemViewModel>.Descending(x => x.UserData?.PlayCount ?? 0),
			(ItemSortBy.PremiereDate, SortOrder.Descending) => SortExpressionComparer<BaseItemViewModel>.Descending(x => x.PremiereDate ?? new DateTimeOffset()),
			(ItemSortBy.Runtime, SortOrder.Descending) => SortExpressionComparer<BaseItemViewModel>.Descending(x => x.RunTimeTicks ?? 0),

			_ => SortExpressionComparer<BaseItemViewModel>.Ascending(LibraryBrowse.SortKey),
		};
	}

	public ReadOnlyObservableCollection<BaseItemViewModel> Items => _items;

	// ---- Seaglass 1.1.4: what the bar says, the A-Z jump, and the way back to the card the user left -------------------

	private IComparer<BaseItemViewModel>? _comparer;
	private BaseItemDto? _library;

	private readonly ILocalSettingsService? _localSettings;

	/// <summary>The sort in use, as its menu says it ("Title").</summary>
	public string SortName => InPlaylistOrder ? PlaylistOrderName : LibraryBrowse.SortName(SortBy);

	/// <summary>Nixie 1.4.0 (item 8): what the Sort button shows in every theme: "Title · A to Z", "Date added · newest first".</summary>
	public string SortText => InPlaylistOrder ? PlaylistOrderName : LibraryBrowse.SortText(SortBy, Order);

	/// <summary>The direction in words: "A to Z", "newest first".</summary>
	public string DirectionWords => LibraryBrowse.DirectionWords(SortBy, Order);

	/// <summary>The other direction in words: "Z to A", "oldest first".</summary>
	public string OtherDirectionWords => LibraryBrowse.OtherDirectionWords(SortBy, Order);

	/// <summary>Up for ascending, down for descending, beside the sort's name.</summary>
	public string SortGlyph => LibraryBrowse.SortGlyph(Order);

	/// <summary>The Sort button for Narrator, its words first: "Title, A to Z, Sort".</summary>
	public string SortButtonName => InPlaylistOrder ? $"{PlaylistOrderName}, Sort" : LibraryBrowse.SortButtonName(SortBy, Order);

	/// <summary>The genres, tags, years and ratings picked in Filter.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(FilterText), nameof(FilterButtonName))]
	public partial int ActiveFilters { get; set; }

	public string FilterText => LibraryBrowse.FilterText(ActiveFilters);

	public string FilterButtonName => LibraryBrowse.FilterButtonName(ActiveFilters);

	/// <summary>"368 movies", or "12 of 368 movies" with filters on.</summary>
	[ObservableProperty]
	public partial string CountText { get; set; } = "";

	/// <summary>Nixie 1.4.0: the library's own count, filters or not ("368 movies"): the head's, while the applied-filter row says what is shown.</summary>
	[ObservableProperty]
	public partial string LibraryCountText { get; set; } = "";

	/// <summary>
	/// Nixie 1.4.0: whether this visit is the page loaded again in place (R, F5: a new view model for the same library), set by
	/// the page: the sort, the filters and the card are put back as for Back (they were lost to a refresh).
	/// </summary>
	public Func<bool>? ReloadedInPlace { get; set; }

	/// <summary>
	/// Nixie 1.4.0 (integration): whether the place put back after R leaves the keyboard alone (the user moved first: a key in
	/// the search box, Down), as the refresh's own restore does; the page asks it before focusing the card. Scrolling back
	/// still happens.
	/// <para>
	/// Nixie 1.4.0 (bug hunt m4: a slow library's first poster took the keyboard out of the search box): for the focus the
	/// load asks for (the arrival's first poster, Back's or R's poster) it is also the app's one arrival rule
	/// (<see cref="LoadFocusYields"/>, every theme's library page alike); never for an A to Z jump, which the user asked for.
	/// The page's own rule, when it sets one, is asked too.
	/// </para>
	/// </summary>
	public Func<bool>? FocusYields
	{
		get => () => FocusYieldsNow(_focusFromLoad, _pageFocusYields?.Invoke() == true, LoadFocusYields?.Invoke(this) == true);
		set => _pageFocusYields = value;
	}

	/// <summary>Nixie 1.4.0 (bug hunt m4): whether the focus asked for leaves the keyboard alone: only a load's, by either rule.</summary>
	public static bool FocusYieldsNow(bool fromLoad, bool pageRule, bool arrivalRule) => fromLoad && (pageRule || arrivalRule);

	/// <summary>
	/// Nixie 1.4.0 (bug hunt m4): the app's one arrival rule for a library's load focus (the app sets it: ArrivalFocus): true when
	/// the keyboard stays where it is (the user moved since the page came, a text box has it, the page was left).
	/// </summary>
	public static Func<LibraryViewModel, bool>? LoadFocusYields { get; set; }

	private Func<bool>? _pageFocusYields;

	// the focus asked for came from the load (the arrival, Back, R), not from an A to Z jump
	private bool _focusFromLoad;

	// ---- Nixie 1.4.0 (the UX review's X4, item 2): the filters on, said; their counts honest; nothing left, said ----------

	/// <summary>Every filter on, one chip each ("Unwatched ✕", "Comedy ✕"), for the applied-filter row.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasAppliedFilters))]
	public partial IReadOnlyList<AppliedFilter> AppliedFilters { get; set; } = [];

	/// <summary>The applied-filter row shows (a filter is on, over a library that has titles).</summary>
	public bool HasAppliedFilters => AppliedFilters.Count > 0 && _itemsCache.Count > 0;

	/// <summary>The applied-filter row's count line (a polite live region): "Showing 223 of 320 movies".</summary>
	[ObservableProperty]
	public partial string ShowingText { get; set; } = "";

	/// <summary>The library has titles, and the filters on leave none: said ("No unwatched movies in Comedy"), not a blank grid.</summary>
	[ObservableProperty]
	public partial bool NoMatches { get; set; }

	/// <summary>The no-matches state's title: "No unwatched movies in Comedy".</summary>
	[ObservableProperty]
	public partial string NoMatchesText { get; set; } = "";

	/// <summary>The no-matches state's message.</summary>
	public string NoMatchesMessage => LibraryNoMatches.Message;

	/// <summary>The titles not watched yet among those the other filters leave: the Unwatched switch's count.</summary>
	[ObservableProperty]
	public partial int UnwatchedCount { get; set; }

	/// <summary>A chip's ✕: that one filter off.</summary>
	[RelayCommand]
	public void RemoveFilter(AppliedFilter? filter)
	{
		if (filter is not null)
		{
			Filter.Remove(filter);
		}
	}

	private void FiltersChanged()
	{
		ActiveFilters = LibraryBrowse.ActiveFilters(Filter);
		AppliedFilters = Filter.Applied();
		Recount();
		UpdateBrowse();
	}

	/// <summary>The Filter menu's counts and the Unwatched switch's, against the other filters on.</summary>
	private void Recount()
	{
		var items = _itemsCache.Items.ToList();
		foreach (var facet in Facets)
		{
			facet.Recount(items, Filter);
		}

		UnwatchedCount = items.Count(x => x.UserData?.Played != true && Filter.Matches(x, FilterGroup.Unwatched));
	}

	// ---- Nixie 1.4.0 (item 13): the A to Z in one shape, the letter in view, where the view is ----------------------------

	/// <summary>The A to Z's tiles: # and A to Z always (those with no titles dimmed), then any other key; none when <see cref="LetterRow"/> is not Letters.</summary>
	[ObservableProperty]
	public partial IReadOnlyList<LetterTile> LetterTiles { get; set; } = [];

	/// <summary>What the row under the toolbar holds: the A to Z (sorted by title, 48 titles or more), else the two directions (its height kept, so a sort never moves the grid), else nothing (nothing shown).</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ShowsLetters), nameof(ShowsDirections))]
	public partial LetterRowMode LetterRow { get; set; }

	public bool ShowsLetters => LetterRow == LetterRowMode.Letters;

	public bool ShowsDirections => LetterRow == LetterRowMode.Directions;

	/// <summary>The letter the first title in view files under (sorted by title), else null.</summary>
	[ObservableProperty]
	public partial string? CurrentLetter { get; set; }

	/// <summary>Where the view is: "M · 324 of 1,000" (large libraries), empty before the grid has scrolled.</summary>
	[ObservableProperty]
	public partial string PositionText { get; set; } = "";

	private int _firstShown;

	/// <summary>The page says which title is first in view (on every scroll): the A to Z marks its letter, the readout says where.</summary>
	public void ShowingFrom(int index)
	{
		if (_items.Count == 0 || index < 0)
		{
			MarkLetter(null);
			CurrentLetter = null;
			PositionText = "";
			return;
		}

		_firstShown = index = Math.Min(index, _items.Count - 1);
		var letter = SortBy == ItemSortBy.Name && !InPlaylistOrder ? LibraryBrowse.LetterOf(LibraryBrowse.SortKey(_items[index])) : null;
		MarkLetter(letter); // the tiles first: a page marks its buttons when CurrentLetter changes
		CurrentLetter = letter;
		PositionText = LibraryBrowse.PositionText(letter, index, _items.Count);
	}

	private void MarkLetter(string? letter)
	{
		foreach (var tile in LetterTiles)
		{
			tile.IsCurrent = letter is not null && string.Equals(tile.Letter, letter, StringComparison.OrdinalIgnoreCase);
		}
	}

	/// <summary>Type-to-jump: the first title, from <paramref name="from"/> on, whose name starts with what was typed, else null.</summary>
	public int? TypeAhead(string typed, int from = 0) => LibraryBrowse.TypeAhead(_items, typed, from);

	/// <summary>
	/// Nixie 1.4.0 (the UX review's X8, item 4): the library just opened (not Back): its first poster gets the focus, by the
	/// program (a ring only for a keyboard user), once the grid is in. The page reads it with <see cref="FocusRequest"/>.
	/// </summary>
	public bool ArrivalFocus { get; set; }

	// ---- Nixie 1.4.0 (items 12 and 13): the library's view, kept per library ----------------------------------------------

	/// <summary>Small, medium or large posters.</summary>
	[ObservableProperty]
	public partial LibraryDensity Density { get; set; } = LibraryDensity.Medium;

	/// <summary>A grid of posters, or a list.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsList))]
	public partial LibraryLayout Layout { get; set; } = LibraryLayout.Grid;

	public bool IsList => Layout == LibraryLayout.List;

	/// <summary>Cinematic: 16:9 stills or posters.</summary>
	[ObservableProperty]
	public partial LibraryArt Art { get; set; } = LibraryArt.Stills;

	[RelayCommand]
	public void SetDensity(LibraryDensity density)
	{
		Density = density;
		SaveView();
	}

	[RelayCommand]
	public void SetLayout(LibraryLayout layout)
	{
		Layout = layout;
		SaveView();
	}

	[RelayCommand]
	public void SetArt(LibraryArt art)
	{
		Art = art;
		SaveView();
	}

	private void SaveView() => LibraryViews.Save(_localSettings, _library?.Id, new LibraryView(Density, Layout, Art));

	private void ReadView()
	{
		var view = LibraryViews.Read(_localSettings, _library?.Id);
		Density = view.Density;
		Layout = view.Layout;
		Art = view.Art;
	}

	/// <summary>The A-Z jump: the letters the library's titles start with, while it is sorted by name (else none).</summary>
	[ObservableProperty]
	public partial IReadOnlyList<LetterJump> Letters { get; set; } = [];

	/// <summary>
	/// The card the page should give the focus to (an A-Z jump, the way back from an item), or -1. Set again
	/// (to the same value too) for each jump: the page acts on the change.
	/// </summary>
	[ObservableProperty]
	public partial int FocusRequest { get; set; } = -1;

	/// <summary>Coming back to the library (Back, Forward): the page sets it before the library loads; its place is then put back.</summary>
	public bool ComingBack { get; set; }

	/// <summary>Seaglass 1.1.4: how far down the grid was when the user left (Back): the page scrolls there before it focuses the card; then null.</summary>
	public double? ReturnOffset { get; set; }


	partial void OnSortByChanged(ItemSortBy value) => SortWordsChanged();

	partial void OnOrderChanged(SortOrder value) => SortWordsChanged();

	partial void OnInPlaylistOrderChanged(bool value) => SortWordsChanged();

	private void SortWordsChanged()
	{
		OnPropertyChanged(nameof(SortName));
		OnPropertyChanged(nameof(SortText));
		OnPropertyChanged(nameof(SortGlyph));
		OnPropertyChanged(nameof(SortButtonName));
		OnPropertyChanged(nameof(SortOrderButtonName));
		OnPropertyChanged(nameof(DirectionWords));
		OnPropertyChanged(nameof(OtherDirectionWords));
	}

	/// <summary>The whole library as the grid shows it across its pages: filtered and in the sort's order.</summary>
	private List<BaseItemViewModel> Ordered()
	{
		var items = _itemsCache.Items.Where(Filter.IsVisible);
		return _comparer is { } comparer ? [.. items.Order(comparer)] : [.. items];
	}

	private void UpdateBrowse()
	{
		var all = _itemsCache.Count;
		var shown = TotalCount;
		CountText = all == 0 ? "" : LibraryBrowse.CountText(shown, all, _library?.CollectionType);
		LibraryCountText = all == 0 ? "" : LibraryBrowse.CountText(all, all, _library?.CollectionType);
		ShowingText = all == 0 ? "" : LibraryBrowse.ShowingText(shown, all, _library?.CollectionType);
		NoMatches = all > 0 && shown == 0 && !IsLoading;
		NoMatchesText = NoMatches ? LibraryNoMatches.Title(Filter, _library?.CollectionType) : "";
		OnPropertyChanged(nameof(HasAppliedFilters));

		// Nixie 1.4.0 (item 13): the A to Z from 48 titles shown, sorted by title; else the row holds the two directions
		var byName = SortBy == ItemSortBy.Name && !InPlaylistOrder;
		LetterRow = all == 0 || shown == 0 ? LetterRowMode.None : byName && shown >= LibraryBrowse.LettersFrom ? LetterRowMode.Letters : LetterRowMode.Directions;
		var letters = byName && all > 0 ? LibraryBrowse.Letters(Ordered().Select(LibraryBrowse.SortKey)) : [];
		if (!letters.SequenceEqual(Letters))
		{
			Letters = letters;
			_lastJump = null;
		}

		var tiles = LetterRow == LetterRowMode.Letters ? Letters : [];
		if (LetterTiles.Count == 0 && tiles.Count > 0 || !LetterTiles.Where(t => t.HasTitles).Select(t => t.Jump).SequenceEqual(tiles))
		{
			LetterTiles = tiles.Count == 0 ? [] : LibraryBrowse.Tiles(tiles);
		}

		ShowingFrom(Math.Min(_firstShown, Math.Max(0, _items?.Count - 1 ?? 0)));
	}

	/// <summary>A-Z: the first title with that letter gets the focus (the grid scrolls to it).</summary>
	[RelayCommand]
	public void JumpTo(LetterJump? jump)
	{
		if (jump is null)
		{
			return;
		}

		// the same letter again goes on to its next run of titles (a key the sort does not keep together), then round
		var run = _lastJump is { } last && last.Letter == jump.Letter ? (last.Run + 1) % jump.Runs.Count : 0;
		_lastJump = (jump.Letter, run);
		ShowIndex(jump.Runs[run]);
	}

	private (string Letter, int Run)? _lastJump;

	/// <param name="fromLoad">Nixie 1.4.0 (bug hunt m4): the load asks (the arrival, Back, R), not the user (a jump): the
	/// arrival rule applies (<see cref="FocusYields"/>).</param>
	private void ShowIndex(int index, bool fromLoad = false)
	{
		_loadAsking = fromLoad;
		_focusFromLoad = fromLoad;
		try
		{
			// the same card again still asks the page to focus it
			if (FocusRequest == index)
			{
				OnPropertyChanged(nameof(FocusRequest));
			}
			else
			{
				FocusRequest = index;
			}
		}
		finally
		{
			_loadAsking = false;
		}
	}

	private bool _loadAsking;

	// a card asked for by anyone but the load (a page's type-to-jump sets FocusRequest itself) is the user's
	partial void OnFocusRequestChanged(int value)
	{
		if (value >= 0)
		{
			_focusFromLoad = _loadAsking;
		}
	}

	/// <summary>
	/// The user leaves the library for an item: where they are (the sort, the filters, how far down <paramref name="offset"/>,
	/// the card <paramref name="item"/>) is kept for Back.
	/// </summary>
	public void LeftAt(BaseItemViewModel? item, double offset = 0)
	{
		if (_library?.Id is not { } library)
		{
			return;
		}

		LibraryPlaces.Remember(JellyfinClient.UserId, library, new LibraryPlace(SortBy, Order, [.. Filter.Genres], [.. Filter.Tags], [.. Filter.Years],
			[.. Filter.OfficialRatings], item?.Id, offset, Filter.Unwatched, Filter.Favorites));
	}

	/// <summary>Back in the library: its sort and filters as they were, the grid as far down, and the card that was left gets the focus.</summary>
	private void PutBack(LibraryPlace place)
	{
		SortBy = place.SortBy;
		Order = place.Order;
		Replace(Filter.Genres, place.Genres);
		Replace(Filter.Tags, place.Tags);
		Replace(Filter.Years, place.Years);
		Replace(Filter.OfficialRatings, place.OfficialRatings);

		Filter.Unwatched = place.Unwatched;
		Filter.Favorites = place.Favorites;

		var index = place.ItemId is { } id ? Ordered().FindIndex(x => x.Id == id) : -1;
		ReturnOffset = place.Offset;
		if (index >= 0)
		{
			ShowIndex(index, fromLoad: true);
		}
	}

	private static void Replace(ObservableCollection<string> target, IReadOnlyList<string> values)
	{
		if (target.SequenceEqual(values))
		{
			return;
		}

		target.Clear();
		foreach (var value in values)
		{
			target.Add(value);
		}
	}

	/// <summary>Seaglass scrolling: the cards in the library as filtered, the "m" of a card's "n of m".</summary>
	[ObservableProperty]
	public partial int TotalCount { get; set; }

	[ObservableProperty]
	public partial ItemSortBy SortBy { get; set; } = ItemSortBy.Name;

	[ObservableProperty]
	public partial SortOrder Order { get; set; } = SortOrder.Ascending;

	/// <summary>
	/// Seaglass 1.1.4 (interaction audit, item 12): the Filter menu's lists, one per kind (genres, years, ratings, tags), each
	/// value a check box with how many titles have it. Made from the library's own titles (all of them are here): no request.
	/// </summary>
	[ObservableProperty]
	public partial IReadOnlyList<FilterFacet> Facets { get; set; } = [];

	/// <summary>The Filter menu's rows, in one list for a virtualised repeater (the review, item 5): each kind, then its values.</summary>
	public IReadOnlyList<object> FilterRows { get; private set; } = [];

	partial void OnFacetsChanged(IReadOnlyList<FilterFacet> value)
	{
		FilterRows = [.. value.SelectMany(f => f.Options.Cast<object>().Prepend(f))];
		OnPropertyChanged(nameof(FilterRows));
	}

	/// <summary>Filter's "Clear filters": every check box off.</summary>
	[RelayCommand]
	public void ClearFilters()
	{
		Filter.Unwatched = false;
		Filter.Favorites = false;
		Filter.Genres.Clear();
		Filter.Tags.Clear();
		Filter.Years.Clear();
		Filter.OfficialRatings.Clear();
	}

	/// <summary>The direction, apart from the sort (one click, no menu): ascending and descending in turn.</summary>
	[RelayCommand]
	public void ToggleSortOrder() => UpdateSortOrder(Order == SortOrder.Descending ? SortOrder.Ascending : SortOrder.Descending);

	/// <summary>The direction button for Narrator: "A to Z, switch to Z to A".</summary>
	public string SortOrderButtonName => LibraryBrowse.SortOrderButtonName(SortBy, Order);

	[ObservableProperty]
	public partial bool IsLoading { get; set; }

	/// <summary>Seaglass: the library's name, the page's title.</summary>
	[ObservableProperty]
	public partial string Title { get; set; } = "";

	/// <summary>fluentfin-mpv: the library failed to load or is empty: said on the page, with Retry.</summary>
	public LoadState State { get; } = new("this library");

	public IJellyfinClient JellyfinClient { get; }

	public LibraryFilter Filter { get; set; } = new();

	/// <summary>
	/// A key picked in the Sort menu. Nixie 1.4.0 (the UX review's X5, item 8): a new key opens in its natural direction (dates
	/// and ratings newest and highest first; Date added opened oldest first); the key in use picked again turns the direction.
	/// </summary>
	[RelayCommand]
	public void UpdateSortBy(ItemSortBy sortBy)
	{
		var again = sortBy == SortBy && !InPlaylistOrder;
		InPlaylistOrder = false; // Nixie 1.3.1: a sort picked replaces a playlist's own order
		if (again)
		{
			ToggleSortOrder();
			return;
		}

		Order = LibraryBrowse.NaturalOrder(sortBy);
		SortBy = sortBy;
	}

	/// <summary>Cinematic's presets: a key and a direction at once (no turn for the key in use).</summary>
	public void SetSort(ItemSortBy sortBy, SortOrder order)
	{
		InPlaylistOrder = false;
		Order = order;
		SortBy = sortBy;
	}

	[RelayCommand]
	public void UpdateSortOrder(SortOrder order)
	{
		InPlaylistOrder = false;
		Order = order;
	}


	public Task OnNavigatedFrom() => Task.CompletedTask;

	/// <summary>
	/// Nixie 1.4.0 (the bug hunt's M2): the library loaded again from the server in place, keeping its sort, its filters, its
	/// view and the card the user is on (<paramref name="on"/>, <paramref name="offset"/> down the grid), as Back keeps them.
	/// For R and F5 without a new page (the refresh navigates to a new view model today, which <see cref="ReloadedInPlace"/>
	/// covers the same way).
	/// </summary>
	public async Task RefreshAsync(BaseItemViewModel? on = null, double offset = 0)
	{
		if (_library is not { } library)
		{
			return;
		}

		LeftAt(on, offset);
		ComingBack = true;
		await State.RunAsync(() => Initialize(library));
	}

	public async Task OnNavigatedTo(object parameter)
	{
		if (parameter is BaseItemDto libraryDto)
		{
			await State.RunAsync(() => Initialize(libraryDto));
		}
		else if(parameter is Guid id)
		{
			// fluentfin-mpv: a library opened by id (the jump list) that cannot be looked up says so, with Retry
			await State.RunAsync(async () =>
			{
				IsLoading = true;
				try
				{
					var dto = await JellyfinClient.GetItem(id);
					return dto is null ? LoadMessages.CouldNotLoad("this library") : await Initialize(dto);
				}
				finally
				{
					IsLoading = false;
				}
			});
		}
	}

	/// <summary>fluentfin-mpv: says how it went (a failure, an empty library), for <see cref="State"/>.</summary>
	private async Task<LoadOutcome> Initialize(BaseItemDto dto)
	{
		IsLoading = true;
		Title = dto.Name ?? "";
		_library = dto;
		ReadView();
		_firstShown = 0;

		try
		{
			// Seaglass 1.1.4: one request. The Filter menu's lists were a second one, asked for after the items, and the grid
			// waited for it (one round trip late); they are now made from the items themselves
			var playlist = dto.Type == BaseItemDto_Type.Playlist && dto.Id is not null;
			var result = playlist ? await JellyfinClient.GetPlaylistItems(dto.Id!.Value) : await JellyfinClient.GetItems(dto);

			if (result is null or { Items: null })
			{
				return LoadMessages.CouldNotLoad(playlist ? "this playlist" : "this library");
			}

			// Nixie 1.3.1: a playlist in its own order: each item's place, then the order (before the items, so they come sorted)
			_playlistPlaces = playlist ? result.Items.Select((x, i) => (x.Id, i)).Where(x => x.Id is not null).GroupBy(x => x.Id!.Value).ToDictionary(g => g.Key, g => g.First().i) : [];
			InPlaylistOrder = playlist;

			// a Retry starts from what the server has now
			_itemsCache.Edit(cache =>
			{
				cache.Clear();
				cache.AddOrUpdate(result.Items.Select(BaseItemViewModel.FromDto));
			});
			Facets = FilterFacet.From(result.Items, Filter);

			var cameBack = false;
			if ((ComingBack || ReloadedInPlace?.Invoke() == true) && dto.Id is { } library && LibraryPlaces.Recall(JellyfinClient.UserId, library) is { } place)
			{
				ComingBack = false;
				cameBack = true;
				PutBack(place);
			}

			FiltersChanged();

			// Nixie 1.4.0 (the UX review's X8, item 4): a library opened anew gives its first poster the focus (Back: the one left)
			if (!cameBack && result.Items.Count > 0 && FocusRequest < 0)
			{
				ArrivalFocus = true;
				ShowIndex(0, fromLoad: true);
			}

			if (result.Items.Count == 0 && playlist)
			{
				return LoadOutcome.Empty("This playlist is empty", $"Items added to it on {LoadMessages.ServerName()} appear here.");
			}

			if (result.Items.Count == 0)
			{
				return LoadOutcome.Empty("This library is empty", $"Movies and shows added to it on {LoadMessages.ServerName()} appear here.");
			}

			return LoadOutcome.Loaded;
		}
		finally
		{
			IsLoading = false;
			UpdateBrowse();
		}
	}
}

/// <summary>Nixie 1.4.0: what the row under a library's toolbar holds.</summary>
public enum LetterRowMode
{
	/// <summary>Nothing to show (loading, empty, nothing matches).</summary>
	None,

	/// <summary>The A to Z (sorted by title, 48 titles or more).</summary>
	Letters,

	/// <summary>The two directions of the sort ("Newest first", "Oldest first"), in the A to Z's room.</summary>
	Directions,
}

public partial class LibraryFilter : ObservableObject
{
	public LibraryFilter()
	{
		Tags.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Tags));
		Genres.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Genres));
		OfficialRatings.CollectionChanged += (_, _) => OnPropertyChanged(nameof(OfficialRatings));
		Years.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Years));
	}

	public ObservableCollection<string> Tags { get; set; } = [];

	/// <summary>Seaglass 1.1.4 (layout audit, item 15): only what is not watched yet (a show: with episodes left).</summary>
	[ObservableProperty]
	public partial bool Unwatched { get; set; }

	/// <summary>Seaglass 1.1.4: only favorites.</summary>
	[ObservableProperty]
	public partial bool Favorites { get; set; }

	[ObservableProperty]
	public partial ObservableCollection<string> Genres { get; set; } = new();

	[ObservableProperty]
	public partial ObservableCollection<string> OfficialRatings { get; set; } = new();

	[ObservableProperty]
	public partial ObservableCollection<string> Years { get; set; } = new();

	/// <summary>The title passes every filter on.</summary>
	public bool IsVisible(BaseItemViewModel dto) => Matches(dto, null);

	/// <summary>
	/// Nixie 1.4.0 (the UX review's X4, item 2): the title passes every filter on but those of <paramref name="except"/> (a
	/// facet's counts are counted so: against the other filters). Values of one kind combine with OR (Comedy or Drama), kinds
	/// with AND (a comedy, from 1999); ticking two genres reached zero titles while every count promised dozens.
	/// </summary>
	public bool Matches(BaseItemViewModel dto, FilterGroup? except)
	{
		if (except != FilterGroup.Unwatched && Unwatched && dto.UserData?.Played == true)
		{
			return false;
		}

		if (except != FilterGroup.Favorites && Favorites && dto.UserData?.IsFavorite != true)
		{
			return false;
		}

		if (except != FilterGroup.Genres && Genres is { Count: > 0 } && dto.Genres?.Any(Genres.Contains) != true)
		{
			return false;
		}

		if (except != FilterGroup.Tags && Tags is { Count: > 0 } && dto.Tags?.Any(Tags.Contains) != true)
		{
			return false;
		}

		if (except != FilterGroup.Ratings && OfficialRatings is { Count: > 0 } && !OfficialRatings.Contains(dto.OfficialRating ?? ""))
		{
			return false;
		}

		// Seaglass 1.1.4 (the review, item 2): a title with no year is not of the year picked (it was let through: "2020 (3)"
		// showed 5); the year as the Filter menu writes it
		if (except != FilterGroup.Years && Years is { Count: > 0 }
			&& !(dto.ProductionYear is > 0 && Years.Contains(dto.ProductionYear.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))))
		{
			return false;
		}

		return true;
	}

	/// <summary>Nixie 1.4.0: every filter on, one chip each, in the Filter menu's order: Unwatched, Favorites, genres, years, ratings, tags.</summary>
	public IReadOnlyList<AppliedFilter> Applied()
	{
		var applied = new List<AppliedFilter>();
		if (Unwatched)
		{
			applied.Add(new AppliedFilter(FilterGroup.Unwatched, "", "Unwatched"));
		}

		if (Favorites)
		{
			applied.Add(new AppliedFilter(FilterGroup.Favorites, "", "Favorites"));
		}

		applied.AddRange(Genres.Select(g => new AppliedFilter(FilterGroup.Genres, g, g)));
		applied.AddRange(Years.Select(y => new AppliedFilter(FilterGroup.Years, y, y)));
		applied.AddRange(OfficialRatings.Select(r => new AppliedFilter(FilterGroup.Ratings, r, $"Rated {r}")));
		applied.AddRange(Tags.Select(t => new AppliedFilter(FilterGroup.Tags, t, t)));
		return applied;
	}

	/// <summary>Nixie 1.4.0: a chip's ✕: that one filter off.</summary>
	public void Remove(AppliedFilter filter)
	{
		switch (filter.Group)
		{
			case FilterGroup.Unwatched:
				Unwatched = false;
				break;
			case FilterGroup.Favorites:
				Favorites = false;
				break;
			case FilterGroup.Genres:
				Genres.Remove(filter.Value);
				break;
			case FilterGroup.Years:
				Years.Remove(filter.Value);
				break;
			case FilterGroup.Ratings:
				OfficialRatings.Remove(filter.Value);
				break;
			case FilterGroup.Tags:
				Tags.Remove(filter.Value);
				break;
		}
	}

	/// <summary>The picked values of a facet's kind.</summary>
	public ObservableCollection<string>? Picked(FilterGroup group) => group switch
	{
		FilterGroup.Genres => Genres,
		FilterGroup.Years => Years,
		FilterGroup.Ratings => OfficialRatings,
		FilterGroup.Tags => Tags,
		_ => null,
	};
}
