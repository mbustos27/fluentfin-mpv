using FluentFin.Controls;
using FluentFin.Core;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace FluentFin.Views;

/// <summary>
/// 1.2.0 (1.1.3 UX audit, interaction item 5): an item page keeps its place. Leaving it saves how far down it was and
/// what had the focus (<see cref="PagePlaces"/>); coming back with Back or Forward scrolls there and focuses that again (a card
/// or an episode row by its item, a button by its place in the page) once the page's content has arrived. The movie, series,
/// season and episode pages each make one: their OnNavigatedTo calls <see cref="Arrived"/>, their OnNavigatingFrom
/// <see cref="Left"/> (OnNavigatedFrom is too late: the page is unloaded by then and the focus has gone to the title bar).
/// The series page also gives its shown season (<see cref="PagePlace.SelectedSeasonId"/>), which Back shows again.
/// </summary>
internal sealed class ItemPagePlace
{
	private readonly Page _page;
	private readonly ScrollViewer _scroller;
	private readonly Func<bool> _loaded;
	private readonly Func<Control?>? _primary;

	// the last element of the page that had the focus, and how it got it: when the page is left from a menu or a flyout the
	// focus is in its popup, not in the page (a row's "Episode details")
	private WeakReference<DependencyObject>? _lastFocus;
	private FocusState _lastFocusState;

	/// <param name="primary">Nixie 1.4.0 (the UX review's item 4): the page's one Primary (its Play), which takes the focus when
	/// the page is opened anew (not on Back or Forward, which give the focus back to where it was); null for a page whose
	/// arrival leaves the focus where it is.</param>
	public ItemPagePlace(Page page, ScrollViewer scroller, Func<bool> loaded, Func<Control?>? primary = null)
	{
		_page = page;
		_scroller = scroller;
		_loaded = loaded;
		_primary = primary;
		page.GotFocus += (_, e) =>
		{
			if (e.OriginalSource is UIElement element && IsInPage(element))
			{
				_lastFocus = new WeakReference<DependencyObject>(element);
				_lastFocusState = element.FocusState;
			}
		};
	}

	internal enum FocusStep
	{
		/// <summary>Put the focus back on what had it.</summary>
		Restore,

		/// <summary>The user is somewhere already: give up, leave the focus where it is.</summary>
		Leave,
	}

	/// <summary>
	/// 1.2.0: whether Back still gives the focus back, a moment after the page came: not once the user has put it in the page,
	/// and not once anything opened over it (a dialog such as F1's shortcut sheet, a menu, a flyout), which has the focus and
	/// would lose it (a harness run: Esc no longer reached the sheet).
	/// </summary>
	internal static FocusStep FocusRestore(bool focusInPage, int openPopups) =>
		focusInPage || openPopups > 0 ? FocusStep.Leave : FocusStep.Restore;

	/// <summary>
	/// 1.2.0 (items review, item 5): how the focus comes back: as keyboard focus (its ring, a card's buttons and lift) only
	/// when the keyboard put it there; focus a click gave comes back as the pointer's, which shows none of that, and Tab still
	/// goes on from it.
	/// </summary>
	internal static FocusState RestoreAs(bool fromKeyboard) => fromKeyboard ? FocusState.Keyboard : FocusState.Pointer;

	internal enum ArrivalStep
	{
		/// <summary>The page or its Primary is not ready yet: look again.</summary>
		Wait,

		/// <summary>Put the focus on the Primary.</summary>
		Focus,

		/// <summary>The user is somewhere in the page already, or something is open over it: leave the focus where it is.</summary>
		Leave,
	}

	/// <summary>
	/// Nixie 1.4.0 (the UX review's item 4): a Movie or Episode page opened anew puts the focus on its Primary once it is drawn,
	/// not on the top bar's Back, where the navigation leaves it; never once the user has put the focus in the page, or while
	/// a menu, flyout or dialog is open over it (it has the focus, and Esc must still reach it).
	/// </summary>
	internal static ArrivalStep ArrivalFocus(bool loaded, bool primaryReady, bool focusInPage, int openPopups) =>
		focusInPage || openPopups > 0 ? ArrivalStep.Leave
		: loaded && primaryReady ? ArrivalStep.Focus
		: ArrivalStep.Wait;

	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(6);

	/// <summary>A page that turns out shorter than it was gets its content this long to grow before it is scrolled to its end.</summary>
	private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(400);

	private string _key = "";
	private DispatcherTimer? _timer;

	/// <returns>The place the page is coming back to (Back or Forward), else null.</returns>
	public PagePlace? Arrived(NavigationEventArgs e)
	{
		_key = PagePlaces.Key(_page.GetType().Name, (e.Parameter as BaseItemDto)?.Id);
		_lastFocus = null;
		if (PagePlaces.ForReturn(_key, e.NavigationMode is NavigationMode.Back or NavigationMode.Forward) is { } place)
		{
			Restore(place);
			return place;
		}

		if (_primary is not null)
		{
			FocusPrimaryWhenDrawn();
		}

		return null;
	}

