using System.ComponentModel;
using FluentFin.Core;
using FluentFin.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Web.WebView2.Core;
using Windows.System;
using Windows.UI;

namespace FluentFin.Controls;

/// <summary>
/// Nixie 1.3.1: the theatre window. A YouTube trailer plays in an overlay inside the main window (not a window of its own):
/// the page dimmed, the theme's <see cref="TrailerFrame"/> centred over it with the trailer's title, Close and the video, 16:9
/// at about 60 % of the window's width (<see cref="Trailers.VideoSize"/>). Esc, Close and a click on the dimmed page close it,
/// and the keyboard goes back to the Trailer button (<see cref="TrailerWindowState"/>); the keyboard moves in when it opens
/// and Tab stays inside. Never while the player is open; the player opening closes it.
/// <para>
/// The video is a WebView2 made for each trailer and closed with the window (the sound stops at once), locked down: its own
/// user-data folder under Nixie's data folder, no DevTools in a release, no host objects, no web messages, no context menu,
/// no permissions, no downloads; it may load only the privacy-enhanced embed (<see cref="Trailers.Navigation"/>), a YouTube
/// link ("Watch on YouTube") and any new window open in the system browser.
/// </para>
/// </summary>
public sealed partial class TrailerTheater : Grid
{
	/// <summary>The window's theatre (MainWindow's), null before the window is up.</summary>
	public static TrailerTheater? Current { get; private set; }

	/// <summary>
	/// The client the embed is told it plays in (YouTube asks embedded players for a Referer; a desktop app has no page of its
	/// own, so it names itself the way an app id does). Generic: nothing about the user or the machine.
	/// </summary>
	internal const string Referer = "https://app.nixie.windows/";

	private readonly Border _dim;
	private readonly TrailerFrame _frame;
	private readonly TrailerWindowState _state;
	private WebView2? _web;
	private Uri _embedBase = Trailers.DefaultEmbedBase;
	private ILogger? _log;
	private long _generation;
	private PlayerChrome? _chrome;

	public TrailerTheater()
	{
		Visibility = Visibility.Collapsed;
		TabFocusNavigation = KeyboardNavigationMode.Cycle;
		XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
		_state = new TrailerWindowState(Refocus);

		_dim = new Border { Background = DimBrush() };
		AutomationProperties.SetAccessibilityView(_dim, AccessibilityView.Raw);
		// a press on the dimmed page closes it (light dismiss, as a flyout's)
		_dim.PointerPressed += (_, e) =>
		{
			e.Handled = true;
			Close();
		};

		_frame = new TrailerFrame { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
		_frame.CloseRequested += (_, _) => Close();

		Children.Add(_dim);
		Children.Add(_frame);

		var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
		escape.Invoked += (_, e) =>
		{
			if (_state.IsOpen)
			{
				e.Handled = true;
				Close();
			}
		};
		KeyboardAccelerators.Add(escape);
		KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

		SizeChanged += (_, _) =>
		{
			Resize();
			KeepCaptionOff();
		};
		Loaded += (_, _) => Current = this;
		Unloaded += (_, _) =>
		{
			Close();
			if (Current == this)
			{
				Current = null;
			}
		};
	}

	/// <summary>Whether the window is showing a trailer (tests and the harness).</summary>
	public bool IsOpen => _state.IsOpen;

	/// <summary>
	/// Plays the YouTube video <paramref name="videoId"/> in the theatre; <paramref name="returnTo"/> gets the keyboard back on
	/// close. False when it cannot (the player is open, the window is not up): the caller then does nothing.
	/// </summary>
	public bool Open(string videoId, string? title, Control? returnTo, FocusState how)
	{
		var playerOpen = PlayerOpen();
		if (!_state.TryOpen(title, returnTo, playerOpen))
		{
			Log()?.LogInformation("[trailer] not opened: the player is open");
			return false;
		}

		var generation = ++_generation;
		_embedBase = EmbedBase();
		var embed = Trailers.EmbedUri(videoId, _embedBase);
		Log()?.LogInformation("[trailer] theatre opened (embed {Host}, {Theme} frame)", embed.Host, ThemeResources.Find(TrailerFrame.TemplateKey) is null ? "shared" : "the theme's");

		_dim.Background = DimBrush();
		_frame.ApplyTheme();
		_frame.Title = _state.Title;
		AutomationProperties.SetName(_frame, _state.AutomationName);
		AutomationProperties.SetName(this, _state.AutomationName);
		WatchPlayer();

		CloseWeb();
		_web = new WebView2 { DefaultBackgroundColor = Color.FromArgb(255, 0, 0, 0) };
		AutomationProperties.SetName(_web, _state.AutomationName);
		_frame.Content = _web;

		Visibility = Visibility.Visible;
		Contain();
		CaptionOff();
		_frame.ApplyTemplate();
		UpdateLayout();
		Resize();
		Appear();
		// the keyboard moves in: on Close (Esc closes from anywhere inside); once laid out, and again after the layout pass the
		// new web view causes (a focus asked for before the template's parts are in the tree does not take)
		var focusHow = how is FocusState.Unfocused or FocusState.Pointer ? FocusState.Programmatic : how;
		void MoveIn()
		{
			if (generation == _generation && _state.IsOpen && !WithinTheater())
			{
				var took = _frame.FocusClose(focusHow) || (_web?.Focus(FocusState.Programmatic) ?? false);
				Log()?.LogInformation("[trailer] keyboard into the window: {Took}, now on {Focused}", took, FocusedName());
			}
		}

		MoveIn();
		DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, MoveIn);

		_ = LoadAsync(_web, embed, generation, videoId);
		return true;
	}

