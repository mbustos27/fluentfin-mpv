using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicData.Binding;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using ReactiveUI;

namespace FluentFin.Core.ViewModels;

public partial class LibraryViewModel : ObservableObject, INavigationAware
{
	// Nixie 1.4.1 (big libraries): every title loaded, by id (a title a later page brings again is kept once) and in the
	// server's order; the grid's list is made from them here (it was DynamicData's SortAndBind, which reset the grid for any
	// batch over 25 titles: each page would have redrawn every card and dropped the keyboard's card)
	private readonly Dictionary<Guid, BaseItemViewModel> _byId = [];
	private readonly List<BaseItemViewModel> _all = [];
	private readonly ShownItems _shown = [];
	private readonly ReadOnlyObservableCollection<BaseItemViewModel> _items;

	/// <summary>Nixie 1.4.1: where a big library's pages are logged (each page's time, a try again, the end).</summary>
	private readonly ILogger<LibraryViewModel>? _logger;

	public LibraryViewModel(IJellyfinClient jellyfinClient, ILocalSettingsService? localSettings = null, ILogger<LibraryViewModel>? logger = null)
	{
		JellyfinClient = jellyfinClient;
		_localSettings = localSettings;
		_logger = logger;
		_items = new(_shown);

		// tie-break so equal keys (e.g. unrated, never played) keep a stable order
		// Nixie 1.3.1: a playlist keeps its own order until a sort is picked (InPlaylistOrder)
		this.WhenAnyValue(x => x.SortBy, x => x.Order, x => x.InPlaylistOrder)
			.Select(x => (IComparer<BaseItemViewModel>)(x.Item3 ? PlaylistComparer() : GetComparer((x.Item1, x.Item2))).ThenByAscending(LibraryBrowse.SortKey).ThenByAscending(i => i.Id ?? Guid.Empty))
			.Subscribe(x =>
			{
				_comparer = x; // Seaglass 1.1.4: the A-Z jump orders the library the same way
				UserMoved();
				Show();
				UpdateBrowse();
			});

		Filter.WhenAnyPropertyChanged()
			.Subscribe(x =>
			{
				UserMoved();
				Show();
				FiltersChanged();
			});
	}

	// Seaglass 1.1.4 (layout audit, item 6): one continuously scrolling grid, as every streaming app has, not pages of 100
	// under a numbered pager: every title is here (Nixie 1.4.1: read from the server a page at a time, LibraryPageReader),
	// sorted and filtered here, and the grid (an ItemsRepeater) draws only the cards near the view

	/// <summary>The grid's titles again from every title loaded: those the filters let through, in the sort's order.</summary>
	private void Show()
	{
		_shown.SyncTo(Ordered());
		// Seaglass scrolling: the cards in the (filtered) library, for Narrator's "n of m"
		TotalCount = _shown.Count;
	}

	/// <summary>Titles read from the server, kept once each by id; returns those new to the library.</summary>
	private List<BaseItemViewModel> Add(IEnumerable<BaseItemDto> dtos)
	{
		var added = new List<BaseItemViewModel>();
		foreach (var dto in dtos)
		{
			var key = dto.Id ?? Guid.Empty;
			if (_byId.ContainsKey(key))
			{
				continue; // a page brought a title again (the library changed between pages; a playlist lists one twice)
			}

			var item = BaseItemViewModel.FromDto(dto);
			_byId[key] = item;
			_all.Add(item);
			added.Add(item);
		}

		return added;
	}

	/// <summary>
	/// Nixie 1.4.1: a page after the first: its titles go into the grid where the sort puts them, one by one (each card
	/// already drawn stays, and so does the keyboard on it); the Filter menu's counts, the counts and the A to Z follow.
	/// </summary>
	private void AddPage(IReadOnlyList<BaseItemDto> page)
	{
		var shown = Add(page).Where(Filter.IsVisible).ToList();
		if (_comparer is { } comparer)
		{
			shown.Sort(comparer);
		}

		UpdatingFromPage = true;
		try
		{
			_shown.InsertSorted(shown, _comparer);
			FollowRequest();
			TotalCount = _shown.Count;
			Recount();
			UpdateBrowse();
		}
		finally
		{
			UpdatingFromPage = false;
		}

		PlaceArrived();
	}

