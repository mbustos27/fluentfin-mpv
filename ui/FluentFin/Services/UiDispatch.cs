using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace FluentFin.Services;

/// <summary>
/// Nixie 1.4.0 (bug hunt M3: the app ended with 0xc000027b, a stowed exception, in CoreMessagingXP, and nothing in its log; that
/// one was a layout cycle, CollectionPage.FocusCard): work queued on the UI thread (DispatcherQueue.TryEnqueue) whose exception
/// cannot end the app. An exception thrown in a queued callback never reaches App.UnhandledException, which logs XAML's own and
/// lets the app go on: the dispatcher fails fast the same way. Queued through here, the exception is logged with its stack
/// ("[ui] ... failed") and the app goes on; a call that cannot be queued (the window closing) does nothing.
/// </summary>
public static class UiDispatch
{
	/// <summary>Queues <paramref name="action"/> on <paramref name="queue"/>; false when it could not be queued.</summary>
	/// <param name="what">What it does, for the log ("the Collection page's arrival focus").</param>
	public static bool Enqueue(DispatcherQueue? queue, string what, Action action, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal)
	{
		try
		{
			return queue?.TryEnqueue(priority, () => Run(what, action)) == true;
		}
		catch (COMException)
		{
			// the queue is shutting down (the window closed)
			return false;
		}
	}

	/// <summary>
	/// Nixie 1.7.3 fix round (the close crash, 0xc000027b with a stowed E_UNEXPECTED): a <see cref="DispatcherQueueTimer"/>'s
	/// Tick that does nothing once the app is closing (it stops its timer) and never throws (<see cref="Run"/>). A tick that
	/// fired in the window's teardown read a XAML property, which threw out of the dispatcher's callback and ended the app,
	/// 3 to 9 s after a close from the player page.
	/// </summary>
	public static Windows.Foundation.TypedEventHandler<DispatcherQueueTimer, object> Tick(string what, Action tick) => (timer, _) =>
	{
		if (App.IsClosing)
		{
			Stop(timer);
			return;
		}

		Run(what, tick);
	};

	/// <summary>
	/// Nixie 1.7.5 (fx3; 1.7.3's close crash, for what a frame drives rather than a timer): a handler of
	/// <c>CompositionTarget.Rendering</c> or of a storyboard's <c>Completed</c> that does nothing once the app is closing and
	/// never throws (<see cref="Run"/>). A fade of Home's heroes that ends in the window's teardown set XAML there, which
	/// answers E_UNEXPECTED out of the compositor's callback.
	/// </summary>
	public static EventHandler<object> Frame(string what, Action frame) => (_, _) =>
	{
		if (!App.IsClosing)
		{
			Run(what, frame);
		}
	};

	/// <summary>Nixie 1.7.5 (fx3): <see cref="Frame"/> for a routed event a frame raises (a picture's <c>ImageOpened</c>).</summary>
	public static Microsoft.UI.Xaml.RoutedEventHandler Routed(string what, Action handler) => (_, _) =>
	{
		if (!App.IsClosing)
		{
			Run(what, handler);
		}
	};

	private static void Stop(DispatcherQueueTimer timer)
	{
		try
		{
			timer.Stop();
		}
		catch (Exception)
		{
			// the queue is shutting down
		}
	}

	/// <summary>Runs <paramref name="action"/> now; an exception is logged with its stack, and the app goes on.</summary>
	public static void Run(string what, Action action)
	{
		try
		{
			action();
		}
		catch (Exception ex)
		{
			Failed(what, ex);
		}
	}

	/// <summary>How many queued callbacks failed this session (the harness reads the log lines; tests read this).</summary>
	public static int Failures { get; private set; }

	internal static void Failed(string what, Exception ex)
	{
		Failures++;
		try
		{
			App.GetService<ILogger<App>>().LogError(ex, "[ui] {What} failed ({Type}: {Message}); the app goes on", what, ex.GetType().Name, ex.Message);
		}
		catch (Exception)
		{
			// no logger (the app is closing): nothing more to do
			Debug.WriteLine($"[ui] {what} failed: {ex}");
		}
	}
}

