using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Settings;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.Mpv;

/// <summary>Opens the player page and plays the backend's dev-only <c>play_file</c> (no server): <c>--mpv-selftest</c>.</summary>
public sealed record MpvSelfTestRequest(string Url, string? Title)
{
	public const string CommandLineSwitch = "--mpv-selftest";
	public const string TestSource = "av://lavfi:testsrc=size=1280x720:rate=30";

	public static MpvSelfTestRequest Default { get; } = new(TestSource, "FluentFin mpv self-test");
}

/// <summary>fluentfin-mpv: the steps of a start, shown on the player page while it waits.</summary>
public enum MpvStartStep
{
	/// <summary>The play request is being built (one request to the server, <see cref="MpvQueue"/>).</summary>
	LoadingQueue,

	/// <summary>The backend process is being started (not shown when it already runs).</summary>
	StartingBackend,

	/// <summary><c>connect</c> is sent (the backend checks the login with the server).</summary>
	Connecting,

	/// <summary>Connected: <c>play</c> (or <c>play_file</c>) is being sent.</summary>
	Playing,
}

/// <summary>
/// Playback with the mpv backend for the player pages. Each page that plays takes a generation; only the newest page's
/// commands reach the backend, so a page that is being left cannot stop what the page replacing it started, and a page
/// that was left while it was still starting the backend does not start playback afterwards.
/// </summary>
public sealed class MpvPlaybackService
{
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly IMpvBackend _backend;
	private readonly IJellyfinClient _jellyfinClient;
	private readonly ILogger<MpvPlaybackService> _logger;
	private readonly Lock _reportsLock = new();
	private readonly List<Task> _reports = [];
	private long _generation;

	/// <summary>fluentfin-mpv (startup-perf): the [perf] timeline of the latest player page's play.</summary>
	private MpvPlayTimeline? _timeline;

	/// <summary>The generation a stop left behind (no page holds it); equal to <see cref="_generation"/> while no page plays.</summary>
	private long _leftGeneration;

	/// <summary>
	/// How long signing out waits in all for the stop of the mpv playback and the backend's Stopped report: the token is
	/// revoked right after, and the user is waiting for the login page.
	/// </summary>
	public TimeSpan SignOutTimeout { get; set; } = TimeSpan.FromSeconds(2);

	public MpvPlaybackService(IMpvBackend backend, IJellyfinClient jellyfinClient, ILogger<MpvPlaybackService> logger)
	{
		_backend = backend;
		_jellyfinClient = jellyfinClient;
		_logger = logger;

		// lives as long as the app (a singleton), like the backend it watches
		backend.PlaybackInterrupted.Subscribe(playback => Track(ReportInterruptedAsync(playback)));
		// fluentfin-mpv (startup-perf): the first opening/playing/position of a play, timed (MpvPlayTimeline)
		backend.Events.Subscribe(e => Volatile.Read(ref _timeline)?.OnEvent(e));
	}

	/// <summary>
	/// Waits, at most <paramref name="timeout"/>, for the Stopped reports FluentFin is sending for a backend that died
	/// (docs/BRIDGE.md §6): closing FluentFin must not cut them off. Never throws.
	/// </summary>
	public async Task WaitForReportsAsync(TimeSpan timeout)
	{
		Task[] pending;
		lock (_reportsLock)
		{
			pending = [.. _reports];
		}

		if (pending.Length == 0)
		{
			return;
		}

		try
		{
			await Task.WhenAll(pending).WaitAsync(timeout);
		}
		catch (TimeoutException)
		{
			_logger.LogWarning("The stop of the interrupted mpv playback was not reported within {Seconds} s", timeout.TotalSeconds);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Waiting for the stop of the interrupted mpv playback failed");
		}
	}

	private void Track(Task report)
	{
		lock (_reportsLock)
		{
			_reports.Add(report);
		}

		_ = report.ContinueWith(done =>
		{
			lock (_reportsLock)
			{
				_reports.Remove(done);
			}
		}, TaskScheduler.Default);
	}

	public IMpvBackend Backend => _backend;

	/// <summary>A player page takes over; anything older is ignored from now on. Logged with its reason, as every change of generation.</summary>
	public long Begin(string reason = "a player page opened")
	{
		var generation = Interlocked.Increment(ref _generation);
		_logger.LogInformation("mpv playback generation {Old} -> {New}: {Reason}", generation - 1, generation, reason);
		Volatile.Write(ref _timeline, MpvPlayTimeline.Begin(_logger, generation));
		return generation;
	}

	public bool IsCurrent(long generation) => Interlocked.Read(ref _generation) == generation;

