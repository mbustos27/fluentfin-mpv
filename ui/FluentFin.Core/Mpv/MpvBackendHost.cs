using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.Mpv;

/// <summary>
/// Runs the backend as a child process (started lazily, restarted after a crash on the next start), keeps the bridge
/// connection to it, copies its stderr into FluentFin's log without tokens, and reports its status.
/// </summary>
public sealed class MpvBackendHost(IMpvBackendLauncher launcher,
								   IMpvVideoSurface surface,
								   ILogger<MpvBackendHost> logger) : IMpvBackend, IDisposable
{
	private const int StderrTailLines = 20;

	/// <summary>How far before the exit stderr can still say why the backend died (unless its last event came later).</summary>
	private const long CrashStderrWindowMs = 1000;

	private readonly SemaphoreSlim _lifecycle = new(1, 1);
	private readonly BehaviorSubject<MpvBackendStatus> _status = new(new(MpvBackendState.NotStarted));
	private readonly Subject<BridgeEvent> _events = new();
	private readonly Subject<MpvInterruptedPlayback> _interrupted = new();
	private Session? _session;

	/// <summary>The newest backend started, kept after it exits: <see cref="ShutdownAsync"/> waits for the handling of its exit.</summary>
	private Session? _lastSession;

	public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(30);
	public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(10);

	/// <summary>connect may log in to the server and start mpv, so it gets longer.</summary>
	public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(45);

	/// <summary>
	/// The whole graceful stop: the <c>shutdown</c> request and the exit; then the job is killed. The backend may take ~9 s
	/// to answer on a slow server (it waits for its player so the final Stopped report goes out, docs/BRIDGE.md §3).
	/// </summary>
	public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);

	/// <summary>
	/// How long <see cref="ShutdownAsync"/> waits for a backend still starting or a <c>connect</c> still unanswered (they may
	/// take 30 and 45 s) before it kills the backend instead, so closing FluentFin never waits for them. That kill is the
	/// close's own: its exit is not a crash (docs/BRIDGE.md §3, §6). A pending <c>play</c> is not waited for at all.
	/// </summary>
	public TimeSpan LifecycleTimeout { get; init; } = TimeSpan.FromSeconds(3);

	/// <summary>
	/// How long <see cref="ShutdownAsync"/> waits, for a backend killed before the close (the watchdog) or dead before it, for
	/// its exit to be seen and handled (the crash described and <see cref="PlaybackInterrupted"/> raised); the handling itself
	/// waits at most 1 s for each of its output pipes.
	/// </summary>
	public TimeSpan ExitHandlingTimeout { get; init; } = TimeSpan.FromSeconds(2);

	public MpvBackendStatus Status => _status.Value;
	public IObservable<MpvBackendStatus> StatusChanged => _status.AsObservable();
	public IObservable<BridgeEvent> Events => _events.AsObservable();
	public IObservable<MpvInterruptedPlayback> PlaybackInterrupted => _interrupted.AsObservable();
	public BridgeState? LastState => Volatile.Read(ref _session)?.LastState;

	/// <summary>A <c>position</c> event and the play session that was current then: a position belongs to one play session.</summary>
	private sealed record PositionMark(string ItemId, string PlaySessionId, long Ticks);

	/// <summary>
	/// A play session the backend has not finished reporting (docs/BRIDGE.md §6): open until its <c>reported</c>.
	/// <paramref name="ServerUrl"/> and <paramref name="UserId"/> are the server and user it is played as: the backend's
	/// connection when the <c>play</c> that caused it was sent (<see cref="TrackPlaySession"/>).
	/// </summary>
	private sealed record OpenPlaySession(string ItemId, string? MediaSourceId, string PlaySessionId, string? ServerUrl, string? UserId);

	private sealed class Session(IMpvBackendProcess process, BridgeClient client, MpvBackendCommand command, bool dev)
	{
		public IMpvBackendProcess Process { get; } = process;
		public BridgeClient Client { get; } = client;
		public MpvBackendCommand Command { get; } = command;
		public bool Dev { get; } = dev;
		private readonly List<StderrLine> _stderrTail = [];
		private readonly Lock _stderrLock = new();
		private BridgeIdentity? _identity;
		private long _readOrder;
		private long _lastEventOrder;
		public IDisposable? EventSubscription { get; set; }
		public Task StderrPump { get; set; } = Task.CompletedTask;

		/// <summary>Completes when <see cref="OnExited"/> has run for this process.</summary>
		public Task ExitHandled { get; set; } = Task.CompletedTask;
		public BridgeHello? Hello { get; set; }
		public bool Started { get; set; }

		/// <summary>Who the backend is connected as (set by the connect, read by the reader thread when a play session opens).</summary>
		public BridgeIdentity? Identity
		{
			get => Volatile.Read(ref _identity);
			set => Volatile.Write(ref _identity, value);
		}

		private BridgeIdentity? _playIdentity;

		/// <summary>
		/// fluentfin-mpv: who the latest <c>play</c> was sent as (the connection current when it was sent), null before the
		/// first. The play sessions it causes are played as that server and user, even when their first <c>state</c> is read
		/// after a <c>connect</c> as someone else was answered (a snapshot sampled before that connect stopped them).
		/// </summary>
		public BridgeIdentity? PlayIdentity
		{
			get => Volatile.Read(ref _playIdentity);
			set => Volatile.Write(ref _playIdentity, value);
		}

		/// <summary>
		/// The message of the latest <c>error</c> event with <c>fatal: true</c> (redacted): the backend said why it is about to
		/// exit, which names the cause of that exit better than its stderr. Null before one.
		/// </summary>
		public volatile string? FatalError;

		/// <summary>A wait for this backend's exit to be handled has run its course (done or timed out): it is not waited for again.</summary>
		public volatile bool ExitWaited;

		/// <summary>
		/// The next number in the order FluentFin read this backend's events and stderr lines (two reader threads): which line
		/// came after the last event. Not a time: lines and events read within one tick of the clock still have an order.
		/// </summary>
		public long NextReadOrder() => Interlocked.Increment(ref _readOrder);

		/// <summary>The <see cref="NextReadOrder"/> of the latest event read, 0 before the first.</summary>
		public long LastEventOrder
		{
			get => Interlocked.Read(ref _lastEventOrder);
			set => Interlocked.Exchange(ref _lastEventOrder, value);
		}

		public BridgeWindow? Window { get; set; }
		public bool Connected { get; set; }
		public string? MpvVersion { get; set; }
		public string? MpvKind { get; set; }
		public BridgeState? LastState { get; set; }

		/// <summary>The latest <c>position</c> event and the item and play session current then (state snapshots carry an older position).</summary>
		public volatile PositionMark? LatestPosition;

		/// <summary>The latest play session, from the first <c>state</c> that shows it until its <c>reported</c>; null otherwise.</summary>
		public volatile OpenPlaySession? OpenPlay;

		/// <summary>Every play session a <c>state</c> showed (only the reader thread touches it): a session opens once, at its first.</summary>
		public HashSet<string> SeenPlaySessions { get; } = new(StringComparer.Ordinal);

		// fluentfin-mpv: whether an exit is a crash is decided by what came first, the exit (or a kill) or FluentFin's own stop,
		// not by when the exit gets handled (docs/BRIDGE.md §6). The kill decision and the stop decision share this lock.
		private readonly Lock _exitLock = new();
		private volatile bool _shuttingDown;
		private volatile string? _killReason;
		private volatile bool _goneWhenStopped;

		/// <summary>FluentFin's own stop has begun (shutdown, stdin EOF, or a kill to close).</summary>
		public bool ShuttingDown => _shuttingDown;

		/// <summary>Why FluentFin killed the backend (the watchdog, a backend that stopped reading its input); null if it did not.</summary>
		public string? KillReason => _killReason;

		/// <summary>
		/// When the stop began, the backend had already died (its process ended, as the system knows it then, even if FluentFin
		/// had not seen the exit yet; or the end of its output): its exit came first.
		/// </summary>
		public bool GoneWhenStopped => _goneWhenStopped;

		/// <summary>Decides a kill unless the stop came first (then false: the exit is the stop's). The first reason stays.</summary>
		public bool TryMarkKilled(string reason)
		{
			lock (_exitLock)
			{
				if (_shuttingDown)
				{
					return false;
				}

				_killReason ??= reason;
				return true;
			}
		}

		/// <summary>FluentFin's stop begins; records whether the backend was already gone. Only the first call counts.</summary>
		public void MarkShuttingDown()
		{
			lock (_exitLock)
			{
				if (_shuttingDown)
				{
					return;
				}

				// fluentfin-mpv: the process's own state, not whether its exit was seen yet (docs/BRIDGE.md §6: what happened first)
				_goneWhenStopped = ProcessHasExited() || Client.IsClosed;
				_shuttingDown = true;
			}
		}

		private bool ProcessHasExited()
		{
			try
			{
				return Process.HasExited;
			}
			catch
			{
				return Process.Exited.IsCompleted;
			}
		}

		/// <summary>The exit is FluentFin's own stop: the stop came first and no kill was decided before it. Not a crash.</summary>
		public bool ExitIsTheStops()
		{
			lock (_exitLock)
			{
				return _shuttingDown && _killReason is null && !_goneWhenStopped;
			}
		}

		public void AddStderr(string line)
		{
			lock (_stderrLock)
			{
				var harmless = IsLibmpvMissing(line);
				if (harmless)
				{
					// the traceback that ends here is that harmless one too: its frame lines back to its "Traceback" line
					for (var i = _stderrTail.Count - 1; i >= 0; i--)
					{
						var earlier = _stderrTail[i];
						var head = earlier.Line.StartsWith("Traceback (most recent call last)", StringComparison.Ordinal);
						if (!head && !earlier.Harmless && (earlier.Line.Length == 0 || !char.IsWhiteSpace(earlier.Line[0])))
						{
							break;
						}

						earlier.Harmless = true;
						if (head)
						{
							break;
						}
					}
				}

				_stderrTail.Add(new StderrLine(Environment.TickCount64, NextReadOrder(), line) { Harmless = harmless });
				if (_stderrTail.Count > StderrTailLines)
				{
					_stderrTail.RemoveAt(0);
				}
			}
		}

		/// <summary>
		/// The last stderr line that reads like an error (a Python exception, an ERROR/CRITICAL log line), or "". Only lines
		/// read at or after <paramref name="since"/> (<see cref="Environment.TickCount64"/>) and read after the event numbered
		/// <paramref name="afterOrder"/> (<see cref="NextReadOrder"/>): an older error says nothing about a crash. Never the
		/// known-harmless traceback of a missing libmpv (<see cref="IsLibmpvMissing"/>).
		/// </summary>
		public string LastStderr(long since = long.MinValue, long afterOrder = 0)
		{
			lock (_stderrLock)
			{
				return _stderrTail.LastOrDefault(x => x.At >= since && x.Order > afterOrder && !x.Harmless && LooksLikeAnError(x.Line))?.Line.Trim() ?? "";
			}
		}

		private static bool LooksLikeAnError(string line) =>
			line.Contains("Error", StringComparison.Ordinal) || line.Contains("ERROR", StringComparison.Ordinal) ||
			line.Contains("CRITICAL", StringComparison.Ordinal) || line.Contains("Fatal", StringComparison.OrdinalIgnoreCase) ||
			line.Contains("Exception", StringComparison.Ordinal) || line.Contains("Traceback", StringComparison.Ordinal);

		/// <summary>
		/// python-mpv's OSError when there is no libmpv (mpv-1.dll / mpv-2.dll / libmpv-2.dll): the backend logs its traceback at
		/// every connect and then uses mpv.exe (docs/BRIDGE.md §4 <c>mpvBackend: external</c>). Harmless, never a crash's cause.
		/// </summary>
		private static bool IsLibmpvMissing(string line) =>
			line.Contains("Cannot find mpv-1.dll", StringComparison.OrdinalIgnoreCase) ||
			line.Contains("Cannot find libmpv", StringComparison.OrdinalIgnoreCase);

		private sealed class StderrLine(long at, long order, string line)
		{
			public long At { get; } = at;
			public long Order { get; } = order;
			public string Line { get; } = line;
			public bool Harmless { get; set; }
		}
	}

	public Task StartAsync(bool dev = false, CancellationToken cancellationToken = default)
	{
		Interlocked.Increment(ref _asked);
		return StartCoreAsync(dev, cancellationToken);
	}

	private async Task StartCoreAsync(bool dev, CancellationToken cancellationToken)
	{
		await _lifecycle.WaitAsync(cancellationToken);
		try
		{
			if (_session is { } running)
			{
				if (!running.Process.Exited.IsCompleted && (running.Dev || !dev))
				{
					return;
				}

				await StopSessionAsync(running, "restarting with --dev");
			}

			await StartSessionAsync(dev, cancellationToken);
		}
		finally
		{
			_lifecycle.Release();
		}
	}

	private async Task StartSessionAsync(bool dev, CancellationToken cancellationToken)
	{
		var command = launcher.Resolve(dev, out var searched);
		if (command is null)
		{
			throw Fail($"The mpv backend was not found (looked for {searched}). Set the backend command in Settings.", null);
		}

		SetStatus(new MpvBackendStatus(MpvBackendState.Starting, Command: command.ToString()));
		logger.LogInformation("Starting the mpv backend ({Source}): {Command}", command.Source, command);
		var launched = System.Diagnostics.Stopwatch.StartNew();

		IMpvBackendProcess process;
		try
		{
			process = launcher.Start(command);
		}
		catch (Exception ex)
		{
			throw Fail($"Could not start the mpv backend {command.FileName}: {ex.Message}", ex);
		}

		var client = new BridgeClient(process.StandardOutput, process.StandardInput, logger) { DefaultTimeout = CommandTimeout };
		var session = new Session(process, client, command, dev);
		var hello = client.Events.FirstAsync(x => x.Name == Bridge.Events.Hello).ToTask(cancellationToken);
		session.EventSubscription = client.Events.Subscribe(e => OnEvent(session, e));
		_session = session;
		Volatile.Write(ref _lastSession, session);

		session.StderrPump = Task.Run(() => PumpStderr(session));
		session.ExitHandled = process.Exited.ContinueWith(t => OnExited(session, t.IsCompletedSuccessfully ? t.Result : -1), TaskScheduler.Default);
		// a write the backend never takes (it stopped reading its input) closes the connection: the backend is hung
		_ = client.Closed.ContinueWith(_ =>
		{
			if (client.CloseReason is TimeoutException && !session.ShuttingDown)
			{
				KillSession(session, "it stopped reading its input");
			}
		}, TaskScheduler.Default);
		client.Start();

		BridgeEvent helloEvent;
		try
		{
			helloEvent = await hello.WaitAsync(HelloTimeout, cancellationToken);
		}
		catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or OperationCanceledException)
		{
			if (process.Exited.IsCompleted)
			{
				// the last stderr lines usually say why
				await Task.WhenAny(session.StderrPump, Task.Delay(TimeSpan.FromSeconds(1)));
			}

			var exited = process.Exited.IsCompleted ? $" (exit code {process.Exited.Result})" : "";
			var reason = ex switch
			{
				TimeoutException => $"The mpv backend did not say hello within {HelloTimeout.TotalSeconds:0} s",
				OperationCanceledException => "Starting the mpv backend was cancelled",
				_ => $"The mpv backend exited while starting{exited}",
			};
			var last = session.LastStderr();
			await StopSessionAsync(session, "start failed", graceful: false);
			if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
			{
				// fluentfin-mpv (startup-perf review): its caller gave the start up (a sign-out during the pre-warm): not a
				// failure, so no error in the log and no "Could not start" in Settings
				logger.LogInformation("{Reason}", reason);
				SetStatus(new MpvBackendStatus(MpvBackendState.Stopped));
				throw new BridgeException(reason, ex);
			}

			throw Fail(string.IsNullOrEmpty(last) ? reason : $"{reason}: {last}", ex);
		}

		session.Hello = helloEvent.DataAs<BridgeHello>();
		if (session.Hello?.Protocol != Bridge.ProtocolVersion)
		{
			await StopSessionAsync(session, "protocol mismatch", graceful: false);
			throw Fail($"The mpv backend speaks bridge protocol {session.Hello?.Protocol}, FluentFin speaks {Bridge.ProtocolVersion}", null);
		}

		session.Started = true;
		logger.LogInformation("The mpv backend is running: pid {Pid}, {Backend} {Version}, bridge {Bridge}; [perf] hello {Ms} ms after the launch",
			process.Id, session.Hello.Backend, session.Hello.Version, session.Hello.Bridge, launched.ElapsedMilliseconds);
		SetStatus(StatusOf(session));
	}

	public Task<string?> ConnectAsync(BridgeIdentity? identity, CancellationToken cancellationToken = default)
	{
		Interlocked.Increment(ref _asked);
		return ConnectCoreAsync(identity, prewarm: false, cancellationToken);
	}

	/// <summary>
	/// fluentfin-mpv (startup-perf): <see cref="StartAsync"/> and <see cref="ConnectAsync"/> calls so far (a play's; not a
	/// pre-warm's), counted as they are asked, before the lifecycle lock: a pre-warm whose connect failed stops the backend
	/// only when no play asked for it meanwhile (<see cref="StopUnusedAfterPrewarmAsync"/>).
	/// </summary>
	private long _asked;

	/// <inheritdoc/>
	public async Task PrewarmAsync(BridgeIdentity identity, CancellationToken cancellationToken = default)
	{
		var asked = Interlocked.Read(ref _asked);
		await StartCoreAsync(dev: false, cancellationToken);
		if (!surface.CanConnectAhead)
		{
			// attached (FluentFin's player controls): the connect's window is FluentFin's own mpv, which asking for it makes
			logger.LogInformation("Pre-warm: the mpv backend is started; it connects at the first play, which makes FluentFin's own mpv");
			return;
		}

		try
		{
			await ConnectCoreAsync(identity, prewarm: true, cancellationToken);
		}
		catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
		{
			await StopUnusedAfterPrewarmAsync(asked, ex);
			throw;
		}
	}

	/// <summary>
	/// fluentfin-mpv (startup-perf review): a pre-warm's connect failed (server down, login refused, mpv would not start):
	/// the backend, and the mpv its connect may have started, would idle unused until sign-out or close. Stopped, under the
	/// lifecycle lock, unless a play asked for the backend since the pre-warm began (its own start and connect then decide;
	/// a play that asks after this finds none and starts one). A cancelled pre-warm is the sign-out's or the close's to stop.
	/// Never throws.
	/// </summary>
	private async Task StopUnusedAfterPrewarmAsync(long asked, Exception failure)
	{
		try
		{
			await _lifecycle.WaitAsync();
			try
			{
				if (Interlocked.Read(ref _asked) != asked || _session is not { Connected: false } session)
				{
					return;
				}

				var why = BridgeRedaction.Redact(failure.Message);
				await StopSessionAsync(session, $"its pre-warm could not connect ({why}); the next play starts it again");
				SetStatus(new MpvBackendStatus(MpvBackendState.Stopped, LastError: why));
			}
			finally
			{
				_lifecycle.Release();
			}
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Stopping the mpv backend after its failed pre-warm failed");
		}
	}

	private async Task<string?> ConnectCoreAsync(BridgeIdentity? identity, bool prewarm, CancellationToken cancellationToken)
	{
		// fluentfin-mpv player controls: embedded, or attached to FluentFin's own mpv (the surface decides)
		var window = await surface.GetWindowAsync();

		await _lifecycle.WaitAsync(cancellationToken);
		try
		{
			var session = RunningSession();

			if (session.Window is { } locked && locked != window)
			{
				// the window is fixed for the lifetime of a backend process (window_locked)
				var dev = session.Dev;
				await StopSessionAsync(session, "the video window changed");
				await StartSessionAsync(dev, cancellationToken);
				session = RunningSession();
			}

			if (session.Connected && Equals(session.Identity, identity))
			{
				return session.MpvVersion;
			}

			logger.LogInformation("Connecting the mpv backend: {Identity}, window {Mode} {Hwnd}{Prewarm}", identity?.ToString() ?? "(dev, no server)", window.Mode, window.Hwnd?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? window.Ipc,
				prewarm ? ", pre-warmed (no video window until the first play)" : "");

			JsonNode? result;
			var watch = System.Diagnostics.Stopwatch.StartNew();
			try
			{
				result = await session.Client.SendAsync(Bridge.Commands.Connect, BridgeCommands.Connect(identity, window, prewarm), ConnectTimeout, cancellationToken);
			}
			catch (BridgeException ex)
			{
				session.Connected = false;
				SetStatus(StatusOf(session) with { LastError = ex.Message });
				throw;
			}

			session.Connected = true;
			session.Identity = identity;
			session.Window = window;
			session.MpvVersion = StringOf(result?["mpv"]);
			session.MpvKind = StringOf(result?["mpvBackend"]);
			logger.LogInformation("[perf] the mpv backend answered connect in {Ms} ms", watch.ElapsedMilliseconds);
			SetStatus(StatusOf(session));
			return session.MpvVersion;
		}
		finally
		{
			_lifecycle.Release();
		}
	}

	public Task<JsonNode?> SendAsync(string command, JsonObject? args = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
	{
		if (Volatile.Read(ref _session) is not { Started: true } session || session.Client.IsClosed)
		{
			return Task.FromException<JsonNode?>(new BridgeClosedException($"The mpv backend is not running, '{command}' was not sent"));
		}

		if (command == Bridge.Commands.Play)
		{
			// fluentfin-mpv: before it is written, so every state it causes is read after this (TrackPlaySession)
			session.PlayIdentity = session.Identity;
		}

		return session.Client.SendAsync(command, args, timeout, cancellationToken);
	}

	/// <inheritdoc/>
	public async Task<bool> WaitForStopReportsAsync(TimeSpan timeout)
	{
		var session = Volatile.Read(ref _session);
		try
		{
			if (session is null)
			{
				return true;
			}

			// the open session closes only at its `reported`; a state sampled before the stop can still show it after that,
			// so the backend is done once it has also gone idle (docs/BRIDGE.md §5)
			bool Owed() => !session.ExitHandled.IsCompleted && (session.OpenPlay is not null || session.LastState is { IsActive: true });
			if (!Owed())
			{
				return true;
			}

			var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			using (_events.Subscribe(_ =>
			{
				if (!Owed())
				{
					done.TrySetResult();
				}
			}))
			{
				// an event between the first look and the subscription
				if (!Owed())
				{
					return true;
				}

				await Task.WhenAny(done.Task, session.ExitHandled).WaitAsync(timeout);
				return true;
			}
		}
		catch (TimeoutException)
		{
			logger.LogWarning("The mpv backend had not finished the stop report of play session {PlaySessionId} after {Seconds} s; going on without it",
				session?.OpenPlay?.PlaySessionId ?? "(none open)", timeout.TotalSeconds);
			return false;
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Waiting for the stop report of the mpv backend failed");
			return false;
		}
	}

	/// <summary>Never throws: the watchdog calls it from a timer thread, where an exception would end FluentFin.</summary>
	public void Kill(string reason)
	{
		if (Volatile.Read(ref _session) is { } session)
		{
			KillSession(session, reason);
		}
	}

	private void KillSession(Session session, string reason)
	{
		try
		{
			// checked and set under the stop's lock: a stop that began first owns the exit (no kill, no crash report); a kill
			// decided first makes the stop send no shutdown, so the exit is this kill's (one Stopped, FluentFin's)
			if (!session.TryMarkKilled(reason))
			{
				return;
			}

			logger.LogError("Killing the mpv backend (pid {Pid}): {Reason}", session.Process.Id, reason);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Killing the mpv backend failed");
		}

		KillProcess(session);
	}

	/// <summary>Ends the backend's job. Never throws, whatever the launcher's Kill does.</summary>
	private void KillProcess(Session session)
	{
		try
		{
			session.Process.Kill();
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Killing the mpv backend (pid {Pid}) reported an error", SafeId(session));
		}
	}

	/// <summary>
	/// Stops the backend gracefully and waits at most about <see cref="LifecycleTimeout"/> + <see cref="ShutdownTimeout"/>
	/// (docs/BRIDGE.md §3): a backend still starting, or with a <c>connect</c> still unanswered, after
	/// <see cref="LifecycleTimeout"/> is ended by killing it, a kill the close decided, so its exit is the close's own and never
	/// reported as a crash; a pending <c>play</c> is not waited for (<c>shutdown</c> is sent alongside it and the backend runs
	/// it under its own deadline); a backend that has not exited 10 s after <c>shutdown</c> was sent is killed. Then, if the backend had been killed before the close (the
	/// watchdog; the close may have finished that kill itself) or had died, up to <see cref="ExitHandlingTimeout"/> more for
	/// that exit to be seen and handled, once (the stop's own wait for it counts): it raises <see cref="PlaybackInterrupted"/>
	/// from a thread-pool thread, so once this returns, any Stopped report FluentFin owes has been asked for (docs/BRIDGE.md §6).
	/// </summary>
	public async Task ShutdownAsync()
	{
		await StopForShutdownAsync();
		await WaitForExitHandlingAsync(Volatile.Read(ref _lastSession));
	}

	/// <summary>
	/// Waits, at most <see cref="ExitHandlingTimeout"/>, for the backend's exit to be seen and handled: whether FluentFin owes
	/// a Stopped report is decided there (docs/BRIDGE.md §6). Also when the exit has not been seen yet: a kill the close itself
	/// just made (finishing a watchdog kill decided before the close) is seen a moment after the kill returns. Not at all when
	/// nothing can be owed: the exit is the stop's own (its shutdown, or a kill the close decided), which the handling never
	/// reports. Nor a second time: once a wait for this exit has run its course, the bound is spent (the close would otherwise
	/// wait twice for an exit that does not come). Never throws.
	/// </summary>
	private async Task WaitForExitHandlingAsync(Session? session)
	{
		try
		{
			if (session is null || session.ExitHandled.IsCompleted || (session.ShuttingDown && session.ExitIsTheStops()) || session.ExitWaited)
			{
				return;
			}

			try
			{
				// the handling runs once the exit is seen, so this waits for both
				await session.ExitHandled.WaitAsync(ExitHandlingTimeout);
			}
			finally
			{
				session.ExitWaited = true;
			}
		}
		catch (TimeoutException)
		{
			if (session!.Process.Exited.IsCompleted)
			{
				logger.LogWarning("The exit of the mpv backend (pid {Pid}) was not handled within {Seconds} s", SafeId(session), ExitHandlingTimeout.TotalSeconds);
			}
			else
			{
				logger.LogWarning("The mpv backend (pid {Pid}) did not exit within {Seconds} s; going on without waiting for its stop report", SafeId(session), ExitHandlingTimeout.TotalSeconds);
			}
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Waiting for the exit of the mpv backend to be handled failed");
		}
	}

	private async Task StopForShutdownAsync()
	{
		if (!await _lifecycle.WaitAsync(LifecycleTimeout))
		{
			if (Volatile.Read(ref _session) is { } busy)
			{
				logger.LogWarning("The mpv backend is still starting or connecting after {Seconds} s; killing it to close", LifecycleTimeout.TotalSeconds);
				// marked first: a kill the close decides is the close's own stop, not a crash (docs/BRIDGE.md §6)
				busy.MarkShuttingDown();
				KillProcess(busy);
			}

			// the start or connect fails now that the process is gone; tidy up if it lets go quickly, else leave it be
			if (!await _lifecycle.WaitAsync(TimeSpan.FromSeconds(1)))
			{
				SetStatus(new MpvBackendStatus(MpvBackendState.Stopped));
				return;
			}
		}

		try
		{
			if (_session is { } session)
			{
				await StopSessionAsync(session, "shutdown");
				SetStatus(new MpvBackendStatus(MpvBackendState.Stopped));
			}
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Stopping the mpv backend failed");
		}
		finally
		{
			_lifecycle.Release();
		}
	}

	private Session RunningSession() =>
		_session is { Started: true } session && !session.Process.Exited.IsCompleted && !session.Client.IsClosed
			? session
			: throw new BridgeClosedException("The mpv backend is not running");

	/// <summary>
	/// Graceful: <c>shutdown</c>, then up to <see cref="ShutdownTimeout"/> in all for the answer and the exit (the backend
	/// answers and exits within ~9 s even on a hung server, docs/BRIDGE.md §3), so the kill comes no later than 10 s after
	/// <c>shutdown</c> was sent, however late the answer came. stdin stays open meanwhile and is closed only when
	/// <c>shutdown</c> could not be sent. Not graceful: stdin EOF and half a second. A backend that is being killed (a kill
	/// decided before this stop) or had already died gets neither (its exit is a crash, docs/BRIDGE.md §6; a final report
	/// from it could only race the kill or FluentFin's own): half a second to exit, then this stop finishes the kill and
	/// waits, bounded, for that exit to be handled. A kill this stop decides itself (the deadline ran out) is the stop's own:
	/// never a crash. Never throws.
	/// </summary>
	private async Task StopSessionAsync(Session session, string why, bool graceful = true)
	{
		session.MarkShuttingDown();
		// no kill is decided after the mark, so this is final
		var gone = session.KillReason is { } killing ? $"it is being killed because {killing}"
			: session.GoneWhenStopped ? "it had already died"
			: null;
		logger.LogInformation("Stopping the mpv backend: {Why}{Gone}", why, gone is null ? "" : $" ({gone})");

		var ask = gone is null;
		graceful &= ask;
		var budget = graceful ? ShutdownTimeout : TimeSpan.FromMilliseconds(500);
		var started = Environment.TickCount64;
		TimeSpan Left(TimeSpan minimum) => TimeSpan.FromMilliseconds(Math.Max(minimum.TotalMilliseconds, budget.TotalMilliseconds - (Environment.TickCount64 - started)));

		var asked = false;
		if (graceful && !session.Client.IsClosed)
		{
			try
			{
				// answered after the final report, just before the exit
				await session.Client.SendAsync(Bridge.Commands.Shutdown, timeout: budget);
				asked = true;
			}
			catch (BridgeTimeoutException ex)
			{
				// sent but not answered in time: the budget is spent, the kill below follows
				asked = true;
				logger.LogInformation("The mpv backend did not confirm shutdown: {Message}", ex.Message);
			}
			catch (BridgeException ex)
			{
				logger.LogInformation("The mpv backend did not take shutdown: {Message}", ex.Message);
			}
		}

		if (ask && !asked)
		{
			// stdin EOF is a shutdown request too; bounded, a write the backend never took may hold the input
			await session.Client.CloseOutputAsync(Left(graceful ? TimeSpan.Zero : TimeSpan.FromMilliseconds(100)));
		}

		try
		{
			// a graceful stop ends at its deadline, answered or not: no extra time after it (docs/BRIDGE.md §3)
			await session.Process.Exited.WaitAsync(Left(graceful ? TimeSpan.Zero : TimeSpan.FromMilliseconds(500)));
		}
		catch (TimeoutException)
		{
			logger.LogWarning("The mpv backend did not exit, killing it");
			KillProcess(session);
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Waiting for the mpv backend to exit failed, killing it");
			KillProcess(session);
		}

		Interlocked.CompareExchange(ref _session, null, session);
		if (gone is not null)
		{
			// a crash: its handling reads the backend's last events (a late `reported` closes the play session), which the
			// release below would cut off. Waited for even when the kill above was this stop's own (a watchdog kill decided
			// before the close, not done yet): that exit is seen only a moment after the kill returns (docs/BRIDGE.md §6)
			await WaitForExitHandlingAsync(session);
		}

		ReleaseSession(session);
	}

	private void ReleaseSession(Session session)
	{
		try
		{
			session.EventSubscription?.Dispose();
			session.Client.Dispose();
			session.Process.Dispose();
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Releasing the mpv backend failed");
		}
	}

	private static int SafeId(Session session)
	{
		try
		{
			return session.Process.Id;
		}
		catch
		{
			return -1;
		}
	}

	private void OnEvent(Session session, BridgeEvent e)
	{
		// stderr read before this says nothing about a crash that comes after it (OnExited)
		session.LastEventOrder = session.NextReadOrder();
		switch (e.Name)
		{
			case Bridge.Events.State when Parse<BridgeState>(e) is { } state:
				session.LastState = state;
				TrackPlaySession(session, state);
				break;
			// a position belongs to the play session current then; one with no play session (play_file) marks nothing
			case Bridge.Events.Position when PositionTicks(e) is { } position && session.LastState is { ItemId: { Length: > 0 } itemId, PlaySessionId: { Length: > 0 } playSessionId }:
				session.LatestPosition = new PositionMark(itemId, playSessionId, position);
				break;
			case Bridge.Events.Reported when ReportedPlaySession(e) is { } reported:
				// the backend is done with this session's Stopped report, whatever the outcome (sent, failed, none, or one this
				// version does not know: it is not read): it is no longer FluentFin's to report (docs/BRIDGE.md §5, §6); one
				// for another (older) session leaves the open one open
				if (session.OpenPlay is { } open && string.Equals(open.PlaySessionId, reported, StringComparison.Ordinal))
				{
					session.OpenPlay = null;
				}

				// a state of this session that comes later (snapshots and reports come from different threads) does not open it
				session.SeenPlaySessions.Add(reported);
				logger.LogDebug("The mpv backend is done with the stop report of play session {PlaySessionId}: {Outcome}", reported, (e.Data as JsonObject)?["outcome"]?.ToJsonString());
				break;
			case Bridge.Events.Error when Parse<BridgeError>(e) is { } error:
				if (error.Fatal && !string.IsNullOrWhiteSpace(error.Message))
				{
					// fluentfin-mpv: why the backend is about to exit, in its own words (OnExited)
					session.FatalError = BridgeRedaction.Redact(error.Message).Trim();
				}

				logger.LogWarning("mpv backend error {Code} (fatal: {Fatal}): {Message}", error.Code, error.Fatal, BridgeRedaction.Redact(error.Message));
				if (session.Started)
				{
					SetStatus(StatusOf(session) with { LastError = $"{error.Code}: {BridgeRedaction.Redact(error.Message)}" });
				}
				break;
		}

		try
		{
			_events.OnNext(e);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "A handler of the mpv backend event {Event} failed", e.Name);
		}
	}

	/// <summary>
	/// A play session opens at the first <c>state</c> that shows it (opening, playing or paused, with a play session id), and
	/// replaces the one open before (the backend reports that one itself). Only a <c>reported</c> closes it, not <c>idle</c>
	/// or <c>ended</c>: a deferred <c>stop</c> goes idle before its report is sent (docs/BRIDGE.md §6). <c>play_file</c> items
	/// have no play session, so they never open one. It is played as the server and user the latest <c>play</c> was sent as
	/// (<see cref="Session.PlayIdentity"/>): a <c>play</c> is sent only after its <c>connect</c> was answered, a <c>connect</c>
	/// as another server or user stops the playback before it answers, and a session only ever comes from a <c>play</c> (or
	/// the queue it started). Not the connection current when its first state is read: a snapshot sampled before a switch's
	/// <c>connect</c> stopped the session can be read after that connect was answered (docs/BRIDGE.md §5). Without any
	/// <c>play</c> sent, the current connection. Runs on the reader thread only.
	/// </summary>
	private void TrackPlaySession(Session session, BridgeState state)
	{
		if (!state.IsActive || state.ItemId is not { Length: > 0 } itemId || state.PlaySessionId is not { Length: > 0 } playSessionId)
		{
			return;
		}

		if (session.SeenPlaySessions.Add(playSessionId))
		{
			var playedAs = session.PlayIdentity ?? session.Identity;
			session.OpenPlay = new OpenPlaySession(itemId, state.MediaSourceId, playSessionId, playedAs?.ServerUrl, playedAs?.UserId);
			logger.LogDebug("Play session {PlaySessionId} opened for {ItemId}", playSessionId, itemId);
		}
		else if (session.OpenPlay is { MediaSourceId: null } open && open.PlaySessionId == playSessionId && state.MediaSourceId is { Length: > 0 } mediaSourceId)
		{
			session.OpenPlay = open with { MediaSourceId = mediaSourceId };
		}
	}

	/// <summary>The play session of a <c>reported</c> event; the rest of it (the outcome, of any type) is not needed.</summary>
	private static string? ReportedPlaySession(BridgeEvent e) =>
		e.Data is JsonObject data && data["playSessionId"] is JsonValue value && value.GetValueKind() == JsonValueKind.String &&
		value.GetValue<string>() is { Length: > 0 } playSessionId
			? playSessionId
			: null;

	private static long? PositionTicks(BridgeEvent e) =>
		e.Data is JsonObject data && data["positionTicks"] is JsonValue value && value.TryGetValue(out long ticks) ? ticks : null;

	private T? Parse<T>(BridgeEvent e) where T : class
	{
		try
		{
			return e.DataAs<T>();
		}
		catch (JsonException ex)
		{
			logger.LogWarning("Unreadable {Event} event from the mpv backend: {Message}", e.Name, ex.Message);
			return null;
		}
	}

	/// <summary>Runs on a thread-pool thread when the process exits; guarded, nothing may escape it.</summary>
	private void OnExited(Session session, int exitCode)
	{
		var exitSeenAt = Environment.TickCount64;
		try
		{
			// decided by what came first (docs/BRIDGE.md §6), whenever this runs: FluentFin's stop (shutdown, stdin EOF or a
			// kill the close decided, any exit code: the backend's report may still be in flight, or its server hung), or the
			// exit or a kill decided before the stop (a crash, even when the close path began before the exit was seen)
			if (session.ExitIsTheStops())
			{
				if (exitCode == 0 || !session.Started)
				{
					logger.LogInformation("The mpv backend exited with code {Code}", exitCode);
				}
				else
				{
					logger.LogWarning("The mpv backend exited with code {Code} after FluentFin stopped it (shutdown, stdin EOF or a kill to close): the server may not have its final playback report; FluentFin does not send one", exitCode);
				}

				return;
			}

			if (session.ShuttingDown)
			{
				logger.LogInformation("The mpv backend exited with code {Code}", exitCode);
			}

			if (!session.Started)
			{
				// StartSessionAsync reports it
				return;
			}

			// let the readers finish the last lines (and events) before describing the crash
			session.Client.Closed.Wait(TimeSpan.FromSeconds(1));
			session.StderrPump.Wait(TimeSpan.FromSeconds(1));
			// its cause: what it said in a fatal error event (it exits after one), else what it wrote just before it died: from
			// about 1 s before the exit, or from its last event if that came later (an error logged earlier, or the startup's
			// harmless libmpv traceback, is not why it died). The exit as the system recorded it: this handler can run seconds
			// after it on a busy thread pool. The fatal event comes first: stdout and stderr are read apart, so the event can be
			// read after the stderr lines it follows, and those lines would then count as older than the last event
			var exitedAt = Math.Min(exitSeenAt, session.Process.ExitedAt ?? exitSeenAt);
			var last = session.FatalError is { Length: > 0 } fatal
				? fatal
				: session.LastStderr(since: exitedAt - CrashStderrWindowMs, afterOrder: session.LastEventOrder);
			var message = session.KillReason is { } killed
				? $"The mpv backend was stopped because {killed}"
				: $"The mpv backend stopped unexpectedly (exit code {exitCode}){(string.IsNullOrEmpty(last) ? "" : $": {last}")}";
			logger.LogError("{Message}", message);

			if (Interlocked.CompareExchange(ref _session, null, session) == session)
			{
				ReleaseSession(session);
				SetStatus(new MpvBackendStatus(MpvBackendState.Crashed, session.Hello?.Version, session.MpvVersion, session.MpvKind, message, session.Command.ToString()));
			}

			// the backend cannot send its Stopped report any more (docs/BRIDGE.md §6): one from FluentFin instead
			if (InterruptedPlayback(session, message) is { } interrupted)
			{
				_interrupted.OnNext(interrupted);
			}
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Handling the exit of the mpv backend failed");
		}
	}

	/// <summary>
	/// The play session the backend left unreported when it died: the open one (<see cref="TrackPlaySession"/>), with the
	/// position of the latest <c>position</c> event for that play session, and the server and user it was played as. No
	/// position when there was none: the snapshot's own position is older, while opening it is always 0, and a position of an
	/// earlier play session of the same item is not this one's; any of them would overwrite the resume point (docs/BRIDGE.md
	/// §6). Null when no session is open.
	/// </summary>
	private static MpvInterruptedPlayback? InterruptedPlayback(Session session, string reason)
	{
		if (session.OpenPlay is not { } open)
		{
			return null;
		}

		var position = session.LatestPosition is { } mark && string.Equals(mark.PlaySessionId, open.PlaySessionId, StringComparison.Ordinal) ? mark.Ticks : (long?)null;
		return new MpvInterruptedPlayback(open.ItemId, open.MediaSourceId, open.PlaySessionId, position, reason, open.ServerUrl, open.UserId);
	}

	private async Task PumpStderr(Session session)
	{
		try
		{
			while (await session.Process.StandardError.ReadLineAsync() is { } line)
			{
				var redacted = BridgeRedaction.Redact(line);
				session.AddStderr(redacted);

				logger.LogInformation("[mpv backend] {Line}", redacted);
			}
		}
		catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
		{
			// the pipe closes with the process
		}
		catch (Exception ex)
		{
			logger.LogDebug(ex, "Reading the mpv backend's stderr stopped");
		}
	}

	private MpvBackendStatus StatusOf(Session session) =>
		new(MpvBackendState.Running, session.Hello?.Version, session.MpvVersion, session.MpvKind, _status.Value.LastError, session.Command.ToString());

	private BridgeException Fail(string message, Exception? inner)
	{
		logger.LogError(inner, "{Message}", message);
		SetStatus(new MpvBackendStatus(MpvBackendState.Failed, LastError: message));
		return new BridgeException(message, inner);
	}

	private void SetStatus(MpvBackendStatus status) => _status.OnNext(status);

	private static string? StringOf(JsonNode? node) =>
		node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

	/// <summary>Kills whatever runs (the container disposes this at exit). Never throws.</summary>
	public void Dispose()
	{
		if (Interlocked.Exchange(ref _session, null) is { } session)
		{
			session.MarkShuttingDown();
			KillProcess(session);
			ReleaseSession(session);
		}
	}
}