	/// <summary>Closes the window: the web view is closed (the sound stops at once) and the keyboard goes back.</summary>
	public void Close()
	{
		if (!_state.IsOpen)
		{
			return;
		}

		_generation++;
		// first: closing the web view and collapsing move the keyboard out, on its way back to the Trailer button
		Release();
		CaptionBack();
		CloseWeb();
		Visibility = Visibility.Collapsed;
		Log()?.LogInformation("[trailer] theatre closed");
		_state.Close();
	}

	/// <summary>
	/// Nixie 1.4.0 (bug hunt M1): a key held back over the theatre (<see cref="Trailers.KeyOverTheater"/>): logged with where the
	/// keyboard is (the harness checks it stayed inside), once per press (not for a held key's repeats). The key is not named.
	/// </summary>
	public void HeldBack(bool repeat)
	{
		if (!repeat)
		{
			Log()?.LogInformation("[trailer] a key over the theatre held back; the keyboard on {Focused} ({Inside})", FocusedName(), WithinTheater() ? "inside" : "OUTSIDE");
		}
	}

	private UIElement? _contained;

	/// <summary>The top bar's own click-through areas while the theatre covers them (put back on close), null when closed.</summary>
	private Windows.Graphics.RectInt32[]? _barPassthrough;

	private const Microsoft.UI.Input.NonClientRegionKind Passthrough = Microsoft.UI.Input.NonClientRegionKind.Passthrough;

	/// <summary>
	/// Nixie 1.4.0 (bug hunt m8): the top bar is the window's title bar (TitleBarControl), so its empty parts are the window's
	/// caption, and the theatre is drawn over them: a click on the dim's top band dragged the window, a double click there
	/// maximized it, instead of closing the theatre. While it is open the whole window but Windows' own buttons (minimize,
	/// maximize, close: they stay) is click-through, so the band is the dim's (a click there closes it, as anywhere on the dim).
	/// The bar's own click-through areas are put back on close.
	/// </summary>
	private void CaptionOff()
	{
		try
		{
			var source = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(App.MainWindow.AppWindow.Id);
			_barPassthrough ??= source.GetRegionRects(Passthrough);
			source.SetRegionRects(Passthrough, [WholeWindowButCaptionButtons()]);
			Log()?.LogInformation("[trailer] the top bar is not the window's caption while the theatre is open");
		}
		catch (Exception ex)
		{
			Log()?.LogWarning(ex, "[trailer] the window's caption could not be set aside");
		}
	}

