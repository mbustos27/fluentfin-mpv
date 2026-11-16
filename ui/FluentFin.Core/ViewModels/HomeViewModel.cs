using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using ReactiveUI;

namespace FluentFin.Core.ViewModels;

public partial class HomeViewModel(IJellyfinClient jellyfinClient,
								   IObservable<IOutboundWebSocketMessage> webSocketMessages,
								   GlobalCommands commands,
								   HomeCache homeCache) : ObservableObject, INavigationAware
{
	private readonly CompositeDisposable _disposable = [];


	[ObservableProperty]
	public partial ObservableCollection<BaseItemViewModel> ContinueItems { get; set; } = [];

	[ObservableProperty]
	public partial ObservableCollection<BaseItemViewModel> NextUpItems { get; set; } = [];

	[ObservableProperty]
	public partial bool HasNextUpItems { get; set; }

	[ObservableProperty]
	public partial bool HasContinueItems { get; set; }

	/// <summary>True until every part of the page has its data.</summary>
	[ObservableProperty]
	public partial bool IsLoading { get; set; }

	// fluentfin-mpv: progressive home. The page no longer waits for everything behind one shimmer: Continue Watching,
	// Next Up and each "Recently added" row (one per view, in the server's order, from the views sign-in already read)
	// show a placeholder until their own request answers, and all those requests go out together.
	[ObservableProperty]
	public partial bool IsContinueLoading { get; set; } = true;

	[ObservableProperty]
	public partial bool IsNextUpLoading { get; set; } = true;

	public ObservableCollection<NamedQueryResult> RecentItems { get; } = [];

	/// <summary>fluentfin-mpv: Home failed, is empty, or only partly loaded: said on the page, with Retry.</summary>
	public LoadState State { get; } = new("Home");

	// fluentfin-mpv: libraries whose last answer was "nothing recent": no placeholder row for them on the next refresh
	private readonly HashSet<Guid> _emptyLibraries = [];

	// fluentfin-mpv: loads run so far; the first may show the cached page and skip a fresh one, a Retry always asks
	private int _loads;

	// fluentfin-mpv: watch-state changes the server reported and this page applied to Continue Watching and Next Up (each
	// "Recently added" row counts its own): an answer asked for before the latest of them is older than what the section
	// shows, and is dropped instead of undoing the change
	private int _continueChanges;
	private int _nextUpChanges;

	public IJellyfinClient JellyfinClient { get; } = jellyfinClient;

	public Task OnNavigatedFrom()
	{
		_disposable.Dispose();
		return Task.CompletedTask;
	}

	public async Task OnNavigatedTo(object parameter)
	{
		// fluentfin-mpv: listen from the start, since rows can be used (mark watched...) while others still load
		ListenToUserDataChanges();

		await State.RunAsync(LoadSections);
	}

	/// <summary>
	/// fluentfin-mpv: loads every section and says how it went: nothing could be loaded, the server has nothing to show, or
	/// some sections failed (docs/research/ux-review.md H6). The first load shows the last home page at once (HomeCache)
	/// and then, unless it is still fresh, the server's answers replace whatever changed, section by section. A Retry
	/// always asks the server: what it retries is what the last answers left out, whatever the cache's age. The client
	/// answers a failed request with null; a failed "Recently added" row only drops its placeholder (or keeps its cached
	/// cards), so the rows count as content, not as failures.
	/// </summary>
	private async Task<LoadOutcome> LoadSections()
	{
		var retry = _loads++ > 0;
		// fluentfin-mpv: the session this load is for, read once: the page is saved under it, and only if it is still the
		// signed-in one when the answers are in (a load that ends after a sign-out and someone else's sign-in is nobody's)
		var serverUrl = JellyfinClient.BaseUrl;
		var userId = JellyfinClient.UserId;
		IsLoading = true;
		try
		{
			var cached = await homeCache.Get(serverUrl, userId);
			if (!retry && cached.Snapshot is { } snapshot)
			{
				Show(snapshot);
			}

			var failed = false;
			if (retry || !cached.IsFresh)
			{
				// a Retry shows an empty section's placeholders again while it asks for it again; a saved page's empty
				// section stays hidden while the page refreshes (placeholders that then go would move every row below)
				if (retry)
				{
					IsContinueLoading |= ContinueItems.Count == 0;
					IsNextUpLoading |= NextUpItems.Count == 0;
				}

				var continueLoaded = UpdateContinueItems();
				var nextUpLoaded = UpdateNextUpItems();
				await Task.WhenAll(continueLoaded, nextUpLoaded, UpdateRecentItems());
				failed = !continueLoaded.Result || !nextUpLoaded.Result;

				if (Snapshot() is { } shown && JellyfinClient.BaseUrl == serverUrl && JellyfinClient.UserId == userId)
				{
					await homeCache.Save(serverUrl, userId, shown, cached.ChangesSeen);
				}
			}

			var nothing = ContinueItems.Count == 0 && NextUpItems.Count == 0 && RecentItems.Count == 0;
			return (failed, nothing) switch
			{
				(true, true) => LoadMessages.CouldNotLoad("Home"),
				(true, false) => LoadOutcome.Partly("Some of Home couldn't be loaded", LoadMessages.CheckServer()),
				(false, true) => LoadOutcome.Empty("Nothing to watch yet",
					$"Movies and shows on {LoadMessages.ServerName()} appear here once its libraries have some. Libraries shows what is there now."),
				_ => LoadOutcome.Loaded,
			};
		}
		finally
		{
			IsLoading = false;
		}
	}

	private void Show(HomeSnapshot snapshot)
	{
		SetContinueItems(snapshot.ContinueItems);
		IsContinueLoading = false;
		SetNextUpItems(snapshot.NextUpItems);
		IsNextUpLoading = false;
		foreach (var row in snapshot.Rows)
		{
			RecentItems.Add(new NamedQueryResult(row.Library) { Items = [.. row.Items.Select(BaseItemViewModel.FromDto)], IsLoading = false });
		}
		_emptyLibraries.UnionWith(snapshot.EmptyLibraries);
	}

	/// <summary>What the page shows, or null when it shows nothing (a page that failed to load is not kept).</summary>
	private HomeSnapshot? Snapshot()
	{
		var rows = RecentItems.Where(x => !x.IsLoading && x.Items.Count > 0).Select(x => new HomeSnapshotRow(x.Library, [.. x.Items.Select(i => i.Dto)])).ToList();
		return ContinueItems.Count + NextUpItems.Count + rows.Count == 0
			? null
			: new HomeSnapshot([.. ContinueItems.Select(x => x.Dto)], [.. NextUpItems.Select(x => x.Dto)], rows, [.. _emptyLibraries]);
	}

	// fluentfin-mpv: an answer that matches what the page shows (a cached page that has not changed) keeps its cards,
	// so a refresh does not redraw anything; they take the answer's item records all the same, so what changed without
	// changing the card (new artwork: a new image tag) reaches the saved page and the cards made from it. Otherwise the
	// section is replaced (false).
	private static bool KeepCards(IReadOnlyList<BaseItemViewModel> shown, IReadOnlyList<BaseItemDto> items)
	{
		if (!Same(shown, items))
		{
			return false;
		}

		for (var i = 0; i < items.Count; i++)
		{
			shown[i].Dto = items[i];
		}
		return true;
	}

	private static bool Same(IReadOnlyList<BaseItemViewModel> shown, IReadOnlyList<BaseItemDto> items) =>
		shown.Count == items.Count && shown.Zip(items).All(x => x.First.Dto.Id == x.Second.Id && x.First.Dto.Name == x.Second.Name && SameUserData(x.First.Dto.UserData, x.Second.UserData));

	private static bool SameUserData(UserItemDataDto? a, UserItemDataDto? b) =>
		a?.Played == b?.Played && a?.IsFavorite == b?.IsFavorite && a?.PlaybackPositionTicks == b?.PlaybackPositionTicks &&
		a?.PlayedPercentage == b?.PlayedPercentage && a?.UnplayedItemCount == b?.UnplayedItemCount;

	// the section's flag follows its list through OnContinueItemsChanged / OnNextUpItemsChanged
	private void SetContinueItems(IReadOnlyList<BaseItemDto> items)
	{
		if (!KeepCards(ContinueItems, items))
		{
			ContinueItems = [.. items.Select(BaseItemViewModel.FromDto)];
		}
		HasContinueItems = ContinueItems.Count > 0;
	}

	private void SetNextUpItems(IReadOnlyList<BaseItemDto> items)
	{
		if (!KeepCards(NextUpItems, items))
		{
			NextUpItems = [.. items.Select(BaseItemViewModel.FromDto)];
		}
		HasNextUpItems = NextUpItems.Count > 0;
	}

	// fluentfin-mpv: a section's flag follows its list, also when a refresh or a Retry replaces the list
	partial void OnContinueItemsChanged(ObservableCollection<BaseItemViewModel> oldValue, ObservableCollection<BaseItemViewModel> newValue)
	{
		if (oldValue is not null)
		{
			oldValue.CollectionChanged -= OnContinueItemsCollectionChanged;
		}
		newValue.CollectionChanged += OnContinueItemsCollectionChanged;
		HasContinueItems = newValue.Count > 0;
	}

	partial void OnNextUpItemsChanged(ObservableCollection<BaseItemViewModel> oldValue, ObservableCollection<BaseItemViewModel> newValue)
	{
		if (oldValue is not null)
		{
			oldValue.CollectionChanged -= OnNextUpItemsCollectionChanged;
		}
		newValue.CollectionChanged += OnNextUpItemsCollectionChanged;
		HasNextUpItems = newValue.Count > 0;
	}

	private void OnContinueItemsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => HasContinueItems = ContinueItems.Count > 0;

	private void OnNextUpItemsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => HasNextUpItems = NextUpItems.Count > 0;

	private void ListenToUserDataChanges()
	{
		webSocketMessages
			.Select(x => x as UserDataChangedMessage)
			.WhereNotNull()
			.ObserveOn(RxSchedulers.MainThreadScheduler)
			.Subscribe(async msg =>
			{
				foreach (var item in msg.Data?.UserDataList ?? [])
				{
					if (item.ItemId is not { } guid)
					{
						continue;
					}

					ProcessContinueWatchingItemChanged(item, guid);
					await ProcessNextUpItemChanged(item, guid);
					ProcessRecentItemChanged(item, guid);
				}
			})
			.DisposeWith(_disposable);
	}

	// fluentfin-mpv: a failed request leaves the section as it is (a placeholder goes, cached cards stay); false when the
	// server did not answer, so the page can say so. An answer asked for before a watch-state change this page applied
	// to the section is dropped: the section is newer.
	private async Task<bool> UpdateContinueItems()
	{
		var changes = _continueChanges;
		try
		{
			if (await JellyfinClient.GetContinueWatching() is not { } response)
			{
				return false;
			}

			if (changes == _continueChanges)
			{
				SetContinueItems(response.Items ?? []);
			}
			return true;
		}
		finally
		{
			IsContinueLoading = false;
		}
	}

	private async Task<bool> UpdateNextUpItems()
	{
		var changes = _nextUpChanges;
		try
		{
			if (await JellyfinClient.GetNextUp() is not { } response)
			{
				return false;
			}

			if (changes == _nextUpChanges)
			{
				SetNextUpItems(response.Items ?? []);
			}
			return true;
		}
		finally
		{
			IsNextUpLoading = false;
		}
	}

	private async Task UpdateRecentItems()
	{
		// fluentfin-mpv: a row per view at once, in the server's order: the cached row of that library, a placeholder, or
		// (a library that had nothing recent last time) none until it has; each fills in, or goes, as its request answers
		var views = (await JellyfinClient.GetUserViews()).Where(x => x.CollectionType is not BaseItemDto_CollectionType.Music).ToList();
		if (views.Count == 0 && RecentItems.Count > 0)
		{
			return; // the views could not be read: the cached rows stay
		}

		var rows = views.Select(view => RecentItems.FirstOrDefault(x => x.Library.Id == view.Id && x.Library.Name == view.Name)
			?? (view.Id is { } id && _emptyLibraries.Contains(id) ? null : new NamedQueryResult(view))).ToList();
		var shown = rows.OfType<NamedQueryResult>().ToList();
		if (!shown.SequenceEqual(RecentItems))
		{
			RecentItems.Clear();
			foreach (var row in shown)
			{
				RecentItems.Add(row);
			}
		}

		await Task.WhenAll(views.Zip(rows).Select(x => x.Second is { } row ? UpdateRecentRow(row) : UpdateHiddenRow(x.First, views)));
	}

	private async Task UpdateHiddenRow(BaseItemDto library, List<BaseItemDto> views)
	{
		if (await JellyfinClient.GetLatestItems(library) is not { Count: > 0 } items)
		{
			return; // still nothing recent (or the request failed)
		}

		_emptyLibraries.Remove(library.Id!.Value);
		var position = views.IndexOf(library);
		RecentItems.Insert(RecentItems.Count(x => views.FindIndex(v => v.Id == x.Library.Id) < position),
			new NamedQueryResult(library) { Items = [.. items.Select(BaseItemViewModel.FromDto)], IsLoading = false });
	}

	private async Task UpdateRecentRow(NamedQueryResult row)
	{
		var changes = row.Changes;
		try
		{
			var items = await JellyfinClient.GetLatestItems(row.Library);

			if (items is null && !row.IsLoading)
			{
				return; // failed: a cached row stays
			}

			if (changes != row.Changes)
			{
				return; // asked for before a watch-state change the row shows: older than the row
			}

			if (items is null or { Count: 0 })
			{
				if (items is not null && row.Library.Id is { } id)
				{
					_emptyLibraries.Add(id);
				}
				RecentItems.Remove(row);
				return;
			}

			if (!KeepCards(row.Items, items))
			{
				row.Items = [.. items.Select(BaseItemViewModel.FromDto)];
			}
		}
		finally
		{
			row.IsLoading = false;
		}
	}

	private void ProcessRecentItemChanged(UserItemDataDto userData, Guid guid)
	{
		foreach (var library in RecentItems)
		{
			if (library.Items.FirstOrDefault(x => x.Id == guid) is not BaseItemViewModel { UserData: not null } dto)
			{
				continue;
			}

			dto.UserData.IsFavorite = userData.IsFavorite;
			dto.UserData.Played = userData.Played;
			dto.UserData.PlayedPercentage = userData.PlayedPercentage;
			dto.UserData.UnplayedItemCount = userData.UnplayedItemCount;
			library.Changes++; // fluentfin-mpv: an answer asked for before this is older than the row
		}
		;
	}

	private void ProcessContinueWatchingItemChanged(UserItemDataDto userData, Guid guid)
	{
		if (ContinueItems.FirstOrDefault(x => x.Id == guid) is not { } item)
		{
			return;
		}

		_continueChanges++; // fluentfin-mpv: an answer asked for before this is older than the section
		if (userData.PlaybackPositionTicks is null or 0 ||
			userData.Played == true)
		{
			ContinueItems.Remove(item);
		}
		else
		{
			item.UserData!.PlayedPercentage = userData.PlayedPercentage;
		}
	}

	private async Task ProcessNextUpItemChanged(UserItemDataDto userData, Guid guid)
	{
		if (NextUpItems.FirstOrDefault(x => x.Id == guid) is not { } item)
		{
			return;
		}

		if (userData.Played == true)
		{
			_nextUpChanges++; // fluentfin-mpv: this request's answer replaces any asked for before it
			await UpdateNextUpItems();
		}
	}

	[RelayCommand]
	private async Task Continue() => await PlayFirstItem(ContinueItems);

	[RelayCommand]
	private async Task NextUp() => await PlayFirstItem(NextUpItems);

	private Task PlayFirstItem(ObservableCollection<BaseItemViewModel> items)
	{
		if (items.Count == 0)
		{
			return Task.CompletedTask;
		}

		return commands.PlayDto(items.First().Dto);
	}
}

// fluentfin-mpv: was a record (Library, Items); a row now exists, showing a placeholder, before its items are in
public partial class NamedQueryResult(BaseItemDto library) : ObservableObject
{
	public BaseItemDto Library { get; } = library;

	[ObservableProperty]
	public partial ObservableCollection<BaseItemViewModel> Items { get; set; } = [];

	[ObservableProperty]
	public partial bool IsLoading { get; set; } = true;

	/// <summary>Watch-state changes Home applied to this row's cards (an answer asked for before the latest is dropped).</summary>
	internal int Changes { get; set; }
}
