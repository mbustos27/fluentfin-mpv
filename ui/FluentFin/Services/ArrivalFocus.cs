using FluentFin.Contracts.Services;
using FluentFin.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FluentFin.Services;

/// <summary>How a page came to be shown, for its arrival focus (<see cref="ArrivalFocus"/>).</summary>
public enum ArrivalKind
{
	/// <summary>Opened anew (a card, a tab, a search result).</summary>
	Opened,

	/// <summary>Back or Forward.</summary>
	Returned,

	/// <summary>R or F5: the same page loaded again; the refresh's own restore puts the keyboard back (<see cref="PageRefresh"/>).</summary>
	Refreshed,

	/// <summary>A theme switch: the same page in the new theme; the switch puts the keyboard back (ThemeAnchors).</summary>
	ThemeSwitched,
}

/// <summary>
/// Nixie 1.4.0 (bug hunt M1, m4, m6; class 9: "arrival focus has four owners and three rules"): the one rule for the focus a
/// page gives itself when it arrives: the Collection page's Play (or the card Back returns to), the Libraries page's first
/// tile, a library's first poster (or the one Back returns to), an item page's Play. The page asks here first, and leaves
/// the keyboard where it is when the user has moved since the navigation (a key, a click, the wheel: <see cref="UserActed"/>),
/// the keyboard is in a text box, a menu, flyout or dialog is open, or the page is no longer the one shown: the same rule as
/// R's place restore (<see cref="PageRefresh.YieldReason"/>), which already yields to the user. A slow Collection page took
/// the keyboard out of the search box the user had typed in, and the next letter (R) refreshed the page.
/// </summary>
public static class ArrivalFocus
{
	/// <summary>A key or click this soon after the navigation is the one that opened the page (or its key repeating), not the user moving on.</summary>
	public const long GraceMs = 400;

	/// <summary>When the user last pressed a key, clicked or turned the wheel (Environment.TickCount64; WindowShortcuts).</summary>
	public static long UserActedAt { get; private set; }

	private static WeakReference<Page>? _page;
	private static long _arrivedAt;
	private static ArrivalKind _kind;

	/// <summary>The user pressed a key (not a refresh key or a modifier alone), clicked or turned the wheel.</summary>
	public static void UserActed() => UserActedAt = Environment.TickCount64;

	/// <summary>The navigation showed <paramref name="page"/> (NavigationService, as each page arrives).</summary>
	public static void Arrived(Page? page, ArrivalKind kind)
	{
		if (page is null)
		{
			return;
		}

		_page = new WeakReference<Page>(page);
		_arrivedAt = Environment.TickCount64;
		_kind = kind;
	}

	/// <summary>How <paramref name="page"/> arrived; null when it is not the page that arrived last.</summary>
	public static ArrivalKind? KindOf(Page page) => _page?.TryGetTarget(out var last) == true && ReferenceEquals(last, page) ? _kind : null;

	/// <summary>
	/// Why an arrival's focus leaves the keyboard where it is now; null: it takes it. The same reasons as R's place restore
	/// (<see cref="PageRefresh.YieldReason"/>: the user moved, a text box, the page left), and a menu or dialog open over it.
	/// </summary>
	/// <param name="userMoved">How the user moved since the page arrived (after <see cref="GraceMs"/>), null when they have not.</param>
	public static string? YieldReason(string? userMoved, bool focusInTextInput, int openPopups, bool pageShown) =>
		PageRefresh.YieldReason(userMoved, focusInTextInput, pageShown)
		?? (openPopups > 0 ? "a menu, flyout or dialog is open" : null);

	/// <summary>
	/// What the user did since the page arrived, for <see cref="YieldReason(string?, bool, int, bool)"/>: after a refresh, what
	/// R's own restore counts (<see cref="PageRefresh.UserMovedSinceRefresh"/>); else a key, a click or the wheel after the grace.
	/// </summary>
	public static string? MovedSince(ArrivalKind kind, long arrivedAt, long userActedAt, bool movedSinceRefresh) =>
		kind == ArrivalKind.Refreshed ? (movedSinceRefresh ? "a key, a click or the wheel since R" : null)
		: userActedAt > arrivedAt + GraceMs ? "a key, a click or the wheel"
		: null;

