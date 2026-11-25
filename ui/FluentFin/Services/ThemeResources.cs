using FluentFin.Controls;
using FluentFin.Themes;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace FluentFin.Services;

/// <summary>
/// Nixie 1.3.0: the XAML side of <see cref="ThemeService"/>. Puts the active theme's dictionary in App.xaml's
/// <see cref="LayoutThemeSlot"/>, at start-up and on every switch, then tells the parts that restyle in place
/// (<see cref="Applied"/>: the top bar's chrome). Pages pick the new keys up when they are created (the switch re-navigates).
/// </summary>
public static class ThemeResources
{
	private static ThemeService? _service;
	private static ILogger? _log;

	/// <summary>The dictionary in the slot (null: none, the shared look).</summary>
	public static ResourceDictionary? Current { get; private set; }

	/// <summary>Raised on the UI thread after the slot's dictionary changed.</summary>
	public static event EventHandler? Applied;

	/// <summary>Shows <paramref name="service"/>'s active theme now and follows its switches.</summary>
	public static void Attach(ThemeService service, ILogger? log = null)
	{
		_log = log;
		if (_service is not null)
		{
			_service.ActiveChanged -= OnActiveChanged;
		}

		_service = service;
		service.ActiveChanged += OnActiveChanged;
		Show(service.Active);
	}

	/// <summary>
	/// 1.7.5 (g4, the basics gate's theme switch): what the last switch did with the page shown (<see cref="ReloadShownPage"/>).
	/// </summary>
	public static ThemeReload LastReload { get; private set; } = ThemeReload.None;

	private static void OnActiveChanged(object? sender, EventArgs e)
	{
		Show(_service?.Active);
		LastReload = ReloadShownPage();
		_log?.LogInformation("Layout theme {Theme}: the page shown {Reload}", _service?.ActiveId, LastReload);
	}

	/// <summary>
	/// 1.7.5 (g4): the page shown goes to the new theme's view in the same turn as its dictionary, whoever chose the theme
	/// (Settings and "Choose a look" through <see cref="ThemeSwitcher"/>, or any other caller of <see cref="ThemeService.Choose"/>).
	/// A page of the old theme left in the window measured against the new dictionary and ended Nixie at the next layout pass
	/// ("Cannot find a Resource with the Name/Key DriveInBoardTextBrush": Drive-in's film page, switched in place).
	/// </summary>
	private static ThemeReload ReloadShownPage()
	{
		try
		{
			return App.GetService<FluentFin.Contracts.Services.INavigationService>() is NavigationService navigation ? navigation.ReloadForTheme() : ThemeReload.None;
		}
		catch (Exception ex)
		{
			_log?.LogWarning(ex, "The page shown could not be shown again in the new layout theme");
			return ThemeReload.None;
		}
	}

	/// <summary>Puts <paramref name="theme"/>'s dictionary in the slot; a dictionary that does not load leaves the slot empty (the shared look).</summary>
	public static void Show(ILayoutTheme? theme)
	{
		if (Slot() is not { } slot)
		{
			_log?.LogWarning("No LayoutThemeSlot in App.xaml: the layout theme's keys are not merged");
			return;
		}

		var outgoing = Current;
		slot.MergedDictionaries.Clear();
		Current = null;
		KeepBehind(outgoing);
		if (theme is not null)
		{
			try
			{
				var dictionary = new ResourceDictionary { Source = theme.Dictionary };
				slot.MergedDictionaries.Add(dictionary);
				Current = dictionary;
			}
			catch (Exception ex)
			{
				_log?.LogError(ex, "The {Theme} theme's dictionary did not load; the shared look is shown", theme.Id);
			}
		}

		Applied?.Invoke(null, EventArgs.Empty);
	}

	/// <summary>1.7.5 (g4): the dictionary of the theme switched away from, at the bottom of the app's resources.</summary>
	private static ResourceDictionary? s_behind;

	/// <summary>
	/// 1.7.5 (g4): the outgoing theme's dictionary is kept first of the app's merged dictionaries, below every other (XAML looks
	/// the last merged up first), so only its own keys resolve there: what an element of that theme still in the window asks
	/// for at its next measure (Drive-in's film and series pages ended Nixie, "Cannot find a Resource with the Name/Key
	/// DriveInBoardTextBrush", when the theme changed under them). The new theme's and the shared keys still win everywhere
	/// else. It is replaced at the next switch.
	/// </summary>
	private static void KeepBehind(ResourceDictionary? outgoing)
	{
		try
		{
			if (Application.Current?.Resources is not { } resources)
			{
				return;
			}

			if (s_behind is null)
			{
				s_behind = new ResourceDictionary();
				resources.MergedDictionaries.Insert(0, s_behind);
			}

			s_behind.MergedDictionaries.Clear();
			if (outgoing is not null)
			{
				s_behind.MergedDictionaries.Add(outgoing);
			}
		}
		catch (Exception ex)
		{
			_log?.LogWarning(ex, "The outgoing layout theme's dictionary could not be kept behind the new one");
		}
	}

	/// <summary>The active theme's resource <paramref name="key"/> (its dictionary's, then its merged ones'), null when it has none.</summary>
	public static object? Find(string key) => Current is { } dictionary ? Find(dictionary, key) : null;

	/// <summary>A key in <paramref name="dictionary"/>, then in its merged dictionaries, last merged first (XAML's lookup order).</summary>
	public static object? Find(ResourceDictionary dictionary, string key)
	{
		if (dictionary.TryGetValue(key, out var value))
		{
			return value;
		}

		for (var i = dictionary.MergedDictionaries.Count - 1; i >= 0; i--)
		{
			if (Find(dictionary.MergedDictionaries[i], key) is { } found)
			{
				return found;
			}
		}

		return null;
	}

	/// <summary>The active theme's TopBarStyle on the bar's chrome; without one (or one for another type), the chrome's default style, 1.2.0's bar.</summary>
	public static void StyleTopBar(TopBarChrome chrome)
	{
		var style = Find(ThemeContract.TopBarStyleKey) as Style;
		chrome.Style = style is not null && (style.TargetType is null || style.TargetType.IsAssignableFrom(typeof(TopBarChrome))) ? style : null;
	}

	/// <summary>The active theme's sign-in backdrop (SignInBackdropTemplate), null when it has none: the dusk scene stays.</summary>
	public static DataTemplate? SignInBackdrop => Find(ThemeContract.SignInBackdropKey) as DataTemplate;

	private static LayoutThemeSlot? Slot() =>
		Application.Current?.Resources is { } resources ? resources.MergedDictionaries.OfType<LayoutThemeSlot>().FirstOrDefault() : null;
}
