using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Text.Json.Nodes;
using FluentFin.Core.Mpv;
using Microsoft.Extensions.Logging;

namespace FluentFin.Tests.Mpv;

public sealed class MpvBackendHostTests : IDisposable
{
	private static readonly BridgeIdentity Alice = new("http://jf:8096", "srv", "Home", "aaaa", "alice", "token-alice-123", "dev", "PC", "FluentFin", "1.0");
	private static readonly BridgeIdentity Bob = Alice with { UserId = "bbbb", UserName = "bob", AccessToken = "token-bob-456" };

	private readonly ListLogger<MpvBackendHost> _log = new();
	private readonly FakeLauncher _launcher;
	private readonly MpvBackendHost _host;

	public MpvBackendHostTests()
	{
		_launcher = new FakeLauncher(() => new FakeBackendProcess(ConnectReturnsVersion));
		_host = new MpvBackendHost(_launcher, new FakeSurface(), _log) { HelloTimeout = TimeSpan.FromSeconds(5), ShutdownTimeout = TimeSpan.FromSeconds(2) };
	}

	public void Dispose() => _host.Dispose();

	private static JsonObject? ConnectReturnsVersion(FakeBackendProcess process, JsonObject request) => (string?)request["cmd"] == "connect"
		? new JsonObject { ["mpv"] = "0.41.0", ["mpvBackend"] = "libmpv" }
		: new JsonObject();

	[Fact]
	public async Task Start_WaitsForHello_AndIsRunning()
	{
		await _host.StartAsync();
		await _host.StartAsync();

		Assert.Single(_launcher.Started);
		Assert.Equal(MpvBackendState.Running, _host.Status.State);
		Assert.Equal("3.3.0", _host.Status.BackendVersion);
		Assert.DoesNotContain("--dev", _launcher.Commands[0].Arguments);
	}

	[Fact]
	public async Task Start_FailsWithTheLastStderrLine_WhenTheBackendExitsBeforeHello()
	{
		var host = new MpvBackendHost(new FakeLauncher(() =>
		{
			var process = new FakeBackendProcess(sayHello: false);
			_ = Task.Run(async () =>
			{
				await process.Stderr("Traceback (most recent call last):");
				await process.Stderr("ModuleNotFoundError: No module named 'jellyfin_mpv_shim.bridge'");
				await Task.Delay(50);
				process.Exit(1);
			});
			return process;
		}), new FakeSurface(), _log) { HelloTimeout = TimeSpan.FromSeconds(5) };

		var error = await Assert.ThrowsAsync<BridgeException>(() => host.StartAsync());

		Assert.Contains("exit code 1", error.Message);
		Assert.Contains("No module named 'jellyfin_mpv_shim.bridge'", error.Message);
		Assert.Equal(MpvBackendState.Failed, host.Status.State);
	}

	[Fact]
	public async Task Start_RefusesAnotherProtocolVersion()
	{
		var host = new MpvBackendHost(new FakeLauncher(() => new FakeBackendProcess(protocol: 2)), new FakeSurface(), _log) { ShutdownTimeout = TimeSpan.FromSeconds(1) };

		var error = await Assert.ThrowsAsync<BridgeException>(() => host.StartAsync());

		Assert.Contains("protocol 2", error.Message);
		Assert.Equal(MpvBackendState.Failed, host.Status.State);
	}

	[Fact]
	public async Task Connect_SendsTheIdentityOnce_AndAgainAfterAUserSwitch()
	{
		await _host.StartAsync();

		var version = await _host.ConnectAsync(Alice);
		await _host.ConnectAsync(Alice);
		await _host.ConnectAsync(Bob);

		var connects = _launcher.Started[0].Requests.Where(x => (string?)x["cmd"] == "connect").ToList();
		Assert.Equal(2, connects.Count);
		Assert.Equal("alice", (string?)connects[0]["args"]!["user"]!["name"]);
		Assert.Equal("token-bob-456", (string?)connects[1]["args"]!["accessToken"]);
		Assert.Equal(0x1234, (long?)connects[0]["args"]!["window"]!["hwnd"]);
		Assert.Equal("0.41.0", version);
		Assert.Equal("0.41.0", _host.Status.MpvVersion);
		Assert.DoesNotContain(_log.Messages, x => x.Contains("token-alice-123") || x.Contains("token-bob-456"));
	}