/// <summary>
/// Nixie 1.4.0 (bug hunt M3): the trail a crash leaves. A native failure (a stowed exception) ends the app before any managed
/// handler runs, so its last log line said nothing about it. Each exception thrown on the UI thread in the app's own code is
/// logged as it is thrown ("[first-chance]", with the stack at that moment), once per kind and place, at most
/// <see cref="Most"/> a session: the next crash's log ends with what threw.
/// </summary>
public static class FirstChanceTrail
{
	/// <summary>The most lines a session writes.</summary>
	public const int Most = 40;

	private static int _uiThread = -1;
	private static int _written;
	private static readonly HashSet<string> s_seen = [];

	// 1.7.3 fix round (the NoMpv close crash): the logger, taken at the start. App.GetService goes through Application.Current,
	// which the close's teardown no longer answers: what threw then (the crash's cause) was never logged
	private static ILogger? s_log;

	/// <summary>The most lines written once the app is closing (its own budget: the session's may be spent).</summary>
	public const int MostAtTheClose = 20;

	private static int _writtenAtTheClose;

	[ThreadStatic]
	private static bool t_inside;

	/// <summary>Follows the exceptions of the calling thread (the UI thread), from now on.</summary>
	public static void Start()
	{
		if (_uiThread != -1)
		{
			return;
		}

		_uiThread = Environment.CurrentManagedThreadId;
		try
		{
			s_log = App.GetService<ILogger<App>>();
		}
		catch (Exception)
		{
			// no logger yet: each line asks for it (as before)
		}

		AppDomain.CurrentDomain.FirstChanceException += (_, e) => OnThrown(e.Exception);
	}

	/// <summary>
	/// Whether an exception is worth a line: thrown on the UI thread, from the app's own code (a frame of FluentFin's on the
	/// stack), and not a cancellation (a page left while it loaded).
	/// </summary>
	internal static bool Worth(Exception exception, bool onUiThread, string stack) =>
		onUiThread && exception is not OperationCanceledException && stack.Contains("FluentFin.", StringComparison.Ordinal);

	private static void OnThrown(Exception exception)
	{
		// 1.7.3 fix round: once the app is closing, every one (a crash there ends the log), within a budget of its own
		var closing = App.IsClosing;
		if (t_inside || Environment.CurrentManagedThreadId != _uiThread || (closing ? _writtenAtTheClose >= MostAtTheClose : _written >= Most))
		{
			return;
		}

		t_inside = true;
		try
		{
			var stack = new StackTrace(2, false).ToString();
			// at the close, the framework's own too: the teardown runs few frames of the app's
			if (closing ? exception is OperationCanceledException : !Worth(exception, onUiThread: true, stack))
			{
				return;
			}

			var at = stack.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("FluentFin.", StringComparison.Ordinal)) ?? "";
			if (!s_seen.Add($"{exception.GetType().FullName}|{exception.HResult}|{at}") && !closing)
			{
				return;
			}

			_ = closing ? _writtenAtTheClose++ : _written++;
			// 1.7.5 (fx3): with the last things done in the window (UiTrail), so the line says what led to it
			(s_log ?? App.GetService<ILogger<App>>()).LogWarning("[first-chance]{Closing} {Type} (0x{HResult:X8}): {Message}\n{Stack}\nlast in the window: {Trail}", closing ? " (closing)" : "",
				exception.GetType().FullName, exception.HResult, exception.Message, stack, UiTrail.Last());
		}
		catch (Exception)
		{
			// never let the trail itself throw
		}
		finally
		{
			t_inside = false;
		}
	}
}

/// <summary>
/// Nixie 1.7.5 (fx3; 1.7.3's Drive-in library crash: 0xc000027b, a stowed E_UNEXPECTED in XAML, and no line in the log): the
/// last things done in the window (the keys that move around, the wheel, presses, the focus's moves, the pages shown), kept
/// in memory and said with
/// every line of the crash trail (<see cref="FirstChanceTrail"/>, <see cref="CrashTrail"/>). Kinds only: a key's name, an
/// element's type and x:Name, a page's type; never a title, a character typed (only "typing", with no count), or anything
/// the server named.
/// </summary>
public static class UiTrail
{
	/// <summary>How many it keeps.</summary>
	public const int Kept = 8;

	private static readonly object s_lock = new();
	private static readonly List<(long At, string What, int Times)> s_last = [];

	/// <summary>What a key press in a text box, a password box or a search box is noted as: nothing more, never a count.</summary>
	public const string Typing = "typing";