	/// <summary>
	/// Why <paramref name="page"/>'s arrival focus leaves the keyboard where it is now; null: it takes it.
	/// </summary>
	/// <param name="restore">
	/// False (the arrival's own focus: Play, a first tile): after R or a theme switch the page never takes the keyboard, the
	/// refresh's (or the switch's) restore puts it back where it was. True (a library putting its poster back, which is the
	/// restore after R): after R it yields as R's restore does.
	/// </param>
	public static string? WhyNot(Page page, bool restore = false)
	{
		var kind = KindOf(page);
		if (kind is null)
		{
			return "the page is not the one that arrived last";
		}

		if (!restore && kind is ArrivalKind.Refreshed or ArrivalKind.ThemeSwitched)
		{
			return kind == ArrivalKind.Refreshed ? "R puts the keyboard back where it was" : "the theme switch puts the keyboard back where it was";
		}

		var root = page.XamlRoot;
		var focused = root is null ? null : FocusManager.GetFocusedElement(root);
		return YieldReason(
			MovedSince(kind.Value, _arrivedAt, UserActedAt, PageRefresh.UserMovedSinceRefresh),
			TextInput.IsTextInput(focused),
			OpenPopups(root),
			Shown(page));
	}

	/// <summary>
	/// The arrival's focus: <paramref name="focus"/> runs unless <see cref="WhyNot"/> names a reason, which is logged instead.
	/// True when it ran and put the keyboard there.
	/// </summary>
	/// <param name="what">What takes the keyboard, for the log ("Play", "the first tile").</param>
	public static bool TryTake(Page page, string what, Func<bool> focus, bool restore = false)
	{
		var name = page.GetType().Name;
		if (WhyNot(page, restore) is { } why)
		{
			Log($"[focus] {name}: arrival leaves the keyboard on {Describe(Focused(page))} ({why})");
			return false;
		}

		var was = Describe(Focused(page));
		var done = focus();
		Log($"[focus] {name}: {what} {(done ? "takes" : "could not take")} the keyboard on arrival (it was on {was})");
		return done;
	}

	/// <summary>
	/// The rule for a library's load focus (its first poster on arrival, the poster Back or R puts back), for every theme's
	/// library page (<see cref="Core.ViewModels.LibraryViewModel.LoadFocusYields"/>): true when the keyboard stays where it is.
	/// After R it yields as R's own restore does (the poster put back is that restore).
	/// </summary>
	public static bool LibraryYields(Core.ViewModels.LibraryViewModel library)
	{
		if (App.GetService<INavigationService>() is not NavigationService { Frame: { Content: Page page } frame } || !ReferenceEquals(frame.GetPageViewModel(), library))
		{
			return true; // the library's page is not the one shown
		}

		if (WhyNot(page, restore: true) is not { } why)
		{
			return false;
		}

		Log($"[focus] {page.GetType().Name}: the poster leaves the keyboard on {Describe(Focused(page))} ({why})");
		return true;
	}

	/// <summary>Whether <paramref name="page"/> is the page the shell shows.</summary>
	public static bool Shown(Page page) =>
		App.GetService<INavigationService>() is NavigationService { Frame.Content: { } shown } && ReferenceEquals(shown, page);

	/// <summary>Menus, flyouts and dialogs open over the window (tooltips do not count).</summary>
	public static int OpenPopups(XamlRoot? root) =>
		root is null ? 0 : VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Count(p => p.IsOpen && p.Child is not ToolTip);

	private static object? Focused(Page page) => page.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null;

	private static string Describe(object? element) =>
		element is FrameworkElement { Name.Length: > 0 } named ? $"{element.GetType().Name} {named.Name}" : element?.GetType().Name ?? "nothing";

	private static void Log(string text) => App.GetService<ILogger<NavigationService>>().LogInformation("{Text}", text);
}
