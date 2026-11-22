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
/// <param name="recentPlays">sg-cw: the plays the mpv player just ended, shown at once (none: Home shows the server's answers only).</param>
public partial class HomeViewModel(IJellyfinClient jellyfinClient,
								   IObservable<IOutboundWebSocketMessage> webSocketMessages,
								   GlobalCommands commands,
								   HomeCache homeCache,
								   ILocalSettingsService? localSettings = null,
								   TimeProvider? time = null,
								   RecentPlays? recentPlays = null) : ObservableObject, INavigationAware, IContinueWatchingRow
{
	private readonly CompositeDisposable _disposable = [];
	private readonly TimeProvider _time = time ?? TimeProvider.System;

	// Seaglass 1.1.4: the server's last answers for Continue Watching and Next Up, less what was taken out here: the rows
	// show them less what is hidden (Continue Watching: items not played for the chosen number of days; Next Up: what
	// Continue Watching shows), and the saved page keeps them whole, so a change of the setting shows at once
	private IReadOnlyList<BaseItemDto> _continueAnswer = [];
	private IReadOnlyList<BaseItemDto> _nextUpAnswer = [];

	// Seaglass 1.1.4: an item put back by an Undo before the server's message about its removal came: that message (position
	// 0), arriving after the Undo, is the state before and is not applied, so it cannot take the card out again. Only that
	// one message, and only for OwnResumePointFor (a server whose websocket is not connected never sends it); anything else
	// the server says about the item is applied at once, a "played" above all (the review, item 3: a merged "mark as
	// watched" was ignored for 30 s).
	private readonly Dictionary<Guid, (long Ticks, long SetAt, bool Played)> _ownResumePoints = [];
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
		ListenToPlays();

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
				await Task.WhenAll(continueLoaded, nextUpLoaded, UpdateRecentItems(), UpdateNixieRows()); // 1.6 collections: Nixie's rows
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
		ShowNixieRows(snapshot.NixieRows); // 1.6 collections
		RefreshHero();
	}

	/// <summary>What the page shows, or null when it shows nothing (a page that failed to load is not kept).</summary>
	private HomeSnapshot? Snapshot()
	{
		var rows = RecentItems.Where(x => !x.IsLoading && x.Items.Count > 0).Select(x => new HomeSnapshotRow(x.Library, [.. x.Items.Select(i => i.Dto)])).ToList();
		return _continueAnswer.Count + _nextUpAnswer.Count + rows.Count == 0
			? null
			: new HomeSnapshot([.. _continueAnswer], [.. _nextUpAnswer], rows, [.. _emptyLibraries]) { NixieRows = NixieSnapshot() }; // 1.6 collections
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
		// sg-cw: with what the mpv player just played, until the server's answers have it
		var withPlays = _plays.Count == 0 ? items : ContinueWatchingRules.WithPlays(items, _plays.Values, RecordOf);
		var visible = ContinueWatchingRules.Visible(withPlays, _time.GetUtcNow(), HideContinueWatchingAfterDays,
			includeRewatching: localSettings?.ReadSetting(Settings.SettingKeys.LeaveOutRewatching) is not true); // 1.5 fixes
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
		var items = ContinueWatchingRules.NextUpWithout(ContinueWatchingRules.NextUpWithoutPlays(_nextUpAnswer, _plays.Values), ContinueItems.Select(x => x.Dto));
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

	/// <summary>Nixie 1.4.0: a library with a "New in" row on Home: not music, collections or playlists.</summary>
	public static bool HasLatestRow(BaseItemDto view) =>
		view.CollectionType is not (BaseItemDto_CollectionType.Music or BaseItemDto_CollectionType.Boxsets or BaseItemDto_CollectionType.Playlists);

	private async Task UpdateRecentItems()
	{
		// fluentfin-mpv: a row per view at once, in the server's order: the cached row of that library, a placeholder, or
		// (a library that had nothing recent last time) none until it has; each fills in, or goes, as its request answers
		// Nixie 1.4.0 (the UX review's X3, CL-M5): no "New in Collections" or "New in Playlists": a box set is made when a film is
		// imported, so the row repeated "New in Movies" (and fed the hero); HasLatestRow
		var views = (await JellyfinClient.GetUserViews()).Where(HasLatestRow).ToList();
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
			if (library.Items.FirstOrDefault(x => x.Id == guid) is not { } item)
			{
				continue;
			}

			ApplyUserData(item, userData);
			library.Changes++; // fluentfin-mpv: an answer asked for before this is older than the row
		}
	}

	/// <summary>
	/// 1.1.4 (the items review): a change the server pushed, into the card's model (its badges, its progress) and into the
	/// item record the card was made from, which the card's menu, Play and the shared Watched and Favorite toggles read:
	/// only the model was updated, so after playback marked an item watched its menu still said "Mark as watched" and sent
	/// the same state again.
	/// </summary>
	internal static void ApplyUserData(BaseItemViewModel item, UserItemDataDto changed)
	{
		var record = item.Dto.UserData ??= new UserItemDataDto { ItemId = item.Id };
		record.IsFavorite = changed.IsFavorite;
		record.Played = changed.Played;
		record.PlayedPercentage = changed.PlayedPercentage;
		record.UnplayedItemCount = changed.UnplayedItemCount;
		record.PlaybackPositionTicks = changed.PlaybackPositionTicks;
		record.PlayCount = changed.PlayCount ?? record.PlayCount;
		record.LastPlayedDate = changed.LastPlayedDate ?? record.LastPlayedDate;

		if (item.UserData is not { } model)
		{
			item.UserData = UserItemDataViewModel.FromDto(record);
			return;
		}

		model.IsFavorite = changed.IsFavorite;
		model.Played = changed.Played;
		model.PlayedPercentage = changed.PlayedPercentage;
		model.UnplayedItemCount = changed.UnplayedItemCount;
		model.PlaybackPositionTicks = changed.PlaybackPositionTicks;
		model.PlayCount = record.PlayCount;
		model.LastPlayedDate = record.LastPlayedDate;
	}

	private void ProcessContinueWatchingItemChanged(UserItemDataDto userData, Guid guid)
	{
		var ticks = userData.PlaybackPositionTicks ?? 0;
		var played = userData.Played == true;

		// Seaglass 1.1.4 (the review, item 1): an item waiting on its Undo that the server says changed elsewhere (played on
		// another device, marked watched): the Undo would put the old resume point back over the new one. The Undo goes;
		// an item with progress again is back in the row, as the server has it.
		// sg-cw: "marked watched" is a change of the watched mark: an episode watched again on Jellyfin 12 was watched in the
		// row already, and its removal's own message still says so
		if (Notice?.Removal is { Settled: false } pending && pending.Dto.Id == guid)
		{
			var markedWatched = played && pending.Dto.UserData?.Played != true;
			if (!markedWatched && ticks == 0)
			{
				pending.EchoSeen = true; // the removal's own message
			}
			else if (markedWatched || ticks != pending.PositionTicks)
			{
				pending.Settled = true;
				Notice = null;
				if (!markedWatched)
				{
					PutBack(pending);
				}
			}
		}

		// the removal's own message, after an Undo that came first (above): not applied, once
		if (_ownResumePoints.TryGetValue(guid, out var own))
		{
			_ownResumePoints.Remove(guid);
			if (played == own.Played && ticks == 0 && own.Ticks != 0 && _time.GetElapsedTime(own.SetAt) <= OwnResumePointFor)
			{
				return;
			}
		}

		// sg-cw: the server spoke about an item the mpv player just played: what it says replaces the play's guess, and an
		// answer asked for before it is older than both rows (the review: Home's first answer, asked before the Stopped report,
		// put a just-watched episode back up next when it came after this message)
		if (_plays.Remove(guid))
		{
			_continueChanges++;
			_nextUpChanges++;
		}
		if (ContinueItems.FirstOrDefault(x => x.Id == guid) is not { } item)
		{
			return;
		}

		_continueChanges++; // fluentfin-mpv: an answer asked for before this is older than the section
		// sg-cw: out with its resume point; a watched mark alone keeps it (an episode watched again on Jellyfin 12 is both)
		if (userData.PlaybackPositionTicks is null or <= 0)
		{
			ContinueItems.Remove(item);
			_continueAnswer = [.. _continueAnswer.Where(x => x.Id != guid)];
		}
		else
		{
			ApplyUserData(item, userData);
		}
	}

	private async Task ProcessNextUpItemChanged(UserItemDataDto userData, Guid guid)
	{
		if (NextUpItems.FirstOrDefault(x => x.Id == guid) is not { } item)
		{
			return;
		}

		ApplyUserData(item, userData);
		if (userData.Played == true)
		{
			_nextUpChanges++; // fluentfin-mpv: this request's answer replaces any asked for before it
			await UpdateNextUpItems();
		}
	}

	// ---- sg-cw: what the mpv player just played, on Home at once -------------------------------------------------------------

	// plays that ended and are not reported yet (RecentPlays), by item: shown over the server's answers until it has them
	private readonly Dictionary<Guid, RecentPlay> _plays = [];

	// the item records of plays Home had no card for (an episode of a show watched again is on no row)
	private readonly Dictionary<Guid, BaseItemDto> _playItems = [];

	/// <summary>
	/// sg-cw: the player's Back makes this page before the player is left, so Home's first answers are older than the
	/// backend's Stopped report, and a play that put an item in Continue Watching (or took it out) did not show until R. Now
	/// the play shows the moment it ends (<see cref="RecentPlays"/>: an <c>ended</c> event, or one that ended before this page
	/// was made), over whatever the server answers meanwhile; once it is reported and the server shows it, Continue Watching
	/// and Next Up are asked again (the next episode comes from there), and answers asked before that are dropped.
	/// </summary>
	private void ListenToPlays()
	{
		if (recentPlays is null)
		{
			return;
		}

		recentPlays.Changes
			.ObserveOn(RxSchedulers.MainThreadScheduler)
			.Subscribe(play => _ = OnPlay(play))
			.DisposeWith(_disposable);
		foreach (var play in recentPlays.Recent(JellyfinClient.BaseUrl, JellyfinClient.UserId).Where(x => !x.Reported))
		{
			_plays[play.ItemId] = play;
		}

		if (_plays.Count > 0)
		{
			_ = FindPlayItems();
		}
	}

	internal async Task OnPlay(RecentPlay play)
	{
		try
		{
			if (!_shown || !play.IsOf(JellyfinClient.BaseUrl, JellyfinClient.UserId))
			{
				return;
			}

			if (!play.Reported)
			{
				_plays[play.ItemId] = play;
				SetContinueItems(_continueAnswer);
				await FindPlayItems();
				return;
			}

			// the server has it (or never will): its answers are what shows now; one asked for before this is older
			_plays.Remove(play.ItemId);
			_continueChanges++;
			_nextUpChanges++;
			await Task.WhenAll(UpdateContinueItems(), UpdateNextUpItems());
			await SaveShown();
		}
		catch (Exception)
		{
			// a refresh that fails leaves the rows as they are
		}
	}

	// a card for a play of an item Home has no record of: the server's record (its position is the play's)
	private async Task FindPlayItems()
	{
		foreach (var play in _plays.Values.Where(x => x.Guess == PlayGuess.Resumable && RecordOf(x.ItemId) is null).ToList())
		{
			if (await JellyfinClient.GetItem(play.ItemId) is { } record && _plays.ContainsKey(play.ItemId))
			{
				_playItems[play.ItemId] = record;
				SetContinueItems(_continueAnswer);
			}
		}
	}

	private BaseItemDto? RecordOf(Guid id) =>
		_continueAnswer.FirstOrDefault(x => x.Id == id)
		?? _nextUpAnswer.FirstOrDefault(x => x.Id == id)
		?? RecentItems.SelectMany(x => x.Items).Select(x => x.Dto).FirstOrDefault(x => x.Id == id)
		?? _playItems.GetValueOrDefault(id);

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
		var removal = new ContinueRemoval(item, item.Dto, index, position, JellyfinClient.UserId, JellyfinClient.BaseUrl ?? "");
		_continueChanges++; // an answer asked for before this still has the item
		_plays.Remove(id); // sg-cw: a play just ended does not bring it back
		ContinueItems.Remove(item);
		_continueAnswer = [.. _continueAnswer.Where(x => x.Id != id)];
		ShowNextUp();
		Notice = new HomeNotice($"Removed {ContinueWatchingRules.NameOf(item.Dto)} from Continue Watching", removal);

		removal.Done = JellyfinClient.SetResumePosition(id, 0);
		var ok = await removal.Done;
		_continueChanges++; // nor does an answer asked for while the server took it
		if (ok)
		{
			await SaveShown();
			return;
		}

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
		// the review, item 4: the resume point is the user's own data, set as whoever is signed in now
		if (removal.User != JellyfinClient.UserId || removal.Server != (JellyfinClient.BaseUrl ?? ""))
		{
			return;
		}

		PutBack(removal);
		if (!await removal.Done)
		{
			return; // the server never took the removal: nothing to put back there
		}

		var id = removal.Dto.Id!.Value;
		if (!removal.EchoSeen)
		{
			_ownResumePoints[id] = (removal.PositionTicks, _time.GetTimestamp(), removal.Dto.UserData?.Played == true);
		}

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
	public string Title => HomeRowTitles.NewIn(CollectionNames.DisplayLibrary(Library.Name)); // 1.6 collections: a tidy library name

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