	/// <summary>
	/// How long after the Primary took the focus it is given back if it left the page by itself: the navigation's own focus
	/// handling, or the page drawing its actions again once its artwork came (Drive-in's board, a documentary's page: Play was
	/// unloaded and the focus went to the top bar's Back).
	/// </summary>
	private static readonly TimeSpan Settled = TimeSpan.FromSeconds(10);

	/// <summary>
	/// Nixie 1.4.0: whether the Primary takes the focus again a moment after it took it: the navigation's own focus handling
	/// moved it out of the page (to the top bar's Back, the first stop of the window, when the page before was unloaded after
	/// the new one drew; a page that loaded quickly lost its Play that way), nothing is open over the page, and the user has
	/// not pressed a key or clicked since.
	/// </summary>
	internal static bool FocusAgain(bool focusInPage, int openPopups, bool userActedSince, int times) =>
		!focusInPage && openPopups == 0 && !userActedSince && times < 3;

	/// <summary>
	/// How long an arrival waits for the page to load (a slow server) before it leaves the focus where it is; the user pressing
	/// a key or clicking meanwhile ends the wait at once.
	/// </summary>
	private static readonly TimeSpan ArrivalPatience = TimeSpan.FromSeconds(15);

	private static string Describe(DependencyObject? element) =>
		element is FrameworkElement { Name.Length: > 0 } named ? $"{element.GetType().Name} {named.Name}" : element?.GetType().Name ?? "nothing";

	private static void Log(string text) =>
		Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(App.GetService<Microsoft.Extensions.Logging.ILogger<ItemPagePlace>>(), "{Text}", text);

	private void FocusPrimaryWhenDrawn()
	{
		var deadline = DateTime.UtcNow + ArrivalPatience;
		DateTime? placedAt = null;
		long placedTick = 0;
		var again = 0;
		_timer?.Stop();
		_timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
		_timer.Tick += (_, _) =>
		{
			if (_page.XamlRoot is not { } root)
			{
				if (DateTime.UtcNow >= deadline)
				{
					_timer?.Stop();
				}

				return;
			}

			var primary = _primary?.Invoke();
			var ready = primary is { IsLoaded: true, IsEnabled: true, Visibility: Visibility.Visible } && primary.ActualWidth > 0;
			var current = FocusManager.GetFocusedElement(root) as DependencyObject;
			var inPage = current is not null && IsInPage(current) && !ReferenceEquals(current, _scroller);
			// Nixie 1.4.0: menus, flyouts and dialogs; not a tooltip (a poster's "Play ..." tooltip still up as a keyboard user's
			// Enter opened the film kept Play from taking the keyboard: seen in Editorial)
			var popups = Services.ArrivalFocus.OpenPopups(root);
			if (placedAt is { } at)
			{
				// placed: watch a while for the focus leaving the page by itself (Play, drawn again, is not ready for a moment);
				// the user's first key or click ends the watch
				if (DateTime.UtcNow - at > Settled || Services.ArrivalFocus.UserActedAt > placedTick)
				{
					_timer?.Stop();
				}
				// (Nixie 1.4.0, bug hunt M1: never out of a text box, however the keyboard got there)
				else if (ready && FocusAgain(inPage || Services.TextInput.IsTextInput(current), popups, Services.ArrivalFocus.UserActedAt > placedTick, again))
				{
					again++;
					Log($"[focus] {_page.GetType().Name}: Play again (the focus had gone to {Describe(current)})");
					primary!.Focus(FocusState.Programmatic);
				}

				return;
			}

			// the user moved on (a key, a click, the wheel) while the page loaded, the keyboard is in a text box, or R or a theme
			// switch brought the page (their restore puts the keyboard back): leave the focus where it is (Services.ArrivalFocus,
			// every page's one arrival rule)
			var why = Services.ArrivalFocus.WhyNot(_page);
			switch (ArrivalFocus(_loaded(), ready, inPage || why is not null, popups))
			{
				case ArrivalStep.Focus:
					// Programmatic: the ring shows when the keyboard brought the user here, not after a click
					Log($"[focus] {_page.GetType().Name}: Play takes the keyboard on arrival (it was on {Describe(current)})");
					primary!.Focus(FocusState.Programmatic);
					placedAt = DateTime.UtcNow;
					placedTick = Environment.TickCount64;
					return;
				case ArrivalStep.Leave:
					Log($"[focus] {_page.GetType().Name}: arrival leaves the keyboard on {Describe(current)} (in the page {inPage}, menus {popups}, {why ?? "nothing else"})");
					_timer?.Stop();
					return;
			}

			if (DateTime.UtcNow >= deadline)
			{
				Log($"[focus] {_page.GetType().Name}: arrival gave up (loaded {_loaded()}, Play ready {ready})");
				_timer?.Stop();
			}
		};
		_timer.Start();
	}