	/// <summary>
	/// Plays for the page with this generation. <paramref name="fullScreen"/> is the window's real state, sent before
	/// <c>play</c>: the backend may still think it is full screen from an earlier page. A page that is gone before the
	/// backend is started, or before <c>connect</c>, starts nothing more (false). <paramref name="onStep"/> hears each step
	/// as it begins, on the caller's context.
	/// </summary>
	public async Task<bool> PlayAsync(long generation, BridgeIdentity identity, BridgePlayRequest request, bool fullScreen = false, CancellationToken cancellationToken = default, Action<MpvStartStep>? onStep = null)
	{
		if (!await StartAndConnectAsync(generation, dev: false, identity, onStep, cancellationToken))
		{
			return false;
		}

		await SyncFullScreenAsync(generation, fullScreen, cancellationToken);
		_logger.LogInformation("Playing {Count} item(s) with mpv from index {Index} ({ItemId}) at {Position}, mediaSource={MediaSource}, audio={Audio}, subtitle={Subtitle}",
			request.ItemIds.Count, request.StartIndex, request.StartItemId, TimeSpan.FromTicks(request.StartPositionTicks), request.MediaSourceId, request.AudioStreamIndex, request.SubtitleStreamIndex);
		var timeline = TimelineOf(generation);
		timeline?.Sending(request.StartItemId);
		var sent = await SendIfCurrentAsync(generation, Bridge.Commands.Play, BridgeCommands.Play(request), cancellationToken: cancellationToken);
		if (sent)
		{
			timeline?.Step("play sent and accepted");
		}

		return sent;
	}

	/// <summary>The timeline of this generation's page, null when a newer page took over.</summary>
	private MpvPlayTimeline? TimelineOf(long generation) => IsCurrent(generation) ? Volatile.Read(ref _timeline) : null;

	/// <summary>Dev only: a backend started with <c>--dev</c>, connected with only the window, plays a file or mpv url.</summary>
	public async Task<bool> PlayFileAsync(long generation, MpvSelfTestRequest request, bool fullScreen = false, CancellationToken cancellationToken = default, Action<MpvStartStep>? onStep = null)
	{
		if (!await StartAndConnectAsync(generation, dev: true, null, onStep, cancellationToken))
		{
			return false;
		}

		await SyncFullScreenAsync(generation, fullScreen, cancellationToken);
		_logger.LogInformation("Self-test: playing {Url} with mpv", request.Url);
		var timeline = TimelineOf(generation);
		timeline?.Sending(null);
		var sent = await SendIfCurrentAsync(generation, Bridge.Commands.PlayFile, BridgeCommands.PlayFile(request.Url, request.Title), cancellationToken: cancellationToken);
		if (sent)
		{
			timeline?.Step("play sent and accepted");
		}

		return sent;
	}

	/// <summary>
	/// fluentfin-mpv: starts the backend and connects it, unless the page with this generation is gone by then: a page left
	/// while its queue loaded (or while the backend started) launches no backend and sends no <c>connect</c>.
	/// </summary>
	private async Task<bool> StartAndConnectAsync(long generation, bool dev, BridgeIdentity? identity, Action<MpvStartStep>? onStep, CancellationToken cancellationToken)
	{
		if (!StillCurrent(generation, "starting the mpv backend"))
		{
			return false;
		}

		var wasRunning = _backend.Status.IsRunning;
		if (!wasRunning)
		{
			onStep?.Invoke(MpvStartStep.StartingBackend);
		}

		await _backend.StartAsync(dev, cancellationToken);
		TimelineOf(generation)?.Step(wasRunning ? "backend already running (pre-warmed or played before)" : "backend started");
		if (!StillCurrent(generation, "connecting the mpv backend"))
		{
			return false;
		}

		onStep?.Invoke(MpvStartStep.Connecting);
		await _backend.ConnectAsync(identity, cancellationToken);
		TimelineOf(generation)?.Step("connected");
		onStep?.Invoke(MpvStartStep.Playing);
		return true;
	}

	private bool StillCurrent(long generation, string what)
	{
		if (IsCurrent(generation))
		{
			return true;
		}

		_logger.LogInformation("Not {What} for generation {Generation}: its player page is gone (generation {Current} now)", what, generation, Interlocked.Read(ref _generation));
		return false;
	}

	private async Task SyncFullScreenAsync(long generation, bool fullScreen, CancellationToken cancellationToken)
	{
		try
		{
			await SendIfCurrentAsync(generation, Bridge.Commands.SetFullscreen, BridgeCommands.Fullscreen(fullScreen), cancellationToken: cancellationToken);
		}
		catch (BridgeException ex)
		{
			_logger.LogWarning("Could not tell the mpv backend whether the window is full screen: {Message}", ex.Message);
		}
	}

