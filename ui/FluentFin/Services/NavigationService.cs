using System.Diagnostics.CodeAnalysis;
using FluentFin.Contracts.Services;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core;
using FluentFin.Core.Services;
using FluentFin.Helpers;
using FluentFin.UI.Core.Contracts.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace FluentFin.Services;

// For more information on navigation between pages see
// https://github.com/microsoft/TemplateStudio/blob/main/docs/WinUI/navigation.md
public class NavigationService : INavigationService
{
	private readonly IPageService _pageService;
	private Frame? _frame;

	public event NavigatedEventHandler? Navigated;

	public Frame? Frame
	{
		get
		{
			// 1.7.5: a released shell's service has no frame until the next shell sets one (never the window's root frame,
			// where a late cast would open the player over the sign-in page)
			if (_frame == null && !_released)
			{
				_frame = App.MainWindow.Content as Frame;
				RegisterFrameEvents();
			}

			return _frame;
		}

		set
		{
			_released = false;
			UnregisterFrameEvents();
			// Nixie 1.7.4: a new shell (a sign-in, Switch user, another server) starts its own history: nothing of the last one is kept
			if (!ReferenceEquals(_frame, value))
			{
				LetGo(_keep.Clear(), "a new frame");
			}

			_frame = value;
			RegisterFrameEvents();
		}
	}

	private bool _released;

	/// <inheritdoc/>
	public void Release()
	{
		UnregisterFrameEvents();
		if (_frame is not null)
		{
			LetGo(_keep.Clear(), "the shell went");
		}

		_frame = null;
		_released = true;
	}

	[MemberNotNullWhen(true, nameof(Frame), nameof(_frame))]
	public bool CanGoBack => Frame != null && Frame.CanGoBack;

	/// <param name="homeCache">Nixie 1.7.4: where a sign-in and a sign-out are heard (the kept pages go) and what may have changed is
	/// counted (a kept page comes back refreshed behind); none in the tests.</param>
	public NavigationService(IPageService pageService, HomeCache? homeCache = null, ILogger<NavigationService>? logger = null)
	{
		_pageService = pageService;
		_homeCache = homeCache;
		_logger = logger;
		_keep = new PageKeep<PageStackEntry>(x => x.SourcePageType, x => x.Parameter);
		// Nixie 1.7.4: a watch state or a favourite changed anywhere (a toggle, a play the backend reported): a page kept since then
		// shows it on its cards as it comes back (Replay), and is refreshed behind
		ItemUserData.Changed += OnUserDataChanged;
		if (homeCache is not null)
		{
			// never another user's or another server's page: a sign-in (Switch user, another server, the same user again) or a
			// sign-out lets go of every kept page (raised on any thread; the keep has its own lock)
			homeCache.SigningIn += (_, _) => { LetGo(_keep.Clear(), "a sign-in"); ClearPageCacheSoon(); };
			homeCache.Forgetting += (_, _) => { LetGo(_keep.Clear(), "a sign-out"); ClearPageCacheSoon(); };
			homeCache.ForgettingLogin += _ => { LetGo(_keep.Clear(), "a saved sign-in removed"); ClearPageCacheSoon(); };
		}
	}

	// ---- Nixie 1.7.4: Back to a page just left shows it as it was (PageKeep, IKeptOnBack) ---------------------------------------

	private readonly HomeCache? _homeCache;
	private readonly ILogger<NavigationService>? _logger;

	/// <summary>Nixie 1.7.4: the view models of the last pages left, for Back and Forward.</summary>
	private readonly PageKeep<PageStackEntry> _keep;

	private int _userDataChanges;

	// Nixie 1.7.4: the watch states and favourites changed lately (the newest last, at most MissedKept), each with its place in
	// the order of changes: a kept page is given the ones it missed when it comes back (Replay)
	private const int MissedKept = 256;
	private readonly Lock _missedLock = new();
	private readonly List<(long Seq, Guid Id, Jellyfin.Sdk.Generated.Models.UserItemDataDto Data)> _missed = [];
	private long _seq;
	private bool _replaying;

	private void OnUserDataChanged(object? sender, ItemUserDataChangedEventArgs e)
	{
		if (_replaying)
		{
			return;
		}

		Interlocked.Increment(ref _userDataChanges);
		lock (_missedLock)
		{
			_missed.Add((++_seq, e.ItemId, e.UserData));
			if (_missed.Count > MissedKept)
			{
				_missed.RemoveAt(0);
			}
		}
	}

	/// <summary>Where the log of changes is now (a page kept now missed what comes after).</summary>
	private long Mark()
	{
		lock (_missedLock)
		{
			return _seq;
		}
	}

