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
using System.Text;
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
/// no permissions, no downloads. Nixie 1.4.0 (YouTube's Error 153 on every trailer): its top page is Nixie's own wrapper
/// (<see cref="Trailers.WrapperHtml"/> at <see cref="Trailers.WrapperUri"/>, answered here through the resource filter: nothing
/// on disk, nothing from the network) holding the privacy-enhanced embed as a frame, so YouTube sees an embedding origin and
/// a Referer it accepts. It may load only that page and, in frames, the embed (<see cref="Trailers.Navigation"/>); a link the
/// user clicks in YouTube's player opens in the browser, nothing opens it by itself. The app reads the player's state from the
/// wrapper (<see cref="Trailers.ReadPoll"/>, by script: the page has no way to send the app anything) and, when YouTube
/// refuses the trailer or it doesn't start, shows Nixie's own message instead (<see cref="TrailerMessageView"/>).
/// </para>
/// </summary>
public sealed partial class TrailerTheater : Grid
{
	/// <summary>The window's theatre (MainWindow's), null before the window is up.</summary>
	public static TrailerTheater? Current { get; private set; }

	private readonly Border _dim;
	private readonly TrailerFrame _frame;
	private readonly TrailerWindowState _state;
	private WebView2? _web;
	private TrailerMessageView? _message;

	/// <summary>
	/// Nixie 1.4.0 (review): the user moved the keyboard inside the theatre since it opened (Tab into the video, a click in
	/// it): the page finishing its load then never takes it back to Close (a Space meant for the player closed the theatre).
	/// </summary>
	private bool _keyboardMoved;

	/// <summary>The theatre's own focus calls are under way (they are not the user's moves).</summary>
	private bool _placing;

	/// <summary>
	/// Nixie 1.4.0 (review): a held Enter that closed the theatre on its Close must not reopen it from Trailer
	/// (<see cref="WindowShortcuts"/> asks it about each key).
	/// </summary>
	public static TheaterRepeatGuard Repeats { get; } = new();
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
		if (!Trailers.IsVideoId(videoId) || !Begin(title, returnTo))
		{
			return false;
		}

		var generation = _generation;
		_embedBase = EmbedBase();
		var embed = Trailers.EmbedUri(videoId, _embedBase);
		Log()?.LogInformation("[trailer] theatre opened (embed {Host} in Nixie's page {Page}, {Theme} frame)", embed.Host, Trailers.WrapperHost, FrameKind());

