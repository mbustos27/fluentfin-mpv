using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Settings;
using Moq;

namespace FluentFin.Tests.Mpv;

/// <summary>
/// fluentfin-mpv (startup-perf): the mpv backend started and connected with <c>prewarm</c> after sign-in (docs/BRIDGE.md §3,
/// §4), so Play skips both; cancelled and shut down at sign-out, cancelled at close; nothing when another player is selected.
/// </summary>
public sealed class MpvPrewarmTests
{
	private static readonly BridgeIdentity Alice = new("http://jf:8096", "srv", "Home", "aaaa", "alice", "token-alice-123", "dev", "PC", "FluentFin", "1.0");
	private static readonly BridgeIdentity Bob = Alice with { UserId = "bbbb", UserName = "bob", AccessToken = "token-bob-456" };

	private readonly RecordingBackend _backend = new();
	private readonly Mock<ISettings> _settings = new();
	private readonly ListLogger<MpvPrewarm> _log = new();
	private BridgeIdentity? _identity = Alice;

	private MpvPrewarm Make(TimeSpan? delay = null)
	{
		_settings.SetupGet(x => x.MediaPlayer).Returns(MediaPlayerType.Mpv);
		return new MpvPrewarm(_backend, _settings.Object, _log) { Delay = delay ?? TimeSpan.Zero, Identity = () => _identity };
	}

	private static async Task Done(MpvPrewarm prewarm) => await prewarm.Current.WaitAsync(TimeSpan.FromSeconds(10));