	/// <summary>
	/// Nixie 1.7.4: a kept page came back: the watch states and favourites changed since it was kept (on the page Back left, a
	/// play) are told again once its cards are in the window (they listen from their Loaded), so a film marked watched shows so
	/// on its card at once; the page's refresh behind (stale) then brings what the server says.
	/// </summary>
	/// <remarks>
	/// Nixie 1.7.5 (gate fix): what was missed is read when it is told, once the cards listen, not when Back began: a play's
	/// Stopped report reaches the server as Back leaves the player, and its watch state (RecentPlays) was told in between, to
	/// rows not yet listening, while the list read at Back told them the play's state from before (the season row kept "1%").
	/// </remarks>
	private void Replay(Page? page, long since)
	{
		if (page is null)
		{
			return;
		}

		void Tell()
		{
			UiDispatch.Enqueue(page.DispatcherQueue, "a kept page's missed changes", () =>
			{
				IReadOnlyList<(Guid Id, Jellyfin.Sdk.Generated.Models.UserItemDataDto Data)> missed;
				lock (_missedLock)
				{
					missed = Core.ViewModels.KeptCards.LatestTold(_missed.Where(x => x.Seq > since).Select(x => (x.Id, x.Data)));
				}

				if (missed.Count == 0)
				{
					return;
				}

				_replaying = true;
				try
				{
					foreach (var (id, data) in missed)
					{
						ItemUserData.Publish(id, data);
					}
				}
				finally
				{
					_replaying = false;
				}

				_logger?.LogInformation("[back] {Page}: {Count} change(s) made since it was left shown", page.GetType().Name, missed.Count);
			}, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
		}

		if (page.IsLoaded)
		{
			Tell();
			return;
		}

		void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
		{
			page.Loaded -= OnLoaded;
			Tell();
		}

		page.Loaded += OnLoaded;
	}

	/// <summary>
	/// Nixie 1.7.5 (gate fix): the refresh behind a kept page is done; a watch state told while it was on its way that is newer
	/// than the last told (a play's Stopped report, then the refresh's answer asked before it) is told again
	/// (<see cref="Core.ViewModels.KeptCards.NewerThanLastTold"/>): Back from the player to the season page kept "1% watched".
	/// </summary>
	private void ReplayNewer(Page? page, long since)
	{
		IReadOnlyList<(Guid Id, Jellyfin.Sdk.Generated.Models.UserItemDataDto Data)> newer;
		lock (_missedLock)
		{
			newer = Core.ViewModels.KeptCards.NewerThanLastTold(_missed.Where(x => x.Seq > since).Select(x => (x.Id, x.Data)));
		}

		if (newer.Count == 0 || page is null)
		{
			return;
		}

		UiDispatch.Enqueue(page.DispatcherQueue, "a kept page's newer watch states", () =>
		{
			_replaying = true;
			try
			{
				foreach (var (id, data) in newer)
				{
					ItemUserData.Publish(id, data);
				}
			}
			finally
			{
				_replaying = false;
			}

			_logger?.LogInformation("[back] {Page}: {Count} watch state(s) told during its refresh told again, newer than its answer", page.GetType().Name, newer.Count);
		}, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
	}

	/// <summary>Nixie 1.7.4: the kept view model Back or Forward is showing, until its page arrives (<see cref="OnNavigated"/>).</summary>
	private (object ViewModel, bool Stale, long Mark, object? Parameter, long Started)? _returning;

	/// <summary>
	/// Nixie 1.7.4: where the frame keeps the next page Forward shows: the end of its forward stack (true) or its start. Learned
	/// from where Back puts the page it leaves (the next page forward, by definition); null until a Back showed it.
	/// </summary>
	private bool? _nextForwardAtEnd;

	/// <summary>Nixie 1.7.4: how many pages are kept now (the tests; the log).</summary>
	internal int KeptPages => _keep.Count;

	/// <summary>The signed-in server and user a kept page must belong to.</summary>
	private static string Session() => $"{SessionInfo.BaseUrl}|{SessionInfo.CurrentUser?.Id}";

	/// <summary>What may have changed what a page shows: the server's change messages, a sign-in, a player opened or left
	/// (HomeCache's count), and every watch state or favourite changed here.</summary>
	private int Changes() => (_homeCache?.ChangeCount ?? 0) + Volatile.Read(ref _userDataChanges);

	/// <summary>The page shown now, as Back or Forward will find it in the history once it is left.</summary>
	private (object ViewModel, Type Page, object? Parameter)? Leaving()
	{
		if (_frame is not { Content: { } content } frame || frame.GetPageViewModel() is not IKeptOnBack { CanKeep: true } viewModel)
		{
			return null;
		}

		return (viewModel, content.GetType(), _currentParameter);
	}

	/// <summary>
	/// The page just left (<see cref="Leaving"/>), kept for its entry in <paramref name="stack"/> (the back stack after a new page or
	/// Forward, the forward stack after Back), found at the end the frame put it.
	/// </summary>
	private void KeepLeft((object ViewModel, Type Page, object? Parameter)? left, IList<PageStackEntry> stack, bool atEnd)
	{
		if (left is not { } page || stack.Count == 0)
		{
			return;
		}

		var entry = stack[atEnd ? stack.Count - 1 : 0];
		if (entry.SourcePageType != page.Page || !Equals(entry.Parameter, page.Parameter))
		{
			// (a season switched in place takes over its entry: Back goes past it, nothing to keep)
			_logger?.LogDebug("[back] {Page} not kept: the history has no entry for it", page.Page.Name);
			return;
		}

		LetGo(_keep.Keep(entry, page.ViewModel, Session(), Changes(), Mark()), "Back's reach", routine: true);
		_logger?.LogDebug("[back] {Page} kept for Back ({Count} kept)", page.Page.Name, _keep.Count);
	}

	/// <summary>The kept view model for <paramref name="target"/>, offered to the page Back or Forward makes for it; null for none.</summary>
	private IDisposable? OfferKept(PageStackEntry target)
	{
		_returning = null;
		if (_keep.Take(target, Session(), Changes()) is not { } kept)
		{
			return null;
		}

		_returning = (kept.ViewModel, kept.Stale, kept.Mark, target.Parameter, System.Diagnostics.Stopwatch.GetTimestamp());
		return ViewModelHandoff.Offer(kept.ViewModel);
	}

	/// <summary>
	/// Lets go of kept view models: a sign-in, a sign-out, a new shell or a theme switch (said in the log); Back's reach and the
	/// history no longer reaching them (<paramref name="routine"/>: said in a detailed log only).
	/// </summary>
	private void LetGo(IReadOnlyList<object> viewModels, string why, bool routine = false)
	{
		if (viewModels.Count == 0)
		{
			return;
		}

		if (routine)
		{
			_logger?.LogDebug("[back] {Count} kept page(s) let go: {Why}", viewModels.Count, why);
		}
		else
		{
			_logger?.LogInformation("[back] {Count} kept page(s) let go: {Why}", viewModels.Count, why);
		}
	}

	// ---- Nixie 1.7.5: Home is kept whole for Back (its page too, not only its view model) -------------------------------------

	/// <summary>
	/// Nixie 1.7.5: the pages the frame keeps whole for Back and Forward (NavigationCacheMode.Enabled): Home, in every theme. Back
	/// to Home made the whole page again (its hero, every row): 1.5 to 1.8 s on a busy PC before its rows, scroll and card were
	/// back. The frame keeps the one instance; a new visit (a tab, a sign-in, R) never gets it (<see cref="ClearPageCache"/>
	/// first), nor another session, nor another theme.
	/// </summary>
	/// <remarks>
	/// Nixie 1.7.5, the gate's second round: the libraries and the search results too, in every theme (a library's grid laid
	/// out again at Back's place took 0.6 to 1.5 s before its card came back: Drive-in's and Editorial's). The frame keeps one
	/// instance of each page type; Back or Forward gets it only when it is the page of the entry reached (its kept view model,
	/// <see cref="PrepareKeptWhole"/>), else every kept page is made anew as before.
	/// </remarks>
	public static bool KeptWhole(Type sharedPage) =>
		sharedPage == typeof(Views.HomePage) || sharedPage == typeof(Views.LibraryPage) || sharedPage == typeof(Views.SearchResultsPage);

	private bool KeptWholePage(Type pageType)
	{
		try
		{
			return KeptWhole(_pageService.SharedPageType(pageType));
		}
		catch (Exception)
		{
			return false;
		}
	}

	// Nixie 1.7.5: the view model of the page the frame keeps of each type kept whole (the one left last), and the page
	private readonly Dictionary<Type, WeakReference<object>> _keptWholeOf = [];
	private readonly Dictionary<Type, WeakReference<Page>> _keptWholePage = [];

	/// <summary>Nixie 1.7.5: the frame's cached pages go (a page kept whole is made again on its next visit).</summary>
	private void ClearPageCache(Frame frame)
	{
		_keptWholeOf.Clear();
		_keptWholePage.Clear();
		var size = frame.CacheSize;
		frame.CacheSize = 0;
		frame.CacheSize = size;
	}

	/// <summary>
	/// Nixie 1.7.5: the page shown is about to be left: the frame keeps it when its type is kept whole, with its view model.
	/// Never a page just dropped (<see cref="DropKeptWhole"/>): the frame keeps it no more.
	/// </summary>
	private void NoteLeaving(Frame frame)
	{
		if (frame.Content is Page { NavigationCacheMode: not NavigationCacheMode.Disabled } page && KeptWholePage(page.GetType()) && frame.GetPageViewModel() is { } viewModel)
		{
			_keptWholeOf[page.GetType()] = new WeakReference<object>(viewModel);
			_keptWholePage[page.GetType()] = new WeakReference<Page>(page);
		}
	}

	/// <summary>
	/// Nixie 1.7.5 (re-review Blocker): the frame's kept page of <paramref name="pageType"/> goes, and only it: the one left last
	/// of that type, and the page shown when it is of that type (WinUI keeps one page per type, the shown one included, and
	/// hands it to the next visit of that type: Movies, the Shows tab, Back gave Movies' entry the Shows page). A page whose
	/// NavigationCacheMode is Disabled leaves the frame's cache; Home kept whole stays when a library's page goes.
	/// </summary>
	private void DropKeptWhole(Frame frame, Type pageType)
	{
		if (frame.Content is Page shown && shown.GetType() == pageType)
		{
			shown.NavigationCacheMode = NavigationCacheMode.Disabled;
		}

		if (_keptWholePage.TryGetValue(pageType, out var left) && left.TryGetTarget(out var leftPage))
		{
			leftPage.NavigationCacheMode = NavigationCacheMode.Disabled;
		}

		_keptWholePage.Remove(pageType);
		_keptWholeOf.Remove(pageType);
	}

	/// <summary>
	/// Nixie 1.7.5: whether the frame's kept page of the target's type may be handed to the entry Back or Forward reaches: the
	/// page shown is of another type (re-review Blocker: one of the same type is the one the frame keeps), and the kept page is
	/// that entry's own (its view model is the one kept for the entry).
	/// </summary>
	public static bool UsesKeptWhole(Type? shownType, Type targetType, object? keptViewModel, object? targetViewModel) =>
		shownType != targetType && keptViewModel is not null && ReferenceEquals(keptViewModel, targetViewModel);

	/// <summary>
	/// Nixie 1.7.5: before Back or Forward to <paramref name="target"/>: the page the frame keeps of its type is used only when it
	/// is that entry's (<see cref="UsesKeptWhole"/>); else that page goes (<see cref="DropKeptWhole"/>), and the entry's page is
	/// made anew (given its kept view model, as before). The page shown is noted unless it was just dropped.
	/// </summary>
	private void PrepareKeptWhole(Frame frame, PageStackEntry target)
	{
		if (KeptWholePage(target.SourcePageType))
		{
			var kept = _keptWholeOf.TryGetValue(target.SourcePageType, out var weak) && weak.TryGetTarget(out var viewModel) ? viewModel : null;
			if (!UsesKeptWhole(frame.Content?.GetType(), target.SourcePageType, kept, _returning?.ViewModel))
			{
				DropKeptWhole(frame, target.SourcePageType);
			}
		}

		NoteLeaving(frame);
	}

	// a sign-in or a sign-out (raised on any thread): no page of the last session is kept whole
	private void ClearPageCacheSoon()
	{
		if (_frame is { } frame)
		{
			frame.DispatcherQueue.TryEnqueue(() => ClearPageCache(frame));
		}
	}

	/// <summary>Lets go of what is kept for entries Back and Forward no longer reach.</summary>
	private void RetainHistory(Frame frame) => LetGo(_keep.Retain(frame.BackStack.Concat(frame.ForwardStack)), "no longer in the history", routine: true);

	private void RegisterFrameEvents()
	{
		if (_frame != null)
		{
			_frame.Navigated += OnNavigated;
		}
	}

	private void UnregisterFrameEvents()
	{
		if (_frame != null)
		{
			_frame.Navigated -= OnNavigated;
		}
	}

	public bool GoBack()
	{
		if (CanGoBack)
		{
			var vmBeforeNavigation = _frame.GetPageViewModel();
			var left = Leaving();
			// Nixie 1.7.4: the page Back shows gets its kept view model, as it was (OnNavigated: Returned, not OnNavigatedTo)
			using (OfferKept(_frame.BackStack[^1]))
			{
				PrepareKeptWhole(_frame, _frame.BackStack[^1]); // Nixie 1.7.5
				_frame.GoBack();
			}

			if (vmBeforeNavigation is INavigationAware navigationAware)
			{
				navigationAware.OnNavigatedFrom();
			}

			// the page left is the next one forward: kept for Forward, at the end of the forward stack the frame put it
			if (left is { } page && _frame.ForwardStack.Count > 0)
			{
				var stack = _frame.ForwardStack;
				bool Is(PageStackEntry x) => x.SourcePageType == page.Page && Equals(x.Parameter, page.Parameter);
				if (stack.Count > 1 && Is(stack[0]) != Is(stack[^1]))
				{
					_nextForwardAtEnd = Is(stack[^1]);
				}

				KeepLeft(left, stack, _nextForwardAtEnd ?? false);
			}

			RetainHistory(_frame);
			return true;
		}

		return false;
	}

	[MemberNotNullWhen(true, nameof(Frame), nameof(_frame))]
	public bool CanGoForward => Frame != null && Frame.CanGoForward;

	/// <summary>Seaglass: forward, the Frame's own forward history (<see cref="PruneForwardStack{T}"/> keeps the player out of it).</summary>
	public bool GoForward()
	{
		if (CanGoForward)
		{
			var vmBeforeNavigation = _frame.GetPageViewModel();
			var left = Leaving();
			// Nixie 1.7.4: the page Forward shows gets its kept view model (only once the frame's forward order is known)
			var forward = _frame.ForwardStack;
			var target = forward[_nextForwardAtEnd == true ? forward.Count - 1 : 0];
			using (_nextForwardAtEnd is not null || forward.Count == 1 ? OfferKept(target) : null)
			{
				if (_nextForwardAtEnd is null && forward.Count > 1)
				{
					_returning = null; // (no entry known: nothing kept is offered)
				}

				PrepareKeptWhole(_frame, target); // Nixie 1.7.5
				_frame.GoForward();
			}

			if (vmBeforeNavigation is INavigationAware navigationAware)
			{
				navigationAware.OnNavigatedFrom();
			}

			KeepLeft(left, _frame.BackStack, atEnd: true);
			RetainHistory(_frame);
			return true;
		}

		return false;
	}

	public bool NavigateTo(string pageKey, object? parameter = null, bool clearNavigation = false)
	{
		try
		{
			var pageType = _pageService.GetPageType(pageKey);
			if (_frame is not { } frame)
			{
				return false;
			}

			return Open(frame.Content?.GetType(), pageType, parameter, () =>
			{
				frame.Tag = clearNavigation;
				var vmBeforeNavigation = frame.GetPageViewModel();
				var left = Leaving();
				_returning = null;
				// Nixie 1.7.5: a new visit to a page kept whole (Home, a library, search results) makes it anew, at its top: never the
				// instance the frame keeps of that type (the page shown when it is one: re-review Blocker), and only that one goes
				if (KeptWholePage(pageType))
				{
					DropKeptWhole(frame, pageType);
				}

				NoteLeaving(frame); // (not a page just dropped)

				// the frame raises Navigated (OnNavigated: Arrived) before Navigate returns
				var navigated = frame.Navigate(pageType, parameter);
				if (navigated && vmBeforeNavigation is INavigationAware navigationAware)
				{
					navigationAware.OnNavigatedFrom();
				}

				// Nixie 1.7.4: the page left stays in reach of Back as it was (none when the history was cleared)
				if (navigated)
				{
					KeepLeft(left, frame.BackStack, atEnd: true);
					RetainHistory(frame);
				}

				return navigated;
			});
		}
		catch
		{
			return false;
		}

	}

	/// <summary>
	/// Whether the frame already shows the place asked for: the same page with the same parameter (or no parameter asked for).
	/// <see cref="NavigateTo"/> refuses it. Nixie 1.6.1 (the merge review's R1): <paramref name="shownParameter"/> is the
	/// parameter the frame shows now, which every arrival sets (<see cref="OnNavigated"/>: Back and Forward too). It was the
	/// last one <see cref="NavigateTo"/> passed, which Back left behind: See all, Back, the same See all did nothing, and so
	/// did Movies, Shows, Back, Shows.
	/// </summary>
	internal static bool ShowsAlready(Type? shownPage, object? shownParameter, Type pageType, object? parameter) =>
		shownPage == pageType && (parameter is null || parameter.Equals(shownParameter));

	/// <summary>
	/// Nixie 1.6.1: <see cref="NavigateTo"/>'s decision and move. <paramref name="navigate"/> moves the frame (which raises
	/// Navigated, so <see cref="Arrived"/>, before it returns) unless the frame already shows that place, by the parameter
	/// the frame shows now. The tests drive it with a frame of their own (NavigationServiceTests).
	/// </summary>
	internal bool Open(Type? shownPage, Type pageType, object? parameter, Func<bool> navigate) =>
		!ShowsAlready(shownPage, _currentParameter, pageType, parameter) && navigate();

	/// <summary>Nixie 1.6.1: a page arrived (new, Back, Forward, a refresh, a theme switch): the parameter the frame shows now.</summary>
	internal void Arrived(object? parameter) => _currentParameter = parameter;

	/// <summary>
	/// Nixie 1.3.0: the parameter of the page the frame shows (the theme switch shows it again with it). Nixie 1.6.1: set by
	/// every arrival, so <see cref="ShowsAlready"/> asks about the page the frame shows, not the last one opened.
	/// </summary>
	private object? _currentParameter;

	/// <summary>Nixie 1.3.0: the page type a theme switch is navigating to, until its Navigated (<see cref="ReloadForTheme"/>).</summary>
	private Type? _pendingThemeReload;

	/// <summary>
	/// Nixie 1.3.0: whether the frame's last navigation was a theme switch's (the same page in the new theme's view, same view
	/// model): the shell leaves the top bar as it was (search text, tab) and the page its view model's state. Set before the
	/// frame's other Navigated handlers run.
	/// </summary>
	public bool LastNavigationWasThemeReload { get; private set; }

	/// <summary>
	/// Nixie 1.3.0: after the layout theme changed (ThemeService). The history's entries become the new theme's pages for the
	/// same view models (pages behind the current one are rebuilt when Back reaches them), the frame's page cache is cleared,
	/// and the current page is shown in the new theme's view with the same view model and parameter, its own history entry
	/// replaced, no transition (the switch crossfades). The player page is never reloaded: the overlay restyles in place and the
	/// pages behind it switch when it closes.
	/// </summary>
	public ThemeReload ReloadForTheme()
	{
		if (_frame is not { } frame)
		{
			return ThemeReload.None;
		}

		// Nixie 1.7.4: the pages behind are rebuilt in the new theme when Back reaches them, as before: nothing kept comes back
		LetGo(_keep.Clear(), "a theme switch");

		// no cached page of the old theme comes back
		var cacheSize = frame.CacheSize;
		frame.CacheSize = 0;
		frame.CacheSize = cacheSize;

		RemapStack(frame.BackStack, x => x.SourcePageType, ThemedPageFor, (x, page) => new PageStackEntry(page, x.Parameter, x.NavigationTransitionInfo));
		RemapStack(frame.ForwardStack, x => x.SourcePageType, ThemedPageFor, (x, page) => new PageStackEntry(page, x.Parameter, x.NavigationTransitionInfo));

		if (frame.Content is not { } content)
		{
			return ThemeReload.None;
		}

		var current = content.GetType();
		var next = ThemedPageFor(current);
		var plan = PlanReload(_pageService.SharedPageType(current), current, next);
		if (plan != ThemeReload.Reloaded)
		{
			return plan;
		}

		_pendingThemeReload = next;
		frame.Tag = false;
		using (ViewModelHandoff.Offer(frame.GetPageViewModel()))
		{
			try
			{
				if (!frame.Navigate(next!, _currentParameter, new SuppressNavigationTransitionInfo()))
				{
					_pendingThemeReload = null;
					return ThemeReload.None;
				}
			}
			catch (Exception)
			{
				_pendingThemeReload = null;
				return ThemeReload.None;
			}
		}

		ClearPageCache(frame); // Nixie 1.7.5: the old theme's page, kept as it left (Home is kept whole), goes too
		return ThemeReload.Reloaded;
	}

	/// <summary>Nixie 1.3.1: the page type a refresh is navigating to, until its Navigated (<see cref="ReloadForRefresh"/>).</summary>
	private Type? _pendingRefresh;

	/// <summary>
	/// Nixie 1.3.1: whether the frame's last navigation was a refresh (R, F5: the same page loaded again): the shell leaves
	/// the top bar as it was, as after a theme switch. Set before the frame's other Navigated handlers run.
	/// </summary>
	public bool LastNavigationWasRefresh { get; private set; }

	/// <summary>
	/// Nixie 1.4.0 (integration: the top bar's trail rule against the library's R): whether the last refresh keeps the page's
	/// place (R, F5: the sort, the filters, the card and how far down) or starts it at its top (the lit tab chosen again on its
	/// own page). A library put its place back on both, so choosing Movies again on Movies stayed far down (Editorial).
	/// </summary>
	public bool LastRefreshKeepsPlace { get; private set; } = true;

	/// <summary>
	/// Nixie 1.3.1 (R, F5, Ctrl+R; the user: "make r refresh the jellyfin page"): the page shown, loaded again: a new view of the
	/// same page with the same parameter and a new view model, which asks the server again (its OnNavigatedTo), in place of
	/// the old one in the history (Back and Forward as they were), no transition. Only a page whose view model
	/// <paramref name="refreshes"/> says has server data; null (nothing done) for any other, and when nothing is shown.
	/// </summary>
	/// <param name="keepPlace">False for a reload that starts the page at its top (<see cref="LastRefreshKeepsPlace"/>).</param>
	public Page? ReloadForRefresh(Func<Type, bool> refreshes, bool keepPlace = true)
	{
		if (_frame is not { Content: Page page } frame)
		{
			return null;
		}

		Type viewModel;
		try
		{
			viewModel = _pageService.GetViewModelType(page.GetType());
		}
		catch (ArgumentException)
		{
			return null;
		}

		if (!refreshes(viewModel))
		{
			return null;
		}

		var before = frame.GetPageViewModel();
		var forward = frame.ForwardStack.ToList();
		_returning = null; // Nixie 1.7.4: R makes a new view model, which asks the server again
		LastRefreshKeepsPlace = keepPlace;
		_pendingRefresh = page.GetType();
		frame.Tag = false;
		// Nixie 1.7.5: R makes the page anew, never the instance kept whole for Back (the page shown, of that type, goes: Blocker)
		if (KeptWholePage(page.GetType()))
		{
			DropKeptWhole(frame, page.GetType());
		}

		try
		{
			if (!frame.Navigate(page.GetType(), _currentParameter, new SuppressNavigationTransitionInfo()))
			{
				_pendingRefresh = null;
				return null;
			}
		}
		catch (Exception)
		{
			_pendingRefresh = null;
			return null;
		}

		// a new navigation empties Forward: it is put back, the refresh is not a step
		foreach (var entry in forward)
		{
			frame.ForwardStack.Add(entry);
		}

		if (before is INavigationAware navigationAware)
		{
			navigationAware.OnNavigatedFrom();
		}

		return frame.Content as Page;
	}

	/// <summary>The page the active theme shows for <paramref name="page"/>'s view model (the shared page when it has none), null for a page the service does not know.</summary>
	private Type? ThemedPageFor(Type page)
	{
		try
		{
			return _pageService.GetPageType(_pageService.GetViewModelType(page).FullName!);
		}
		catch (ArgumentException)
		{
			return null;
		}
	}

	/// <summary>
	/// What a theme switch does with the page shown: the player stays (<see cref="ThemeReload.Deferred"/>), a page the theme
	/// shows the same way stays (<see cref="ThemeReload.SamePage"/>), any other is shown again in the new view.
	/// </summary>
	internal static ThemeReload PlanReload(Type sharedCurrent, Type current, Type? next) =>
		sharedCurrent == typeof(Views.VideoPlayerPage) ? ThemeReload.Deferred
		: next is null ? ThemeReload.None
		: next == current ? ThemeReload.SamePage
		: ThemeReload.Reloaded;

	/// <summary>
	/// Nixie 1.3.0: the history keeps view models, not views: each entry whose page the active theme shows differently is
	/// replaced by the theme's page for the same view model, with the same parameter. The number replaced.
	/// </summary>
	internal static int RemapStack<T>(IList<T> stack, Func<T, Type> pageType, Func<Type, Type?> pageFor, Func<T, Type, T> withPage)
	{
		var replaced = 0;
		for (var i = 0; i < stack.Count; i++)
		{
			var page = pageType(stack[i]);
			if (pageFor(page) is { } themed && themed != page)
			{
				var entry = withPage(stack[i], themed);
				stack.RemoveAt(i);
				stack.Insert(i, entry);
				replaced++;
			}
		}

		return replaced;
	}

	// Nixie 1.7.5: the page Back (or Forward) showed with its kept view model (held weakly) and when Back began, until its place
	// is put back
	private static (WeakReference<Page>? Page, long Started) _placeFrom;

	/// <summary>
	/// Nixie 1.7.5 (rule 10): a page Back showed as it was has put its place back (how far down, the card left from): the time
	/// since Back, once per Back (the page's own line comes after "shown as it was", which is the view model's part only). A
	/// library put its place back on a low-priority pass, after its posters' work, about 2.5 s after Back under load.
	/// </summary>
	public static void PlacePutBack(Page page, string what)
	{
		if (!PuttingBack(page))
		{
			return;
		}

		var started = _placeFrom.Started;
		var steps = _placeSteps.Count > 0 ? $" ({string.Join(", ", _placeSteps)})" : "";
		_placeFrom = default;
		_placeSteps.Clear();
		App.GetService<ILogger<NavigationService>>().LogInformation("[perf] back: {Page}'s {What} put back in {Ms} ms{Steps}", page.GetType().Name, what,
			(long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, steps);
	}

	// Nixie 1.7.5: the steps of the place being put back, each with its time since Back, said with it ("the page loaded 900")
	private static readonly List<string> _placeSteps = [];

	/// <summary>Nixie 1.7.5 (rule 10): a step of Back's place on <paramref name="page"/> (its time since Back goes in the place's line).</summary>
	public static void PlaceStep(Page page, string step)
	{
		if (PuttingBack(page) && _placeSteps.Count < 8)
		{
			_placeSteps.Add($"{step} {(long)System.Diagnostics.Stopwatch.GetElapsedTime(_placeFrom.Started).TotalMilliseconds}");
		}
	}

	/// <summary>
	/// Nixie 1.7.5 (rule 10): the step "view at place" in Back's place line once <paramref name="scroller"/> shows
	/// <paramref name="offset"/> (its ScrollTo landed), so the line tells the scroll's landing from the page's own work.
	/// </summary>
	public static void StepWhenAt(Page page, Microsoft.UI.Xaml.Controls.ScrollView scroller, double offset)
	{
		if (!PuttingBack(page))
		{
			return;
		}

		void OnViewChanged(Microsoft.UI.Xaml.Controls.ScrollView sender, object args)
		{
			if (!PuttingBack(page))
			{
				sender.ViewChanged -= OnViewChanged;
				return;
			}

			if (Math.Abs(sender.VerticalOffset - offset) < 1)
			{
				sender.ViewChanged -= OnViewChanged;
				PlaceStep(page, "view at place");
			}
		}

		scroller.ViewChanged += OnViewChanged;
	}

	/// <summary>Nixie 1.7.5: whether <paramref name="page"/> is the one Back (or Forward) just showed with its kept view model, its place not yet put back.</summary>
	public static bool PuttingBack(Page page) => _placeFrom.Page?.TryGetTarget(out var shown) == true && ReferenceEquals(shown, page);

	// ---- Nixie 1.7.5 (review Major 3): the pages still alive, for the harness's leak check ----------------------------------

	/// <summary>Whether the run counts the pages still alive after each navigation (NIXIE_HARNESS_LEAKS=1; never set in a release).</summary>
	private static readonly bool CountsLivePages = Environment.GetEnvironmentVariable("NIXIE_HARNESS_LEAKS") == "1";

	private static readonly List<WeakReference<Page>> SeenPages = [];

	/// <summary>
	/// Nixie 1.7.5: the pages alive, by type, after a full collection ("SeriesPage 2, EpisodePage 1"): a page left and not kept
	/// (only Home is kept whole) is collected unless something still holds it (then-175-leak.ps1 reads the log line).
	/// </summary>
	public static string LivePages(IEnumerable<WeakReference<Page>> pages) =>
		string.Join(", ", pages.Select(x => x.TryGetTarget(out var page) ? page.GetType().Name : null).OfType<string>()
			.GroupBy(x => x).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}"));

	private void CountLivePages(Page? shown)
	{
		if (shown is not null)
		{
			SeenPages.Add(new WeakReference<Page>(shown));
		}

		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
		SeenPages.RemoveAll(x => !x.TryGetTarget(out _));
		_logger?.LogInformation("[leak] live pages: {Pages}", LivePages(SeenPages));
	}

	private async void OnNavigated(object sender, NavigationEventArgs e)
	{
		if (sender is Frame frame)
		{
			if (CountsLivePages)
			{
				CountLivePages(frame.Content as Page);
			}

			Arrived(e.Parameter);

			// Nixie 1.3.0: a theme switch's navigation: the same page in the new view, its view model kept as it was (no
			// OnNavigatedTo: nothing is fetched again), the old view's history entry gone, no page transition
			if (_pendingThemeReload is { } reload && e.SourcePageType == reload && e.NavigationMode == NavigationMode.New)
			{
				_pendingThemeReload = null;
				ViewModelHandoff.Clear();
				LastNavigationWasThemeReload = true;
				ArrivalFocus.Arrived(frame.Content as Page, ArrivalKind.ThemeSwitched, frame.XamlRoot); // Nixie 1.4.0 (bug hunt M1): the arrival focus's one rule
				if (frame.BackStack.Count > 0)
				{
					frame.BackStack.RemoveAt(frame.BackStack.Count - 1);
				}

				Navigated?.Invoke(sender, e);
				return;
			}

			LastNavigationWasThemeReload = false;

			// Nixie 1.3.1: a refresh's navigation: the same page again, in place of the old one in the history, no transition;
			// its new view model loads as on any arrival (OnNavigatedTo, below)
			var refresh = _pendingRefresh is { } refreshed && e.SourcePageType == refreshed && e.NavigationMode == NavigationMode.New;
			LastNavigationWasRefresh = refresh;
			// Nixie 1.4.0 (bug hunt M1, m4): when and how the page came, for its arrival focus (it yields to a user who moved since)
			ArrivalFocus.Arrived(frame.Content as Page, refresh ? ArrivalKind.Refreshed
				: e.NavigationMode is NavigationMode.Back or NavigationMode.Forward ? ArrivalKind.Returned : ArrivalKind.Opened, frame.XamlRoot);
			if (refresh)
			{
				_pendingRefresh = null;
				if (frame.BackStack.Count > 0)
				{
					frame.BackStack.RemoveAt(frame.BackStack.Count - 1);
				}
			}
			else
			{
				// Seaglass 1.1.4: the motion follows the direction (a tab, in, Back out)
				PageTransition.Play(e.Content, e.NavigationMode);
			}
			var clearNavigation = (bool)frame.Tag;
			if (clearNavigation)
			{
				frame.BackStack.Clear();
				frame.ForwardStack.Clear();
			}

			// Nixie 1.3.0: the rules below name the shared pages; a layout theme's page counts as the shared page it stands for
			Type Shared(Type page) => _pageService.SharedPageType(page);

			// Seaglass: Forward never leads into the player (it would start playing again)
			PruneForwardStack(frame.ForwardStack, x => Shared(x.SourcePageType));

			// fluentfin-mpv: a player page replacing a player page (a remote Play while playing) takes over its history entry
			DropReplacedPlayerPage(frame.BackStack, x => Shared(x.SourcePageType), Shared(e.SourcePageType), e.NavigationMode);

			// 1.2.0: a season pill switches seasons in place in the history: Back goes to the page before the seasons
			DropSwitchedSeasonPage(frame.BackStack, x => Shared(x.SourcePageType), x => x.Parameter, Shared(e.SourcePageType), e.Parameter, e.NavigationMode);

			// Nixie 1.7.4: what Back and Forward no longer reach goes (a refresh's old entry, a season switched in place, a cleared history)
			RetainHistory(frame);

			// Nixie 1.7.4: Back or Forward showed a kept view model: the page as it was, at once (no OnNavigatedTo: nothing is asked
			// again), refreshed behind when something may have changed since
			var returning = _returning;
			_returning = null;
			var viewModel = frame.GetPageViewModel();
			if (returning is { } kept && ReferenceEquals(viewModel, kept.ViewModel) && viewModel is IKeptOnBack keptOnBack
				&& e.NavigationMode is NavigationMode.Back or NavigationMode.Forward)
			{
				if (Equals(e.Parameter, kept.Parameter))
				{
					var name = e.SourcePageType.Name;
					Replay(frame.Content as Page, kept.Mark);
					_placeFrom = (frame.Content is Page shown ? new WeakReference<Page>(shown) : null, kept.Started); // Nixie 1.7.5: the place's own line (PlacePutBack)
					_placeSteps.Clear();
					if (frame.Content is Page whole && _keptWholePage.TryGetValue(whole.GetType(), out var left) && left.TryGetTarget(out var leftPage) && ReferenceEquals(leftPage, whole))
					{
						PlaceStep(whole, "page kept whole"); // (the frame gave the page left, not a new one)
					}
					if (frame.Content is Page arriving && !arriving.IsLoaded)
					{
						// the page in the window (its first layout): the place's line says when
						void OnArrivingLoaded(object s, Microsoft.UI.Xaml.RoutedEventArgs a)
						{
							arriving.Loaded -= OnArrivingLoaded;
							PlaceStep(arriving, "page loaded");
						}

						arriving.Loaded += OnArrivingLoaded;
					}
					var refreshFrom = Mark(); // Nixie 1.7.5 gate fix: what is told while the refresh behind is on its way (ReplayNewer)
					var returned = keptOnBack.Returned(e.Parameter, kept.Stale);
					_logger?.LogInformation("[perf] back: {Page} shown as it was in {Ms} ms{Behind}", name,
						(long)System.Diagnostics.Stopwatch.GetElapsedTime(kept.Started).TotalMilliseconds, kept.Stale ? ", refreshed behind" : "");
					Navigated?.Invoke(sender, e);
					try
					{
						await returned;
					}
					catch (Exception ex)
					{
						// a refresh behind that fails leaves the page as it was
						_logger?.LogWarning(ex, "[back] {Page}: the refresh behind failed", name);
					}

					if (kept.Stale)
					{
						ReplayNewer(frame.Content as Page, refreshFrom);
					}

					return;
				}

				// (never expected: the entry Back reached was not the one the view model was kept for) it loads its own page again
				_logger?.LogWarning("[back] {Page}: a kept view model reached another entry; loaded again", e.SourcePageType.Name);
			}

			if (viewModel is INavigationAware navigationAware)
			{
				await navigationAware.OnNavigatedTo(e.Parameter);
			}

			Navigated?.Invoke(sender, e);
		}
	}

	/// <summary>
	/// Seaglass: the player page is not a forward target (Back out of the player, then Forward, would start the item again).
	/// Removes every player entry from the forward history; true when one was removed.
	/// </summary>
	internal static bool PruneForwardStack<T>(IList<T> forwardStack, Func<T, Type> pageType)
	{
		var removed = false;
		for (var i = forwardStack.Count - 1; i >= 0; i--)
		{
			if (pageType(forwardStack[i]) == typeof(Views.VideoPlayerPage))
			{
				forwardStack.RemoveAt(i);
				removed = true;
			}
		}

		return removed;
	}

	/// <summary>
	/// 1.2.0 (1.1.3 UX audit, interaction item 10): a season page opened from a season page (its season pills) takes
	/// over that page's history entry, so looking at seasons 2, 3 and 4 does not put three pages before the series in Back's
	/// way (Microsoft's navigation history: moving between peers in place is not added to the history). Only a season of the
	/// same series (items review, item 7): a season of another show opened from a season page (a search) is a new place.
	/// </summary>
	internal static bool DropSwitchedSeasonPage<T>(IList<T> backStack, Func<T, Type> pageType, Func<T, object?> parameter, Type incoming, object? incomingParameter, NavigationMode mode)
	{
		if (mode != NavigationMode.New || incoming != typeof(Views.SeasonPage) || backStack.Count == 0 || pageType(backStack[^1]) != incoming)
		{
			return false;
		}

		if (parameter(backStack[^1]) is not Jellyfin.Sdk.Generated.Models.BaseItemDto { SeriesId: { } left }
			|| incomingParameter is not Jellyfin.Sdk.Generated.Models.BaseItemDto { SeriesId: { } right } || left != right)
		{
			return false;
		}

		backStack.RemoveAt(backStack.Count - 1);
		return true;
	}

	/// <summary>
	/// fluentfin-mpv: when a player page was replaced by a new one, the old one leaves the back stack, so Back from the new
	/// page goes to where the user was before the first player page instead of playing the old item again.
	/// </summary>
	internal static bool DropReplacedPlayerPage<T>(IList<T> backStack, Func<T, Type> pageType, Type incoming, NavigationMode mode)
	{
		if (mode != NavigationMode.New || incoming != typeof(Views.VideoPlayerPage) || backStack.Count == 0 || pageType(backStack[^1]) != incoming)
		{
			return false;
		}

		backStack.RemoveAt(backStack.Count - 1);
		return true;
	}
}

/// <summary>Nixie 1.3.0: what a theme switch did with the page shown (<see cref="NavigationService.ReloadForTheme"/>).</summary>
public enum ThemeReload
{
	/// <summary>No page, or one the page service does not know: nothing to show again.</summary>
	None,

	/// <summary>The page looks the same in the new theme (a shared page): it stays.</summary>
	SamePage,

	/// <summary>The player: it stays; the pages behind it switch when it closes.</summary>
	Deferred,

	/// <summary>The page was shown again in the new theme's view, with the same view model.</summary>
	Reloaded,
}