	[Fact]
	public async Task Connect_Failure_IsReportedAndRetriedNextTime()
	{
		var refuse = true;
		var host = new MpvBackendHost(new FakeLauncher(() => new FakeBackendProcess((_, request) =>
			(string?)request["cmd"] == "connect" && refuse ? null : new JsonObject { ["mpv"] = "0.41.0" })), new FakeSurface(), _log);
		await host.StartAsync();

		var error = await Assert.ThrowsAsync<BridgeCommandException>(() => host.ConnectAsync(Alice));
		refuse = false;
		await host.ConnectAsync(Alice);

		Assert.Equal("auth_failed", error.Code);
		Assert.Equal(MpvBackendState.Running, host.Status.State);
		host.Dispose();
	}

	[Fact]
	public async Task Crash_IsReported_FailsPendingRequests_AndTheNextStartRestarts()
	{
		var states = new ConcurrentQueue<MpvBackendState>();
		using var subscription = _host.StatusChanged.Subscribe(x => states.Enqueue(x.State));
		await _host.StartAsync();
		var first = _launcher.Started[0];
		await _host.SendAsync(Bridge.Commands.Ping);

		// the next request is never answered: the process dies first
		var hanging = new TaskCompletionSource();
		first.Respond = (_, _) => { hanging.Task.Wait(); return new JsonObject(); };
		var unanswered = _host.SendAsync(Bridge.Commands.GetState, timeout: TimeSpan.FromSeconds(10));
		await first.Stderr("Fatal Python error: Segmentation fault");
		await first.Stderr("2026-11-14 00:10:09,753 [    INFO] mpv: cplayer: VO: [gpu-next] 1280x720 rgb24");
		await Task.Delay(100);
		first.Exit(3);
		hanging.SetResult();

		await Assert.ThrowsAsync<BridgeClosedException>(() => unanswered);
		await BridgeClientTests.WaitUntil(() => _host.Status.State == MpvBackendState.Crashed);
		Assert.Contains("exit code 3", _host.Status.LastError);
		Assert.Contains("Segmentation fault", _host.Status.LastError);
		Assert.DoesNotContain("gpu-next", _host.Status.LastError);
		await Assert.ThrowsAsync<BridgeClosedException>(() => _host.SendAsync(Bridge.Commands.Ping));

		await _host.StartAsync();

		Assert.Equal(2, _launcher.Started.Count);
		Assert.Equal(MpvBackendState.Running, _host.Status.State);
		Assert.Contains(MpvBackendState.Crashed, states);
	}

	// ---- round 5, item 4: the crash message names what the backend wrote just before it died: lines from about 1 s before
	// the exit, or after its last event if that came later; never the startup's harmless libmpv traceback

	/// <summary>python-mpv's traceback when there is no libmpv, as the backend logs it at every connect on this PC (then it uses mpv.exe).</summary>
	private static readonly string[] LibmpvMissingTraceback =
	[
		"2026-11-14 11:40:23,855 [ WARNING] player: Could not load libmpv.",
		"Traceback (most recent call last):",
		"  File \"C:\\fluentfin-mpv\\backend\\jellyfin_mpv_shim\\player.py\", line 79, in <module>",
		"    import mpv",
		"  File \"C:\\fluentfin-mpv\\backend\\.venv\\Lib\\site-packages\\mpv.py\", line 50, in <module>",
		"    raise OSError('Cannot find mpv-1.dll, mpv-2.dll or libmpv-2.dll in your system %PATH%. One way to deal with this is to ship the dll with your script')",
		"OSError: Cannot find mpv-1.dll, mpv-2.dll or libmpv-2.dll in your system %PATH%. One way to deal with this is to ship the dll with your script",
		"2026-11-14 11:40:23,858 [    INFO] player: Using external mpv playback backend.",
	];

	private static readonly string[] RealCrash =
	[
		"Traceback (most recent call last):",
		"  File \"C:\\fluentfin-mpv\\backend\\jellyfin_mpv_shim\\bridge.py\", line 412, in _player_loop",
		"    self._tick()",
		"ValueError: the player thread died",
	];

