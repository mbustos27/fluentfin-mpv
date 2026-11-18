using Microsoft.UI.Xaml;

namespace FluentFin.Themes;

/// <summary>
/// Nixie 1.3.0: App.xaml's place for the active layout theme's dictionary, the last of the app's merged dictionaries so a
/// theme's keys win over the shared styles' (App.xaml's own keys still win over both). Empty in XAML;
/// <see cref="Services.ThemeResources"/> puts the theme's dictionary in it at start-up and swaps it on a switch.
/// </summary>
public sealed partial class LayoutThemeSlot : ResourceDictionary
{
}