	/// <summary>
	/// Notes <paramref name="what"/> now; the same as the last one counts it again (a wheel burst is one entry). Typing is never
	/// counted: a count would say how long a password is.
	/// </summary>
	public static void Note(string what)
	{
		lock (s_lock)
		{
			var now = Environment.TickCount64;
			if (s_last.Count > 0 && s_last[^1].What == what)
			{
				s_last[^1] = (now, what, what == Typing ? 1 : s_last[^1].Times + 1);
				return;
			}

			s_last.Add((now, what, 1));
			if (s_last.Count > Kept)
			{
				s_last.RemoveAt(0);
			}
		}
	}

	/// <summary>The last ones, the newest first, each with how long ago ("wheel x12 0.3 s ago; pressed PageDown 1.2 s ago"); "nothing yet".</summary>
	public static string Last()
	{
		lock (s_lock)
		{
			if (s_last.Count == 0)
			{
				return "nothing yet";
			}

			var now = Environment.TickCount64;
			return string.Join("; ", Enumerable.Reverse(s_last).Select(e => string.Create(System.Globalization.CultureInfo.InvariantCulture,
				$"{e.What}{(e.Times > 1 ? $" x{e.Times}" : "")} {(now - e.At) / 1000.0:0.0} s ago")));
		}
	}

	internal static void Clear()
	{
		lock (s_lock)
		{
			s_last.Clear();
		}
	}