	/// <summary>Writes the lines to the backend's stderr and waits until the host has read the last one.</summary>
	private async Task StderrRead(FakeBackendProcess backend, params string[] lines)
	{
		foreach (var line in lines)
		{
			await backend.Stderr(line);
		}

		await BridgeClientTests.WaitUntil(() => _log.Messages.Any(x => x.EndsWith(lines[^1], StringComparison.Ordinal)));
	}

	/// <summary>Sends a position event and waits until the host has handled it.</summary>
	private async Task PositionRead(FakeBackendProcess backend)
	{
		var seen = 0;
		using var subscription = _host.Events.Where(e => e.Name == Bridge.Events.Position).Subscribe(_ => Interlocked.Increment(ref seen));
		await backend.Event("position", new JsonObject { ["positionTicks"] = 1_000 });
		await BridgeClientTests.WaitUntil(() => Volatile.Read(ref seen) > 0);
	}

	private async Task<string?> CrashMessage(FakeBackendProcess backend)
	{
		backend.Exit(3);
		await BridgeClientTests.WaitUntil(() => _host.Status.State == MpvBackendState.Crashed);
		return _host.Status.LastError;
	}

	[Fact]
	public async Task CrashMessage_NamesTheErrorWrittenAfterTheLastEvent()
	{
		await _host.StartAsync();
		var backend = _launcher.Started[0];
		await PositionRead(backend);
		await StderrRead(backend, RealCrash);

		Assert.Equal("The mpv backend stopped unexpectedly (exit code 3): ValueError: the player thread died", await CrashMessage(backend));
	}

	[Fact]
	public async Task CrashMessage_DoesNotNameAnErrorWrittenBeforeTheLastEvent()
	{
		// an error the backend logged and went on from (an event came after it) is not why it died
		await _host.StartAsync();
		var backend = _launcher.Started[0];
		await StderrRead(backend, "2026-11-14 12:00:00,000 [   ERROR] bridge: a progress report failed: 500");
		await PositionRead(backend);

		Assert.Equal("The mpv backend stopped unexpectedly (exit code 3)", await CrashMessage(backend));
	}

	[Fact]
	public async Task CrashMessage_DoesNotNameAnErrorWrittenMoreThanASecondBeforeTheExit()
	{
		await _host.StartAsync();
		var backend = _launcher.Started[0];
		await StderrRead(backend, "2026-11-14 12:00:00,000 [   ERROR] bridge: a progress report failed: 500");
		await Task.Delay(1400);

		Assert.Equal("The mpv backend stopped unexpectedly (exit code 3)", await CrashMessage(backend));
	}

