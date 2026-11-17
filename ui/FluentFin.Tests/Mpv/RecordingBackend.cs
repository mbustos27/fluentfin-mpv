using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json.Nodes;
using FluentFin.Core.Mpv;

namespace FluentFin.Tests.Mpv;

/// <summary>An <see cref="IMpvBackend"/> that records what the player page and the sign-out ask of it, in order.</summary>
internal sealed class RecordingBackend : IMpvBackend
{
	private readonly ConcurrentQueue<(string Command, JsonObject? Args)> _sent = new();

	public Exception? StartFailure { get; set; }
	public Task StartGate { get; set; } = Task.CompletedTask;

	/// <summary><c>connect</c> waits for this, then fails with <see cref="ConnectFailure"/> if set.</summary>
	public Task ConnectGate { get; set; } = Task.CompletedTask;
	public Exception? ConnectFailure { get; set; }

	/// <summary>This command is refused (after it was recorded).</summary>
	public (string Command, Exception Failure)? CommandFailure { get; set; }
	public int Starts { get; private set; }
	public bool Dev { get; private set; }
	public List<BridgeIdentity?> Identities { get; } = [];

	/// <summary>The commands sent, without the Skip intro switches and the auto-play choice every play sends first (<see cref="AllCommands"/>; the tests of them are in VideoPlayerViewModelMpvTests.Controls), so the order tests keep reading what they are about.</summary>
	public string[] Commands => [.. AllCommands.Where(x => !IsPlaySetting(x))];

	/// <summary>The settings every play sends before it (1.2.0: set_auto_play too).</summary>
	public static bool IsPlaySetting(string command) => command is "set_skip_options" or "set_auto_play";

	public string[] AllCommands => [.. _sent.Select(x => x.Command)];

	public JsonObject Args(string command) => _sent.Last(x => x.Command == command).Args!;

	/// <summary>Commands sent and waits for the stop report, in order; a test adds its own steps (navigation, logout).</summary>
	public ConcurrentQueue<string> Timeline { get; } = new();

	/// <summary>When the backend is done with its Stopped report: <see cref="WaitForStopReportsAsync"/> waits for this (done by default).</summary>
	public Task ReportsDone { get; set; } = Task.CompletedTask;

	public MpvBackendStatus Status { get; set; } = new(MpvBackendState.NotStarted);
	public IObservable<MpvBackendStatus> StatusChanged => Observable.Return(Status);
	public Subject<BridgeEvent> EventSubject { get; } = new();
	public IObservable<BridgeEvent> Events => EventSubject;
	public BridgeState? LastState { get; set; }
	public IObservable<MpvInterruptedPlayback> PlaybackInterrupted { get; } = new Subject<MpvInterruptedPlayback>();

	public async Task StartAsync(bool dev = false, CancellationToken cancellationToken = default)
	{
		Starts++;
		Dev |= dev;
		await StartGate;
		if (StartFailure is not null)
		{
			throw StartFailure;
		}

		Status = new MpvBackendStatus(MpvBackendState.Running);
	}

	public async Task<string?> ConnectAsync(BridgeIdentity? identity, CancellationToken cancellationToken = default)
	{
		Identities.Add(identity);
		await ConnectGate;
		if (ConnectFailure is not null)
		{
			throw ConnectFailure;
		}

		return "0.41.0";
	}

	/// <summary>Identities pre-warmed, in order (<see cref="PrewarmAsync"/>: a start, then a <c>connect</c> with <c>prewarm</c>).</summary>
	public List<BridgeIdentity> Prewarmed { get; } = [];

	/// <summary><see cref="ShutdownAsync"/> calls.</summary>
	public int Shutdowns { get; private set; }

	public async Task PrewarmAsync(BridgeIdentity identity, CancellationToken cancellationToken = default)
	{
		Timeline.Enqueue("prewarm");
		await StartAsync(false, cancellationToken);
		cancellationToken.ThrowIfCancellationRequested();
		await ConnectGate.WaitAsync(cancellationToken);
		if (ConnectFailure is not null)
		{
			throw ConnectFailure;
		}

		Prewarmed.Add(identity);
	}

	/// <summary>A command is answered only when the task this returns for it completes (null: at once); the service holds its gate meanwhile.</summary>
	public Func<string, Task?>? Hold { get; set; }

	public Task<JsonNode?> SendAsync(string command, JsonObject? args = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
	{
		_sent.Enqueue((command, args));
		if (!IsPlaySetting(command))
		{
			Timeline.Enqueue("send " + command);   // the switches every play sends are not part of the orders the timeline tests read
		}

		if (CommandFailure is { } failure && failure.Command == command)
		{
			return Task.FromException<JsonNode?>(failure.Failure);
		}

		if (Hold?.Invoke(command) is { } held)
		{
			return held.ContinueWith<JsonNode?>(_ => new JsonObject(), TaskScheduler.Default);
		}

		return Task.FromResult<JsonNode?>(new JsonObject());
	}

	public async Task<bool> WaitForStopReportsAsync(TimeSpan timeout)
	{
		Timeline.Enqueue("wait for the stop report");
		try
		{
			await ReportsDone.WaitAsync(timeout);
			Timeline.Enqueue("stop report done");
			return true;
		}
		catch (TimeoutException)
		{
			Timeline.Enqueue("stop report timed out");
			return false;
		}
	}

	public Task ShutdownAsync()
	{
		Shutdowns++;
		Timeline.Enqueue("shutdown");
		Status = new MpvBackendStatus(MpvBackendState.Stopped);
		return Task.CompletedTask;
	}

	public void Kill(string reason)
	{
	}
}
