using FluentFin.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace FluentFin.Services;

/// <summary>
/// 1.5 (the button review's L1, L2; WCAG 2.1.4, character key shortcuts): Home's N and C, in every theme, can be turned off in
/// Settings ("Letter keys on Home"); and, as before, they are letters while the keyboard is in a text box.
/// </summary>
public static class HomeLetterKeys
{
	/// <summary>Whether N or C on Home does nothing now: the switch is off, or the keyboard is in a text box.</summary>
	public static bool Ignore(XamlRoot? root) =>
		Ignore(App.GetService<ISettings>().HomeLetterKeysOff, root is null ? null : FocusManager.GetFocusedElement(root));

	/// <summary>The rule itself (tests): off, or typing.</summary>
	public static bool Ignore(bool switchedOff, object? focused) => switchedOff || TextInput.IsTextInput(focused);
}
