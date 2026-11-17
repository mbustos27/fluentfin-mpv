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
		var shortcut = AppShortcuts.For((int)e.Key, Down(VirtualKey.Control), Down(VirtualKey.Shift), Down(VirtualKey.Menu), TextInput.IsTextInput(e.OriginalSource));
		if (shortcut is { } found && Run(root, found))
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

		var kind = e.GetCurrentPoint(root).Properties.PointerUpdateKind;
		if (AppMouse.For(kind == PointerUpdateKind.XButton1Pressed, kind == PointerUpdateKind.XButton2Pressed) is { } shortcut)
		{
			// not handled: the press goes on to whatever is under the pointer
			_ = Run(root, shortcut);
		}
	}

	// -- doing it --------------------------------------------------------------------------------------------------------

	/// <summary>Carries the shortcut out where it applies; true when it did something (the key is then handled).</summary>
	private static bool Run(UIElement root, AppShortcut shortcut)
	{
		try
		{
			if (!AppShortcuts.AllowedIn(shortcut, CurrentContext()) || HasOpenPopup(root.XamlRoot))
			{
				return false;
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
		try
		{
			await new ShortcutsDialog { XamlRoot = xamlRoot }.ShowAsync();
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

	public static bool IsTextInputType(Type type) =>
		typeof(TextBox).IsAssignableFrom(type) || typeof(PasswordBox).IsAssignableFrom(type) || typeof(RichEditBox).IsAssignableFrom(type)
		|| typeof(AutoSuggestBox).IsAssignableFrom(type) || typeof(NumberBox).IsAssignableFrom(type);
}
