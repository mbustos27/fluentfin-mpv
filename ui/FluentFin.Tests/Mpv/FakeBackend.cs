using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using FluentFin.Core.Mpv;

namespace FluentFin.Tests.Mpv;

/// <summary>A backend process played by the test over real pipes: answers requests, sends events, exits on demand.</summary>
internal sealed class FakeBackendProcess : IMpvBackendProcess
{
	private static int s_nextId = 1000;
	private readonly PipePair _pipes = new();
	private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly Task _loop;

	public FakeBackendProcess(Func<FakeBackendProcess, JsonObject, JsonObject?>? respond = null, bool sayHello = true, int protocol = 1, bool readsInput = true)
	{
		Respond = respond ?? ((_, _) => new JsonObject());
		// a backend that never reads its stdin: the pipe fills and the UI's writes block
		_loop = readsInput ? Task.Run(Loop) : Task.CompletedTask;
		if (sayHello)
		{
			_ = Event("hello", new JsonObject { ["protocol"] = protocol, ["backend"] = "jellyfin-mpv-shim", ["version"] = "3.3.0", ["bridge"] = "1" });
		}
	}

	/// <summary>Result for a request, or null to answer with an error.</summary>
	public Func<FakeBackendProcess, JsonObject, JsonObject?> Respond { get; set; }

	public ConcurrentQueue<JsonObject> Requests { get; } = new();
	public IEnumerable<string> Commands => Requests.Select(x => (string)x["cmd"]!);
	/// <summary>The UI closed the backend's stdin (EOF while the process was running).</summary>
	public bool StdinClosed { get; private set; }

	private volatile bool _exiting;
	public bool Killed { get; private set; }

	/// <summary><see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> of the first <see cref="Kill"/>, 0 before.</summary>
	public long KilledAt { get; private set; }

	/// <summary>Thrown by <see cref="Kill"/> after the process is gone, like Process.Kill(entireProcessTree) racing the job close.</summary>
	public Exception? KillFailure { get; set; }

	/// <summary>False: <see cref="Kill"/> does not end the process (a kill that did nothing).</summary>
	public bool KillEnds { get; set; } = true;

	/// <summary>
	/// Null: a kill's exit is seen at once, inside <see cref="Kill"/>. A value: the process ends at once (its pipes close),
	/// and its exit is seen that much later on a thread-pool thread, like the real launcher (TerminateJobObject, then the
	/// registered wait's callback). <see cref="TimeSpan.Zero"/>: as soon as a thread-pool thread gets to it.
	/// </summary>
	public TimeSpan? KillExitSeenAfter { get; set; }

	/// <summary>
	/// The exit code once <c>shutdown</c> was answered (1: the backend's 8 s deadline ran out, docs/BRIDGE.md §3); null: it
	/// answers and does not exit, and keeps reading.
	/// </summary>
	public int? ShutdownExitCode { get; set; } = 0;

	/// <summary>The exit code at stdin EOF (also a shutdown request, docs/BRIDGE.md §1); null: it does not exit.</summary>
	public int? EofExitCode { get; set; } = 0;

	public int Id { get; } = Interlocked.Increment(ref s_nextId);
	public TextReader StandardOutput => _pipes.UiReader;
	public TextWriter StandardInput => _pipes.UiWriter;
	public TextReader StandardError => _pipes.UiStderr;
	public Task<int> Exited => _exited.Task;

	private long _exitedAt;

	/// <summary>When the process ended (<see cref="Environment.TickCount64"/>), even if its exit is seen later; null before.</summary>
	public long? ExitedAt => Volatile.Read(ref _exitedAt) is var at and not 0 ? at : null;

	private void MarkEnded() => Interlocked.CompareExchange(ref _exitedAt, Environment.TickCount64, 0);

	/// <summary>Like the real launcher (asked of Windows): true from the moment the process ended, even before its exit is seen.</summary>
	public bool HasExited => ExitedAt is not null;

	public Task Event(string name, JsonObject data) => _pipes.SendFromBackend(new JsonObject { ["event"] = name, ["data"] = data }.ToJsonString());

	public Task Stderr(string line) => _pipes.BackendStderr.WriteLineAsync(line);

