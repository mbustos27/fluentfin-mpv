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
			(s_log ?? App.GetService<ILogger<App>>()).LogWarning("[first-chance]{Closing} {Type} (0x{HResult:X8}): {Message}\n{Stack}", closing ? " (closing)" : "",
				exception.GetType().FullName, exception.HResult, exception.Message, stack);
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
