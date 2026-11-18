using FluentFin.Core.ViewModels;
using FluentFin.Services;
using FluentFin.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FluentFin.Views;

public sealed partial class TitleBarControl : UserControl
{
	public TitleBarViewModel ViewModel { get; } = (TitleBarViewModel)App.GetService<ITitleBarViewModel>();

	// fluentfin-mpv: no search while a player page is open (docs/research/ux-review.md V9); Seaglass 1.1.4: nor signed out
	// (TitleBarViewModel.IsSearchShown)

	public TitleBarControl()
	{
		InitializeComponent();
		_current = this;

		// Seaglass 1.1.4 (layout audit, item 17): the window's caption buttons follow the app's theme, not Windows' (they stayed
		// pale grey on the light title bar when Seaglass was light and Windows dark)
		Loaded += (_, _) => ApplyCaptionTheme();
		ActualThemeChanged += (_, _) => ApplyCaptionTheme();
	}

	/// <summary>The caption buttons' theme for the app's: light or dark; in high contrast, Windows' own.</summary>
	public static TitleBarTheme CaptionThemeFor(ElementTheme actual, bool highContrast) =>
		highContrast ? TitleBarTheme.UseDefaultAppMode
		: actual == ElementTheme.Light ? TitleBarTheme.Light
		: TitleBarTheme.Dark;

	/// <summary>
	/// The caption buttons' colours for the app's theme: WinUI's text and subtle fills (TextFillColorPrimary for the glyph,
	/// SubtleFillColorSecondary/Tertiary behind it on hover and press, TextFillColorDisabled while the window is inactive), on
	/// a transparent background. Null in high contrast: Windows' own. With the content extended into the title bar the system
	/// draws the buttons from these colours; PreferredTheme alone left them pale on the light title bar.
	/// </summary>
	public static CaptionColors? CaptionColorsFor(ElementTheme actual, bool highContrast) =>
		highContrast ? null
		: actual == ElementTheme.Light
			? new CaptionColors(Argb(0xE4, 0, 0, 0), Argb(0x5C, 0, 0, 0), Argb(0x09, 0, 0, 0), Argb(0x06, 0, 0, 0), Argb(0x9E, 0, 0, 0))
			: new CaptionColors(Argb(0xFF, 0xFF, 0xFF, 0xFF), Argb(0x5D, 0xFF, 0xFF, 0xFF), Argb(0x0F, 0xFF, 0xFF, 0xFF), Argb(0x0A, 0xFF, 0xFF, 0xFF), Argb(0xC5, 0xFF, 0xFF, 0xFF));

	private static Windows.UI.Color Argb(byte a, byte r, byte g, byte b) => Windows.UI.Color.FromArgb(a, r, g, b);

	private void ApplyCaptionTheme()
	{
		try
		{
			var highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
			var bar = App.MainWindow.AppWindow.TitleBar;
			bar.PreferredTheme = CaptionThemeFor(ActualTheme, highContrast);

			var colors = CaptionColorsFor(ActualTheme, highContrast);
			Windows.UI.Color? transparent = colors is null ? null : Microsoft.UI.Colors.Transparent;
			bar.ButtonBackgroundColor = transparent;
			bar.ButtonInactiveBackgroundColor = transparent;
			bar.ButtonForegroundColor = colors?.Foreground;
			bar.ButtonInactiveForegroundColor = colors?.Inactive;
			bar.ButtonHoverBackgroundColor = colors?.HoverBackground;
			bar.ButtonHoverForegroundColor = colors?.Foreground;
			bar.ButtonPressedBackgroundColor = colors?.PressedBackground;
			bar.ButtonPressedForegroundColor = colors?.PressedForeground;
		}
		catch (Exception)
		{
			// decoration: the buttons keep Windows' colours
		}
	}

	private static TitleBarControl? _current;

	/// <summary>Seaglass: the search keys (Ctrl+E, /, WindowShortcuts): the search box takes the focus when it shows.</summary>
	public static bool FocusSearch() =>
		_current is { SearchBox.Visibility: Microsoft.UI.Xaml.Visibility.Visible } bar && bar.SearchBox.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);

	private void TitleBar_BackRequested(Microsoft.UI.Xaml.Controls.TitleBar sender, object args)
	{
		ViewModel.GoBack();
	}

	private void CloseAccountFlyout(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
	{
		AccountFlyout.Hide();
	}

	private void KeyboardAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		if (FocusManager.GetFocusedElement() is AutoSuggestBox sb && sb == SearchBox)
		{
			return;
		}

		SearchBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
	}
}

/// <summary>Seaglass 1.1.4 (layout audit, item 17): the window's caption buttons' colours (TitleBarControl.CaptionColorsFor).</summary>
public sealed record CaptionColors(Windows.UI.Color Foreground, Windows.UI.Color Inactive, Windows.UI.Color HoverBackground, Windows.UI.Color PressedBackground, Windows.UI.Color PressedForeground);
