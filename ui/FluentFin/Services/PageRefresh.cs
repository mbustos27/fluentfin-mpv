using FluentFin.Contracts.Services;
using FluentFin.Core.Journal;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using FluentFin.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Services;

/// <summary>
/// Nixie 1.3.1 (R, F5, Ctrl+R; the user: "make r refresh the jellyfin page"): the page shown loaded again from the server, in
/// place: the same page and parameter with a new view model (<see cref="NavigationService.ReloadForRefresh"/>; the caches
/// that would answer instead are marked stale first), the place kept (<see cref="ThemeAnchors"/>: the item the keyboard was
/// on, each row's first visible item), the page's own loading state meanwhile, and Narrator says "Refreshed". Only the pages
/// with server data (<see cref="Refreshable"/>); nothing on the others (Settings).
/// </summary>
public static class PageRefresh
{
	/// <summary>What Narrator says.</summary>
	public const string Announcement = "Refreshed";

	/// <summary>How long the place is looked for while the rows come back.</summary>
	public static readonly TimeSpan RestoreWithin = TimeSpan.FromSeconds(6);

	/// <summary>The pages a refresh loads again: Home, a library, a See all grid, an item, series or season page, search results, the Diary.</summary>
	public static IReadOnlyList<Type> Refreshable { get; } =
	[
		typeof(HomeViewModel),
		typeof(LibraryViewModel),
		typeof(CollectionViewModel), // Nixie 1.4.0: a collection or a playlist, and the Libraries page (its counts)
		typeof(LibrariesLandingPageViewModel),
		typeof(ItemGridViewModel),
		typeof(MovieViewModel),
		typeof(EpisodeViewModel),
		typeof(SeriesViewModel),
		typeof(SeasonViewModel),
		typeof(SearchResultsViewModel),
		typeof(DiaryViewModel),
	];

	public static bool Refreshes(Type? viewModel) => viewModel is not null && Refreshable.Contains(viewModel);

	/// <summary>The refresh whose place is still being put back (null when none is).</summary>
	private static RefreshRestore? _pending;

	/// <summary>Why the user's own move since the refresh wins over its place (null: nothing yet).</summary>
	private static string? _userMoved;

	/// <summary>
	/// Nixie 1.4.0 (bug hunt M4: R, R 150 ms apart loaded the page twice and the second took its place from the page still
	/// loading, the keyboard ending on the TitleBar): whether a refresh is still under way on the page shown (<paramref name="samePage"/>):
	/// its place not yet back (<paramref name="running"/>), and the user has not moved since. Another R then joins it (<see cref="Run"/>).
	/// <para>
	/// Nixie 1.4.0 (bug hunt m1: a place that could not be found kept the restore running, and every R for 6 s joined it and
	/// did nothing): only while the refresh is still coming in (<paramref name="running"/> is <see cref="RefreshRestore.Settling"/>:
	/// its page loading, or loaded half a second ago at most). Once it is in, R loads it again.
	/// </para>
	/// </summary>
	public static bool Coalesces(bool running, bool samePage, string? userMoved) => running && samePage && userMoved is null;

	/// <summary>
	/// Nixie 1.4.0 (integration: the hunt's M3 against the library's M2): whether the user moved (a key, a click, the wheel)
	/// since the last refresh began. The libraries put their place back on R too, and their focus must yield the same way.
	/// </summary>
	public static bool UserMovedSinceRefresh => _userMoved is not null;

	/// <summary>
	/// Nixie 1.4.0 (bug hunt M3): the user pressed a key (not a refresh key, not a modifier alone), clicked or turned the
	/// wheel: a refresh's place restore stops at its next try and leaves the keyboard where the user put it.
	/// </summary>
	public static void UserActed(string how)
	{
		if (_pending is { IsRunning: true })
		{
			_userMoved ??= how;
		}
	}

	/// <summary>
	/// Why a running restore gives up now (null: it goes on): the user moved (<see cref="UserActed"/>), the keyboard is in a
	/// text box (never pulled out of one), or the page is no longer the one shown.
	/// </summary>
	public static string? YieldReason(string? userMoved, bool focusInTextInput, bool pageShown) =>
		userMoved is not null ? $"the user moved first ({userMoved})"
		: focusInTextInput ? "the keyboard is in a text box"
		: !pageShown ? "the page was left"
		: null;

	/// <summary>Refreshes the page shown; false (nothing done) when it is not one of <see cref="Refreshable"/>.</summary>
	public static bool Run()
	{
		if (App.GetService<INavigationService>() is not NavigationService { Frame.Content: Page shown } navigation)
		{
			return false;
		}

		if (Coalesces(_pending?.Settling == true, ReferenceEquals(_pending?.Page, shown), _userMoved))
		{
			App.GetService<ILogger<NavigationService>>().LogInformation("Refresh: one is already loading this page; this key joins it");
			return true;
		}

		var anchors = ThemeAnchors.Capture(shown);
		var focused = shown.XamlRoot is { } root ? Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) as FrameworkElement : null;
		var where = focused is null ? "nothing" : $"{focused.GetType().Name} (its item: {focused.DataContext?.GetType().Name ?? "none"})";
		var cache = App.GetService<HomeCache>();
		var page = navigation.ReloadForRefresh(viewModel =>
		{
			if (!Refreshes(viewModel))
			{
				return false;
			}

			// Home's page and a series' episodes are kept between visits: this visit asks the server
			cache.MarkStale();
			return true;
		});
		if (page is not { } fresh)
		{
			return false;
		}

		var log = App.GetService<ILogger<NavigationService>>();
		log.LogInformation("Refresh: {Page} loaded again from the server (the keyboard was on {Focused})", fresh.GetType().Name, where);
		_pending?.Stop("a new refresh");
		_userMoved = null;
		_pending = anchors.RestoreAfterRefresh(fresh, RestoreWithin, text => log.LogInformation("Refresh: {Place}", text), () => YieldReason(
			_userMoved,
			fresh.XamlRoot is { } root && TextInput.IsTextInput(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root)),
			ReferenceEquals(navigation.Frame?.Content, fresh)));
		Announce(navigation.Frame);
		return true;
	}

	private static void Announce(FrameworkElement? element)
	{
		if (element is null)
		{
			return;
		}

		var peer = FrameworkElementAutomationPeer.FromElement(element) ?? FrameworkElementAutomationPeer.CreatePeerForElement(element);
		peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent, Announcement, "NixiePageRefreshed");
	}
}
