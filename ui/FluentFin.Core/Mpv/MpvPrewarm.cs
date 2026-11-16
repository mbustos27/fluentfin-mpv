using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Settings;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.Mpv;

/// <summary>
/// fluentfin-mpv (startup-perf): after sign-in, with the mpv player selected, the backend is started and connected in the
/// background with <c>prewarm</c> (docs/BRIDGE.md §3, §4), so pressing Play skips both (measured on a real server: backend
/// start ~0.24 s + connect ~0.5 s). With FluentFin's own player controls it is only started: their in-process mpv is made at
/// the first play, which connects (<see cref="IMpvVideoSurface.CanConnectAhead"/>). Idle, the backend holds no video window of mpv's (no input queues attached to FluentFin's,
/// no GPU surface) and has made no request but the connect's user check; it reports nothing. A crash while idle is harmless:
/// there is no play session to report (§6), and the next play starts it again. Signing out cancels a pre-warm in flight and
/// shuts the backend down, so no process keeps the login the sign-out revokes; the next sign-in pre-warms again, as whoever
/// signed in. Closing FluentFin cancels it (<see cref="Cancel"/>); the close shuts the backend down. Never throws.
/// </summary>
public sealed class MpvPrewarm(IMpvBackend backend, ISettings settings, ILogger<MpvPrewarm> logger)
{
	private readonly Lock _lock = new();
	private CancellationTokenSource? _cts;
	private Task _current = Task.CompletedTask;
	private bool _closed;

	/// <summary>How long after sign-in it starts: the home page's first requests go first.</summary>
	public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(2);

	/// <summary>Who to connect as: FluentFin's login when it is asked for (null = nobody signed in).</summary>
	public Func<BridgeIdentity?> Identity { get; set; } = () => MpvIdentity.FromSession(settings);

	/// <summary>The latest pre-warm, or the shutdown of a sign-out; completes, never faults.</summary>
	public Task Current
	{
		get
		{
			lock (_lock)
			{
				return _current;
			}
		}
	}

	/// <summary>FluentFin signed in (its shell is shown): pre-warm the backend as that login, if the mpv player is selected.</summary>
	public void SignedIn()
	{
		try
		{
			if (settings.MediaPlayer != MediaPlayerType.Mpv)
			{
				logger.LogInformation("Not pre-warming the mpv backend: the selected player is {Player}", settings.MediaPlayer);
				return;
			}

			if (Identity() is not { } identity)
			{
				logger.LogInformation("Not pre-warming the mpv backend: nobody is signed in");
				return;
			}

			lock (_lock)
			{
				if (_closed)
				{
					return;
				}

				_cts?.Cancel();
				var cts = _cts = new CancellationTokenSource();
				var previous = _current;
				// off the caller's thread (the UI thread): starting the process and reading its pipes happen there
				_current = Task.Run(() => PrewarmAfterAsync(previous, identity, cts.Token));
			}
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Pre-warming the mpv backend failed");
		}
	}

	/// <summary>
	/// FluentFin is signing out (its shell is left, before the token is revoked): a pre-warm in flight is cancelled and the
	/// backend shut down in the background (bounded, docs/BRIDGE.md §3). Its playback was already stopped and reported by
	/// <see cref="MpvPlaybackService.StopForSignOutAsync"/>.
	/// </summary>
	public void SignedOut()
	{
		lock (_lock)
		{
			_cts?.Cancel();
			_cts = null;
			if (!_closed)
			{
				var previous = _current;
				_current = Task.Run(() => ShutDownAfterAsync(previous));
			}
		}
	}

	/// <summary>FluentFin is closing: no pre-warm from now on, and one in flight is cancelled (the close shuts the backend down).</summary>
	public void Cancel(string reason)
	{
		lock (_lock)
		{
			_closed = true;
			if (_cts is { } cts)
			{
				logger.LogInformation("Cancelling the mpv backend's pre-warm: {Reason}", reason);
				cts.Cancel();
				_cts = null;
			}
		}
	}

	private async Task PrewarmAfterAsync(Task previous, BridgeIdentity identity, CancellationToken cancellationToken)
	{
		// a sign-out's shutdown first: whatever it is shutting down is not this login's
		await Quietly(previous);
		try
		{
			await Task.Delay(Delay, cancellationToken);
			if (backend.LastState is { IsActive: true })
			{
				logger.LogInformation("Not pre-warming the mpv backend: it is playing");
				return;
			}

			var watch = System.Diagnostics.Stopwatch.StartNew();
			logger.LogInformation("Pre-warming the mpv backend for {Identity} (backend {Status})", identity, backend.Status.State);
			await backend.PrewarmAsync(identity, cancellationToken);
			logger.LogInformation("[perf] the mpv backend is pre-warmed ({Ms} ms): Play skips its start{Connect}", watch.ElapsedMilliseconds,
				backend.Status.MpvVersion is null ? "" : " and connect");
		}
		catch (Exception ex) when (cancellationToken.IsCancellationRequested)
		{
			// a start cancelled while it waited for hello fails as a BridgeException, not as a cancellation
			logger.LogInformation("The mpv backend's pre-warm was cancelled ({Error})", BridgeRedaction.Redact(ex.Message));
		}
		catch (Exception ex)
		{
			// as text, redacted: a failure's message may quote a request (docs/BRIDGE.md §1)
			logger.LogWarning("Pre-warming the mpv backend failed; Play starts it instead: {Error}", BridgeRedaction.Redact(ex.Message));
		}
	}

	private async Task ShutDownAfterAsync(Task previous)
	{
		await Quietly(previous);
		try
		{
			if (backend.Status.State is MpvBackendState.Running or MpvBackendState.Starting)
			{
				logger.LogInformation("Signing out: shutting the mpv backend down (the login it holds is about to be revoked)");
				await backend.ShutdownAsync();
			}
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Shutting the mpv backend down for the sign-out failed");
		}
	}

	private static async Task Quietly(Task task)
	{
		try
		{
			await task;
		}
		catch
		{
			// logged where it ran
		}
	}
}
