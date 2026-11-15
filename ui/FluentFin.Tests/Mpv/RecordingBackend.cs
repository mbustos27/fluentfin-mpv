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

	public string[] Commands => [.. _sent.Select(x => x.Command)];

	public JsonObject Args(string command) => _sent.Last(x => x.Command == command).Args!;

	/// <summary>Commands sent and waits for the stop report, in order; a test adds its own steps (navigation, logout).</summary>
	public ConcurrentQueue<string> Timeline { get; } = new();

	/// <summary>When the backend is done with its Stopped report: <see cref="WaitForStopReportsAsync"/> waits for this (done by default).</summary>
	public Task ReportsDone { get; set; } = Task.CompletedTask;

	public MpvBackendStatus Status { get; private set; } = new(MpvBackendState.NotStarted);
	public IObservable<MpvBackendStatus> StatusChanged => Observable.Return(Status);
	public Subject<BridgeEvent> EventSubject { get; } = new();
	public IObservable<BridgeEvent> Events => EventSubject;
	public BridgeState? LastState => null;
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

	public Task<JsonNode?> SendAsync(string command, JsonObject? args = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
	{
		_sent.Enqueue((command, args));
		Timeline.Enqueue("send " + command);
		if (CommandFailure is { } failure && failure.Command == command)
		{
			return Task.FromException<JsonNode?>(failure.Failure);
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

	public Task ShutdownAsync() => Task.CompletedTask;

	public void Kill(string reason)
	{
	}
}