	/// <param name="selectedSeasonId">The series page's shown season, to show it again on Back.</param>
	public void Left(Guid? selectedSeasonId = null)
	{
		_timer?.Stop();
		if (_key.Length == 0)
		{
			return;
		}

		Guid? item = null;
		IReadOnlyList<int>? path = null;
		string? name = null;
		var fromKeyboard = false;
		DependencyObject? focused = _page.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject now && IsInPage(now) ? now : null;
		var state = focused is UIElement element ? element.FocusState : FocusState.Unfocused;
		if (focused is null && _lastFocus?.TryGetTarget(out var last) == true && IsInPage(last))
		{
			focused = last;
			state = _lastFocusState;
		}

		if (focused is not null)
		{
			item = ItemOf(focused);
			path = item is null ? PathOf(focused) : null;
			name = item is null ? NameOf(focused) : null;
			fromKeyboard = state == FocusState.Keyboard;
		}

		PagePlaces.Save(_key, new PagePlace(_scroller.VerticalOffset, item, path, fromKeyboard, selectedSeasonId, name));
	}

	private void Restore(PagePlace place)
	{
		var deadline = DateTime.UtcNow + Patience;
		DateTime? loadedAt = null;
		var scrolled = false;
		var focused = place.FocusedItemId is null && place.FocusedPath is null && place.FocusedName is null;
		_timer?.Stop();
		_timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
		_timer.Tick += (_, _) =>
		{
			var now = DateTime.UtcNow;
			if (_loaded())
			{
				loadedAt ??= now;
			}

			if (!scrolled)
			{
				var settled = loadedAt is { } at && now - at >= Settle;
				if (PagePlaces.OffsetToRestore(place.VerticalOffset, _scroller.ScrollableHeight, settled || now >= deadline) is { } offset)
				{
					_scroller.ChangeView(null, offset, null, disableAnimation: true);
					scrolled = true;
				}
			}
			else if (!focused && _page.XamlRoot is { } root)
			{
				var current = FocusManager.GetFocusedElement(root) as DependencyObject;
				switch (FocusRestore(current is not null && IsInPage(current) && !ReferenceEquals(current, _scroller), Services.ArrivalFocus.OpenPopups(root)))
				{
					case FocusStep.Leave:
						focused = true;
						break;
					case FocusStep.Restore when Target(place) is Control control && control.Focus(RestoreAs(place.FocusFromKeyboard)):
						focused = true;
						break;
				}
			}

			if ((scrolled && focused) || now >= deadline)
			{
				_timer?.Stop();
			}
		};
		_timer.Start();
	}

	private DependencyObject? Target(PagePlace place) =>
		place.FocusedItemId is { } id ? Find(_page, id)
		: place.FocusedName is { } name && _page.FindName(name) is Control { IsLoaded: true } named ? named
		: place.FocusedPath is { } path ? ElementAt(path) : null;

	/// <summary>
	/// Nixie 1.4.0: the element's x:Name when the page finds it by that name (the page's own named controls, such as its Play,
	/// "PlayButton"); null for a template's parts or a name the page does not know.
	/// </summary>
	private string? NameOf(DependencyObject element) =>
		element is FrameworkElement { Name.Length: > 0 } named && ReferenceEquals(_page.FindName(named.Name), named) ? named.Name : null;

	private bool IsInPage(DependencyObject element)
	{
		for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (ReferenceEquals(node, _page))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>The item of the card or episode row the element is in (or is).</summary>
	private static Guid? ItemOf(DependencyObject element)
	{
		for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			switch (node)
			{
				case BaseItemDtoCard card:
					return card.Model?.Dto.Id;
				case EpisodeRow row:
					return row.Dto?.Id;
			}
		}

		return null;
	}

	/// <summary>The first card or episode row of the page showing that item.</summary>
	private static Control? Find(DependencyObject root, Guid id)
	{
		var count = VisualTreeHelper.GetChildrenCount(root);
		for (var i = 0; i < count; i++)
		{
			var child = VisualTreeHelper.GetChild(root, i);
			switch (child)
			{
				case BaseItemDtoCard card when card.Model?.Dto.Id == id:
					return card;
				case EpisodeRow row when row.Dto?.Id == id:
					return row;
			}

			if (Find(child, id) is { } found)
			{
				return found;
			}
		}

		return null;
	}

	/// <summary>Where the element is in the page: the child index at each level from the page down.</summary>
	private List<int>? PathOf(DependencyObject element)
	{
		var path = new List<int>();
		for (var node = element; !ReferenceEquals(node, _page);)
		{
			var parent = VisualTreeHelper.GetParent(node);
			if (parent is null)
			{
				return null;
			}

			var index = -1;
			for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
			{
				if (ReferenceEquals(VisualTreeHelper.GetChild(parent, i), node))
				{
					index = i;
					break;
				}
			}

			path.Add(index);
			node = parent;
		}

		path.Reverse();
		return path;
	}

	private DependencyObject? ElementAt(IReadOnlyList<int> path)
	{
		DependencyObject node = _page;
		foreach (var index in path)
		{
			if (index < 0 || index >= VisualTreeHelper.GetChildrenCount(node))
			{
				return null;
			}

			node = VisualTreeHelper.GetChild(node, index);
		}

		return node;
	}
}
