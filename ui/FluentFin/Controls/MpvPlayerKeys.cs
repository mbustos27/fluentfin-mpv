using FluentFin.Core.Mpv;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace FluentFin.Controls;

/// <summary>
/// fluentfin-mpv player controls: the WinUI side of the player page's one key router (<see cref="PlayerKeyRouter"/>, docs/SHORTCUTS.md).
/// Nixie 1.3.1: the page hands it every key that reaches the window (its root's PreviewKeyDown), before the focused element
/// sees the key, so the keys work the same whichever control has the keyboard: a button a click or a closing menu left the
/// focus on, the seek bar, the title bar, nothing at all. 1.3.0 listened on the player's own grid and let a focused button keep
/// Space and Enter, so Space opened the volume menu again after it closed, or left full screen, and with the focus outside the
/// player nothing worked until the bars hid. What the focused element is (<see cref="FocusOf"/>) is the only WinUI input.
/// </summary>
public static class MpvPlayerKeys
{
	/// <summary>
	/// Set on a theme's list or tab strip inside the player (Cinematic's tabs and panel): with the focus in it, the arrows, Home,
	/// End, Page Up/Down, Space and Enter are its own (<see cref="PlayerFocus.List"/>).
	/// </summary>
	public static readonly DependencyProperty OwnsArrowsProperty =
		DependencyProperty.RegisterAttached("OwnsArrows", typeof(bool), typeof(MpvPlayerKeys), new PropertyMetadata(false));

	public static bool GetOwnsArrows(DependencyObject element) => (bool)element.GetValue(OwnsArrowsProperty);

	public static void SetOwnsArrows(DependencyObject element, bool value) => element.SetValue(OwnsArrowsProperty, value);

	/// <summary>The name of the seek bar (MpvPlayerControls.xaml): the arrows there seek and change the volume.</summary>
	public const string SeekBarName = "SeekBar";

	/// <summary>
	/// What has the keyboard, as the router sees it: nothing (null), a text input, the seek bar, a list or menu (an item of
	/// one, an element inside a theme's <see cref="OwnsArrowsProperty"/> panel, or one outside <paramref name="windowContent"/>:
	/// an open flyout's), a button the keyboard is on, or anything else (the player, the title bar, a control the mouse clicked).
	/// </summary>
	public static PlayerFocus FocusOf(object? element, UIElement? windowContent = null)
	{
		if (element is null)
		{
			return PlayerFocus.Player;
		}

		if (Services.TextInput.IsTextInput(element))
		{
			return PlayerFocus.TextInput;
		}

		if (element is not DependencyObject focused)
		{
			return PlayerFocus.Player;
		}

		if (element is Slider { Name: SeekBarName })
		{
			return PlayerFocus.SeekBar;
		}

		if (element is Slider or SelectorItem or MenuFlyoutItemBase or ListViewBase or RadioButton or RadioButtons or ComboBox
			|| InArrowOwner(focused) || (windowContent is not null && !IsWithin(focused, windowContent)))
		{
			return PlayerFocus.List;
		}

		// a control the mouse clicked keeps no claim on the keyboard (1.3.1: the player's own take no focus from a click)
		return element is ButtonBase or ToggleSwitch && element is not Control { FocusState: FocusState.Pointer } ? PlayerFocus.Button : PlayerFocus.Player;
	}

	private static bool InArrowOwner(DependencyObject element)
	{
		for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (GetOwnsArrows(node))
			{
				return true;
			}
		}

		return false;
	}

	private static bool IsWithin(DependencyObject element, DependencyObject ancestor)
	{
		for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (ReferenceEquals(node, ancestor))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Seaglass 1.1.3: the keys whose auto-repeat does nothing (<paramref name="wasKeyDown"/>: the key was already down). Held,
	/// F and F11 switched full screen about 15 times a second, and Escape left full screen and then the player. (The router
	/// itself goes further: <see cref="PlayerKeyRouter.Repeats"/>.)
	/// </summary>
	public static bool IgnoresRepeat(VirtualKey key, bool wasKeyDown) =>
		wasKeyDown && key is VirtualKey.F or VirtualKey.F11 or VirtualKey.Escape;

	/// <summary>The router's decision for a key event, with the modifiers read from the keyboard's state now.</summary>
	public static PlayerKeyDecision Decide(VirtualKey key, bool wasKeyDown, bool videoShown, PlayerFocus focus)
	{
		var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread;
		bool Down(VirtualKey k) => state(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
		return PlayerKeyRouter.Route((int)key, Down(VirtualKey.Control), Down(VirtualKey.Shift), Down(VirtualKey.Menu), wasKeyDown, videoShown, focus);
	}

	/// <summary>
	/// The page's key handler with FluentFin's controls (on the window's root, before any focused element): the router decides,
	/// <paramref name="view"/> does the player's action, and the key is handled; otherwise it goes on to the focused element.
	/// </summary>
	public static void OnPreviewKeyDown(MpvPlayerControls view, bool videoShown, KeyRoutedEventArgs e)
	{
		if (e.Handled)
		{
			return;
		}

		var root = view.XamlRoot;
		var focused = root is null ? e.OriginalSource : FocusManager.GetFocusedElement(root) ?? e.OriginalSource;
		var decision = Decide(e.Key, e.KeyStatus.WasKeyDown, videoShown, FocusOf(focused, root?.Content));
		switch (decision.Route)
		{
			case PlayerKeyRoute.Player when decision.Action is { } action:
				view.Run(action);
				e.Handled = true;
				break;
			case PlayerKeyRoute.Swallow:
				e.Handled = true;
				break;
		}
	}
}