	/// <summary>
	/// The backend died while playing, so its Stopped report will never come: send one (docs/BRIDGE.md §6), so the server
	/// closes the play session and its transcode now and keeps the resume point. Without a known position for that play
	/// session (still opening) it is sent as failed with no position: the server then leaves the item's resume point and
	/// played state alone, where a position of 0 would reset the one and no position at all would mark it played. Only as
	/// the server and user the session was played as: FluentFin's client must still be that server and user (after a server
	/// or user switch the backend reports or gives up the old session itself, docs/BRIDGE.md §5, §6). Never throws.
	/// </summary>
	private async Task ReportInterruptedAsync(MpvInterruptedPlayback playback)
	{
		try
		{
			if (!Guid.TryParse(playback.ItemId, out var itemId))
			{
				_logger.LogWarning("The mpv backend ended while playing an item with an unreadable id; no stop reported");
				return;
			}

			// read when the crash is handled: FluentFin may have switched servers or users since the session opened
			var current = _jellyfinClient.BaseUrl;
			var currentUser = _jellyfinClient.UserId;
			if (playback.ServerUrl is null || playback.UserId is null)
			{
				_logger.LogWarning("The mpv backend ended while playing {ItemId}, but the server and user it was played as are not known; not reporting its stop", itemId);
				return;
			}

			if (!SameServer(playback.ServerUrl, current))
			{
				// no token, no credentials, no query in the log: the servers by scheme, host and port only
				_logger.LogWarning("The mpv backend ended while playing {ItemId} from {Server}, which is not FluentFin's server any more ({Current}); not reporting its stop there (the backend reports or gives up a session of a server it was switched away from)",
					itemId, BridgeRedaction.ServerForLog(playback.ServerUrl), BridgeRedaction.ServerForLog(current));
				return;
			}

			if (!SameUser(playback.UserId, currentUser))
			{
				// user ids only: never a token
				_logger.LogWarning("The mpv backend ended while playing {ItemId} as user {User}, who is not FluentFin's user any more ({Current}); not reporting its stop as another user (the backend reports or gives up a session of a user it was switched away from)",
					itemId, playback.UserId, currentUser.ToString("N"));
				return;
			}

			if (playback.PositionTicks is { } position)
			{
				_logger.LogWarning("The mpv backend ended while playing {ItemId} at {Position}; reporting the stop for it", itemId, TimeSpan.FromTicks(position));
			}
			else
			{
				_logger.LogWarning("The mpv backend ended before {ItemId} had a position (still opening); reporting the stop for it as failed", itemId);
			}

			await _jellyfinClient.Stop(new PlaybackStopInfo
			{
				ItemId = itemId,
				MediaSourceId = playback.MediaSourceId,
				PlaySessionId = playback.PlaySessionId,
				PositionTicks = playback.PositionTicks,
				Failed = playback.PositionKnown ? null : true,
			});
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Reporting the stop of the interrupted mpv playback failed");
		}
	}

