using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using FluentFin.Core.Mpv;

namespace FluentFin.Tests.Mpv;

/// <summary>
/// fluentfin-mpv player controls: FluentFin's own mpv lives as long as the backend attached to it (docs/BRIDGE.md §4,
/// attached). Whatever ends the backend -- a crash, a kill, its shutdown, a restart for another window -- the surface is told
/// once, so that mpv is ended too: a crashed backend once left it playing behind the error screen and after Back.
/// </summary>
public sealed class BackendEndsItsMpvTests : IDisposable
{
	private static readonly BridgeIdentity Alice = new("http://jf:8096", "srv", "Home", "aaaa", "alice", "token-alice-123", "dev", "PC", "FluentFin", "1.0");

	private readonly ListLogger<MpvBackendHost> _log = new();
	private readonly FakeLauncher _launcher = new(() => new FakeBackendProcess((_, request) => (string?)request["cmd"] == "connect"
		? new JsonObject { ["mpv"] = "0.42.0", ["mpvBackend"] = "external" }
		: new JsonObject()));

	private readonly RecordingSurface _surface = new();
	private readonly MpvBackendHost _host;

	public BackendEndsItsMpvTests()
	{
		_host = new MpvBackendHost(_launcher, _surface, _log) { HelloTimeout = TimeSpan.FromSeconds(5), ShutdownTimeout = TimeSpan.FromSeconds(2) };
		_surface.Launcher = _launcher;
	}

	public void Dispose() => _host.Dispose();

	private sealed class RecordingSurface : IMpvVideoSurface
	{
		public string Pipe { get; set; } = "fluentfin-mpv-1-0011223344556677";

		public FakeLauncher? Launcher { get; set; }

		/// <summary>Each window ended, and whether the newest backend's process had ended when the surface heard it.</summary>
		public ConcurrentQueue<(BridgeWindow Window, bool BackendGone)> Ended { get; } = new();

		public Task<nint> GetHandleAsync() => throw new InvalidOperationException("attached: no window handle is asked for");

		public Task<BridgeWindow> GetWindowAsync() => Task.FromResult(BridgeWindow.Attached(Pipe));

		public void OnBackendExited(BridgeWindow window) =>
			Ended.Enqueue((window, Launcher!.Started.FirstOrDefault(x => x.Requests.Any(r => (string?)r["args"]?["window"]?["ipc"] == window.Ipc))?.HasExited ?? false));

		public string[] EndedPipes => [.. Ended.Select(x => x.Window.Ipc!)];
	}

	private async Task Connected()
	{
		await _host.StartAsync();
		await _host.ConnectAsync(Alice);
	}

	[Fact]
	public async Task WhileTheBackendRuns_ItsMpvIsLeftAlone()
	{
		await Connected();
		await _host.ConnectAsync(Alice);
		await _host.SendAsync("stop");

		Assert.Empty(_surface.Ended);
	}

	[Fact]
	public async Task ACrash_EndsTheMpvTheBackendWasAttachedTo()
	{
		await Connected();

		_launcher.Started[0].Exit(3);

		await BridgeClientTests.WaitUntil(() => !_surface.Ended.IsEmpty);
		await BridgeClientTests.WaitUntil(() => _host.Status.State == MpvBackendState.Crashed);
		var (window, gone) = Assert.Single(_surface.Ended);
		Assert.Equal(BridgeWindow.Attached(_surface.Pipe), window);
		Assert.True(gone, "told after the backend's process ended");
	}

	[Fact]
	public async Task AKill_EndsIt()
	{
		await Connected();

		_host.Kill("it stopped answering (test)");

		await BridgeClientTests.WaitUntil(() => !_surface.Ended.IsEmpty);
		Assert.Equal([_surface.Pipe], _surface.EndedPipes);
	}

	[Fact]
	public async Task TheShutdown_EndsIt_OnceTheBackendHasExited()
	{
		await Connected();

		await _host.ShutdownAsync();

		var (window, gone) = Assert.Single(_surface.Ended);
		Assert.Equal(_surface.Pipe, window.Ipc);
		Assert.True(gone, "the backend's own stop of mpv (its final report) comes first; FluentFin ends that mpv after the exit");
	}

	[Fact]
	public async Task AShutdownThatHasToKill_StillEndsIt()
	{
		await Connected();
		// the backend answers shutdown but never exits (a hung final report): killed at the deadline
		_launcher.Started[0].ShutdownExitCode = null;

		await _host.ShutdownAsync();

		Assert.True(_launcher.Started[0].Killed);
		Assert.Equal([_surface.Pipe], _surface.EndedPipes);
	}

	[Fact]
	public async Task ARestartForANewWindow_EndsTheOldOne_NotTheNew()
	{
		await Connected();
		var old = _surface.Pipe;

		// FluentFin's mpv ended by itself and a new one listens elsewhere: the backend on the old pipe is replaced
		_surface.Pipe = "fluentfin-mpv-1-8899aabbccddeeff";
		await _host.ConnectAsync(Alice);

		Assert.Equal(2, _launcher.Started.Count);
		Assert.Equal([old], _surface.EndedPipes);

		await _host.ShutdownAsync();
		Assert.Equal([old, _surface.Pipe], _surface.EndedPipes);
	}

	[Fact]
	public async Task ACrashThenTheClose_EndItOnce()
	{
		await Connected();
		_launcher.Started[0].Exit(3);
		await BridgeClientTests.WaitUntil(() => _host.Status.State == MpvBackendState.Crashed);

		await _host.ShutdownAsync();
		_host.Dispose();

		Assert.Equal([_surface.Pipe], _surface.EndedPipes);
	}

	[Fact]
	public async Task TheAppsDispose_EndsIt()
	{
		await Connected();

		_host.Dispose();

		Assert.Equal([_surface.Pipe], _surface.EndedPipes);
	}
}