	/// <summary>
	/// Nixie 1.4.1: what changes now comes from a page arriving, not from the user (the applied-filter row's live count is
	/// not read out for each page; it is for the user's own filter change, loading or not).
	/// </summary>
	public bool UpdatingFromPage { get; private set; }

	/// <summary>
	/// Nixie 1.4.1: the card asked for now is a place put back late (its page came after the grid showed): the page focuses
	/// it only when the keyboard is in the grid or nowhere; anywhere else (the search box, the toolbar) the grid only
	/// scrolls to it. The page reads it with <see cref="FocusRequest"/>, as <see cref="ArrivalFocus"/>.
	/// </summary>
	public bool LateFocus { get; set; }

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
	/// </summary>
	public Func<bool>? FocusYields { get; set; }

	// ---- Nixie 1.4.0 (the UX review's X4, item 2): the filters on, said; their counts honest; nothing left, said ----------

	/// <summary>Every filter on, one chip each ("Unwatched ✕", "Comedy ✕"), for the applied-filter row.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasAppliedFilters))]
	public partial IReadOnlyList<AppliedFilter> AppliedFilters { get; set; } = [];

	/// <summary>The applied-filter row shows (a filter is on, over a library that has titles).</summary>
	public bool HasAppliedFilters => AppliedFilters.Count > 0 && _all.Count > 0;

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

	// ---- Nixie 1.4.1 (big libraries): the rest of the library, read after the first page --------------------------------

	/// <summary>
	/// The pages after the first are still being read: the grid has the first titles, and the counts say how many of how
	/// many ("Loading… 1,200 of 25,842 movies"); the sorts, the filters and the A to Z work on the titles in so far.
	/// </summary>
	[ObservableProperty]
	public partial bool IsLoadingMore { get; private set; }

	/// <summary>A page failed every try: the library is shown as far as it came, and the counts say the rest did not load (R reads it again).</summary>
	[ObservableProperty]
	public partial bool LoadStopped { get; private set; }

	/// <summary>The pages after the first, while they are read (done at once when there are none). Tests wait for it.</summary>
	public Task RestLoaded { get; private set; } = Task.CompletedTask;

	/// <summary>Coming back (Back, R) to a title not in yet: how long more pages are read before the grid shows, so the place comes back as it was.</summary>
	internal TimeSpan PlaceWait { get; set; } = TimeSpan.FromSeconds(3);

	/// <summary>The library's size as the server counted it (the counts say it while the rest loads).</summary>
	private int? _serverTotal;

	/// <summary>The load in progress: a new load (Retry, R) or leaving the page stops it, and its pages are not wanted.</summary>
	private CancellationTokenSource? _loading;

	/// <summary>Back or R to a title whose page has not come yet: it gets the focus when it comes, unless the user moved meanwhile.</summary>
	private LibraryPlace? _pendingPlace;

	private bool _puttingBack;

	/// <summary>The user scrolled, jumped, sorted or filtered: a place still coming is not put back over them.</summary>
	private void UserMoved()
	{
		if (!_puttingBack)
		{
			_pendingPlace = null;
			LateFocus = false; // a jump made before the page took the late place's request is the user's, focused as ever
		}
	}

	/// <summary>The title of a place still coming is in: the grid goes as far down as it was, and the card gets the focus.</summary>
	private void PlaceArrived()
	{
		if (_pendingPlace is not { ItemId: { } id } place || !_byId.TryGetValue(id, out var item))
		{
			return;
		}

		_pendingPlace = null;
		var index = _shown.IndexOf(item);
		if (index >= 0)
		{
			LateFocus = !IsLoading; // while the grid still waits (Back's few pages), it is the place as ever
			ReturnOffset = place.Offset;
			ShowIndex(index);
		}
	}

