using System.Text.Json.Nodes;

namespace FluentFin.Core.Mpv;

/// <summary>The jellyfin-mpv-shim backend process and the bridge connection to it (docs/BRIDGE.md).</summary>
public interface IMpvBackend
{
	MpvBackendStatus Status { get; }

	/// <summary>The current status first, then every change (from any thread).</summary>
	IObservable<MpvBackendStatus> StatusChanged { get; }

	/// <summary>Every event of every backend process this app starts (from the reader thread).</summary>
	IObservable<BridgeEvent> Events { get; }

	/// <summary>The last <c>state</c> snapshot of the running backend, null before the first one.</summary>
	BridgeState? LastState { get; }

	/// <summary>
	/// Once per backend process that exits unexpectedly (a crash, or a kill decided before FluentFin's own stop such as the
	/// watchdog's; never after <c>shutdown</c>, stdin EOF or a kill the close decided, whatever the exit code) while a play
	/// session was open: shown by a <c>state</c> and not yet closed by its <c>reported</c>. The playback the backend can no
	/// longer report as stopped, with the server and user it was played as (docs/BRIDGE.md §6). Not replayed to later subscribers.
	/// </summary>
	IObservable<MpvInterruptedPlayback> PlaybackInterrupted { get; }

	/// <summary>Starts the backend unless it is running; <paramref name="dev"/> starts it with <c>--dev</c> (restarting one without).</summary>
	Task StartAsync(bool dev = false, CancellationToken cancellationToken = default);

	/// <summary>
	/// Sends <c>connect</c> with this identity and the embedded video window, unless the running backend is already connected
	/// as exactly this identity. A null identity sends only <c>protocol</c> and <c>window</c> (dev backends). Returns the mpv version.
	/// </summary>
	Task<string?> ConnectAsync(BridgeIdentity? identity, CancellationToken cancellationToken = default);

	/// <summary>
	/// fluentfin-mpv (startup-perf): starts the backend unless it is running and connects it as this identity with
	/// <c>prewarm</c> (docs/BRIDGE.md §4: mpv without its video window until the first play), unless it is already connected
	/// as exactly this identity. A later <see cref="ConnectAsync"/> as the same identity then sends nothing. With a surface
	/// that cannot connect ahead (<see cref="IMpvVideoSurface.CanConnectAhead"/>: FluentFin's own player controls) it only
	/// starts the backend: the first play makes FluentFin's mpv and connects.
	/// </summary>
	Task PrewarmAsync(BridgeIdentity identity, CancellationToken cancellationToken = default);

	Task<JsonNode?> SendAsync(string command, JsonObject? args = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

	/// <summary>
	/// Sends <c>shutdown</c> and kills the backend if it has not exited 10 s later (docs/BRIDGE.md §3); a backend still
	/// starting, or with a <c>connect</c> still unanswered, after 3 s is killed instead. A pending <c>play</c> is not waited
	/// for: the backend runs <c>shutdown</c> alongside it. Those kills are the close's own: never reported as a crash. A backend that is being killed (a kill decided before the close, such as the watchdog's) or had already died
	/// gets no <c>shutdown</c>; this waits, bounded (about 2 s), for its exit to be seen and handled, so when it returns, its
	/// <see cref="PlaybackInterrupted"/> has been raised if a play session was open (docs/BRIDGE.md §6).
	/// </summary>
	Task ShutdownAsync();

	/// <summary>
	/// Waits, at most <paramref name="timeout"/>, until the running backend owes no Stopped report (docs/BRIDGE.md §5, §6): no
	/// play session a <c>state</c> showed is still waiting for its <c>reported</c>, and the latest <c>state</c> is not
	/// active; or the backend has exited and its exit has been handled. For a sign-out, which revokes the token the backend
	/// reports with. True when so (at once when nothing runs), false when the time ran out. Never throws.
	/// </summary>
	Task<bool> WaitForStopReportsAsync(TimeSpan timeout);

	/// <summary>
	/// Kills the backend (and the mpv it started) now, for example when mpv hangs; reported as a crash with this reason.
	/// Ignored once FluentFin's own stop of that backend has begun (its exit is then the stop's, docs/BRIDGE.md §6).
	/// Never throws (the watchdog calls it from a timer thread).
	/// </summary>
	void Kill(string reason);
}

public enum MpvBackendState
{
	NotStarted,
	Starting,
	Running,
	Stopped,
	Crashed,
	Failed,
}

public sealed record MpvBackendStatus(MpvBackendState State,
									  string? BackendVersion = null,
									  string? MpvVersion = null,
									  string? MpvKind = null,
									  string? LastError = null,
									  string? Command = null)
{
	public bool IsRunning => State is MpvBackendState.Running;

	/// <summary>One line for the Settings page.</summary>
	public string Describe()
	{
		var text = State switch
		{
			MpvBackendState.NotStarted => "Not running, starts after sign-in (or with the first MPV playback)",
			MpvBackendState.Starting => "Starting",
			MpvBackendState.Running => "Running" +
				(BackendVersion is null ? "" : $", jellyfin-mpv-shim {BackendVersion}") +
				(MpvVersion is null ? ", not connected yet" : $", mpv {MpvVersion}{(MpvKind is null ? "" : $" ({MpvKind})")}"),
			MpvBackendState.Stopped => "Stopped, starts at the next sign-in or MPV playback",
			MpvBackendState.Crashed => "Stopped unexpectedly, restarts with the next MPV playback",
			MpvBackendState.Failed => "Could not start",
			_ => State.ToString(),
		};

		return LastError is null ? text : $"{text}. Last error: {LastError}";
	}
}

/// <summary>
/// The play session the backend left unreported when it died: ids from the <c>state</c> that opened it, the position from
/// its latest <c>position</c> event for that play session. Null when there was none (it was still opening, and an opening
/// snapshot always says 0; a position of an earlier play session of the same item is not this one's): then the stop is
/// reported as failed with no position, so the item's resume point stays. <paramref name="ServerUrl"/> and
/// <paramref name="UserId"/> are the server and user it was played as (the backend's connection when the <c>play</c> that
/// caused it was sent, null when there was none): it is reported only while FluentFin's client is still that server and user, never to a server or as a
/// user FluentFin switched to since (docs/BRIDGE.md §6).
/// </summary>
public sealed record MpvInterruptedPlayback(string ItemId, string? MediaSourceId, string? PlaySessionId, long? PositionTicks, string Reason, string? ServerUrl = null, string? UserId = null)
{
	/// <summary>A <c>position</c> event for this play session was seen: <see cref="PositionTicks"/> is where it was.</summary>
	public bool PositionKnown => PositionTicks is not null;