		ClearContent();
		_web = new WebView2 { DefaultBackgroundColor = Color.FromArgb(255, 0, 0, 0) };
		AutomationProperties.SetName(_web, _state.AutomationName);
		_frame.Content = _web;
		Show(generation, how);
		_ = LoadAsync(_web, generation, videoId);
		return true;
	}

	/// <summary>
	/// Nixie 1.4.0: opens the theatre with Nixie's message instead of a trailer (a trailer on another site: "This trailer can't
	/// play in Nixie", with Open in browser). False when it cannot (the player is open, the window is not up).
	/// </summary>
	public bool OpenMessage(TrailerMessage message, string? title, Control? returnTo, FocusState how)
	{
		if (!Begin(title, returnTo))
		{
			return false;
		}

		var generation = _generation;
		Log()?.LogInformation("[trailer] theatre opened (Nixie's message, {Theme} frame)", FrameKind());
		ShowMessage(message, "a trailer on another site");
		Show(generation, how);
		return true;
	}

	/// <summary>Opens the window's state for a trailer (false while the player is open); a new generation either way it opens.</summary>
	private bool Begin(string? title, Control? returnTo)
	{
		if (!_state.TryOpen(title, returnTo, PlayerOpen()))
		{
			Log()?.LogInformation("[trailer] not opened: the player is open");
			return false;
		}

		++_generation;
		_keyboardMoved = false;
		return true;
	}

	private static string FrameKind() => ThemeResources.Find(TrailerFrame.TemplateKey) is null ? "shared" : "the theme's";

	/// <summary>Shows the window (frame, dim, the keyboard moving in) around whatever its content is now.</summary>
	private void Show(long generation, FocusState how)
	{
		_dim.Background = DimBrush();
		_frame.ApplyTheme();
		_frame.Title = _state.Title;
		AutomationProperties.SetName(_frame, _state.AutomationName);
		AutomationProperties.SetName(this, _state.AutomationName);
		WatchPlayer();

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
				var took = Place(() => _message?.FocusClose(focusHow) ?? (_frame.FocusClose(focusHow) || (_web?.Focus(FocusState.Programmatic) ?? false)));
				Log()?.LogInformation("[trailer] keyboard into the window: {Took}, now on {Focused}", took, FocusedName());
			}
		}

		MoveIn();
		DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, MoveIn);
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
		ClearContent();
		Visibility = Visibility.Collapsed;
		Log()?.LogInformation("[trailer] theatre closed");
		Repeats.Arm();
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
		// 1.7.3 fix round (the close crash's class): the theater's Unloaded at the close closes it, after the window has gone
		// (its AppWindow is null then): nothing to put back
		if (App.IsClosing || App.MainWindow.AppWindow is null)
		{
			return;
		}

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
		if (!_state.IsOpen || args.NewFocusedElement is not DependencyObject target)
		{
			return;
		}

		if (IsInside(target))
		{
			// the user moved it inside (Tab, the arrows, a click): from now the theatre leaves the keyboard where the user put it
			if (!_placing && !_keyboardMoved && args.FocusState is FocusState.Keyboard or FocusState.Pointer && WithinTheater())
			{
				_keyboardMoved = true;
				Log()?.LogInformation("[trailer] the user moved the keyboard inside the theatre (to {Target})", (target as FrameworkElement)?.GetType().Name ?? target.GetType().Name);
			}

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

	/// <summary>Empties the screen: the web view closed (the sound stops at once), the message gone.</summary>
	private void ClearContent()
	{
		CloseWeb();
		if (_message is { } message)
		{
			_message = null;
			message.LinkRequested -= OnMessageLink;
			message.CloseRequested -= OnMessageClose;
			if (ReferenceEquals(_frame.Content, message))
			{
				_frame.Content = null;
			}
		}
	}

	private void CloseWeb()
	{
		if (_web is not { } web)
		{
			return;
		}

		_web = null;
		if (ReferenceEquals(_frame.Content, web))
		{
			_frame.Content = null;
		}

		try
		{
			web.Close();
		}
		catch (Exception ex)
		{
			Log()?.LogWarning(ex, "[trailer] the web view did not close cleanly");
		}
	}

	/// <summary>
	/// Nixie 1.4.0: Nixie's message in the screen's place, instead of the trailer (or of YouTube's error page): the web view
	/// closes, the keyboard goes to the message's Close (never to the button that opens the browser), Narrator reads it.
	/// </summary>
	private void ShowMessage(TrailerMessage message, string why)
	{
		var view = new TrailerMessageView(message);
		view.LinkRequested += OnMessageLink;
		view.CloseRequested += OnMessageClose;
		CloseWeb();
		if (_message is { } old)
		{
			old.LinkRequested -= OnMessageLink;
			old.CloseRequested -= OnMessageClose;
		}

		_message = view;
		_frame.Content = view;
		Log()?.LogInformation("[trailer] Nixie's message ({Why}): {Text} [{Link}]", why, message.AutomationText, message.LinkText);
		var generation = _generation;
		void Focus()
		{
			if (generation == _generation && _state.IsOpen && ReferenceEquals(_message, view))
			{
				var took = Place(() => view.FocusClose(FocusState.Programmatic));
				Log()?.LogInformation("[trailer] keyboard to the message's Close: {Took}, now on {Focused}", took, FocusedName());
			}
		}

		view.Loaded += (_, _) =>
		{
			Focus();
			view.Announce();
		};
		DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Focus);
	}

	private async void OnMessageLink(object? sender, EventArgs e)
	{
		if (sender is TrailerMessageView view)
		{
			await TrailerBrowser.Open(view.Message.Link, $"the user picked {view.Message.LinkText}", Log());
		}
	}

	private void OnMessageClose(object? sender, EventArgs e) => Close();

	private async Task LoadAsync(WebView2 web, long generation, string videoId)
	{
		try
		{
			var folder = Path.Combine(App.GetService<KnownFolders>().ApplicationData, "WebView2");
			Directory.CreateDirectory(folder);
			var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, folder, new CoreWebView2EnvironmentOptions
			{
				// the user's click on Trailer is the gesture: the embed starts at once (autoplay=1) instead of waiting for a
				// second click inside YouTube's player (the web view's own policy: no media with sound before a click in it)
				AdditionalBrowserArguments = Trailers.BrowserArguments,
			});
			// Nixie 1.4.0 (review): InPrivate, so YouTube's cookies and storage end with the web view (kept in the WebView2
			// folder they were a lasting identifier of this install); the folder keeps only the browser's own files
			var controller = environment.CreateCoreWebView2ControllerOptions();
			controller.IsInPrivateModeEnabled = true;
			await web.EnsureCoreWebView2Async(environment, controller);
			if (generation != _generation || web != _web)
			{
				return; // closed meanwhile (Close closed the web view)
			}

			var core = web.CoreWebView2;
			Log()?.LogInformation("[trailer] the web view is {Mode}", core.Profile.IsInPrivateModeEnabled ? "InPrivate (nothing kept)" : "NOT InPrivate");
			Lock(core.Settings);
			if (TrailerBrowser.HarnessOn())
			{
				core.IsMuted = true; // a harness run never makes a sound (its live YouTube check included)
			}

			// Nixie's own page, answered here: the one address of its host gets the wrapper, anything else there a 404
			var html = Trailers.WrapperHtml(videoId, _embedBase);
			var headers = Trailers.WrapperHeaders(_embedBase);
			core.AddWebResourceRequestedFilter(Trailers.WrapperFilter, CoreWebView2WebResourceContext.All);
			core.WebResourceRequested += (_, e) =>
			{
				Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var target);
				e.Response = Trailers.IsWrapper(target) && e.Request.Method == "GET"
					? environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)).AsRandomAccessStream(), 200, "OK", headers)
					: environment.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");
			};
			core.NavigationStarting += (_, e) => OnNavigationStarting(e, frame: false);
			core.FrameNavigationStarting += (_, e) => OnNavigationStarting(e, frame: true);
			// the player is watched (it plays, or Nixie's message says why not) once Nixie's page is up: its DOM, not its load,
			// which waits for YouTube's whole frame (live, up to 20 s while the trailer already played)
			var watching = false;
			void Watch(CoreWebView2 sender)
			{
				if (!watching && generation == _generation && _state.IsOpen)
				{
					watching = true;
					_ = WatchAsync(web, sender, generation, videoId);
				}
			}

			core.DOMContentLoaded += (sender, _) => Watch(sender);
			// the web view takes the keyboard when its page loads: give it back to Close (Tab reaches the video), once, unless the
			// user has moved it since the theatre opened (Nixie 1.4.0, review: the load can come 20 s in, and a Space meant for
			// the player then pressed Close)
			var first = true;
			core.NavigationCompleted += async (sender, e) =>
			{
				if (!first || generation != _generation || !_state.IsOpen)
				{
					return;
				}

				first = false;
				Watch(sender);
				var touched = false;
				try
				{
					touched = Trailers.ReadPoll(await sender.ExecuteScriptAsync("JSON.stringify(window.nixieTrailer || null)"))?.Touched == true;
				}
				catch (Exception)
				{
				}

				if (generation != _generation || !_state.IsOpen || _message is not null)
				{
					return;
				}

				if (_keyboardMoved || touched)
				{
					Log()?.LogInformation("[trailer] page loaded{Failed}; the keyboard stays where the user put it ({Focused})", e.IsSuccess ? "" : $" (failed: {e.WebErrorStatus})", FocusedName());
					return;
				}

				Log()?.LogInformation("[trailer] page loaded{Failed}; the keyboard was on {Focused}", e.IsSuccess ? "" : $" (failed: {e.WebErrorStatus})", FocusedName());
				Place(() => _frame.FocusClose(FocusState.Programmatic));
				DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
				{
					if (generation == _generation && _state.IsOpen && _message is null && !_keyboardMoved)
					{
						var took = Place(() => _frame.FocusClose(FocusState.Programmatic));
						Log()?.LogInformation("[trailer] keyboard to Close after the page loaded: {Took}, now on {Focused}", took, FocusedName());
					}
				});
			};
			core.NewWindowRequested += OnNewWindowRequested;
			core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
			core.DownloadStarting += (_, e) => e.Cancel = true;
			core.Navigate(Trailers.WrapperUri.AbsoluteUri);
		}
		catch (Exception ex)
		{
			// no WebView2 runtime, or it failed to start: Nixie's message, with Watch on YouTube (the browser only if picked)
			Log()?.LogWarning(ex, "[trailer] the theatre's web view could not start");
			if (generation == _generation && _state.IsOpen)
			{
				ShowMessage(Trailers.NoPlayer(videoId), "the web view did not start");
			}
		}
	}

	/// <summary>
	/// Nixie 1.4.0: watches the player through the wrapper (<see cref="Trailers.ReadPoll"/>, <see cref="Trailers.Judge"/>) while
	/// this trailer is on: each change of its state is logged; an error, or no sign of the player at all within
	/// <see cref="Trailers.QuietLimit"/> while the user did nothing with it, puts Nixie's message in its place.
	/// </summary>
	private async Task WatchAsync(WebView2 web, CoreWebView2 core, long generation, string videoId)
	{
		var since = System.Diagnostics.Stopwatch.StartNew();
		var started = false;
		TrailerPoll? last = null;
		while (generation == _generation && web == _web)
		{
			await Task.Delay(started ? 1000 : 300);
			if (generation != _generation || web != _web)
			{
				return;
			}

			TrailerPoll? poll = null;
			try
			{
				poll = Trailers.ReadPoll(await core.ExecuteScriptAsync("JSON.stringify(window.nixieTrailer || null)"));
			}
			catch (Exception)
			{
				// the page is between states (or the web view is closing): the next round, or the limit, decides
			}

			if (generation != _generation || web != _web)
			{
				return;
			}

			if (poll is not null && poll != last)
			{
				last = poll;
				Log()?.LogInformation("[trailer] the player: ready {Ready}, state {State}, error {Error}, touched {Touched} ({Ms} ms)", poll.Ready, poll.State, poll.Error, poll.Touched, since.ElapsedMilliseconds);
			}

			switch (Trailers.Judge(poll, since.Elapsed, started, _keyboardMoved))
			{
				case TrailerVerdict.Started when !started:
					started = true;
					Log()?.LogInformation("[trailer] the player started (state {State})", poll?.State);
					break;
				case TrailerVerdict.Refused:
					ShowMessage(Trailers.Refused(poll!.Error, videoId), $"YouTube refused it: error {poll.Error}");
					return;
				case TrailerVerdict.NotStarted:
					ShowMessage(Trailers.NotStarted(videoId), $"no sign of the player within {Trailers.QuietLimit.TotalSeconds:0} s");
					return;
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

	private void OnNavigationStarting(CoreWebView2NavigationStartingEventArgs e, bool frame)
	{
		Uri.TryCreate(e.Uri, UriKind.Absolute, out var target);
		switch (Trailers.Navigation(target, _embedBase, frame, e.IsUserInitiated))
		{
			case TrailerNavigation.Allow:
				return;
			case TrailerNavigation.OpenInBrowser:
				e.Cancel = true;
				_ = TrailerBrowser.Open(target, "a YouTube link the user clicked in the player", Log());
				return;
			default:
				e.Cancel = true;
				Log()?.LogInformation("[trailer] {Where} navigation to {Host} blocked{User}", frame ? "a frame's" : "the page's",
					target?.Host is { Length: > 0 } host ? host : target?.Scheme ?? "(not a web address)", e.IsUserInitiated ? " (clicked)" : "");
				return;
		}
	}

	private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs e)
	{
		e.Handled = true;
		Uri.TryCreate(e.Uri, UriKind.Absolute, out var target);
		if (Trailers.OpensInBrowser(target, e.IsUserInitiated))
		{
			_ = TrailerBrowser.Open(target, "a link the user clicked in the player", Log());
		}
		else
		{
			Log()?.LogInformation("[trailer] a new window refused ({Host}{User})", target?.Host ?? "not a web address", e.IsUserInitiated ? "" : ", not the user's click");
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

	/// <summary>Runs one of the theatre's own focus calls (not counted as the user's move).</summary>
	private bool Place(Func<bool> focus)
	{
		_placing = true;
		try { return focus(); }
		finally { _placing = false; }
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
