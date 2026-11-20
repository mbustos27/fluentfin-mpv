using FluentFin.Contracts.Services;
using FluentFin.Core;
using FluentFin.Core.Services;
using FluentFin.Core.Shortcuts;
using FluentFin.Core.ViewModels;
using FluentFin.Dialogs.Views;
using FluentFin.ViewModels;
using FluentFin.Views;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace FluentFin.Services;

/// <summary>
/// Seaglass: the window's own key and mouse handling (docs/SHORTCUTS.md): the global keys and Mouse 4 / Mouse 5 (back and
/// forward), at the window level so they work on every page and in the player. The decisions are pure and in
/// <see cref="AppShortcuts"/> / <see cref="AppMouse"/>; this only reads the key state, applies the text-input and open-popup
/// rules, and carries the action out. Pointer presses are only observed (never handled), so cards and controls still get them.
/// </summary>
public static class WindowShortcuts
{
	private static bool _helpOpen;

	public static void Attach(UIElement root)
	{
		root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), false);
		root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
		// Nixie 1.4.0 (bug hunt M3): the wheel is the user moving too (a refresh's place restore stops); and (bug hunt m6) a
		// page's arrival focus leaves the keyboard alone (a user reading down a slow film page with the wheel kept their place)
		root.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, _) =>
		{
			PageRefresh.UserActed("the wheel");
			ArrivalFocus.UserActed();
		}), true);
	}

	// -- keys ------------------------------------------------------------------------------------------------------------

	private static void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Handled || sender is not UIElement root)
		{
			return;
		}

		var state = InputKeyboardSource.GetKeyStateForCurrentThread;
		bool Down(VirtualKey k) => state(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
		var shortcut = AppShortcuts.For((int)e.Key, Down(VirtualKey.Control), Down(VirtualKey.Shift), Down(VirtualKey.Menu),
			TextInput.IsTextInput(e.OriginalSource), ownsLetters: TextInput.OwnsLetters(e.OriginalSource)
				// Nixie 1.4.0: R in a grid while a title is being typed is its letter; on the A to Z a letter presses that letter
				|| Controls.GridTypeAhead.Continues(e.OriginalSource) || Controls.LetterBar.OwnsLetters(e.OriginalSource));

		// Nixie 1.4.0 (bug hunt M3): any other key the user presses ends a refresh's place restore (a refresh key joins the
		// refresh under way, a modifier alone is the start of a chord)
		if (shortcut != AppShortcut.Refresh && !AppShortcuts.IsModifierKey((int)e.Key))
		{
			PageRefresh.UserActed("a key");
			ArrivalFocus.UserActed(); // 1.4.0 (bug hunt M1): a page's arrival focus yields too
		}

		// Nixie 1.4.0 (bug hunt M1): the theatre is modal. Its keys are decided here, before any page handler or key
		// accelerator (a key handled here runs no accelerator: the search box's Ctrl+F, Home's N and C): Esc and Back close
		// it from wherever the keyboard is, its own controls get Tab, Enter, Space and the arrows, and the rest is held back.
		if (Controls.TrailerTheater.Current is { IsOpen: true } theater)
		{
			switch (Trailers.KeyOverTheater((int)e.Key, Down(VirtualKey.Control), Down(VirtualKey.Shift), Down(VirtualKey.Menu)))
			{
				case TheaterKey.Close:
					e.Handled = true;
					theater.Close();
					return;
				case TheaterKey.HoldBack:
					e.Handled = true;
					theater.HeldBack(e.KeyStatus.WasKeyDown);
					return;
			}

			return;
		}

		// Nixie 1.4.0 (bug hunt M4): a held key's repeats are handled and do nothing (Run); they never reach the page either
		if (shortcut is { } found && Run(root, found, e.KeyStatus.WasKeyDown))
		{
			e.Handled = true;
		}
	}

	// -- the mouse -------------------------------------------------------------------------------------------------------

	private static void OnPointerPressed(object sender, PointerRoutedEventArgs e)
	{
		if (sender is not UIElement root || e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse)
		{
			return;
		}

		PageRefresh.UserActed("a click");
		ArrivalFocus.UserActed();
		var kind = e.GetCurrentPoint(root).Properties.PointerUpdateKind;
		if (AppMouse.For(kind == PointerUpdateKind.XButton1Pressed, kind == PointerUpdateKind.XButton2Pressed) is { } shortcut)
		{
			// not handled: the press goes on to whatever is under the pointer
			_ = Run(root, shortcut);
		}
	}

	// -- doing it --------------------------------------------------------------------------------------------------------

	/// <summary>Carries the shortcut out where it applies; true when it did something (the key is then handled).</summary>
	private static bool Run(UIElement root, AppShortcut shortcut, bool repeat = false)
	{
		try
		{
			var theater = Controls.TrailerTheater.Current;
			switch (Gate(shortcut, CurrentContext(), HasOpenPopup(root.XamlRoot), theater?.IsOpen == true))
			{
				case ShortcutGate.Ignore:
					return false;
				case ShortcutGate.HoldBack:
					return true;
				case ShortcutGate.CloseTrailer:
					theater!.Close();
					return true;
			}

			// Nixie 1.4.0 (bug hunt M4: a held R reloaded the page 16 times in a second): a held key acts once
			if (repeat && !AppShortcuts.Repeats(shortcut))
			{
				return true;
			}

			switch (shortcut)
			{
				case AppShortcut.Back:
					return BackForwardService().GoBack();
				case AppShortcut.Forward:
					return BackForwardService().GoForward();
				case AppShortcut.FocusSearch:
					return TitleBarControl.FocusSearch();
				case AppShortcut.OpenSettings:
					return App.GetService<INavigationService>().NavigateTo<SettingsViewModel>();
				case AppShortcut.GoHome:
					return App.GetService<INavigationService>().NavigateTo<HomeViewModel>();
				case AppShortcut.ShowHelp:
					ShowHelp(root.XamlRoot);
					return true;
				case AppShortcut.Refresh:
					return PageRefresh.Run();
				default:
					return false;
			}
		}
		catch (Exception ex)
		{
			App.GetService<ILogger<MainWindow>>().LogWarning(ex, "Shortcut {Shortcut} failed", shortcut);
			return false;
		}
	}

	/// <summary>What a global shortcut does now (pure, for the tests).</summary>
	internal enum ShortcutGate
	{
		/// <summary>Nothing: not where the user is (the player, signed out), or a menu or dialog has the keys.</summary>
		Ignore,

		/// <summary>It runs.</summary>
		Run,

		/// <summary>Nixie 1.3.1: a trailer plays in the theatre window: Back (a key or Mouse 4) closes it, nothing else runs.</summary>
		CloseTrailer,

		/// <summary>
		/// Nixie 1.4.0 (bug hunt M1): over a trailer, every other shortcut is handled and does nothing. Left unhandled (1.3.1's
		/// Ignore) it went on to the key accelerators: the search box's own Ctrl+F took the keyboard out from under the dim.
		/// </summary>
		HoldBack,
	}

	/// <summary>
	/// Whether <paramref name="shortcut"/> runs: allowed where the user is (<see cref="AppShortcuts.AllowedIn"/>), no menu or
	/// dialog open (<paramref name="popupOpen"/>: the keys are theirs), and over a trailer (<paramref name="trailerOpen"/>) only
	/// Back, which closes it; the rest is held back (handled, nothing runs). A key typed in a text box never gets here
	/// (<see cref="AppShortcuts.For"/>); a key over a trailer is decided first (<see cref="Trailers.KeyOverTheater"/>), so this
	/// rule is the mouse's (Mouse 4 closes it, Mouse 5 does nothing).
	/// </summary>
	internal static ShortcutGate Gate(AppShortcut shortcut, ShortcutContext context, bool popupOpen, bool trailerOpen) =>
		trailerOpen && !popupOpen ? (shortcut == AppShortcut.Back ? ShortcutGate.CloseTrailer : ShortcutGate.HoldBack)
		: !AppShortcuts.AllowedIn(shortcut, context) || popupOpen ? ShortcutGate.Ignore
		: ShortcutGate.Run;

	private static ShortcutContext CurrentContext()
	{
		if (App.GetService<PlayerChrome>().IsPlayerOpen)
		{
			return ShortcutContext.Player;
		}

		return App.GetService<ITitleBarViewModel>().User is null ? ShortcutContext.SignedOut : ShortcutContext.Browsing;
	}

	/// <summary>
	/// The navigation service whose frame shows the page: the shell's once signed in, else the window's root frame (server
	/// list, login; the self-test's player). Back from the shell's first page does nothing: it never returns to the login page.
	/// </summary>
	public static INavigationService BackForwardService()
	{
		var setup = App.GetKeyedService<INavigationService>(NavigationRegions.InitialSetup);
		return setup.Frame?.Content is ShellPage ? App.GetService<INavigationService>() : setup;
	}

	/// <summary>A menu, flyout or dialog is open: the keys and buttons are theirs. Tooltips do not count.</summary>
	private static bool HasOpenPopup(XamlRoot? xamlRoot)
	{
		if (xamlRoot is null)
		{
			return false;
		}

		foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
		{
			if (popup.IsOpen && popup.Child is not ToolTip)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>The shortcut sheet ("?"): one at a time, and not over another dialog or menu.</summary>
	public static async void ShowHelp(XamlRoot? xamlRoot)
	{
		if (_helpOpen || xamlRoot is null || HasOpenPopup(xamlRoot))
		{
			return;
		}

		_helpOpen = true;
		// Nixie 1.3.1: F1 or ? closes the sheet too (ShortcutsDialog); whichever way it closes, the keyboard goes back where it was
		var before = FocusManager.GetFocusedElement(xamlRoot) as Control;
		try
		{
			await new ShortcutsDialog { XamlRoot = xamlRoot }.ShowAsync();
			if (before is { XamlRoot: not null } && FocusManager.GetFocusedElement(xamlRoot) is null)
			{
				before.Focus(FocusState.Programmatic);
			}
		}
		catch (Exception ex)
		{
			App.GetService<ILogger<MainWindow>>().LogWarning(ex, "The shortcut sheet failed to open");
		}
		finally
		{
			_helpOpen = false;
		}
	}
}

/// <summary>Seaglass: where a key is typing (no shortcut then, except Esc, which no shortcut uses).</summary>
public static class TextInput
{
	/// <summary>The focused element takes text: a text, password or rich edit box (a search box and a number box hold a TextBox), an editable combo box.</summary>
	public static bool IsTextInput(object? element) =>
		element is not null && (IsTextInputType(element.GetType()) || element is ComboBox { IsEditable: true });

	/// <summary>
	/// Nixie 1.4.0 (bug hunt m2: R on the Audio box refreshed the page instead of its type-ahead): the focused control types
	/// with its bare letters: a closed combo box jumps to the entry that starts with the letter.
	/// </summary>
	public static bool OwnsLetters(object? element) => element is ComboBox;

	public static bool IsTextInputType(Type type) =>
		typeof(TextBox).IsAssignableFrom(type) || typeof(PasswordBox).IsAssignableFrom(type) || typeof(RichEditBox).IsAssignableFrom(type)
		|| typeof(AutoSuggestBox).IsAssignableFrom(type) || typeof(NumberBox).IsAssignableFrom(type);
}