	/// <summary>Every page read after the first, each into the grid as it comes; then the Filter menu's lists from every title.</summary>
	private async Task ReadRestAsync(LibraryPageReader reader, CancellationTokenSource loading, Task<IReadOnlyList<BaseItemDto>?>? onItsWay = null)
	{
		var cancellationToken = loading.Token;
		try
		{
			// the page Back's wait left on its way first, then the next ones
			while (await (onItsWay ?? reader.ReadNextAsync(cancellationToken)) is { } page)
			{
				onItsWay = null;
				if (cancellationToken.IsCancellationRequested)
				{
					return;
				}

				AddPage(page);
			}
		}
		catch (OperationCanceledException)
		{
			return; // left, or loaded again: this load's pages are not wanted
		}
		catch (Exception ex) when (ReferenceEquals(_loading, loading))
		{
			// never "Loading…" for ever: the library as far as it came, said so
			Debug.WriteLine($"Library: the rest stopped: {ex}");
			IsLoadingMore = false;
			LoadStopped = true;
			UpdateBrowse();
			return;
		}

		if (ReferenceEquals(_loading, loading))
		{
			RestRead(reader);
		}
	}

	/// <summary>The reader stopped: every title is in (or a page failed every try): the counts say so, and the Filter menu lists every value.</summary>
	private void RestRead(LibraryPageReader reader)
	{
		IsLoadingMore = false;
		LoadStopped = reader.HasFailed;
		_pendingPlace = null; // its title is not in the library any more: the user stays where they are
		Facets = FilterFacet.From([.. _all.Select(x => x.Dto)], Filter);
		// the filters did not change (FiltersChanged would say they did, and the grid would go to its top)
		Recount();
		UpdateBrowse();
	}

	private void FiltersChanged()
	{
		ActiveFilters = LibraryBrowse.ActiveFilters(Filter);
		// Nixie 1.4.1: a new list only when the filters did change: the pages take a changed list for a new set of titles
		// and start the grid at its top
		var applied = Filter.Applied();
		if (!applied.SequenceEqual(AppliedFilters))
		{
			AppliedFilters = applied;
		}

		Recount();
		UpdateBrowse();
	}

