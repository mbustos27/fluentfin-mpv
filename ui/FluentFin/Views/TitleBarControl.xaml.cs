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
		// review: high contrast switched on or off while it runs (the theme may not change with it); the window's theme settings,
		// once it exists (AccessibilitySettings' event cannot be subscribed in a desktop app: it threw here, and the window
		// never opened)
		Loaded += (_, _) => WatchHighContrast();

		// 1.2.0 (the top bar): the window's title bar is this TitleBar, so the bar drags the window and its tabs, search and
		// account take clicks (the TitleBar marks them passthrough once it is the window's). Without it Windows' default caption
		// strip covered the bar's top 32 px: a click on a tab (or the old search box) there dragged the window instead.
		Loaded += (_, _) => RegisterAsWindowTitleBar();

		// 1.2.0 (the top bar): narrow windows fold the tabs and search; a hero under the bar gets the scrim
		SizeChanged += (_, e) =>
		{
			ApplyWidth(e.NewSize.Width);
			QueuePassthrough();
		};
		ViewModel.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName is nameof(TitleBarViewModel.IsSearchShown))
			{
				_searchOpen = false;
				ShowSearch();
			}
			else if (e.PropertyName is nameof(TitleBarViewModel.IsContentUnderBar))
			{
				ShowBackground();
			}
			else if (e.PropertyName is nameof(TitleBarViewModel.IsBackButtonShown) or nameof(TitleBarViewModel.IsVisible))
			{
				// Back coming or going moves the headers without changing their size
				QueuePassthrough();
			}
		};
		ShowSearch();
		ShowBackground();
	}

	private void RegisterAsWindowTitleBar()
	{
		try
		{
			App.MainWindow.SetTitleBar(TitleBar);
		}
		catch (Exception)
		{
			// the window keeps Windows' default caption strip
		}
	}

	private bool _regionsQueued;

	/// <summary>
	/// 1.2.0: the bar's headers changed size (the search box or the account appearing at sign-in, the tabs once the libraries
	/// arrive, the box opened from its button), or the window did. The TitleBar works out which of its areas take clicks only
	/// when the window is resized, so until then they stayed drag area (seen: the search box and the last tab answered
	/// "caption"). The bar sets them itself, after layout: Back, the left header (the wordmark, the tabs), the right header.
	/// </summary>
	private void OnHeaderSizeChanged(object sender, SizeChangedEventArgs e) => QueuePassthrough();

	private void QueuePassthrough()
	{
		if (_regionsQueued)
		{
			return;
		}

		_regionsQueued = true;
		DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
		{
			_regionsQueued = false;
			SetPassthrough();
		});
	}

	private void SetPassthrough()
	{
		try
		{
			if (XamlRoot is null || !ViewModel.IsVisible)
			{
				return;
			}

			var scale = XamlRoot.RasterizationScale;
			var rects = new List<Windows.Graphics.RectInt32>();
			foreach (var element in new FrameworkElement?[] { BackButton(), LeftHeaderPanel, RightHeaderPanel })
			{
				if (element is { Visibility: Visibility.Visible, ActualWidth: > 0, ActualHeight: > 0 })
				{
					var r = element.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
					rects.Add(PassthroughRect(r, scale));
				}
			}

			Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(App.MainWindow.AppWindow.Id)
				.SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough, [.. rects]);
		}
		catch (Exception)
		{
			// the TitleBar's own areas stay
		}
	}

	/// <summary>An element's bounds (view pixels) as the window's physical pixels, for the non-client regions.</summary>
	public static Windows.Graphics.RectInt32 PassthroughRect(Windows.Foundation.Rect bounds, double scale) => new(
		(int)Math.Round(bounds.X * scale), (int)Math.Round(bounds.Y * scale),
		(int)Math.Round(bounds.Width * scale), (int)Math.Round(bounds.Height * scale));

	private FrameworkElement? _backButton;

	/// <summary>The TitleBar's own Back button (its template's).</summary>
	private FrameworkElement? BackButton() => _backButton ??= Find(TitleBar, "BackButton");

	private static FrameworkElement? Find(DependencyObject root, string name)
	{
		for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
		{
			var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
			if (child is FrameworkElement { Name: var n } fe && n == name)
			{
				return fe;
			}

			if (Find(child, name) is { } found)
			{
				return found;
			}
		}

		return null;
	}

	/// <summary>1.2.0: at and below this width (the window's) the tabs are one menu button and search one icon.</summary>
	public const double CompactWidth = 1100;

	/// <summary>The search box's width: narrower in a middling window, so the tabs keep their room.</summary>
	public static double SearchWidthFor(double width) => width >= 1500 ? 360 : 260;

	private bool _compact;

	/// <summary>Narrow windows: the search box was opened from its button (it folds back when left empty).</summary>
	private bool _searchOpen;

	private void ApplyWidth(double width)
	{
		_compact = width <= CompactWidth;
		Tabs.IsCompact = _compact;
		SearchBox.Width = SearchWidthFor(width);
		ShowSearch();
	}

	private void ShowSearch()
	{
		var shown = ViewModel.IsSearchShown;
		SearchBox.Visibility = shown && (!_compact || _searchOpen) ? Visibility.Visible : Visibility.Collapsed;
		SearchIconButton.Visibility = shown && _compact && !_searchOpen ? Visibility.Visible : Visibility.Collapsed;
	}

	/// <summary>Opens the search box (narrow windows) and gives it the keyboard; false when search is not offered here.</summary>
	private bool OpenSearch(FocusState how)
	{
		if (!ViewModel.IsSearchShown)
		{
			return false;
		}

		if (_compact && !_searchOpen)
		{
			_searchOpen = true;
			ShowSearch();
			SearchBox.UpdateLayout();
		}

		return SearchBox.Focus(how);
	}

	private void OnSearchIconClick(object sender, RoutedEventArgs e) => OpenSearch(FocusState.Keyboard);

	/// <summary>Narrow windows: the box folds back to its button when the keyboard leaves it empty (not for its own list).</summary>
	private void OnSearchLostFocus(object sender, RoutedEventArgs e)
	{
		if (!_compact || !_searchOpen || !string.IsNullOrEmpty(SearchBox.Text))
		{
			return;
		}

		DispatcherQueue.TryEnqueue(() =>
		{
			var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
			for (var e = focused; e is not null; e = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(e))
			{
				if (ReferenceEquals(e, SearchBox))
				{
					return;
				}
			}

			_searchOpen = false;
			ShowSearch();
		});
	}

	/// <summary>
	/// The bar's background: its glass, or over a page under it (a hero) a dark scrim, the bar's text in the dark theme so it
	/// reads on artwork (the caption buttons follow: ActualThemeChanged).
	/// </summary>
	private void ShowBackground()
	{
		var under = ViewModel.IsContentUnderBar;
		BarGlass.Visibility = under ? Visibility.Collapsed : Visibility.Visible;
		BarScrim.Visibility = under ? Visibility.Visible : Visibility.Collapsed;
		RequestedTheme = under ? ElementTheme.Dark : ElementTheme.Default;
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

	/// <summary>The window's theme settings: high contrast, and its changes (kept: its Changed is raised only while it lives).</summary>
	private Microsoft.UI.System.ThemeSettings? _themeSettings;

	private void WatchHighContrast()
	{
		try
		{
			if (_themeSettings is null)
			{
				_themeSettings = Microsoft.UI.System.ThemeSettings.CreateForWindowId(App.MainWindow.AppWindow.Id);
				_themeSettings.Changed += (_, _) => DispatcherQueue.TryEnqueue(ApplyCaptionTheme);
				ApplyCaptionTheme();
			}
		}
		catch (Exception)
		{
			// the caption colours follow the theme only
		}
	}

	private void ApplyCaptionTheme()
	{
		try
		{
			var highContrast = _themeSettings?.HighContrast ?? new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
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
	public static bool FocusSearch() => _current is { } bar && bar.OpenSearch(FocusState.Keyboard);

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

		OpenSearch(FocusState.Programmatic);
	}
}

/// <summary>Seaglass 1.1.4 (layout audit, item 17): the window's caption buttons' colours (TitleBarControl.CaptionColorsFor).</summary>
public sealed record CaptionColors(Windows.UI.Color Foreground, Windows.UI.Color Inactive, Windows.UI.Color HoverBackground, Windows.UI.Color PressedBackground, Windows.UI.Color PressedForeground);