	[Fact]
	public async Task SignedIn_WithTheMpvPlayer_StartsAndConnectsAsTheLogin()
	{
		var prewarm = Make();

		prewarm.SignedIn();
		await Done(prewarm);

		Assert.Equal(1, _backend.Starts);
		Assert.False(_backend.Dev);
		Assert.Equal([Alice], _backend.Prewarmed);
		Assert.Empty(_backend.Commands);
		Assert.Contains(_log.Messages, x => x.Contains("[perf] the mpv backend is pre-warmed", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData(MediaPlayerType.Flyleaf)]
	[InlineData(MediaPlayerType.Vlc)]
	[InlineData(MediaPlayerType.WindowsMediaPlayer)]
	public async Task SignedIn_WithAnotherPlayer_StartsNothing(MediaPlayerType player)
	{
		var prewarm = Make();
		_settings.SetupGet(x => x.MediaPlayer).Returns(player);

		prewarm.SignedIn();
		await Done(prewarm);

		Assert.Equal(0, _backend.Starts);
		Assert.Empty(_backend.Prewarmed);
	}

	[Fact]
	public async Task SignedIn_WithNobodySignedIn_StartsNothing()
	{
		var prewarm = Make();
		_identity = null;

		prewarm.SignedIn();
		await Done(prewarm);

		Assert.Equal(0, _backend.Starts);
	}

	[Fact]
	public async Task ItWaitsItsDelay_SoTheHomePageGoesFirst()
	{
		var prewarm = Make(TimeSpan.FromMilliseconds(400));

		prewarm.SignedIn();
		await Task.Delay(100);
		Assert.Equal(0, _backend.Starts);

		await Done(prewarm);
		Assert.Equal([Alice], _backend.Prewarmed);
	}

	[Fact]
	public async Task NotWhilePlaying()
	{
		var prewarm = Make();
		_backend.LastState = new BridgeState { State = Bridge.States.Playing, ItemId = "x" };

		prewarm.SignedIn();
		await Done(prewarm);

		Assert.Equal(0, _backend.Starts);
	}

	[Fact]
	public async Task SignedOut_CancelsAPrewarmInFlight_ThenShutsTheBackendDown()
	{
		var prewarm = Make();
		var connect = new TaskCompletionSource();
		_backend.ConnectGate = connect.Task;

		prewarm.SignedIn();
		await WaitUntil(() => _backend.Starts == 1);
		prewarm.SignedOut();
		await Done(prewarm);

		Assert.Empty(_backend.Prewarmed);
		Assert.Equal(1, _backend.Shutdowns);
		Assert.Equal(["prewarm", "shutdown"], _backend.Timeline);
		Assert.Contains(_log.Messages, x => x.Contains("pre-warm was cancelled", StringComparison.Ordinal));
	}

	[Fact]
	public async Task SignedOut_WithNoBackendRunning_ShutsNothingDown()
	{
		var prewarm = Make();

		prewarm.SignedOut();
		await Done(prewarm);

		Assert.Equal(0, _backend.Shutdowns);
	}

	[Fact]
	public async Task ASwitchOfUser_ShutsTheOldLoginsBackendDown_BeforeThePrewarmAsTheNewOne()
	{
		var prewarm = Make();
		prewarm.SignedIn();
		await Done(prewarm);

		// Switch user: the shell is left (sign-out), then entered as the other user
		prewarm.SignedOut();
		_identity = Bob;
		prewarm.SignedIn();
		await Done(prewarm);

		Assert.Equal(["prewarm", "shutdown", "prewarm"], _backend.Timeline);
		Assert.Equal([Alice, Bob], _backend.Prewarmed);
	}

	[Fact]
	public async Task Closing_CancelsAPrewarmInItsDelay_AndAllowsNoNewOne()
	{
		var prewarm = Make(TimeSpan.FromSeconds(30));
		prewarm.SignedIn();

		prewarm.Cancel("closing");
		await Done(prewarm);
		prewarm.SignedIn();
		prewarm.SignedOut();
		await Done(prewarm);

		Assert.Equal(0, _backend.Starts);
		Assert.Equal(0, _backend.Shutdowns);
	}

	[Fact]
	public async Task AFailedPrewarm_IsLoggedRedacted_AndNeverThrows()
	{
		var prewarm = Make();
		_backend.StartFailure = new BridgeException("could not start: http://jf/x?api_key=secret-key-1234", null);

		prewarm.SignedIn();
		await Done(prewarm);

		var line = Assert.Single(_log.Messages, x => x.Contains("Pre-warming the mpv backend failed", StringComparison.Ordinal));
		Assert.DoesNotContain("secret-key-1234", line);
	}

	private static async Task WaitUntil(Func<bool> condition)
	{
		for (var i = 0; i < 500 && !condition(); i++)
		{
			await Task.Delay(10);
		}

		Assert.True(condition());
	}
}

public sealed class MpvBackendHostPrewarmTests : IDisposable
{
	private static readonly BridgeIdentity Alice = new("http://jf:8096", "srv", "Home", "aaaa", "alice", "token-alice-123", "dev", "PC", "FluentFin", "1.0");

	private readonly ListLogger<MpvBackendHost> _log = new();
	private readonly FakeLauncher _launcher;
	private readonly MpvBackendHost _host;

	public MpvBackendHostPrewarmTests()
	{
		_launcher = new FakeLauncher(() => new FakeBackendProcess((_, request) => (string?)request["cmd"] == "connect"
			? new JsonObject { ["mpv"] = "0.41.0", ["mpvBackend"] = "external" }
			: new JsonObject()));
		_host = new MpvBackendHost(_launcher, new FakeSurface(), _log) { HelloTimeout = TimeSpan.FromSeconds(5), ShutdownTimeout = TimeSpan.FromSeconds(2) };
	}

	public void Dispose() => _host.Dispose();

	private List<JsonObject> Connects() => [.. _launcher.Started.SelectMany(p => p.Requests).Where(x => (string?)x["cmd"] == "connect")];

	[Fact]
	public async Task Prewarm_StartsTheBackend_AndConnectsWithPrewarm()
	{
		await _host.PrewarmAsync(Alice);

		Assert.Single(_launcher.Started);
		var connect = Assert.Single(Connects());
		Assert.True((bool?)connect["args"]!["prewarm"]);
		Assert.Equal("token-alice-123", (string?)connect["args"]!["accessToken"]);
		Assert.Equal(MpvBackendState.Running, _host.Status.State);
		Assert.Equal("0.41.0", _host.Status.MpvVersion);
	}

	[Fact]
	public async Task APlayAfterThePrewarm_StartsAndConnectsNothing()
	{
		await _host.PrewarmAsync(Alice);

		// MpvPlaybackService.PlayAsync: start, then connect as the same login
		await _host.StartAsync();
		await _host.ConnectAsync(Alice);

		Assert.Single(_launcher.Started);
		Assert.Single(Connects());
	}

	[Fact]
	public async Task AnOrdinaryConnect_SendsNoPrewarm()
	{
		await _host.StartAsync();
		await _host.ConnectAsync(Alice);

		Assert.Null(Assert.Single(Connects())["args"]!["prewarm"]);
	}
}
