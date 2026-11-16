using System.Text.Json.Nodes;
using FluentFin.Core.Mpv;

namespace FluentFin.Tests.Mpv;

/// <summary>
/// fluentfin-mpv player controls: the backend attached to an mpv FluentFin runs itself (docs/BRIDGE.md §4,
/// <c>window.mode = "attached"</c>): what <c>connect</c> carries, and a new pipe restarting the backend.
/// </summary>
public sealed class AttachedWindowTests : IDisposable
{
	private static readonly BridgeIdentity Alice = new("http://jf:8096", "srv", "Home", "aaaa", "alice", "token-alice-123", "dev", "PC", "FluentFin", "1.0");

	private readonly ListLogger<MpvBackendHost> _log = new();
	private readonly FakeLauncher _launcher = new(() => new FakeBackendProcess((_, request) => (string?)request["cmd"] == "connect"
		? new JsonObject { ["mpv"] = "0.42.0", ["mpvBackend"] = "external" }
		: new JsonObject()));

	private readonly AttachedSurface _surface = new();
	private readonly MpvBackendHost _host;

	public AttachedWindowTests()
	{
		// the fake backend says hello at once: the long bound only keeps a loaded test machine's stall from failing the start
		_host = new MpvBackendHost(_launcher, _surface, _log) { HelloTimeout = TimeSpan.FromSeconds(30), ShutdownTimeout = TimeSpan.FromSeconds(2) };
	}

	public void Dispose() => _host.Dispose();

	private sealed class AttachedSurface : IMpvVideoSurface
	{
		public string Pipe { get; set; } = "fluentfin-mpv-1-0011223344556677";

		public Task<nint> GetHandleAsync() => throw new InvalidOperationException("attached: no window handle is asked for");

		public Task<BridgeWindow> GetWindowAsync() => Task.FromResult(BridgeWindow.Attached(Pipe));
	}

	[Fact]
	public void Connect_CarriesThePipe_AndNoHandle()
	{
		var args = BridgeCommands.Connect(Alice, BridgeWindow.Attached("fluentfin-mpv-7-abc"));

		Assert.True(JsonNode.DeepEquals(new JsonObject { ["mode"] = "attached", ["ipc"] = "fluentfin-mpv-7-abc" }, args["window"]), args["window"]!.ToJsonString());
	}

	[Fact]
	public void Embedded_IsUnchanged_AndCarriesNoPipe()
	{
		var args = BridgeCommands.Connect(Alice, BridgeWindow.Embedded(0x1234));

		Assert.True(JsonNode.DeepEquals(new JsonObject { ["mode"] = "embedded", ["hwnd"] = 0x1234L }, args["window"]), args["window"]!.ToJsonString());
	}

	[Fact]
	public async Task Host_ConnectsWithTheSurfacesWindow()
	{
		await _host.StartAsync();
		await _host.ConnectAsync(Alice);

		var connect = Assert.Single(_launcher.Started[0].Requests, x => (string?)x["cmd"] == "connect");
		Assert.Equal("attached", (string?)connect["args"]!["window"]!["mode"]);
		Assert.Equal(_surface.Pipe, (string?)connect["args"]!["window"]!["ipc"]);
		Assert.Null(connect["args"]!["window"]!["hwnd"]);
	}

	[Fact]
	public async Task ANewPipe_RestartsTheBackend_TheWindowIsFixedForItsLife()
	{
		await _host.StartAsync();
		await _host.ConnectAsync(Alice);
		await _host.ConnectAsync(Alice);
		Assert.Single(_launcher.Started);

		// FluentFin's mpv ended and a new one listens elsewhere: the backend attached to the old pipe is replaced
		_surface.Pipe = "fluentfin-mpv-1-8899aabbccddeeff";
		await _host.ConnectAsync(Alice);

		Assert.Equal(2, _launcher.Started.Count);
		var connect = Assert.Single(_launcher.Started[1].Requests, x => (string?)x["cmd"] == "connect");
		Assert.Equal(_surface.Pipe, (string?)connect["args"]!["window"]!["ipc"]);
	}
}
