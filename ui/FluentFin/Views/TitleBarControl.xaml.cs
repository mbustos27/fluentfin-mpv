using FluentFin.Contracts.Services;
using FluentFin.Controls;
using FluentFin.Core.Services;
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

	/// <summary>Nixie 1.4.0: the tabs' fold, and the lit destination (the search box's and the account's marks).</summary>
	private readonly TopNavigation _top = App.GetService<TopNavigation>();

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
		SizeChanged += (_, _) =>
		{
			Relayout();
			QueuePassthrough();
		};
		Tabs.TabsChanged += (_, _) => QueueRelayout();
		ViewModel.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName is nameof(TitleBarViewModel.IsSearchShown))
			{
				_searchOpen = false;
				QueueRelayout();
			}
			else if (e.PropertyName is nameof(TitleBarViewModel.IsAccountShown) or nameof(TitleBarViewModel.IsNavigationShown))
			{
				QueueRelayout();
			}
			else if (e.PropertyName is nameof(TitleBarViewModel.IsContentUnderBar))
			{
				ShowBackground();
			}
			else if (e.PropertyName is nameof(TitleBarViewModel.IsPlayerPageShown))
			{
				ApplyCaptionHeight();
				QueueRelayout();
			}
			else if (e.PropertyName is nameof(TitleBarViewModel.IsBackButtonShown) or nameof(TitleBarViewModel.IsVisible))
			{
				// Back coming or going moves the headers without changing their size
				QueueRelayout();
			}
		};
		ShowSearch();
		ShowBackground();

		// Nixie 1.4.0 (the trail rule): the search box and Settings are destinations: lit like a tab while they are
		_top.PropertyChanged += OnTopChanged;
		Unloaded += (_, _) => _top.PropertyChanged -= OnTopChanged;
		Loaded += (_, _) =>
		{
			_top.PropertyChanged -= OnTopChanged;
			_top.PropertyChanged += OnTopChanged;
			ShowLit();
		};

		// Nixie 1.3.0: the active layout theme's TopBarStyle on the chrome, now and on every switch (the bar itself is kept)
		ThemeResources.StyleTopBar(BarChrome);
		ThemeResources.Applied += OnThemeApplied;
	}

	/// <summary>
	/// Nixie 1.3.0: a theme switch restyles the chrome behind the bar; the bar's parts stay where they are (the search text,
	/// an open account menu). The chrome's Padding may inset them differently: the bar measures again and the window's
	/// click-through areas follow.
	/// </summary>
	private void OnThemeApplied(object? sender, EventArgs e)
	{
		ThemeResources.StyleTopBar(BarChrome);
		if (IsLoaded)
		{
			QueueRelayout();
			QueuePassthrough();
		}
	}

	private void OnTopChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(TopNavigation.Lit))
		{
			DispatcherQueue.TryEnqueue(ShowLit);
		}
	}

	/// <summary>
	/// Nixie 1.4.0 (the review's X7, CL-m9): the search box wears the tabs' underline while its results (or an item opened from
	/// it) show; the account button an accent ring while Settings (or a page opened from it) shows.
	/// </summary>
	private void ShowLit()
	{
		SearchLit.Visibility = _top.IsSearchLit ? Visibility.Visible : Visibility.Collapsed;
		AccountLit.Visibility = _top.IsSettingsLit ? Visibility.Visible : Visibility.Collapsed;
		// Narrator hears it as it hears the marked tab's "selected"
		Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(SearchBox, _top.IsSearchLit ? "Current" : "");
		// the box's text field is what takes the keyboard (and what Narrator reads)
		if (Find(SearchBox, "TextBox") is { } field)
		{
			Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(field, _top.IsSearchLit ? "Current" : "");
		}

		Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(SearchIconButton, _top.IsSearchLit ? "Current" : "");
		Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(AccountButton, _top.IsSettingsLit ? "Settings is open" : "");
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

		ApplyCaptionHeight();
	}

	/// <summary>
	/// Nixie 1.4.0 (the review's X14: CL-M3, DI-M8, C-M10, E-m11): the caption buttons as tall as the 48 px bar, so their glyphs
	/// share its centre line, y 24 (they were 32 px tall, centred 8 px above everything else). Over the player, whose own top
	/// bar is laid out under them, they keep the standard height.
	/// </summary>
	private void ApplyCaptionHeight()
	{
		try
		{
			App.MainWindow.AppWindow.TitleBar.PreferredHeightOption = CaptionHeightFor(ViewModel.IsPlayerPageShown);
		}
		catch (Exception)
		{
			// the caption buttons keep their height
		}
	}

	/// <summary>The caption buttons' height: the bar's (tall) on every page but the player's.</summary>
	public static TitleBarHeightOption CaptionHeightFor(bool playerPage) => playerPage ? TitleBarHeightOption.Standard : TitleBarHeightOption.Tall;

	/// <summary>Nixie 1.4.0 (CL-p3): the wordmark goes Home, as a logo does.</summary>
	private void OnWordmarkClick(object sender, RoutedEventArgs e)
	{
		// as the Home tab: on Home, back to its top
		if (_top.AllTabs.FirstOrDefault(t => t.Kind == TopTabKind.Home) is { } home)
		{
			_top.Open(home);
		}
	}

	/// <summary>
	/// Nixie 1.4.0 (CL-m1, DI-M8, A3): Back is 40 x 32, 16 px in from the window's edge (it was 40 x 44 at 2 px), the bar's
	/// other controls' subtle fill at radius 8 on hover and press; the wordmark 16 px after its arrow (4 px after the slot,
	/// 16 from the edge when Back is not shown).
	/// </summary>
	private void StyleBackButton()
	{
		if (BackButton() is Control back && back.Margin != BackMargin)
		{
			back.Margin = BackMargin;
			back.CornerRadius = new CornerRadius(8);
		}

		Wordmark.Margin = new Thickness(ViewModel.IsBackButtonShown ? 4 : 14, 0, 0, 0);
		// an empty subtitle (every build but a test build's) takes no room: the wordmark to the first tab is the rhythm's 24
		SubtitleText.Visibility = string.IsNullOrEmpty(SubtitleText.Text) ? Visibility.Collapsed : Visibility.Visible;
	}

	/// <summary>Back's slot: 16 from the edge (the TitleBar lays Back at its own left edge, measured); 8 above and below make it 32 tall, centred on 24.</summary>
	public static readonly Thickness BackMargin = new(16, 8, 0, 8);

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
			// Nixie 1.4.0: the right header's parts one by one, so the room between a centred search box and the account drags
			foreach (var element in new FrameworkElement?[] { BackButton(), LeftHeaderPanel, SearchHost, SignOutPanel, AccountButton })
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

	/// <summary>The TitleBar's own Back button (its template's PART_BackButton, loaded once Back is shown).</summary>
	private FrameworkElement? BackButton() => _backButton ??= Find(TitleBar, "PART_BackButton");

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

	private bool _compact;

	/// <summary>Narrow windows: the search box was opened from its button (it folds back when left empty).</summary>
	private bool _searchOpen;

	private bool _layoutQueued;

	/// <summary>The wordmark's width, kept from when it last showed (it hides in the narrowest windows).</summary>
	private double _wordmarkWidth;

	private void QueueRelayout()
	{
		if (_layoutQueued)
		{
			return;
		}

		_layoutQueued = true;
		DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
		{
			_layoutQueued = false;
			Relayout();
		});
	}

	/// <summary>
	/// Review of 590eaf6: the bar folds when what it holds does not fit (TopBar.Fit), from the widths measured now: Back, the
	/// wordmark, the tabs laid out in a row, the account, the caption buttons' inset (a fixed 1100 px let a long library name
	/// push the account under Minimize). The search box takes the room left.
	/// </summary>
	private void Relayout()
	{
		// Nixie 1.3.0: less the room the theme's chrome insets the bar by (TopBarChrome.Padding)
		var width = ActualWidth - BarChrome.Padding.Left - BarChrome.Padding.Right;
		if (width <= 0)
		{
			return;
		}

		var scale = XamlRoot?.RasterizationScale ?? 1;
		double inset;
		try
		{
			inset = App.MainWindow.AppWindow.TitleBar.RightInset / scale;
		}
		catch (Exception)
		{
			inset = 0;
		}

		if (inset <= 0)
		{
			// not known yet: three caption buttons
			inset = 3 * 46;
		}

		// the TitleBar's own room around its headers, as laid out (the sums of the parts ran about 32 px short): where the left
		// header starts (after Back and the TitleBar's padding), and how far the right header ends from the window's edge
		var leftStart = 0.0;
		try
		{
			leftStart = LeftHeaderPanel.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
			if (RightHeaderPanel.ActualWidth > 0)
			{
				var rightEnd = RightHeaderPanel.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(RightHeaderPanel.ActualWidth, 0)).X;
				inset = Math.Max(inset, width - rightEnd);
			}
		}
		catch (Exception)
		{
			// not laid out yet: the parts' sum
		}

		if (Wordmark.Visibility == Visibility.Visible && Wordmark.ActualWidth > 0)
		{
			_wordmarkWidth = Wordmark.ActualWidth + Wordmark.Margin.Left + Wordmark.Margin.Right;
		}

		var navigation = ViewModel.IsNavigationShown;
		var fit = TopBar.Fit(
			width,
			inset + _slack,
			back: Math.Max(leftStart, ViewModel.IsBackButtonShown ? Math.Max(BackButton()?.ActualWidth ?? 0, 48) : 0),
			wordmark: _wordmarkWidth > 0 ? _wordmarkWidth : 120,
			row: navigation ? Tabs.RowWidths() : new TopRowWidths(0, [], 0, 0),
			menuButton: navigation ? Tabs.MenuButtonWidth() : 0,
			account: ViewModel.IsAccountShown ? AccountButton.Width : 0,
			search: ViewModel.IsSearchShown,
			searchOpen: _searchOpen);

		_compact = fit.Compact;
		StyleBackButton();
		// Nixie 1.4.0: the library tabs the bar has no room for fold into "Libraries ▾" (the row changes: TabsChanged, measured again)
		if (navigation)
		{
			_top.Fold(fit.ShownLibraries);
		}

		Tabs.IsCompact = fit.Compact;
		Tabs.IsMenuHidden = !fit.ShowSections;
		Tabs.IsMenuLabelHidden = !fit.ShowSectionsLabel;
		Wordmark.Visibility = fit.ShowWordmark ? Visibility.Visible : Visibility.Collapsed;
		SearchBox.Width = fit.Compact ? (fit.SearchWidth > 0 ? fit.SearchWidth : TopBar.MaxOpenSearchWidth) : Math.Max(TopBar.MinSearchWidth, fit.SearchWidth);
		ShowSearch();
		QueuePassthrough();

		// then the gap really left, once laid out: short of the minimum, keep more room and lay out again (TopBar.Slack)
		DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, CheckGap);
	}

	/// <summary>Room kept beyond the measured parts (TopBar.Slack).</summary>
	private double _slack;

	private void CheckGap()
	{
		try
		{
			if (RightHeaderPanel.ActualWidth <= 0 || LeftHeaderPanel.ActualWidth <= 0)
			{
				return;
			}

			var leftEnd = LeftHeaderPanel.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(LeftHeaderPanel.ActualWidth, 0)).X;
			// the gap with the search box pinned to the account (where it stands when it is not centred)
			var rightStart = RightHeaderPanel.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0)).X + SearchHost.Margin.Right;
			CentreSearch(leftEnd);
			var slack = TopBar.Slack(_slack, rightStart - leftEnd);
			if (slack > _slack)
			{
				_slack = slack;
				Relayout();
			}
		}
		catch (Exception)
		{
			// not laid out
		}
	}

	/// <summary>
	/// Nixie 1.4.0 (the review's A4, CL-p2, DI-p2, C-P7): the search box centred in the window while that leaves the tabs their
	/// drag gap, else pinned 8 px from the account (TopBar.SearchOffset); the narrow window's button and open box stay pinned.
	/// </summary>
	private void CentreSearch(double leftEnd)
	{
		var offset = 0.0;
		if (!_compact && SearchBox.Visibility == Visibility.Visible && AccountButton.Visibility == Visibility.Visible && AccountButton.ActualWidth > 0)
		{
			var accountLeft = AccountButton.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
			offset = TopBar.SearchOffset(ActualWidth, leftEnd, SearchBox.Width, accountLeft - TopBar.Spacing);
		}

		if (Math.Abs(SearchHost.Margin.Right - offset) >= 1)
		{
			SearchHost.Margin = new Thickness(0, 0, offset, 0);
		}
	}

	private void ShowSearch()
	{
		var shown = ViewModel.IsSearchShown;
		SearchHost.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
		SearchBox.Visibility = shown && (!_compact || _searchOpen) ? Visibility.Visible : Visibility.Collapsed;
		SearchIconButton.Visibility = shown && _compact && !_searchOpen ? Visibility.Visible : Visibility.Collapsed;
		ShowLit();
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
			Relayout();
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
			Relayout();
		});
	}

	/// <summary>
	/// The bar's background: its glass, or over a page under it (a hero) a dark scrim, the bar's text in the dark theme so it
	/// reads on artwork (the caption buttons follow: ActualThemeChanged).
	/// </summary>
	private void ShowBackground()
	{
		var under = ViewModel.IsContentUnderBar;
		BarChrome.IsOverHero = under; // Nixie 1.3.0: the chrome's state (TopBarChrome: OverContent or OverHero), in the theme's look
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
}

/// <summary>Seaglass 1.1.4 (layout audit, item 17): the window's caption buttons' colours (TitleBarControl.CaptionColorsFor).</summary>
public sealed record CaptionColors(Windows.UI.Color Foreground, Windows.UI.Color Inactive, Windows.UI.Color HoverBackground, Windows.UI.Color PressedBackground, Windows.UI.Color PressedForeground);
