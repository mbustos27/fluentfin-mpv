using System.Globalization;
using FluentFin.Core.Mpv;
using Microsoft.Extensions.Logging;

namespace FluentFin.Mpv;

/// <summary>
/// fluentfin-mpv player controls: one libmpv core inside FluentFin's process, rendering in composition mode (no window of
/// its own: it presents into a swap chain FluentFin shows in a SwapChainPanel). The backend attaches to it over its IPC
/// pipe (docs/BRIDGE.md §4, <c>window.mode = "attached"</c>) and plays exactly as it would in its own mpv; this class only
/// creates it with the options the shim can no longer give at construction, watches its swap chain, and ends it.
/// mpv is never waited for on the UI thread: property writes from the UI are asynchronous, and the event loop runs on a
/// thread of its own.
/// </summary>
internal sealed unsafe class MpvInProcessCore : IDisposable
{
	private readonly ILogger _logger;
	private readonly Thread _events;
	private volatile bool _stop;
	private int _disposed;

	public nint Ctx { get; }

	/// <summary>The pipe name (no <c>\\.\pipe\</c>) the core answers JSON IPC on: what the backend is told to attach to.</summary>
	public string Pipe { get; }

	/// <summary>The core ended by itself (a <c>quit</c> reached it): every later call on it is refused here.</summary>
	public bool IsShutDown { get; private set; }

	/// <summary>The swap chain mpv presents to changed (event thread); 0 = none. The pointer is mpv's: the panel takes its own reference.</summary>
	public event Action<long>? SwapChainChanged;

	/// <summary>The core ended by itself (event thread).</summary>
	public event Action? ShutDown;

	private MpvInProcessCore(ILogger logger, nint ctx, string pipe)
	{
		_logger = logger;
		Ctx = ctx;
		Pipe = pipe;
		_events = new Thread(EventLoop) { IsBackground = true, Name = "mpv core events" };
	}

