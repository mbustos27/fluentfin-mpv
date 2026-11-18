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


	public LibraryViewModel(IJellyfinClient jellyfinClient)
	{
		JellyfinClient = jellyfinClient;

		// tie-break so equal keys (e.g. unrated, never played) keep a stable order
		var comparer = this.WhenAnyValue(x => x.SortBy, x => x.Order)
						   .Select(x => (IComparer<BaseItemViewModel>)GetComparer(x).ThenByAscending(i => i.Name ?? "").ThenByAscending(i => i.Id ?? Guid.Empty))
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
				ActiveFilters = LibraryBrowse.ActiveFilters(Filter);
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

	private SortExpressionComparer<BaseItemViewModel> GetComparer((ItemSortBy, SortOrder) sortDescription)
	{
		return sortDescription switch
		{
			(ItemSortBy.Name, SortOrder.Ascending) => SortExpressionComparer<BaseItemViewModel>.Ascending(x => x.Name ?? ""),
			(ItemSortBy.CommunityRating, SortOrder.Ascending) => SortExpressionComparer<BaseItemViewModel>.Ascending(x => x.CommunityRating ?? 0),
			(ItemSortBy.CriticRating, SortOrder.Ascending) => SortExpressionComparer<BaseItemViewModel>.Ascending(x => x.CriticRating ?? 0),
			(ItemSortBy.DateCreated, SortOrder.Ascending) => SortExpressionComparer<BaseItemViewModel>.Ascending(x => x.DateCreated ?? new DateTimeOffset()),
			(ItemSortBy.DatePlayed, SortOrder.Ascending) => SortExpressionComparer<BaseItemViewModel>.Ascending(x => x.UserData?.LastPlayedDate ?? new DateTimeOffset()),
			(ItemSortBy.PlayCount, SortOrder.Ascending) => SortExpressionComparer<BaseItemViewModel>.Ascending(x => x.UserData?.PlayCount ?? 0),
			(ItemSortBy.PremiereDate, SortOrder.Ascending) => SortExpressionComparer<BaseItemViewModel>.Ascending(x => x.PremiereDate ?? new DateTimeOffset()),
			(ItemSortBy.Runtime, SortOrder.Ascending) => SortExpressionComparer<BaseItemViewModel>.Ascending(x => x.RunTimeTicks ?? 0),

			(ItemSortBy.Name, SortOrder.Descending) => SortExpressionComparer<BaseItemViewModel>.Descending(x => x.Name ?? ""),
			(ItemSortBy.CommunityRating, SortOrder.Descending) => SortExpressionComparer<BaseItemViewModel>.Descending(x => x.CommunityRating ?? 0),
			(ItemSortBy.CriticRating, SortOrder.Descending) => SortExpressionComparer<BaseItemViewModel>.Descending(x => x.CriticRating ?? 0),
			(ItemSortBy.DateCreated, SortOrder.Descending) => SortExpressionComparer<BaseItemViewModel>.Descending(x => x.DateCreated ?? new DateTimeOffset()),
			(ItemSortBy.DatePlayed, SortOrder.Descending) => SortExpressionComparer<BaseItemViewModel>.Descending(x => x.UserData?.LastPlayedDate ?? new DateTimeOffset()),
			(ItemSortBy.PlayCount, SortOrder.Descending) => SortExpressionComparer<BaseItemViewModel>.Descending(x => x.UserData?.PlayCount ?? 0),
			(ItemSortBy.PremiereDate, SortOrder.Descending) => SortExpressionComparer<BaseItemViewModel>.Descending(x => x.PremiereDate ?? new DateTimeOffset()),
			(ItemSortBy.Runtime, SortOrder.Descending) => SortExpressionComparer<BaseItemViewModel>.Descending(x => x.RunTimeTicks ?? 0),

			_ => SortExpressionComparer<BaseItemViewModel>.Ascending(x => x.Name ?? ""),
		};
	}

	public ReadOnlyObservableCollection<BaseItemViewModel> Items => _items;

	// ---- Seaglass 1.1.4: what the bar says, the A-Z jump, and the way back to the card the user left -------------------

	private IComparer<BaseItemViewModel>? _comparer;
	private BaseItemDto? _library;

	/// <summary>The sort in use, on the Sort button ("Name").</summary>
	public string SortName => LibraryBrowse.SortName(SortBy);

	/// <summary>Up for ascending, down for descending, beside the sort's name.</summary>
	public string SortGlyph => LibraryBrowse.SortGlyph(Order);

	/// <summary>The Sort button for Narrator: "Sort: Name, ascending".</summary>
	public string SortButtonName => LibraryBrowse.SortButtonName(SortBy, Order);

	/// <summary>The genres, tags, years and ratings picked in Filter.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(FilterText), nameof(FilterButtonName))]
	public partial int ActiveFilters { get; set; }

	public string FilterText => LibraryBrowse.FilterText(ActiveFilters);

	public string FilterButtonName => LibraryBrowse.FilterButtonName(ActiveFilters);

	/// <summary>"368 movies", or "12 of 368 movies" with filters on.</summary>
	[ObservableProperty]
	public partial string CountText { get; set; } = "";

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


	partial void OnSortByChanged(ItemSortBy value)
	{
		OnPropertyChanged(nameof(SortName));
		OnPropertyChanged(nameof(SortButtonName));
	}

	partial void OnOrderChanged(SortOrder value)
	{
		OnPropertyChanged(nameof(SortGlyph));
		OnPropertyChanged(nameof(SortButtonName));
		OnPropertyChanged(nameof(SortOrderButtonName));
	}

	/// <summary>The whole library as the grid shows it across its pages: filtered and in the sort's order.</summary>
	private List<BaseItemViewModel> Ordered()
	{
		var items = _itemsCache.Items.Where(Filter.IsVisible);
		return _comparer is { } comparer ? [.. items.Order(comparer)] : [.. items];
	}

	private void UpdateBrowse()
	{
		CountText = _itemsCache.Count == 0 ? "" : LibraryBrowse.CountText(TotalCount, _itemsCache.Count, _library?.CollectionType);
		var letters = SortBy == ItemSortBy.Name && _itemsCache.Count > 0 ? LibraryBrowse.Letters(Ordered().Select(x => x.Name)) : [];
		if (!letters.SequenceEqual(Letters))
		{
			Letters = letters;
		}
	}

	/// <summary>A-Z: the first title with that letter gets the focus (the grid scrolls to it).</summary>
	[RelayCommand]
	public void JumpTo(LetterJump? jump)
	{
		if (jump is null)
		{
			return;
		}

		ShowIndex(jump.Index);
	}

	private void ShowIndex(int index)
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

	/// <summary>The direction button for Narrator: "Ascending, switch to descending".</summary>
	public string SortOrderButtonName => LibraryBrowse.SortOrderButtonName(Order);

	[ObservableProperty]
	public partial bool IsLoading { get; set; }

	/// <summary>Seaglass: the library's name, the page's title.</summary>
	[ObservableProperty]
	public partial string Title { get; set; } = "";

	/// <summary>fluentfin-mpv: the library failed to load or is empty: said on the page, with Retry.</summary>
	public LoadState State { get; } = new("this library");

	public IJellyfinClient JellyfinClient { get; }

	public LibraryFilter Filter { get; set; } = new();

	[RelayCommand]
	public void UpdateSortBy(ItemSortBy sortBy)
	{
		SortBy = sortBy;
	}

	[RelayCommand]
	public void UpdateSortOrder(SortOrder order)
	{
		Order = order;
	}


	public Task OnNavigatedFrom() => Task.CompletedTask;

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

		try
		{
			// Seaglass 1.1.4: one request. The Filter menu's lists were a second one, asked for after the items, and the grid
			// waited for it (one round trip late); they are now made from the items themselves
			var result = await JellyfinClient.GetItems(dto);

			if (result is null or { Items: null })
			{
				return LoadMessages.CouldNotLoad("this library");
			}

			// a Retry starts from what the server has now
			_itemsCache.Edit(cache =>
			{
				cache.Clear();
				cache.AddOrUpdate(result.Items.Select(BaseItemViewModel.FromDto));
			});
			Facets = FilterFacet.From(result.Items, Filter);

			if (ComingBack && dto.Id is { } library && LibraryPlaces.Recall(JellyfinClient.UserId, library) is { } place)
			{
				ComingBack = false;
				PutBack(place);
			}

			UpdateBrowse();

			if (result.Items.Count == 0)
			{
				return LoadOutcome.Empty("This library is empty", $"Movies and shows added to it on {LoadMessages.ServerName()} appear here.");
			}

			return LoadOutcome.Loaded;
		}
		finally
		{
			IsLoading = false;
		}
	}
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

	public bool IsVisible(BaseItemViewModel dto)
	{
		bool hasMatchingTags = true;
		bool hasMatchingGenres = true;
		bool hasMatchingOfficialRatings = true;
		bool hasMatchingYears = true;

		if (Tags is { Count: > 0 })
		{
			hasMatchingTags = dto.Tags?.Intersect(Tags).Count() == Tags.Count;
		}

		if (Genres is { Count: > 0 })
		{
			hasMatchingGenres = dto.Genres?.Intersect(Genres).Count() == Genres.Count;
		}

		if (OfficialRatings is { Count: > 0 })
		{
			hasMatchingOfficialRatings = OfficialRatings.Contains(dto.OfficialRating ?? "");
		}

		if (dto.ProductionYear is > 0 && Years is { Count: > 0 })
		{
			hasMatchingYears = Years.Contains(dto.ProductionYear.Value.ToString());
		}

		if (Unwatched && dto.UserData?.Played == true)
		{
			return false;
		}

		if (Favorites && dto.UserData?.IsFavorite != true)
		{
			return false;
		}

		return hasMatchingTags && hasMatchingGenres && hasMatchingOfficialRatings && hasMatchingYears;
	}
}
