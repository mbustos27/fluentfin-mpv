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
/// Escape (the page's accelerator: full screen off, else back), and never Space or Enter on a focused button or menu item
/// (Cancel, Go back, a notice's close, the bar's Back, Audio, Subtitles): those press it. No XAML state of its own, so the
/// decision can be checked without a window.
/// </summary>
public static class MpvPlayerKeys
{
	/// <summary>The page's decision for a key whose event started at <paramref name="source"/> (the focused element).</summary>
	public static bool Takes(VirtualKey key, bool videoShown, object? source) => Takes(key, videoShown, PressedByKeys(source));

	/// <summary>The decision (pure): <paramref name="onAButton"/> = the focused element is pressed with Space or Enter.</summary>
	public static bool Takes(VirtualKey key, bool videoShown, bool onAButton) =>
		videoShown && key != VirtualKey.Escape && !(onAButton && key is VirtualKey.Space or VirtualKey.Enter or VirtualKey.GamepadA);

	/// <summary>
	/// Space and Enter press this element: a button of any kind, a menu item, a list item, a toggle switch, that the
	/// keyboard (Tab) or the app gave the focus to. One the mouse just clicked keeps no claim on Space: there it plays and
	/// pauses, as in every player (a click on the bar's +30 s must not make Space skip again).
	/// </summary>
	public static bool PressedByKeys(object? element) =>
		element is ButtonBase or MenuFlyoutItemBase or SelectorItem or ToggleSwitch && element is not Control { FocusState: FocusState.Pointer };

	/// <summary>
	/// The page's PreviewKeyDown with FluentFin's controls: the key goes to <paramref name="view"/> when
	/// <see cref="Takes(VirtualKey, bool, object)"/> says so and it is one of the player's keys; then it is handled here.
	/// </summary>
	public static void OnPreviewKeyDown(MpvPlayerControls view, bool videoShown, KeyRoutedEventArgs e)
	{
		if (!e.Handled && Takes(e.Key, videoShown, e.OriginalSource) && view.HandleKey(e.Key))
		{
			e.Handled = true;
		}
	}
}
