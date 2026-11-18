using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FluentFin.Controls;

/// <summary>
/// fluentfin-mpv player controls: which keys the player page hands to the player (<see cref="MpvPlayerControls.HandleKey"/>)
/// before the focused element sees them (the page previews them: a focused slider or button would take arrows and
/// Space otherwise). Only while the video is shown (the loading screen and an error screen have their own button), never
/// Escape (the page's accelerator: full screen off, nothing else, PlayerEscape), and never Space or Enter on a focused button or menu item
/// (Cancel, Go back, a notice's close, the bar's Back, Audio, Subtitles): those press it. No XAML state of its own, so the
/// decision can be checked without a window.
/// </summary>
public static class MpvPlayerKeys
{
	/// <summary>The page's decision for a key whose event started at <paramref name="source"/> (the focused element).</summary>
	public static bool Takes(VirtualKey key, bool videoShown, object? source) => Takes(key, videoShown, PressedByKeys(source));

	/// <summary>The decision (pure): <paramref name="onAButton"/> = the focused element is pressed with Space or Enter.</summary>
	public static bool Takes(VirtualKey key, bool videoShown, bool onAButton) => Takes(key, videoShown, onAButton, alt: false);

	/// <summary>1.2.0: with Alt held, Enter is the player's (Alt+Enter, full screen), never a focused button's press.</summary>
	public static bool Takes(VirtualKey key, bool videoShown, bool onAButton, bool alt) =>
		videoShown && key != VirtualKey.Escape && !(onAButton && !(alt && key == VirtualKey.Enter) && key is VirtualKey.Space or VirtualKey.Enter or VirtualKey.GamepadA);

	/// <summary>
	/// Space and Enter press this element: a button of any kind, a menu item, a list item, a toggle switch, that the
	/// keyboard (Tab) or the app gave the focus to. One the mouse just clicked keeps no claim on Space: there it plays and
	/// pauses, as in every player (a click on the bar's +30 s must not make Space skip again).
	/// </summary>
	public static bool PressedByKeys(object? element) =>
		element is ButtonBase or MenuFlyoutItemBase or SelectorItem or ToggleSwitch && element is not Control { FocusState: FocusState.Pointer };

	/// <summary>
	/// Seaglass 1.1.3: the keys whose auto-repeat does nothing (<paramref name="wasKeyDown"/>: the key was already down). Held,
	/// F and F11 switched full screen about 15 times a second, and Escape left full screen and then the player.
	/// </summary>
	public static bool IgnoresRepeat(VirtualKey key, bool wasKeyDown) =>
		wasKeyDown && key is VirtualKey.F or VirtualKey.F11 or VirtualKey.Escape;

	/// <summary>
	/// The page's PreviewKeyDown with FluentFin's controls: the key goes to <paramref name="view"/> when
	/// <see cref="Takes(VirtualKey, bool, object)"/> says so and it is one of the player's keys; then it is handled here. A
	/// repeat of a key that ignores repeats (<see cref="IgnoresRepeat"/>) is handled and does nothing.
	/// </summary>
	public static void OnPreviewKeyDown(MpvPlayerControls view, bool videoShown, KeyRoutedEventArgs e)
	{
		var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
		if (e.Handled || Services.TextInput.IsTextInput(e.OriginalSource) || !Takes(e.Key, videoShown, PressedByKeys(e.OriginalSource), alt))
		{
			return;
		}

		if (IgnoresRepeat(e.Key, e.KeyStatus.WasKeyDown) || view.HandleKey(e.Key))
		{
			e.Handled = true;
		}
	}
}
