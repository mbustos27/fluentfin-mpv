using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FluentFin.Services;

/// <summary>
/// 1.7.5 (g4): the window's close in the log, step by step. One close of the basics gate's settings run took 25 s inside
/// MainWindow.Close() (App.xaml.cs: "[close] the window: closed after 24999 ms") with nothing logged between the services'
/// stop and the window's end, and the Application event log held nothing for it. While the close runs, each step it reaches is
/// logged (<see cref="Step"/>: the page shown, the window's Closed handlers done, its content unloaded), and every
/// <see cref="Every"/> a line says it is still closing and the last step reached, so the next slow close says where it waited.
/// The timer is a thread pool's (it only logs; nothing of the UI is touched from it).
/// </summary>
public sealed class CloseWatch : IDisposable
{
	/// <summary>How often a close still running says so.</summary>
	public static readonly TimeSpan Every = TimeSpan.FromSeconds(3);

	private readonly ILogger _log;
	private readonly Stopwatch _since;
	private readonly Timer _timer;
	private volatile string _step;
	private int _disposed;

	public CloseWatch(ILogger log, Stopwatch since, string firstStep, TimeSpan? every = null)
	{
		_log = log;
		_since = since;
		_step = firstStep;
		log.LogInformation("[close] {Step} after {Ms} ms", firstStep, since.ElapsedMilliseconds);
		var period = every ?? Every;
		_timer = new Timer(_ => StillClosing(), null, period, period);
	}

	/// <summary>The last step the close reached (logged as it is set).</summary>
	public string Step
	{
		get => _step;
		set
		{
			_step = value;
			_log.LogInformation("[close] {Step} after {Ms} ms", value, _since.ElapsedMilliseconds);
		}
	}

	/// <summary>The line a close still running writes.</summary>
	public static string StillClosingLine(long ms, string step) => $"[close] the window is still closing after {ms} ms; the last step: {step}";

	private void StillClosing()
	{
		if (Volatile.Read(ref _disposed) == 0)
		{
			_log.LogWarning("{Line}", StillClosingLine(_since.ElapsedMilliseconds, _step));
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) == 0)
		{
			_timer.Dispose();
		}
	}
}