	[Fact]
	public async Task CrashMessage_NeverNamesTheHarmlessLibmpvTraceback()
	{
		// even right before the exit (a crash just after a connect): it is logged at every connect and harmless
		await _host.StartAsync();
		var backend = _launcher.Started[0];
		await PositionRead(backend);
		await StderrRead(backend, LibmpvMissingTraceback);

		var message = await CrashMessage(backend);

		Assert.Equal("The mpv backend stopped unexpectedly (exit code 3)", message);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task CrashMessage_NamesARealError_BeforeOrAfterTheLibmpvTraceback(bool realErrorFirst)
	{
		await _host.StartAsync();
		var backend = _launcher.Started[0];
		await PositionRead(backend);
		await StderrRead(backend, realErrorFirst ? [.. RealCrash, .. LibmpvMissingTraceback] : [.. LibmpvMissingTraceback, .. RealCrash]);

		Assert.Equal("The mpv backend stopped unexpectedly (exit code 3): ValueError: the player thread died", await CrashMessage(backend));
	}

	[Fact]
	public async Task CrashMessage_NamesARealError_FollowedByAOneLineLibmpvWarning()
	{
		// the libmpv message logged as one line (no traceback of its own): only that line is harmless, the real traceback
		// before it is not
		await _host.StartAsync();
		var backend = _launcher.Started[0];
		await PositionRead(backend);
		await StderrRead(backend, [.. RealCrash, "2026-11-14 11:40:23,855 [ WARNING] player: Could not load libmpv: Cannot find mpv-1.dll, mpv-2.dll or libmpv-2.dll in your system %PATH%."]);

		Assert.Equal("The mpv backend stopped unexpectedly (exit code 3): ValueError: the player thread died", await CrashMessage(backend));
	}

	/// <summary>Sends an error event and waits until the host has handled it.</summary>
	private async Task ErrorRead(FakeBackendProcess backend, string message, bool fatal)
	{
		var seen = 0;
		using var subscription = _host.Events.Where(e => e.Name == Bridge.Events.Error).Subscribe(_ => Interlocked.Increment(ref seen));
		await backend.Event("error", new JsonObject { ["code"] = "mpv_failed", ["message"] = message, ["fatal"] = fatal });
		await BridgeClientTests.WaitUntil(() => Volatile.Read(ref seen) > 0);
	}

	[Fact]
	public async Task CrashMessage_NamesTheBackendsOwnFatalError_EvenWhenItsStderrWasReadBeforeTheEvent()
	{
		// round 6, item 3: the backend says why it is about to exit (a fatal error event), then exits 1. stdout and stderr are
		// read apart: its traceback, written before the event, was read before it, so by read order alone it is "older than
		// the last event" and dropped, and the message lost its cause
		await _host.StartAsync();
		var backend = _launcher.Started[0];
		await StderrRead(backend, RealCrash);
		await ErrorRead(backend, "mpv could not be restarted: ValueError: the player thread died; GET /Items?api_key=secret-in-error", fatal: true);

		var message = await CrashMessage(backend);

		Assert.Equal("The mpv backend stopped unexpectedly (exit code 3): mpv could not be restarted: ValueError: the player thread died; GET /Items?api_key=***", message);
	}

	[Fact]
	public async Task CrashMessage_ANonFatalErrorEvent_IsNotTheCause()
	{
		// an error the backend went on from (fatal: false) says nothing about why it died later
		await _host.StartAsync();
		var backend = _launcher.Started[0];
		await ErrorRead(backend, "mpv could not open the media", fatal: false);
		await StderrRead(backend, "Fatal Python error: Segmentation fault");

		Assert.Equal("The mpv backend stopped unexpectedly (exit code 3): Fatal Python error: Segmentation fault", await CrashMessage(backend));
	}

	[Fact]
	public async Task CrashMessage_NamesTheErrorWrittenJustBeforeTheExit_WhenTheExitIsSeenLate()
	{
		// the exit's handling can run seconds after the exit on a busy thread pool (it failed a loaded test run): the second
		// before the exit is counted from the exit itself, as recorded, not from when it was seen
		var inner = new FakeBackendProcess(ConnectReturnsVersion);
		using var host = new MpvBackendHost(new ProcessLauncher(new LateSeenExitProcess(inner, TimeSpan.FromSeconds(1.5))), new FakeSurface(), _log);
		await host.StartAsync();
		await StderrRead(inner, "Fatal Python error: Segmentation fault");

		inner.Exit(3);
		await BridgeClientTests.WaitUntil(() => host.Status.State == MpvBackendState.Crashed, 10_000);

		Assert.Equal("The mpv backend stopped unexpectedly (exit code 3): Fatal Python error: Segmentation fault", host.Status.LastError);
	}

	/// <summary>A process whose exit is seen <c>late</c> after it happened, like a wait callback on a busy thread pool; knows when it really ended.</summary>
	private sealed class LateSeenExitProcess : IMpvBackendProcess
	{
		private readonly FakeBackendProcess _inner;

		public LateSeenExitProcess(FakeBackendProcess inner, TimeSpan late)
		{
			_inner = inner;
			Exited = inner.Exited.ContinueWith(async t =>
			{
				await Task.Delay(late);
				return t.Result;
			}, TaskScheduler.Default).Unwrap();
		}

		public int Id => _inner.Id;
		public TextReader StandardOutput => _inner.StandardOutput;
		public TextWriter StandardInput => _inner.StandardInput;
		public TextReader StandardError => _inner.StandardError;
		public Task<int> Exited { get; }
		public long? ExitedAt => _inner.ExitedAt;
		public void Kill() => _inner.Kill();
		public void Dispose() => _inner.Dispose();
	}

	private sealed class ProcessLauncher(IMpvBackendProcess process) : IMpvBackendLauncher
	{
		public MpvBackendCommand? Resolve(bool dev, out string searched)
		{
			searched = "fake";
			return new MpvBackendCommand("python.exe", ["-I", "-m", MpvBackendCommand.BridgeModule], "test");
		}

		public IMpvBackendProcess Start(MpvBackendCommand command) => process;
	}

	[Fact]
	public async Task StartFailure_DoesNotNameTheLibmpvTraceback_ButTheRealError()
	{
		var host = new MpvBackendHost(new FakeLauncher(() =>
		{
			var process = new FakeBackendProcess(sayHello: false);
			_ = Task.Run(async () =>
			{
				foreach (var line in RealCrash.Concat(LibmpvMissingTraceback))
				{
					await process.Stderr(line);
				}
				await Task.Delay(50);
				process.Exit(1);
			});
			return process;
		}), new FakeSurface(), _log) { HelloTimeout = TimeSpan.FromSeconds(5) };

		var error = await Assert.ThrowsAsync<BridgeException>(() => host.StartAsync());

		Assert.Contains("ValueError: the player thread died", error.Message);
		Assert.DoesNotContain("mpv-1.dll", error.Message);
	}

	[Fact]
	public async Task Kill_StopsTheBackendAndSaysWhy()
	{
		await _host.StartAsync();

		_host.Kill("mpv stopped responding");

		await BridgeClientTests.WaitUntil(() => _host.Status.State == MpvBackendState.Crashed);
		Assert.True(_launcher.Started[0].Killed);
		Assert.Contains("mpv stopped responding", _host.Status.LastError);
	}

	[Fact]
	public async Task Stderr_GoesToTheLogWithoutTokens()
	{
		await _host.StartAsync();

		await _launcher.Started[0].Stderr("requests: GET http://jf:8096/Items?api_key=token-alice-123 200");
		await _launcher.Started[0].Stderr("shim: headers {'X-Emby-Token': 'token-alice-123'}");

		await BridgeClientTests.WaitUntil(() => _log.Messages.Count(x => x.StartsWith("[mpv backend]")) == 2);
		Assert.DoesNotContain(_log.Messages, x => x.Contains("token-alice-123"));
		Assert.Contains(_log.Messages, x => x.Contains("api_key=***"));
	}

	[Fact]
	public async Task Shutdown_AsksTheBackendToExit()
	{
		await _host.StartAsync();
		var process = _launcher.Started[0];

		await _host.ShutdownAsync();

		Assert.Contains("shutdown", process.Commands);
		Assert.True(process.Exited.IsCompleted);
		Assert.False(process.Killed);
		Assert.Equal(MpvBackendState.Stopped, _host.Status.State);
	}

	[Fact]
	public async Task DevStart_RestartsABackendThatRunsWithoutDev()
	{
		await _host.StartAsync(dev: false);
		await _host.StartAsync(dev: true);
		await _host.StartAsync(dev: false);

		Assert.Equal(2, _launcher.Started.Count);
		Assert.Contains("--dev", _launcher.Commands[1].Arguments);
		Assert.Contains("shutdown", _launcher.Started[0].Commands);
	}

	[Fact]
	public async Task StateEvents_AreRelayedAndRemembered()
	{
		var events = new ConcurrentQueue<BridgeEvent>();
		using var subscription = _host.Events.Subscribe(events.Enqueue);
		await _host.StartAsync();

		await _launcher.Started[0].Event("state", new JsonObject { ["state"] = "playing", ["volume"] = 70, ["itemId"] = "abc" });

		await BridgeClientTests.WaitUntil(() => _host.LastState?.State == "playing");
		Assert.Equal(70, _host.LastState!.Volume);
		Assert.Contains(events, x => x.Name == "state");
	}
}

internal sealed class ListLogger<T> : ILogger<T>
{
	private readonly ConcurrentQueue<string> _messages = new();
	private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

	public IReadOnlyList<string> Messages => [.. _messages];

	/// <summary>The messages with their levels.</summary>
	public IReadOnlyList<(LogLevel Level, string Message)> Entries => [.. _entries];

	public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

	public bool IsEnabled(LogLevel logLevel) => true;

	public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
	{
		var message = formatter(state, exception);
		_entries.Enqueue((logLevel, message));
		_messages.Enqueue(message);
	}
}
