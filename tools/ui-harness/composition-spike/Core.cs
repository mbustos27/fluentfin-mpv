using System.Runtime.InteropServices;
using System.Text;

namespace CompositionSpike;

/// <summary>
/// One libmpv core in this process: options set before mpv_initialize (what the shim can no longer set when it only
/// attaches over IPC), an event thread (mpv_wait_event is never called on the UI thread), and the composition swap
/// chain reported as it appears or changes. Every core a test starts has ao=null: tests make no sound.
/// </summary>
internal sealed unsafe class Core : IDisposable
{
	private readonly Thread _events;
	private volatile bool _stop;
	private readonly object _logLock = new();
	private readonly StreamWriter? _log;
	private readonly List<string> _logLines = [];

	public nint Ctx { get; }

	/// <summary>The core shut down by itself (a <c>quit</c> reached it): from here every call on it is invalid.</summary>
	public ManualResetEventSlim ShutDown { get; } = new();

	/// <summary>display-swapchain changed (event thread); 0 = gone.</summary>
	public event Action<long>? SwapChainChanged;

	public event Action<int>? EventSeen;

	public Core(IReadOnlyList<(string Name, string Value)> options, string? logPath)
	{
		Ctx = Libmpv.mpv_create();
		if (Ctx == 0)
		{
			throw new InvalidOperationException("mpv_create failed");
		}

		foreach (var (name, value) in options)
		{
			var rc = Libmpv.mpv_set_option_string(Ctx, name, value);
			if (rc < 0)
			{
				Host.Log($"option {name}={value}: {Libmpv.Error(rc)}");
			}
		}

		if (logPath is not null)
		{
			_log = new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true };
			Libmpv.mpv_request_log_messages(Ctx, "v");
		}

		var init = Libmpv.mpv_initialize(Ctx);
		if (init < 0)
		{
			throw new InvalidOperationException($"mpv_initialize: {Libmpv.Error(init)}");
		}

		Libmpv.mpv_observe_property(Ctx, 1, "display-swapchain", Libmpv.FormatInt64);
		_events = new Thread(EventLoop) { IsBackground = true, Name = "mpv events" };
		_events.Start();
	}

	/// <summary>Log lines (mpv's own, level v and up) that contain <paramref name="text"/>.</summary>
	public List<string> LogLinesWith(string text)
	{
		lock (_logLock)
		{
			return [.. _logLines.Where(l => l.Contains(text, StringComparison.OrdinalIgnoreCase))];
		}
	}

	private void EventLoop()
	{
		while (!_stop)
		{
			var e = Libmpv.mpv_wait_event(Ctx, 0.5);
			if (e == null)
			{
				continue;
			}

			try
			{
				switch (e->EventId)
				{
					case Libmpv.EventNone:
						continue;
					case Libmpv.EventShutdown:
						Host.Log("mpv core: SHUTDOWN event");
						ShutDown.Set();
						return;
					case Libmpv.EventIdLogMessage:
						var m = (Libmpv.EventLogMessage*)e->Data;
						var line = $"[{Libmpv.Utf8(m->Prefix)}] {Libmpv.Utf8(m->Level)}: {Libmpv.Utf8(m->Text)?.TrimEnd()}";
						lock (_logLock)
						{
							_log?.WriteLine(line);
							_logLines.Add(line);
							if (_logLines.Count > 20000)
							{
								_logLines.RemoveRange(0, 5000);
							}
						}

						continue;
					case Libmpv.EventPropertyChange:
						var p = (Libmpv.EventProperty*)e->Data;
						if (Libmpv.Utf8(p->Name) == "display-swapchain")
						{
							var value = p->Format == Libmpv.FormatInt64 && p->Data != 0 ? *(long*)p->Data : 0;
							SwapChainChanged?.Invoke(value);
						}

						continue;
					default:
						EventSeen?.Invoke(e->EventId);
						continue;
				}
			}
			catch (Exception ex)
			{
				Host.Log($"mpv event handler failed: {ex.Message}");
			}
		}
	}

	public long? Int64(string name) => ShutDown.IsSet ? null : Libmpv.GetInt64(Ctx, name);

	public double? Double(string name) => ShutDown.IsSet ? null : Libmpv.GetDouble(Ctx, name);

	public string? String(string name) => ShutDown.IsSet ? null : Libmpv.GetString(Ctx, name);

	public int Set(string name, string value) => ShutDown.IsSet ? -1 : Libmpv.mpv_set_property_string(Ctx, name, value);

	public int Command(params string[] args) => ShutDown.IsSet ? -1 : Libmpv.Command(Ctx, args);

	public void Dispose()
	{
		_stop = true;
		if (!ShutDown.IsSet)
		{
			Libmpv.mpv_wakeup(Ctx);
		}

		_events.Join(3000);
		// after a SHUTDOWN event the handle still has to be destroyed (client.h)
		Libmpv.mpv_terminate_destroy(Ctx);
		lock (_logLock)
		{
			_log?.Dispose();
		}
	}
}
