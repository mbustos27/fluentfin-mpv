// A stand-in for the WinUI dispatcher: one thread runs every continuation the view model posts, one at a time, as the
// app's UI thread does. It times each dispatch (view-model work on the UI thread) and calls AfterEach after every one,
// which is where the page would get a chance to render.
using System.Collections.Concurrent;
using System.Diagnostics;

sealed class UiThread : SynchronizationContext, IDisposable
{
	private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
	private readonly Thread _thread;

	public Action? AfterEach { get; set; }
	public double BusyMs { get; private set; }
	public double LongestMs { get; private set; }
	public int Dispatches { get; private set; }

	public UiThread()
	{
		_thread = new Thread(Loop) { IsBackground = true, Name = "bench-ui" };
		_thread.Start();
	}

	public void ResetCounters() => Send(_ => { BusyMs = 0; LongestMs = 0; Dispatches = 0; }, null);

	public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

	public override void Send(SendOrPostCallback d, object? state)
	{
		if (Thread.CurrentThread == _thread)
		{
			d(state);
			return;
		}
		using var done = new ManualResetEventSlim();
		Exception? error = null;
		Post(s => { try { d(s); } catch (Exception ex) { error = ex; } finally { done.Set(); } }, state);
		done.Wait();
		if (error is not null) throw error;
	}

	public override SynchronizationContext CreateCopy() => this;

	public Task Run(Func<Task> action)
	{
		var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		Post(async _ =>
		{
			try { await action(); tcs.SetResult(); }
			catch (Exception ex) { tcs.SetException(ex); }
		}, null);
		return tcs.Task;
	}

	private void Loop()
	{
		SetSynchronizationContext(this);
		foreach (var (callback, state) in _queue.GetConsumingEnumerable())
		{
			var start = Stopwatch.GetTimestamp();
			try { callback(state); }
			catch (Exception ex) { Console.Error.WriteLine($"ui thread: {ex}"); }
			var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
			BusyMs += ms;
			LongestMs = Math.Max(LongestMs, ms);
			Dispatches++;
			try { AfterEach?.Invoke(); }
			catch (Exception ex) { Console.Error.WriteLine($"ui observer: {ex}"); }
		}
	}

	public void Dispose() => _queue.CompleteAdding();
}