	/// <summary>The backend is done with the Stopped report of this play session (docs/BRIDGE.md §5).</summary>
	public Task Reported(string? itemId, string playSessionId, string outcome = "sent") =>
		Event("reported", new JsonObject { ["itemId"] = itemId, ["playSessionId"] = playSessionId, ["outcome"] = outcome });

	/// <summary>Like a process that ends: its ends of all three pipes close (a write the UI has pending on stdin fails).</summary>
	public void Exit(int code) => Exit(code, seenAfter: null);

	private void Exit(int code, TimeSpan? seenAfter)
	{
		MarkEnded();
		_exiting = true;
		_pipes.CloseBackendOutput();
		_pipes.CloseBackendInput();
		if (seenAfter is not { } delay)
		{
			_exited.TrySetResult(code);
			return;
		}

		// seen on a thread-pool thread, not inside this call
		_ = Task.Run(async () =>
		{
			if (delay > TimeSpan.Zero)
			{
				await Task.Delay(delay);
			}
			_exited.TrySetResult(code);
		});
	}

	/// <summary>The process exits but its pipes stay open, as when something it started still holds them.</summary>
	public void ExitLeavingThePipesOpen(int code)
	{
		MarkEnded();
		_exited.TrySetResult(code);
	}

	public void Kill()
	{
		if (!Killed)
		{
			KilledAt = System.Diagnostics.Stopwatch.GetTimestamp();
		}
		Killed = true;
		if (KillEnds)
		{
			Exit(-1, KillExitSeenAfter);
		}

		if (KillFailure is not null)
		{
			throw KillFailure;
		}
	}

	private async Task Loop()
	{
		try
		{
			while (await _pipes.BackendReader.ReadLineAsync() is { } line)
			{
				var request = JsonNode.Parse(line)!.AsObject();
				Requests.Enqueue(request);
				var result = Respond(this, request);
				var response = result is null
					? new JsonObject { ["id"] = request["id"]!.DeepClone(), ["ok"] = false, ["error"] = new JsonObject { ["code"] = "auth_failed", ["message"] = "no" } }
					: new JsonObject { ["id"] = request["id"]!.DeepClone(), ["ok"] = true, ["result"] = result };
				await _pipes.SendFromBackend(response.ToJsonString());

				if ((string?)request["cmd"] == "shutdown" && ShutdownExitCode is { } code)
				{
					Exit(code);
					return;
				}
			}

			if (_exiting)
			{
				// the input ended because the process did (a kill, an exit), not because the UI closed it
				return;
			}

			// stdin EOF = shutdown
			StdinClosed = true;
			if (EofExitCode is { } eofCode)
			{
				Exit(eofCode);
			}
		}
		catch (Exception ex) when (ex is IOException or ObjectDisposedException)
		{
		}
	}

	public void Dispose()
	{
		// a kill's exit that is still on its way is seen when it comes: releasing the real process's handles does not end
		// the wait for its exit (JobProcess waits on a duplicate handle)
		if (!(Killed && KillEnds && KillExitSeenAfter is not null))
		{
			MarkEnded();
			_exited.TrySetResult(-1);
		}
		_pipes.Dispose();
	}
}

internal sealed class FakeLauncher(Func<FakeBackendProcess> start) : IMpvBackendLauncher
{
	public List<FakeBackendProcess> Started { get; } = [];
	public List<MpvBackendCommand> Commands { get; } = [];

	public MpvBackendCommand? Resolve(bool dev, out string searched)
	{
		searched = "fake";
		return new MpvBackendCommand("python.exe", ["-I", "-m", MpvBackendCommand.BridgeModule, "--config-dir", "cfg", .. (dev ? new[] { "--dev" } : [])], "test");
	}

	public IMpvBackendProcess Start(MpvBackendCommand command)
	{
		Commands.Add(command);
		var process = start();
		Started.Add(process);
		return process;
	}
}

internal sealed class FakeSurface : IMpvVideoSurface
{
	public nint Handle { get; set; } = 0x1234;

	public Task<nint> GetHandleAsync() => Task.FromResult(Handle);
}
