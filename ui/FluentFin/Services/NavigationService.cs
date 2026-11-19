using System.Diagnostics.CodeAnalysis;
using FluentFin.Contracts.Services;
using FluentFin.Contracts.ViewModels;
using FluentFin.Helpers;
using FluentFin.UI.Core.Contracts.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace FluentFin.Services;

// For more information on navigation between pages see
// https://github.com/microsoft/TemplateStudio/blob/main/docs/WinUI/navigation.md
public class NavigationService : INavigationService
{
	private readonly IPageService _pageService;
	private object? _lastParameterUsed;
	private Frame? _frame;

	public event NavigatedEventHandler? Navigated;

	public Frame? Frame
	{
		get
		{
			if (_frame == null)
			{
				_frame = App.MainWindow.Content as Frame;
				RegisterFrameEvents();
			}

			return _frame;
		}

		set
		{
			UnregisterFrameEvents();
			_frame = value;
			RegisterFrameEvents();
		}
	}

	[MemberNotNullWhen(true, nameof(Frame), nameof(_frame))]
	public bool CanGoBack => Frame != null && Frame.CanGoBack;

	public NavigationService(IPageService pageService)
	{
		_pageService = pageService;
	}

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
			_frame.GoBack();
			if (vmBeforeNavigation is INavigationAware navigationAware)
			{
				navigationAware.OnNavigatedFrom();
			}

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
			_frame.GoForward();
			if (vmBeforeNavigation is INavigationAware navigationAware)
			{
				navigationAware.OnNavigatedFrom();
			}

			return true;
		}

		return false;
	}

	public bool NavigateTo(string pageKey, object? parameter = null, bool clearNavigation = false)
	{
		try
		{
			var pageType = _pageService.GetPageType(pageKey);

			if (_frame != null && (_frame.Content?.GetType() != pageType || (parameter != null && !parameter.Equals(_lastParameterUsed))))
			{
				_frame.Tag = clearNavigation;
				var vmBeforeNavigation = _frame.GetPageViewModel();
				var navigated = _frame.Navigate(pageType, parameter);
				if (navigated)
				{
					_lastParameterUsed = parameter;
					if (vmBeforeNavigation is INavigationAware navigationAware)
					{
						navigationAware.OnNavigatedFrom();
					}
				}

				return navigated;
			}

			return false;
		}
		catch
		{
			return false;
		}

	}

	/// <summary>Nixie 1.3.0: the parameter of the page the frame shows (the theme switch shows it again with it).</summary>
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
	/// Nixie 1.3.1 (R, F5, Ctrl+R; the user: "make r refresh the jellyfin page"): the page shown, loaded again: a new view of the
	/// same page with the same parameter and a new view model, which asks the server again (its OnNavigatedTo), in place of
	/// the old one in the history (Back and Forward as they were), no transition. Only a page whose view model
	/// <paramref name="refreshes"/> says has server data; null (nothing done) for any other, and when nothing is shown.
	/// </summary>
	public Page? ReloadForRefresh(Func<Type, bool> refreshes)
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
		_pendingRefresh = page.GetType();
		frame.Tag = false;
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

		_lastParameterUsed = _currentParameter;
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

	private async void OnNavigated(object sender, NavigationEventArgs e)
	{
		if (sender is Frame frame)
		{
			_currentParameter = e.Parameter;

			// Nixie 1.3.0: a theme switch's navigation: the same page in the new view, its view model kept as it was (no
			// OnNavigatedTo: nothing is fetched again), the old view's history entry gone, no page transition
			if (_pendingThemeReload is { } reload && e.SourcePageType == reload && e.NavigationMode == NavigationMode.New)
			{
				_pendingThemeReload = null;
				ViewModelHandoff.Clear();
				LastNavigationWasThemeReload = true;
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

			if (frame.GetPageViewModel() is INavigationAware navigationAware)
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