	/// <summary>
	/// The same server: scheme, host and port compared without case, the path without a trailing slash. A session with no
	/// known server, or no current server, is never the same (the report could go anywhere).
	/// </summary>
	public static bool SameServer(string? sessionServer, string? currentServer)
	{
		if (!Uri.TryCreate(sessionServer, UriKind.Absolute, out var a) || !Uri.TryCreate(currentServer, UriKind.Absolute, out var b))
		{
			return false;
		}

		return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) &&
			   string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase) &&
			   a.Port == b.Port &&
			   string.Equals(a.AbsolutePath.TrimEnd('/'), b.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);
	}

	/// <summary>
	/// The same user: Jellyfin ids compared as ids, so case and dashes do not matter (docs/BRIDGE.md §2). A session with an
	/// unreadable user id, or no current user (the empty id), is never the same.
	/// </summary>
	public static bool SameUser(string? sessionUser, Guid currentUser) =>
		Guid.TryParse(sessionUser, out var user) && user != Guid.Empty && user == currentUser;

	/// <summary>Sends a command for the page with this generation, unless a newer page took over or the backend is not running.</summary>
	public async Task<bool> SendIfCurrentAsync(long generation, string command, JsonObject? args = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			if (!IsCurrent(generation))
			{
				_logger.LogInformation("Not sending '{Command}' to the mpv backend: the player page that asked for it is gone", command);
				return false;
			}

			await _backend.SendAsync(command, args, timeout, cancellationToken);
			return true;
		}
		finally
		{
			_gate.Release();
		}
	}

	/// <summary>
	/// The page with this generation is left (<paramref name="reason"/>, logged): stop playback (the backend reports the stop
	/// to the server) unless a newer page already took over. Never throws.
	/// </summary>
	public async Task StopAsync(long generation, string reason = "its player page was left")
	{
		await _gate.WaitAsync();
		try
		{
			if (Interlocked.CompareExchange(ref _generation, generation + 1, generation) != generation)
			{
				_logger.LogInformation("mpv playback generation {Generation} ended ({Reason}): generation {Current} has taken over, nothing to stop", generation, reason, Interlocked.Read(ref _generation));
				return;
			}

			Interlocked.Exchange(ref _leftGeneration, generation + 1);
			_logger.LogInformation("mpv playback generation {Old} -> {New}: {Reason}", generation, generation + 1, reason);
			if (!_backend.Status.IsRunning)
			{
				_logger.LogInformation("Nothing to stop: the mpv backend is not running");
				return;
			}

			await _backend.SendAsync(Bridge.Commands.Stop, timeout: TimeSpan.FromSeconds(5));
		}
		catch (BridgeException ex)
		{
			_logger.LogWarning("Could not stop mpv playback: {Message}", ex.Message);
		}
		finally
		{
			_gate.Release();
		}
	}

	/// <summary>
	/// fluentfin-mpv: FluentFin is about to sign out (Log out, Switch user). That leaves the player page by navigating the
	/// window's root frame, which tells the page nothing, and then revokes the token the backend reports with. So first: the
	/// page that plays is left here, exactly as leaving it does (one <c>stop</c>; the page's own leave later finds it done and
	/// sends nothing), and the backend's Stopped report is waited for, so it goes out while the token is valid (and any
	/// Stopped FluentFin itself owes for a backend that died meanwhile, docs/BRIDGE.md §6). At most
	/// <paramref name="timeout"/> (default <see cref="SignOutTimeout"/>) in all: a hung backend or server does not hold the
	/// sign-out. Never throws.
	/// </summary>
	public async Task StopForSignOutAsync(TimeSpan? timeout = null)
	{
		var bound = timeout ?? SignOutTimeout;
		var watch = System.Diagnostics.Stopwatch.StartNew();
		TimeSpan Left() => bound - watch.Elapsed is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero;
		try
		{
			var generation = Interlocked.Read(ref _generation);
			if (generation != Interlocked.Read(ref _leftGeneration))
			{
				_logger.LogInformation("Signing out: stopping the mpv playback of generation {Generation} first, so the backend reports its stop with a login the server still accepts", generation);
				await StopAsync(generation, "signing out").WaitAsync(Left());
			}
			else
			{
				_logger.LogInformation("Signing out: no player page plays with mpv (generation {Generation}); waiting only for stop reports still owed", generation);
			}

			if (!await _backend.WaitForStopReportsAsync(Left()))
			{
				return;
			}

			await WaitForReportsAsync(Left());
		}
		catch (TimeoutException)
		{
			_logger.LogWarning("Stopping the mpv playback for the sign-out took more than {Seconds} s; signing out without waiting for it", bound.TotalSeconds);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Stopping the mpv playback for the sign-out failed");
		}
	}
}

/// <summary>The identity of the current FluentFin login, for <c>connect</c>.</summary>
public static class MpvIdentity
{
	/// <summary>Null when nobody is logged in.</summary>
	public static BridgeIdentity? FromSession(ISettings? settings)
	{
		if (string.IsNullOrEmpty(SessionInfo.BaseUrl) || SessionInfo.CurrentUser?.Id is not { } userId || string.IsNullOrEmpty(SessionInfo.AccessToken))
		{
			return null;
		}

		var serverName = settings?.Servers?.FirstOrDefault(x => !string.IsNullOrEmpty(x.Id) && x.Id == SessionInfo.ServerId)?.DisplayName;
		if (string.IsNullOrEmpty(serverName) && Uri.TryCreate(SessionInfo.BaseUrl, UriKind.Absolute, out var url))
		{
			serverName = url.Host;
		}

		return new BridgeIdentity(
			SessionInfo.BaseUrl,
			SessionInfo.ServerId,
			serverName,
			userId.ToString("N"),
			SessionInfo.CurrentUser.Name,
			SessionInfo.AccessToken,
			SessionInfo.DeviceId ?? "",
			SessionInfo.DeviceName ?? Environment.MachineName,
			SessionInfo.ClientName ?? "FluentFin",
			SessionInfo.ClientVersion ?? "");
	}
}
