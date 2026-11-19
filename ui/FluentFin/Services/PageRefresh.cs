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

	/// <summary>Refreshes the page shown; false (nothing done) when it is not one of <see cref="Refreshable"/>.</summary>
	public static bool Run()
	{
		if (App.GetService<INavigationService>() is not NavigationService { Frame.Content: Page shown } navigation)
		{
			return false;
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
		anchors.RestoreAfterRefresh(fresh, RestoreWithin, text => log.LogInformation("Refresh: {Place}", text));
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