	/// <summary>The window's client area but the caption buttons' corner, in physical pixels.</summary>
	private static Windows.Graphics.RectInt32 WholeWindowButCaptionButtons()
	{
		var window = App.MainWindow.AppWindow;
		var size = window.ClientSize;
		return new Windows.Graphics.RectInt32(0, 0, Math.Max(0, size.Width - window.TitleBar.RightInset), size.Height);
	}

	/// <summary>
	/// The window was resized while open: the top bar sets its click-through areas again, for the new size (TitleBarControl,
	/// after layout); those are the ones put back on close, and the whole window is click-through again.
	/// </summary>
	private void KeepCaptionOff()
	{
		if (!_state.IsOpen || _barPassthrough is null)
		{
			return;
		}

		void Again()
		{
			if (!_state.IsOpen)
			{
				return;
			}

			try
			{
				var source = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(App.MainWindow.AppWindow.Id);
				var whole = WholeWindowButCaptionButtons();
				var now = source.GetRegionRects(Passthrough);
				if (now is not [var only] || !only.Equals(whole))
				{
					if (now.Length > 0 && !now.Any(r => r.X == 0 && r.Y == 0 && r.Height > whole.Height / 2))
					{
						_barPassthrough = now;
					}

					source.SetRegionRects(Passthrough, [whole]);
				}
			}
			catch (Exception)
			{
			}
		}

		// after the bar's own update (queued at the same priority when the window's size changed), and once more after that
		DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
		{
			Again();
			DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Again);
		});
	}

	private void CaptionBack()
	{
		if (_barPassthrough is not { } saved)
		{
			return;
		}

		_barPassthrough = null;
		try
		{
			Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(App.MainWindow.AppWindow.Id).SetRegionRects(Passthrough, saved);
			Log()?.LogInformation("[trailer] the top bar is the window's caption again ({Count} click-through area(s))", saved.Length);
		}
		catch (Exception ex)
		{
			Log()?.LogWarning(ex, "[trailer] the window's caption could not be put back");
		}
	}

	/// <summary>
	/// Nixie 1.4.0 (bug hunt M1): while it is open, the keyboard stays inside: a move to anything under the dim (the page's own
	/// focus calls, a refresh's place, the top bar) is refused. Tab and Shift+Tab already cycle inside (TabFocusNavigation).
	/// </summary>
	private void Contain()
	{
		if (_contained is not null || XamlRoot?.Content is not UIElement root)
		{
			return;
		}

		_contained = root;
		root.GettingFocus += OnRootGettingFocus;
	}

	private void Release()
	{
		if (_contained is { } root)
		{
			root.GettingFocus -= OnRootGettingFocus;
			_contained = null;
		}
	}

	private void OnRootGettingFocus(UIElement sender, GettingFocusEventArgs args)
	{
		if (!_state.IsOpen || args.NewFocusedElement is not DependencyObject target || IsInside(target))
		{
			return;
		}

		// the first move in comes from outside (the Trailer button): only moves out are refused
		if (!WithinTheater())
		{
			return;
		}

		var refused = args.TryCancel();
		Log()?.LogInformation("[trailer] the keyboard kept inside (it was going to {Target}): {Refused}", (target as FrameworkElement)?.GetType().Name ?? target.GetType().Name, refused);
	}

	private bool IsInside(DependencyObject element)
	{
		for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (ReferenceEquals(node, this))
			{
				return true;
			}
		}

		return false;
	}

	private void CloseWeb()
	{
		if (_web is not { } web)
		{
			return;
		}

		_web = null;
		_frame.Content = null;
		try
		{
			web.Close();
		}
		catch (Exception ex)
		{
			Log()?.LogWarning(ex, "[trailer] the web view did not close cleanly");
		}
	}

	private async Task LoadAsync(WebView2 web, Uri embed, long generation, string videoId)
	{
		try
		{
			var folder = Path.Combine(App.GetService<KnownFolders>().ApplicationData, "WebView2");
			Directory.CreateDirectory(folder);
			var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, folder, new CoreWebView2EnvironmentOptions());
			await web.EnsureCoreWebView2Async(environment);
			if (generation != _generation || web != _web)
			{
				return; // closed meanwhile (Close closed the web view)
			}

			var core = web.CoreWebView2;
			Lock(core.Settings);
			core.NavigationStarting += OnNavigationStarting;
			// the web view takes the keyboard when its page loads: give it back to Close (Tab reaches the video), once
			var first = true;
			core.NavigationCompleted += (_, _) =>
			{
				if (first && generation == _generation && _state.IsOpen)
				{
					first = false;
					Log()?.LogInformation("[trailer] page loaded; the keyboard was on {Focused}", FocusedName());
					_frame.FocusClose(FocusState.Programmatic);
					DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
					{
						if (generation == _generation && _state.IsOpen)
						{
							var took = _frame.FocusClose(FocusState.Programmatic);
							Log()?.LogInformation("[trailer] keyboard to Close after the page loaded: {Took}, now on {Focused}", took, FocusedName());
						}
					});
				}
			};
			core.NewWindowRequested += OnNewWindowRequested;
			core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
			core.DownloadStarting += (_, e) => e.Cancel = true;
			var request = environment.CreateWebResourceRequest(embed.AbsoluteUri, "GET", null, $"Referer: {Referer}");
			core.NavigateWithWebResourceRequest(request);
		}
		catch (Exception ex)
		{
			// no WebView2 runtime, or it failed to start: the trailer opens in the browser instead, as before 1.3.1
			Log()?.LogWarning(ex, "[trailer] the theatre's web view could not start; opening the trailer in the browser");
			if (generation == _generation)
			{
				Close();
				await Launcher.LaunchUriAsync(new Uri($"https://www.youtube.com/watch?v={videoId}"));
			}
		}
	}

	/// <summary>The web view's settings: nothing but the page (no DevTools in a release, no bridge to the app, no menus).</summary>
	private static void Lock(CoreWebView2Settings settings)
	{
		settings.AreDevToolsEnabled = !AppVersion.IsReleasePackage;
		settings.AreHostObjectsAllowed = false;
		settings.IsWebMessageEnabled = false;
		settings.AreDefaultContextMenusEnabled = false;
		settings.AreDefaultScriptDialogsEnabled = false;
		settings.IsStatusBarEnabled = false;
		settings.IsZoomControlEnabled = false;
		settings.AreBrowserAcceleratorKeysEnabled = false;
		settings.IsPasswordAutosaveEnabled = false;
		settings.IsGeneralAutofillEnabled = false;
		settings.IsBuiltInErrorPageEnabled = true;
	}

	private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs e)
	{
		Uri.TryCreate(e.Uri, UriKind.Absolute, out var target);
		switch (Trailers.Navigation(target, _embedBase))
		{
			case TrailerNavigation.Allow:
				return;
			case TrailerNavigation.OpenInBrowser:
				e.Cancel = true;
				Log()?.LogInformation("[trailer] a YouTube link opens in the browser");
				_ = Launcher.LaunchUriAsync(target!);
				return;
			default:
				e.Cancel = true;
				Log()?.LogInformation("[trailer] navigation to {Host} blocked", target?.Host ?? "(not a web address)");
				return;
		}
	}

	private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs e)
	{
		e.Handled = true;
		if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) && Trailers.OpensInBrowser(target))
		{
			Log()?.LogInformation("[trailer] a new window opens in the browser");
			_ = Launcher.LaunchUriAsync(target);
		}
	}

	private void Resize()
	{
		if (!_state.IsOpen || ActualWidth <= 0 || ActualHeight <= 0)
		{
			return;
		}

		// the frame's own parts (title, marquee, posts): its height without the video
		_frame.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
		var chrome = Math.Max(0, _frame.DesiredSize.Height - _frame.VideoHeight);
		var (w, h) = Trailers.VideoSize(ActualWidth, ActualHeight, chrome);
		_frame.VideoWidth = w;
		_frame.VideoHeight = h;
	}

	/// <summary>A short fade and rise in when motion is allowed; at once otherwise.</summary>
	private void Appear()
	{
		if (!SafeMotion.Allowed)
		{
			_frame.Opacity = 1;
			_dim.Opacity = 1;
			_frame.RenderTransform = null;
			return;
		}

		var lift = new TranslateTransform { Y = 14 };
		_frame.RenderTransform = lift;
		var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
		var story = new Storyboard();
		void Add(DependencyObject target, string property, double from, double to, int ms)
		{
			var animation = new DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromMilliseconds(ms), EasingFunction = ease };
			Storyboard.SetTarget(animation, target);
			Storyboard.SetTargetProperty(animation, property);
			story.Children.Add(animation);
		}

		Add(_dim, "Opacity", 0, 1, 160);
		Add(_frame, "Opacity", 0, 1, 220);
		Add(lift, "Y", 14, 0, 260);
		story.Begin();
	}

	private void WatchPlayer()
	{
		if (_chrome is not null)
		{
			return;
		}

		try
		{
			_chrome = App.GetService<PlayerChrome>();
			_chrome.PropertyChanged += OnChromeChanged;
		}
		catch (Exception)
		{
			_chrome = null;
		}
	}

	private void OnChromeChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(PlayerChrome.IsPlayerOpen) && _chrome?.IsPlayerOpen == true)
		{
			DispatcherQueue.TryEnqueue(Close);
		}
	}

	private static bool PlayerOpen()
	{
		try { return App.GetService<PlayerChrome>().IsPlayerOpen; }
		catch (Exception) { return false; }
	}

	private void Refocus(object target)
	{
		if (target is not Control control)
		{
			return;
		}

		// now, and again once the collapse has settled (WinUI moves the keyboard off a collapsed element after this)
		void Back()
		{
			if (!_state.IsOpen && control.XamlRoot is not null && control.Visibility == Visibility.Visible)
			{
				control.Focus(FocusState.Programmatic);
			}
		}

		Back();
		DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
		{
			Back();
			Log()?.LogInformation("[trailer] keyboard back: now on {Focused}", FocusedName());
		});
	}

	private string FocusedName() =>
		XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is FrameworkElement f ? $"{f.GetType().Name} {AutomationProperties.GetName(f)}" : "nothing";

	/// <summary>Whether the keyboard is already inside the theatre (on Close, or in the video).</summary>
	private bool WithinTheater() =>
		XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused && IsInside(focused);

	/// <summary>The dim over the page: a deep dusk at 72 % (the frame's light edge reads on it); the window colour in high contrast.</summary>
	private static Brush DimBrush()
	{
		try
		{
			if (GlassMode.IsHighContrast && Application.Current.Resources.TryGetValue("SystemColorWindowColor", out var window) && window is Color c)
			{
				return new SolidColorBrush(Color.FromArgb(0xE6, c.R, c.G, c.B));
			}
		}
		catch (Exception)
		{
		}

		return new SolidColorBrush(Color.FromArgb(0xB8, 0x0C, 0x03, 0x07));
	}

	/// <summary>The embed base of this run (the harness's fake page only in a dev build under the harness: <see cref="Trailers.EmbedBase"/>).</summary>
	private static Uri EmbedBase() => Trailers.EmbedBase(
		AppIdentity.ReadEnvironment(Trailers.EmbedBaseVariable),
		harnessOn: Environment.GetEnvironmentVariable(GlassActivity.AlwaysActiveVariable) == "1",
		releasePackage: AppVersion.IsReleasePackage);

	private ILogger? Log()
	{
		try { return _log ??= App.GetService<ILogger<TrailerTheater>>(); }
		catch (Exception) { return null; }
	}
}