	/// <summary>The Filter menu's counts and the Unwatched switch's, against the other filters on.</summary>
	private void Recount()
	{
		var items = _all;
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
		if (index > 0 && index != _firstShown)
		{
			UserMoved(); // scrolled down, or the keyboard went on: a place still coming is not put back over them
		}

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

	/// <summary>The whole library as the grid shows it: filtered and in the sort's order.</summary>
	private List<BaseItemViewModel> Ordered()
	{
		var items = _all.Where(Filter.IsVisible);
		return _comparer is { } comparer ? [.. items.Order(comparer)] : [.. items];
	}

	private void UpdateBrowse()
	{
		var all = _all.Count;
		var shown = TotalCount;
		var kind = _library?.CollectionType;
		// Nixie 1.4.1: while the rest loads, or after it stopped, the counts say how many of the server's count are in
		var whole = Math.Max(all, _serverTotal ?? all);
		if (all == 0)
		{
			CountText = LibraryCountText = ShowingText = "";
		}
		else if (IsLoadingMore || (LoadStopped && whole > all))
		{
			LibraryCountText = IsLoadingMore ? LibraryBrowse.LoadingText(all, whole, kind) : LibraryBrowse.StoppedText(all, whole, kind);
			CountText = shown == all ? LibraryCountText : LibraryBrowse.PartCountText(shown, all, kind, IsLoadingMore);
			ShowingText = $"Showing {LibraryBrowse.PartCountText(shown, all, kind, IsLoadingMore)}";
		}
		else
		{
			CountText = LibraryBrowse.CountText(shown, all, kind);
			LibraryCountText = LibraryBrowse.CountText(all, all, kind);
			ShowingText = LibraryBrowse.ShowingText(shown, all, kind);
		}

		// Nixie 1.4.1: no "nothing matches" while more titles may still match
		NoMatches = all > 0 && shown == 0 && !IsLoading && !IsLoadingMore;
		NoMatchesText = NoMatches ? LibraryNoMatches.Title(Filter, _library?.CollectionType) : "";
		OnPropertyChanged(nameof(HasAppliedFilters));

		// Nixie 1.4.0 (item 13): the A to Z from 48 titles shown, sorted by title; else the row holds the two directions
		// (Nixie 1.4.1: from the grid's own list, which is the library filtered and in the sort's order: no second sort)
		var byName = SortBy == ItemSortBy.Name && !InPlaylistOrder;
		LetterRow = all == 0 || shown == 0 ? LetterRowMode.None : byName && shown >= LibraryBrowse.LettersFrom ? LetterRowMode.Letters : LetterRowMode.Directions;
		var letters = byName && all > 0 ? LibraryBrowse.Letters(_shown.Select(LibraryBrowse.SortKey)) : [];
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

		UserMoved();
		// the same letter again goes on to its next run of titles (a key the sort does not keep together), then round
		var run =_lastJump is { } last && last.Letter == jump.Letter ? (last.Run + 1) % jump.Runs.Count : 0;
		_lastJump = (jump.Letter, run);
		ShowIndex(jump.Runs[run]);
	}

	private (string Letter, int Run)? _lastJump;

	/// <summary>Nixie 1.4.1: the card of the request not yet taken: a page's titles inserted before it move the request with it.</summary>
	private BaseItemViewModel? _requested;

	/// <summary>The request still waiting for the page follows its card, which a page's titles may have pushed further on.</summary>
	private void FollowRequest()
	{
		if (FocusRequest >= 0 && _requested is { } card && _shown.IndexOf(card) is >= 0 and var index && index != FocusRequest)
		{
			FocusRequest = index;
		}
	}

	// whoever asks (a jump, Back, the arrival, a page keeping its poster across a rebuild), the card asked for is remembered
	partial void OnFocusRequestChanged(int value) => _requested = value >= 0 && value < _shown.Count ? _shown[value] : null;

	private void ShowIndex(int index)
	{
		// the card too when the index is the one asked already (OnFocusRequestChanged does not run for the same value)
		_requested = index >= 0 && index < _shown.Count ? _shown[index] : null;
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
		_puttingBack = true;
		try
		{
			SortBy = place.SortBy;
			Order = place.Order;
			Replace(Filter.Genres, place.Genres);
			Replace(Filter.Tags, place.Tags);
			Replace(Filter.Years, place.Years);
			Replace(Filter.OfficialRatings, place.OfficialRatings);

			Filter.Unwatched = place.Unwatched;
			Filter.Favorites = place.Favorites;
		}
		finally
		{
			_puttingBack = false;
		}

		var item = place.ItemId is { } id ? _byId.GetValueOrDefault(id) : null;
		if (item is null && place.ItemId is not null && IsLoadingMore)
		{
			// Nixie 1.4.1: its page has not come yet: it is put back when it comes (the grid is not scrolled meanwhile)
			_pendingPlace = place;
			return;
		}

		var index = item is null ? -1 : _shown.IndexOf(item);
		ReturnOffset = place.Offset;
		if (index >= 0)
		{
			ShowIndex(index);
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

	partial void OnFacetsChanged(IReadOnlyList<FilterFacet> oldValue, IReadOnlyList<FilterFacet> newValue)
	{
		// Nixie 1.4.1: the lists made again (every title in, a Retry) stop following the filter's picks
		foreach (var facet in oldValue ?? [])
		{
			facet.Detach();
		}
	}

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


	public Task OnNavigatedFrom()
	{
		// Nixie 1.4.1: the rest of a big library is not asked for once the page is left
		_loading?.Cancel();
		return Task.CompletedTask;
	}

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
			// Nixie 1.4.1: leaving while the library is looked up stops it too (it went on to read the whole library)
			await State.RunAsync(async () =>
			{
				// each run its own (Retry and R run this again: the last run's was cancelled by the load it started)
				_loading?.Cancel();
				var lookup = _loading = new CancellationTokenSource();
				IsLoading = true;
				try
				{
					var dto = await JellyfinClient.GetItem(id);
					lookup.Token.ThrowIfCancellationRequested();
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
		// Nixie 1.4.1: a load still reading pages (a Retry, R) stops: its pages are not wanted
		_loading?.Cancel();
		var loading = _loading = new CancellationTokenSource();
		var cancellationToken = loading.Token;
		IsLoading = true;
		IsLoadingMore = false;
		LoadStopped = false;
		_serverTotal = null;
		_pendingPlace = null;
		RestLoaded = Task.CompletedTask;
		Title = dto.Name ?? "";
		_library = dto;
		ReadView();
		_firstShown = 0;

		try
		{
			// Seaglass 1.1.4: the Filter menu's lists were a second request, asked for after the items, and the grid waited for
			// it (one round trip late); they are made from the items themselves. Nixie 1.4.1: a library comes a page at a time
			// (LibraryPageReader): the first page shows at once, the rest follows (ReadRestAsync); a playlist is one request
			var playlist = dto.Type == BaseItemDto_Type.Playlist && dto.Id is not null;
			var reader = playlist ? null : new LibraryPageReader(JellyfinClient, dto) { Log = _logger };
			var first = playlist ? (await JellyfinClient.GetPlaylistItems(dto.Id!.Value))?.Items : await reader!.ReadFirstAsync(cancellationToken);
			cancellationToken.ThrowIfCancellationRequested();

			if (first is null)
			{
				return LoadMessages.CouldNotLoad(playlist ? "this playlist" : "this library");
			}

			// Nixie 1.3.1: a playlist in its own order: each item's place, then the order (before the items, so they come sorted)
			_playlistPlaces = playlist ? first.Select((x, i) => (x.Id, i)).Where(x => x.Id is not null).GroupBy(x => x.Id!.Value).ToDictionary(g => g.Key, g => g.First().i) : [];
			InPlaylistOrder = playlist;
			_serverTotal = reader?.Total;
			IsLoadingMore = reader is { IsDone: false };

			// a Retry starts from what the server has now
			_byId.Clear();
			_all.Clear();
			Add(first);
			Show();
			// from the first page while the rest loads (their counts follow each page), from every title once all are in
			Facets = FilterFacet.From(first, Filter);

			var cameBack = false;
			if ((ComingBack || ReloadedInPlace?.Invoke() == true) && dto.Id is { } library && LibraryPlaces.Recall(JellyfinClient.UserId, library) is { } place)
			{
				ComingBack = false;
				cameBack = true;
				PutBack(place);
			}

			FiltersChanged();

			// Nixie 1.4.1: Back or R to a title further on: a few more pages before the grid shows (it shows at once otherwise),
			// so the card comes back where it was rather than the grid jumping to it later
			// (at most PlaceWait: a page still on its way then goes on in the background, ReadRestAsync)
			Task<IReadOnlyList<BaseItemDto>?>? onItsWay = null;
			if (reader is not null && _pendingPlace is not null)
			{
				var waiting = Stopwatch.StartNew();
				while (_pendingPlace is not null && !reader.IsDone && !reader.HasFailed && waiting.Elapsed < PlaceWait)
				{
					var next = reader.ReadNextAsync(cancellationToken);
					var left = PlaceWait - waiting.Elapsed;
					if (left <= TimeSpan.Zero || await Task.WhenAny(next, Task.Delay(left, cancellationToken)) != next)
					{
						onItsWay = next;
						break;
					}

					cancellationToken.ThrowIfCancellationRequested();
					if (await next is not { } page)
					{
						break;
					}

					AddPage(page);
				}

				cancellationToken.ThrowIfCancellationRequested();
			}

			// Nixie 1.4.0 (the UX review's X8, item 4): a library opened anew gives its first poster the focus (Back: the one left)
			if (!cameBack && _all.Count > 0 && FocusRequest < 0)
			{
				ArrivalFocus = true;
				ShowIndex(0);
			}

			if (onItsWay is not null || reader is { IsDone: false, HasFailed: false })
			{
				RestLoaded = ReadRestAsync(reader!, loading, onItsWay);
			}
			else if (reader is not null && IsLoadingMore)
			{
				RestRead(reader); // every page came while the place was awaited (or one failed)
			}

			if (_all.Count == 0 && playlist)
			{
				return LoadOutcome.Empty("This playlist is empty", $"Items added to it on {LoadMessages.ServerName()} appear here.");
			}

			if (_all.Count == 0)
			{
				return LoadOutcome.Empty("This library is empty", $"Movies and shows added to it on {LoadMessages.ServerName()} appear here.");
			}

			return LoadOutcome.Loaded;
		}
		finally
		{
			// a load that was replaced leaves the page to the one that replaced it
			if (ReferenceEquals(_loading, loading))
			{
				IsLoading = false;
				UpdateBrowse();
			}
		}
	}
}

/// <summary>
/// Nixie 1.4.1: the library grid's list. A new sort or filter makes it anew (one reset, as DynamicData's binding did for
/// more than 25 changes; a small library's changes still go one by one, so they animate); a page's titles are inserted
/// where the sort puts them (a binary search: the cards drawn stay drawn, and the keyboard's card keeps the keyboard).
/// </summary>
internal sealed class ShownItems : ObservableCollection<BaseItemViewModel>
{
	/// <summary>DynamicData's reset threshold, kept: up to this many on either side, the changes go one by one.</summary>
	private const int OneByOneUpTo = 25;

	/// <summary>The list becomes <paramref name="target"/>.</summary>
	public void SyncTo(IReadOnlyList<BaseItemViewModel> target)
	{
		if (Count > OneByOneUpTo || target.Count > OneByOneUpTo)
		{
			ResetTo(target);
			return;
		}

		var keep = new HashSet<BaseItemViewModel>(target, ReferenceEqualityComparer.Instance);
		for (var i = Count - 1; i >= 0; i--)
		{
			if (!keep.Contains(this[i]))
			{
				RemoveAt(i);
			}
		}

		for (var i = 0; i < target.Count; i++)
		{
			if (i < Count && ReferenceEquals(this[i], target[i]))
			{
				continue;
			}

			var at = IndexOf(target[i]);
			if (at >= 0)
			{
				Move(at, i);
			}
			else
			{
				Insert(i, target[i]);
			}
		}
	}

	/// <summary>The list becomes <paramref name="target"/>, said once (a reset).</summary>
	public void ResetTo(IReadOnlyList<BaseItemViewModel> target)
	{
		CheckReentrancy();
		Items.Clear();
		foreach (var item in target)
		{
			Items.Add(item);
		}

		OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
		OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
		OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
	}

	/// <summary>
	/// <paramref name="sorted"/> (in the sort's order) inserted where <paramref name="comparer"/> puts each, after any equal
	/// one; at the end without a sort. A card whose sort key changed in place (watched from its menu, sorted by date played)
	/// leaves the list a little out of order: the search still ends somewhere in it, never fails.
	/// </summary>
	public void InsertSorted(IReadOnlyList<BaseItemViewModel> sorted, IComparer<BaseItemViewModel>? comparer)
	{
		if (sorted.Count == 0)
		{
			return;
		}

		if (Count == 0)
		{
			ResetTo(sorted);
			return;
		}

		var from = 0;
		foreach (var item in sorted)
		{
			var at = comparer is null ? Count : After(item, comparer, from);
			Insert(at, item);
			from = at + 1;
		}
	}

	private int After(BaseItemViewModel item, IComparer<BaseItemViewModel> comparer, int from)
	{
		int low = Math.Min(from, Count), high = Count;
		while (low < high)
		{
			var middle = low + ((high - low) / 2);
			if (comparer.Compare(this[middle], item) <= 0)
			{
				low = middle + 1;
			}
			else
			{
				high = middle;
			}
		}

		return low;
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