	/// <summary>Follows the window's input from <paramref name="root"/> (handled events too) and the focus's moves; once.</summary>
	public static void Attach(Microsoft.UI.Xaml.UIElement root)
	{
		// 1.7.5 review (privacy): a key by name only when it moves around (KeyName); in a text, password or search box only
		// "typing"; never a character key, so no password, address, name or search ever reaches the log
		root.AddHandler(Microsoft.UI.Xaml.UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler((_, e) =>
		{
			if (KeyName(e.Key, InTextEntry(e.OriginalSource as Microsoft.UI.Xaml.DependencyObject)) is { } key)
			{
				Note(key);
			}
		}), true);
		root.AddHandler(Microsoft.UI.Xaml.UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) => Note($"press on {Describe(e.OriginalSource)}")), true);
		// the wheel by name only: the trail asks nothing more of the input stack (no GetCurrentPoint) on the busiest event
		root.AddHandler(Microsoft.UI.Xaml.UIElement.PointerWheelChangedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => Note("wheel")), true);
		Microsoft.UI.Xaml.Input.FocusManager.GotFocus += (_, e) => Note($"focus on {Describe(e.NewFocusedElement)}");
	}

	/// <summary>
	/// 1.7.5 review (privacy): how a key press is noted, or null for none. In a text entry (<paramref name="inTextEntry"/>)
	/// only <see cref="Typing"/>, whatever the key. Elsewhere only the keys that move around, by name ("pressed Enter"): the
	/// arrows, Tab, Enter, Esc, Back, Home, End, the page keys, the menu key and the gamepad's buttons. A character key (a
	/// letter, a digit, the numpad, a symbol, Space, the IME's) is never noted.
	/// </summary>
	internal static string? KeyName(Windows.System.VirtualKey key, bool inTextEntry)
	{
		if (inTextEntry)
		{
			return Typing;
		}

		return Navigation(key) ? $"pressed {key}" : null;

		static bool Navigation(Windows.System.VirtualKey key) => key is Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down
			or Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right or Windows.System.VirtualKey.Tab or Windows.System.VirtualKey.Enter
			or Windows.System.VirtualKey.Escape or Windows.System.VirtualKey.Back or Windows.System.VirtualKey.GoBack or Windows.System.VirtualKey.GoForward
			or Windows.System.VirtualKey.Home or Windows.System.VirtualKey.End or Windows.System.VirtualKey.PageUp or Windows.System.VirtualKey.PageDown
			or Windows.System.VirtualKey.Application
			|| (key >= Windows.System.VirtualKey.GamepadA && key <= Windows.System.VirtualKey.GamepadRightThumbstickLeft);
	}

	/// <summary>Whether <paramref name="element"/> is in a text box, a password box, a search box or a rich edit box.</summary>
	internal static bool InTextEntry(Microsoft.UI.Xaml.DependencyObject? element)
	{
		for (var node = element; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
		{
			if (node is Microsoft.UI.Xaml.Controls.TextBox or Microsoft.UI.Xaml.Controls.PasswordBox or Microsoft.UI.Xaml.Controls.AutoSuggestBox
				or Microsoft.UI.Xaml.Controls.RichEditBox or Microsoft.UI.Xaml.Controls.NumberBox)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>A page shown (its type's name).</summary>
	public static void NotePage(Type? page) => Note($"page {page?.Name ?? "none"}");

	/// <summary>An element as the trail says it: its type, and its x:Name when it has one (never its text).</summary>
	internal static string Describe(object? element) => element switch
	{
		null => "nothing",
		Microsoft.UI.Xaml.FrameworkElement { Name.Length: > 0 } named => $"{named.GetType().Name} {named.Name}",
		_ => element.GetType().Name,
	};
}

/// <summary>
/// Nixie 1.7.5 (fx3): what reaches the app's last handlers is logged in full where the first-chance trail misses it: XAML's
/// <c>Application.UnhandledException</c> (its message carries the stowed error's text, which the exception's own often
/// lacks) and <c>CoreApplication.UnhandledErrorDetected</c> (an error reported as unhandled, such as an async callback's),
/// each with its type, HRESULT, stack and the last things done in the window (<see cref="UiTrail"/>). XAML's stays handled
/// as before. An unhandled error is taken out with Propagate, which marks it handled, and logged: the app goes on, as it does
/// after XAML's (1.7.5 review: a rethrow after Propagate did not end the app either).
/// </summary>
public static class CrashTrail
{
	/// <summary>The line: where it came from, the exception's type, HRESULT and message, XAML's message when it differs, its stack and the trail.</summary>
	internal static string Describe(string where, Exception? exception, string? xamlMessage)
	{
		var text = new System.Text.StringBuilder();
		text.Append("[crash-trail] ").Append(where).Append(": ");
		if (exception is null)
		{
			text.Append("no exception");
		}
		else
		{
			text.Append(exception.GetType().FullName).Append(" (0x").Append(exception.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)).Append("): ")
				.Append(exception.Message);
		}

		if (!string.IsNullOrWhiteSpace(xamlMessage) && xamlMessage != exception?.Message)
		{
			text.Append("\nXAML says: ").Append(xamlMessage);
		}

		text.Append("\nstack: ").Append(exception?.StackTrace is { Length: > 0 } stack ? stack : "(none: thrown outside managed code)");
		if (exception?.InnerException is { } inner)
		{
			text.Append("\ninner: ").Append(inner.GetType().FullName).Append(" (0x").Append(inner.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)).Append("): ").Append(inner.Message);
		}

		text.Append("\nlast in the window: ").Append(UiTrail.Last());
		return text.ToString();
	}

	/// <summary>Logs it as an error; never throws (the app may be on its way out).</summary>
	public static void Log(string where, Exception? exception, string? xamlMessage = null)
	{
		try
		{
			App.GetService<ILogger<App>>().LogError("{Line}", Describe(where, exception, xamlMessage));
		}
		catch (Exception)
		{
			Debug.WriteLine(Describe(where, exception, xamlMessage));
		}
	}

	/// <summary>
	/// Follows <c>CoreApplication.UnhandledErrorDetected</c> where the process has it (a desktop app may not): an error reported
	/// as unhandled is logged, and the app goes on. Says in the log whether it could listen.
	/// </summary>
	public static void ListenForUnhandledErrors(ILogger log)
	{
		try
		{
			Windows.ApplicationModel.Core.CoreApplication.UnhandledErrorDetected += OnUnhandledErrorDetected;
			log.LogInformation("[crash-trail] listening for unhandled errors (UnhandledErrorDetected)");
		}
		catch (Exception ex)
		{
			log.LogInformation("[crash-trail] UnhandledErrorDetected isn't available here ({Type}: {Message})", ex.GetType().Name, ex.Message);
		}
	}

	private static void OnUnhandledErrorDetected(object? sender, Windows.ApplicationModel.Core.UnhandledErrorDetectedEventArgs e)
	{
		try
		{
			e.UnhandledError.Propagate();
		}
		catch (Exception ex)
		{
			// Propagate has marked it handled: logged, the app goes on (as after XAML's UnhandledException)
			Log("an unhandled error (UnhandledErrorDetected)", ex);
		}
	}
}
