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
		if (t_inside || Environment.CurrentManagedThreadId != _uiThread || _written >= Most)
		{
			return;
		}

		t_inside = true;
		try
		{
			var stack = new StackTrace(2, false).ToString();
			if (!Worth(exception, onUiThread: true, stack))
			{
				return;
			}

			var at = stack.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("FluentFin.", StringComparison.Ordinal)) ?? "";
			if (!s_seen.Add($"{exception.GetType().FullName}|{exception.HResult}|{at}"))
			{
				return;
			}

			_written++;
			App.GetService<ILogger<App>>().LogWarning("[first-chance] {Type} (0x{HResult:X8}): {Message}\n{Stack}", exception.GetType().FullName, exception.HResult, exception.Message, stack);
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
