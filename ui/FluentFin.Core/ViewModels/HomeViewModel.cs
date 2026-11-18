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

/// <param name="localSettings">Seaglass 1.1.4: where Continue Watching's "hide after N days" is kept (none: nothing hidden).</param>
/// <param name="time">Seaglass 1.1.4: the clock that says how long ago an item was played (the system's by default).</param>
public partial class HomeViewModel(IJellyfinClient jellyfinClient,
								   IObservable<IOutboundWebSocketMessage> webSocketMessages,
								   GlobalCommands commands,
								   HomeCache homeCache,
								   ILocalSettingsService? localSettings = null,
								   TimeProvider? time = null) : ObservableObject, INavigationAware, IContinueWatchingRow
{
	private readonly CompositeDisposable _disposable = [];
	private readonly TimeProvider _time = time ?? TimeProvider.System;

	// Seaglass 1.1.4: the server's last answers for Continue Watching and Next Up, less what was taken out here: the rows
	// show them less what is hidden (Continue Watching: items not played for the chosen number of days; Next Up: what
	// Continue Watching shows), and the saved page keeps them whole, so a change of the setting shows at once
	private IReadOnlyList<BaseItemDto> _continueAnswer = [];
	private IReadOnlyList<BaseItemDto> _nextUpAnswer = [];

	// Seaglass 1.1.4: the resume point this page last set for an item (a removal: 0; an Undo: the old one). The server's
	// UserDataChanged messages come in order; one that does not match is the state before (the removal's message arriving
	// after its Undo) and is not applied, so it cannot take the card out again. Kept for OwnResumePointFor: a server whose
	// websocket is not connected never says, and the server's later changes must not be ignored for good.
	private readonly Dictionary<Guid, (long Ticks, long SetAt)> _ownResumePoints = [];
	internal static readonly TimeSpan OwnResumePointFor = TimeSpan.FromSeconds(30);

	private bool _shown;


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

	/// <summary>
	/// Seaglass: the hero's slides (<see cref="HeroSlides.Pick"/>): since 1.1.4 the latest items not yet watched, then Next
	/// Up, never what Continue Watching shows. Replaced as a whole, and only when what the hero would show has changed, so a
	/// refresh that changes nothing redraws nothing.
	/// </summary>
	[ObservableProperty]
	public partial IReadOnlyList<HeroSlide> Hero { get; set; } = [];

	private void RefreshHero()
	{
		var slides = HeroSlides.Pick(ContinueItems.Select(x => x.Dto), RecentItems.Where(r => !r.IsLoading).Select(r => (IReadOnlyList<BaseItemDto>)[.. r.Items.Select(i => i.Dto)]),
			NextUpItems.Select(x => x.Dto));
		if (!Hero.SequenceEqual(slides, HeroSlideComparer.Instance))
		{
			Hero = slides;
		}
	}

	private sealed class HeroSlideComparer : IEqualityComparer<HeroSlide>
	{
		public static readonly HeroSlideComparer Instance = new();

		public bool Equals(HeroSlide? a, HeroSlide? b) =>
			a is not null && b is not null && a.Dto.Id == b.Dto.Id && a.IsResume == b.IsResume && a.BackdropTag == b.BackdropTag && a.LogoTag == b.LogoTag;

		public int GetHashCode(HeroSlide slide) => HashCode.Combine(slide.Dto.Id, slide.BackdropTag);
	}

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
		_shown = false;
		_disposable.Dispose();
		return Task.CompletedTask;
	}

	public async Task OnNavigatedTo(object parameter)
	{
		_shown = true;
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
		RefreshHero();
	}

	/// <summary>What the page shows, or null when it shows nothing (a page that failed to load is not kept).</summary>
	private HomeSnapshot? Snapshot()
	{
		var rows = RecentItems.Where(x => !x.IsLoading && x.Items.Count > 0).Select(x => new HomeSnapshotRow(x.Library, [.. x.Items.Select(i => i.Dto)])).ToList();
		return _continueAnswer.Count + _nextUpAnswer.Count + rows.Count == 0
			? null
			: new HomeSnapshot([.. _continueAnswer], [.. _nextUpAnswer], rows, [.. _emptyLibraries]);
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

	/// <summary>
	/// Seaglass 1.1.4: Continue Watching hides items not played for this many days (0: never, the default; 30, 60 or 90). On
	/// this PC only: nothing changes on the server. A change shows in the row at once. Settings' card is
	/// <see cref="ContinueWatchingSettingViewModel"/>, over the same setting.
	/// </summary>
	public int HideContinueWatchingAfterDays
	{
		get => ContinueWatchingRules.ReadHideAfterDays(localSettings);
		set
		{
			if (localSettings is null || ContinueWatchingRules.HideAfterDaysChoices.Contains(value) is false || value == HideContinueWatchingAfterDays)
			{
				return;
			}

			ContinueWatchingRules.SaveHideAfterDays(localSettings, value);
			OnPropertyChanged();
			SetContinueItems(_continueAnswer);
		}
	}

	// the section's flag follows its list through OnContinueItemsChanged / OnNextUpItemsChanged
	private void SetContinueItems(IReadOnlyList<BaseItemDto> items)
	{
		_continueAnswer = items;
		var visible = ContinueWatchingRules.Visible(items, _time.GetUtcNow(), HideContinueWatchingAfterDays);
		// Seaglass 1.1.4: the row's item records, before their cards are made: a card's menu asks whose row it is in
		foreach (var dto in visible)
		{
			ContinueWatchingRows.Register(dto, this);
		}

		if (!KeepCards(ContinueItems, visible))
		{
			ContinueItems = [.. visible.Select(BaseItemViewModel.FromDto)];
		}
		HasContinueItems = ContinueItems.Count > 0;
		ShowNextUp();
		RefreshHero();
	}

	private void SetNextUpItems(IReadOnlyList<BaseItemDto> items)
	{
		_nextUpAnswer = items;
		ShowNextUp();
		RefreshHero();
	}

	// Seaglass 1.1.4: Next Up without what Continue Watching shows (a server before Jellyfin 10.9 lists an episode in
	// progress in both)
	private void ShowNextUp()
	{
		var items = ContinueWatchingRules.NextUpWithout(_nextUpAnswer, ContinueItems.Select(x => x.Dto));
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

	private void OnContinueItemsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
	{
		HasContinueItems = ContinueItems.Count > 0;
		RefreshHero();
	}

	private void OnNextUpItemsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
	{
		HasNextUpItems = NextUpItems.Count > 0;
		RefreshHero();
	}

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
		RefreshHero();
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
			RefreshHero();
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
		// Seaglass 1.1.4: the server telling of a resume point this page set (a removal, an Undo): applied once it says what
		// was set last; before that it is the earlier state (a removal's message coming after its Undo)
		if (_ownResumePoints.TryGetValue(guid, out var own))
		{
			if (_time.GetElapsedTime(own.SetAt) > OwnResumePointFor)
			{
				_ownResumePoints.Remove(guid);
			}
			else if ((userData.PlaybackPositionTicks ?? 0) != own.Ticks)
			{
				return;
			}
			else
			{
				_ownResumePoints.Remove(guid);
			}
		}

		if (ContinueItems.FirstOrDefault(x => x.Id == guid) is not { } item)
		{
			return;
		}

		_continueChanges++; // fluentfin-mpv: an answer asked for before this is older than the section
		if (userData.PlaybackPositionTicks is null or 0 ||
			userData.Played == true)
		{
			ContinueItems.Remove(item);
			_continueAnswer = [.. _continueAnswer.Where(x => x.Id != guid)];
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

	// ---- Seaglass 1.1.4: Remove from Continue Watching, with an Undo -----------------------------------------------------

	/// <summary>The short notice under the rows after a removal (with its Undo), or null.</summary>
	[ObservableProperty]
	public partial HomeNotice? Notice { get; set; }

	bool IContinueWatchingRow.IsShown => _shown;

	Task IContinueWatchingRow.Remove(BaseItemDto dto) => RemoveFromContinueWatching(dto);

	/// <summary>
	/// Takes the item out of the row at once and clears its resume point on the server (Jellyfin has no "hide from
	/// Continue Watching": its list is the items with a resume point, so this is how jellyfin-web's own reset works), with
	/// an Undo on the notice that puts both back. A server that does not take it gets the card back and a notice saying so.
	/// </summary>
	public async Task RemoveFromContinueWatching(BaseItemDto dto)
	{
		if (dto.Id is not { } id || ContinueItems.FirstOrDefault(x => x.Id == id) is not { } item)
		{
			return;
		}

		var index = ContinueItems.IndexOf(item);
		var position = dto.UserData?.PlaybackPositionTicks ?? item.Dto.UserData?.PlaybackPositionTicks ?? 0;
		var removal = new ContinueRemoval(item, item.Dto, index, position);
		_continueChanges++; // an answer asked for before this still has the item
		ContinueItems.Remove(item);
		_continueAnswer = [.. _continueAnswer.Where(x => x.Id != id)];
		ShowNextUp();
		Notice = new HomeNotice($"Removed {ContinueWatchingRules.NameOf(item.Dto)} from Continue Watching", removal);

		_ownResumePoints[id] = (0, _time.GetTimestamp());
		removal.Done = JellyfinClient.SetResumePosition(id, 0);
		var ok = await removal.Done;
		_continueChanges++; // nor does an answer asked for while the server took it
		if (ok)
		{
			await SaveShown();
			return;
		}

		_ownResumePoints.Remove(id);
		if (!removal.Settled)
		{
			removal.Settled = true;
			PutBack(removal);
			Notice = new HomeNotice($"Couldn't remove {ContinueWatchingRules.NameOf(item.Dto)}: {LoadMessages.ServerName()} didn't answer", null);
		}
	}

	/// <summary>Undo on the notice: the card back where it was and its resume point back on the server.</summary>
	[RelayCommand]
	public async Task UndoRemoval()
	{
		if (Notice?.Removal is not { Settled: false } removal)
		{
			return;
		}

		removal.Settled = true;
		Notice = null;
		PutBack(removal);
		if (!await removal.Done)
		{
			return; // the server never took the removal: nothing to put back there
		}

		var id = removal.Dto.Id!.Value;
		_ownResumePoints[id] = (removal.PositionTicks, _time.GetTimestamp());
		var ok = await JellyfinClient.SetResumePosition(id, removal.PositionTicks);
		_continueChanges++;
		if (!ok)
		{
			_ownResumePoints.Remove(id);
			ContinueItems.Remove(removal.Item);
			_continueAnswer = [.. _continueAnswer.Where(x => x.Id != id)];
			ShowNextUp();
			Notice = new HomeNotice($"Couldn't put {ContinueWatchingRules.NameOf(removal.Dto)} back: {LoadMessages.ServerName()} didn't answer", null);
		}

		await SaveShown();
	}

	/// <summary>The notice's time is up (or it was closed): it goes, and the removal stays.</summary>
	public void DismissNotice(HomeNotice notice)
	{
		if (ReferenceEquals(Notice, notice))
		{
			Notice = null;
		}
	}

	private void PutBack(ContinueRemoval removal)
	{
		_continueChanges++;
		if (!ContinueItems.Contains(removal.Item))
		{
			ContinueItems.Insert(Math.Min(removal.Index, ContinueItems.Count), removal.Item);
		}

		if (!_continueAnswer.Any(x => x.Id == removal.Dto.Id))
		{
			var answer = _continueAnswer.ToList();
			// where it was among the items shown: after the one shown before it
			var before = ContinueItems.IndexOf(removal.Item) is > 0 and var at ? ContinueItems[at - 1].Dto.Id : null;
			var place = before is null ? 0 : answer.FindIndex(x => x.Id == before) + 1;
			answer.Insert(Math.Clamp(place, 0, answer.Count), removal.Dto);
			_continueAnswer = answer;
		}

		ShowNextUp();
	}

	// what the page shows now is the saved page (Back to Home shows it at once; without this the removed card came back
	// until the refresh)
	private async Task SaveShown()
	{
		var serverUrl = JellyfinClient.BaseUrl;
		var userId = JellyfinClient.UserId;
		if (Snapshot() is { } shown)
		{
			var cached = await homeCache.Get(serverUrl, userId);
			if (JellyfinClient.BaseUrl == serverUrl && JellyfinClient.UserId == userId)
			{
				await homeCache.Save(serverUrl, userId, shown, cached.ChangesSeen);
			}
		}
	}

	// Seaglass 1.1.4 (interaction audit, item 4): N and C no longer play the first Next Up or Continue Watching item (a stray
	// letter started a film); they take the focus to those rows (HomePage), and Enter plays from there
	internal GlobalCommands Commands => commands;
}

// fluentfin-mpv: was a record (Library, Items); a row now exists, showing a placeholder, before its items are in
public partial class NamedQueryResult(BaseItemDto library) : ObservableObject
{
	public BaseItemDto Library { get; } = library;

	/// <summary>Seaglass 1.1.4 (UX review, fix 9): the row's title, "New in Movies" (it was "Recently Added In Movies").</summary>
	public string Title => HomeRowTitles.NewIn(Library.Name);

	[ObservableProperty]
	public partial ObservableCollection<BaseItemViewModel> Items { get; set; } = [];

	[ObservableProperty]
	public partial bool IsLoading { get; set; } = true;

	/// <summary>Watch-state changes Home applied to this row's cards (an answer asked for before the latest is dropped).</summary>
	internal int Changes { get; set; }
}

/// <summary>Seaglass 1.1.4: the names of Home's rows.</summary>
public static class HomeRowTitles
{
	/// <summary>A library's row of its latest items: "New in Movies"; "New" for a library with no name.</summary>
	public static string NewIn(string? library) => string.IsNullOrWhiteSpace(library) ? "New" : $"New in {library.Trim()}";
}