	/// <summary>Never the server URL as given (records print every member by default): it may carry credentials or an api_key.</summary>
	public override string ToString() =>
		$"{ItemId} (play session {PlaySessionId}, media source {MediaSourceId}) at {(PositionTicks is { } ticks ? ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) : "no position")}" +
		$" on {BridgeRedaction.ServerForLog(ServerUrl)} as user {UserId}: {Reason}";
}

/// <summary>The window mpv draws into. Created on the UI thread the first time it is asked for, then the same for the app's lifetime.</summary>
public interface IMpvVideoSurface
{
	Task<nint> GetHandleAsync();

	/// <summary>
	/// fluentfin-mpv player controls: the <c>connect.window</c> for the next connect (docs/BRIDGE.md §4): embedded into
	/// <see cref="GetHandleAsync"/>'s window, or attached to an mpv FluentFin runs itself. A different window than the
	/// running backend's restarts it (it is fixed for a backend's life).
	/// </summary>
	async Task<BridgeWindow> GetWindowAsync() => BridgeWindow.Embedded(await GetHandleAsync());

	/// <summary>
	/// fluentfin-mpv (startup-perf): <see cref="GetWindowAsync"/> may be asked ahead of playback without making a player:
	/// true for an embedded window (FluentFin's child window), false for attached (asking makes FluentFin's in-process
	/// mpv). The pre-warm connects only when it is true; otherwise it only starts the backend, and the first play makes
	/// the player and connects (docs/BRIDGE.md §3, §4).
	/// </summary>
	bool CanConnectAhead => true;
}

/// <summary>Finds and starts the backend process; the Windows implementation puts it in a kill-on-close job object.</summary>
public interface IMpvBackendLauncher
{
	MpvBackendCommand? Resolve(bool dev, out string searched);

	IMpvBackendProcess Start(MpvBackendCommand command);
}

public interface IMpvBackendProcess : IDisposable
{
	int Id { get; }

	/// <summary>The backend's stdout: protocol lines only.</summary>
	TextReader StandardOutput { get; }

	/// <summary>The backend's stdin: requests. Closing it is a shutdown request.</summary>
	TextWriter StandardInput { get; }

	/// <summary>The backend's stderr: free-form log text.</summary>
	TextReader StandardError { get; }

	/// <summary>Completes with the exit code when the process has exited.</summary>
	Task<int> Exited { get; }

	/// <summary>
	/// When the process ended, as an <see cref="Environment.TickCount64"/> value: the time the system recorded, not when
	/// <see cref="Exited"/> was seen (a busy thread pool can see it seconds later). Null before the exit, or when not known.
	/// </summary>
	long? ExitedAt => null;

	/// <summary>
	/// The process has ended, as the system knows it now: true from the moment it ended, even before <see cref="Exited"/>
	/// is seen (a busy thread pool can see it seconds later). Whether an exit came before FluentFin's own stop is decided
	/// with this (docs/BRIDGE.md §6). Never throws.
	/// </summary>
	bool HasExited => Exited.IsCompleted;

	/// <summary>Ends the process and everything it started. Should not throw; callers guard it anyway.</summary>
	void Kill();
}