	/// <summary>
	/// The options the backend would have given mpv at construction (backend/docs/bridge.md, attached mode): FluentFin's own
	/// window decides size and full screen, mpv's input and OSC are off (FluentFin draws the controls and owns all input),
	/// the backend's config folder holds the user's mpv.conf. Pure, so it can be checked without a core.
	/// </summary>
	public static List<(string Name, string Value)> Options(string pipe, string? configDir)
	{
		var options = new List<(string, string)>
		{
			("vo", "gpu-next"),
			("gpu-context", "d3d11"),
			("d3d11-output-mode", "composition"),
			("hwdec", "auto-safe"),
			("idle", "yes"),
			("force-window", "yes"),
			("keep-open", "no"),
			("osc", "no"),
			("input-default-bindings", "no"),
			("input-vo-keyboard", "no"),
			("input-cursor", "no"),
			("terminal", "no"),
			("audio-display", "no"),
			("background-color", "#000000"),
			("input-ipc-server", @"\\.\pipe\" + pipe),
		};

		if (!string.IsNullOrEmpty(configDir))
		{
			options.Add(("config", "yes"));
			options.Add(("config-dir", configDir));
		}
		else
		{
			options.Add(("config", "no"));
		}

		return options;
	}

	/// <summary>A pipe name nobody else uses: the process id and 16 random hex digits.</summary>
	public static string NewPipeName() =>
		string.Create(CultureInfo.InvariantCulture, $"fluentfin-mpv-{Environment.ProcessId}-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()}");

	/// <summary>Creates and starts a core. <paramref name="extraOptions"/> come after the defaults (tests: <c>ao=null</c>).</summary>
	public static MpvInProcessCore Create(ILogger logger, string? configDir, IEnumerable<(string Name, string Value)>? extraOptions = null)
	{
		var ctx = Libmpv.mpv_create();
		if (ctx == 0)
		{
			throw new InvalidOperationException("mpv_create failed");
		}

		var pipe = NewPipeName();
		try
		{
			foreach (var (name, value) in Options(pipe, configDir).Concat(extraOptions ?? []))
			{
				var rc = Libmpv.mpv_set_option_string(ctx, name, value);
				if (rc < 0)
				{
					logger.LogWarning("mpv option {Name}={Value} was refused: {Error}", name, value, Libmpv.Error(rc));
				}
			}

			Libmpv.mpv_request_log_messages(ctx, "warn");
			var init = Libmpv.mpv_initialize(ctx);
			if (init < 0)
			{
				throw new InvalidOperationException($"mpv_initialize failed: {Libmpv.Error(init)}");
			}
		}
		catch
		{
			Libmpv.mpv_terminate_destroy(ctx);
			throw;
		}

		var core = new MpvInProcessCore(logger, ctx, pipe);
		Libmpv.mpv_observe_property(ctx, 1, "display-swapchain", Libmpv.FormatInt64);
		core._events.Start();
		logger.LogInformation("Started the in-process mpv core (composition mode), IPC pipe {Pipe}", pipe);
		return core;
	}

	/// <summary>Sets a property without waiting (any thread). False when the core is gone or refused it at once.</summary>
	public bool SetAsync(string name, string value)
	{
		if (IsShutDown || Volatile.Read(ref _disposed) != 0)
		{
			return false;
		}

		var rc = Libmpv.SetStringAsync(Ctx, name, value);
		if (rc < 0)
		{
			_logger.LogDebug("mpv property {Name}={Value}: {Error}", name, value, Libmpv.Error(rc));
		}

		return rc >= 0;
	}

	/// <summary>The swap chain mpv presents to now, 0 for none (a synchronous read: not for the UI thread while playing).</summary>
	public long CurrentSwapChain() => IsShutDown || Volatile.Read(ref _disposed) != 0 ? 0 : Libmpv.GetInt64(Ctx, "display-swapchain") ?? 0;

	private void EventLoop()
	{
		while (!_stop)
		{
			var e = Libmpv.mpv_wait_event(Ctx, 1);
			if (e == null)
			{
				continue;
			}

			try
			{
				switch (e->EventId)
				{
					case Libmpv.EventIdNone:
						continue;

					case Libmpv.EventIdShutdown:
						IsShutDown = true;
						_logger.LogWarning("The in-process mpv core ended (a quit reached it)");
						ShutDown?.Invoke();
						return;

					case Libmpv.EventIdLogMessage:
						// redacted: a stream or subtitle URL mpv complains about can carry an api_key (docs/BRIDGE.md §1)
						var m = (Libmpv.EventLogMessage*)e->Data;
						if (IsExpected(Libmpv.Utf8(m->Text)))
						{
							continue;
						}

						_logger.LogWarning("mpv [{Prefix}] {Level}: {Text}", Libmpv.Utf8(m->Prefix), Libmpv.Utf8(m->Level), BridgeRedaction.Redact(Libmpv.Utf8(m->Text)?.TrimEnd() ?? ""));
						continue;

					case Libmpv.EventIdPropertyChange:
						var p = (Libmpv.EventProperty*)e->Data;
						if (Libmpv.Utf8(p->Name) == "display-swapchain")
						{
							var value = p->Format == Libmpv.FormatInt64 && p->Data != 0 ? *(long*)p->Data : 0;
							SwapChainChanged?.Invoke(value);
						}

						continue;
				}
			}
			catch (Exception ex)
			{
				// an exception escaping this thread would end FluentFin
				_logger.LogError(ex, "Handling an event of the in-process mpv core failed");
			}
		}
	}

	/// <summary>
	/// What mpv always says in composition mode, not a fault: it has no output to ask about the display (mpv PR #16795; the
	/// display's HDR and rate come from <see cref="MpvCompositionVideo.ApplyDisplay"/>).
	/// </summary>
	internal static bool IsExpected(string? text) => text?.Contains("Failed to query swap chain's output information", StringComparison.Ordinal) == true;

	/// <summary>
	/// Ends the core: the event thread stops, then <c>mpv_terminate_destroy</c> runs on a thread of its own and is waited
	/// for at most <paramref name="wait"/> (a hung core must not hold up FluentFin's close; the process exit ends it).
	/// </summary>
	public void Dispose() => Dispose(TimeSpan.FromSeconds(3));

	public void Dispose(TimeSpan wait)
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
		{
			return;
		}

		_stop = true;
		if (!IsShutDown)
		{
			Libmpv.mpv_wakeup(Ctx);
		}

		_events.Join(TimeSpan.FromSeconds(2));
		var ctx = Ctx;
		var done = new Thread(() => Libmpv.mpv_terminate_destroy(ctx)) { IsBackground = true, Name = "mpv core end" };
		done.Start();
		if (!done.Join(wait))
		{
			_logger.LogWarning("The in-process mpv core did not end within {Seconds} s; left to the process exit", wait.TotalSeconds);
		}
	}
}
