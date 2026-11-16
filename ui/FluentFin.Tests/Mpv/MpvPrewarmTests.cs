using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Settings;
using FluentFin.Mpv;
using Microsoft.Extensions.Logging;
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
		// a margin no busy test machine eats: the check comes ~0.1 s in, the delay ends at 2 s
		var prewarm = Make(TimeSpan.FromSeconds(2));

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

	/// <summary>A host whose backend answers each <c>connect</c> with <paramref name="connect"/>'s result (null: an error).</summary>
	private (MpvBackendHost Host, FakeLauncher Launcher) HostWhoseConnect(Func<int, JsonObject?> connect, bool sayHello = true)
	{
		var connects = 0;
		var launcher = new FakeLauncher(() => new FakeBackendProcess((_, request) => (string?)request["cmd"] == "connect"
			? connect(Interlocked.Increment(ref connects))
			: new JsonObject(), sayHello));
		return (new MpvBackendHost(launcher, new FakeSurface(), _log) { HelloTimeout = TimeSpan.FromSeconds(5), ShutdownTimeout = TimeSpan.FromSeconds(2) }, launcher);
	}

	[Fact]
	public async Task APrewarmThatCannotConnect_StopsTheBackend_AndTheNextPlayStartsAnother()
	{
		// review: the backend (and the mpv its connect started) idled unused until sign-out or close
		var (host, launcher) = HostWhoseConnect(_ => null);
		using (host)
		{
			await Assert.ThrowsAnyAsync<BridgeException>(() => host.PrewarmAsync(Alice));

			Assert.Contains("shutdown", launcher.Started[0].Commands);
			Assert.True(launcher.Started[0].Exited.IsCompleted);
			Assert.Equal(MpvBackendState.Stopped, host.Status.State);

			await host.StartAsync();
			Assert.Equal(2, launcher.Started.Count);
		}
	}

	[Fact]
	public async Task APrewarmThatCannotConnect_KeepsTheBackend_WhenAPlayAskedForItMeanwhile()
	{
		using var answer = new ManualResetEventSlim();
		var (host, launcher) = HostWhoseConnect(n =>
		{
			if (n == 1)
			{
				answer.Wait(TimeSpan.FromSeconds(10));
				return null;
			}

			return new JsonObject { ["mpv"] = "0.41.0", ["mpvBackend"] = "external" };
		});
		using (host)
		{
			var prewarm = host.PrewarmAsync(Alice);
			await WaitUntil(() => launcher.Started.Count == 1 && launcher.Started[0].Commands.Contains("connect"));

			// a play: start (the pre-warm's connect holds the backend), then its own connect
			var start = host.StartAsync();
			answer.Set();
			await Assert.ThrowsAnyAsync<BridgeException>(() => prewarm);
			await start.WaitAsync(TimeSpan.FromSeconds(10));
			Assert.Equal("0.41.0", await host.ConnectAsync(Alice));

			Assert.Single(launcher.Started);
			Assert.DoesNotContain("shutdown", launcher.Started[0].Commands);
			Assert.Equal(MpvBackendState.Running, host.Status.State);
		}
	}

	[Fact]
	public async Task ASignOutDuringThePrewarmsStart_IsNoFailure()
	{
		// review: a cancelled start was logged as an error and Settings said "Could not start"
		var (host, _) = HostWhoseConnect(_ => new JsonObject(), sayHello: false);
		using (host)
		{
			using var signOut = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

			await Assert.ThrowsAnyAsync<Exception>(() => host.PrewarmAsync(Alice, signOut.Token));

			Assert.Equal(MpvBackendState.Stopped, host.Status.State);
			Assert.Null(host.Status.LastError);
			Assert.DoesNotContain(_log.Entries, x => x.Level >= LogLevel.Error);
		}
	}

	private static async Task WaitUntil(Func<bool> condition)
	{
		for (var i = 0; i < 500 && !condition(); i++)
		{
			await Task.Delay(10);
		}

		Assert.True(condition());
	}

	/// <summary>FluentFin's own player controls: the connect's window is FluentFin's in-process mpv, made when it is asked for.</summary>
	private sealed class AttachedSurface : IMpvVideoSurface
	{
		public int WindowRequests;

		public Task<nint> GetHandleAsync() => throw new InvalidOperationException("attached: no window handle is asked for");

		public Task<BridgeWindow> GetWindowAsync()
		{
			Interlocked.Increment(ref WindowRequests);
			return Task.FromResult(BridgeWindow.Attached("fluentfin-mpv-1-0011223344556677"));
		}

		public bool CanConnectAhead => false;

		/// <summary>The windows whose mpv ended with their backend (pc-fixes: FluentFin's mpv lives as long as its backend).</summary>
		public System.Collections.Concurrent.ConcurrentQueue<BridgeWindow> Ended { get; } = new();

		public void OnBackendExited(BridgeWindow window) => Ended.Enqueue(window);
	}

	[Fact]
	public async Task WithFluentFinsControls_ThePrewarmOnlyStartsTheBackend_AndTheFirstPlayMakesTheirMpvAndConnects()
	{
		var surface = new AttachedSurface();
		using var host = new MpvBackendHost(_launcher, surface, _log) { HelloTimeout = TimeSpan.FromSeconds(5), ShutdownTimeout = TimeSpan.FromSeconds(2) };

		await host.PrewarmAsync(Alice);

		Assert.Single(_launcher.Started);
		Assert.Equal(MpvBackendState.Running, host.Status.State);
		Assert.Empty(Connects());
		Assert.Equal(0, surface.WindowRequests);

		// MpvPlaybackService.PlayAsync: start (running already), then connect
		await host.StartAsync();
		await host.ConnectAsync(Alice);

		Assert.Single(_launcher.Started);
		var connect = Assert.Single(Connects());
		Assert.Equal("attached", (string?)connect["args"]!["window"]!["mode"]);
		Assert.Null(connect["args"]!["prewarm"]);
		Assert.Equal(1, surface.WindowRequests);

		// the mpv the play made ends with this backend (a sign-out's shutdown); the next login's play gets a new one
		await host.ShutdownAsync();
		Assert.Equal([BridgeWindow.Attached("fluentfin-mpv-1-0011223344556677")], surface.Ended);
	}

	[Fact]
	public async Task WithFluentFinsControls_ASignOutAfterThePrewarm_EndsNoMpv_NoneWasMade()
	{
		var surface = new AttachedSurface();
		using var host = new MpvBackendHost(_launcher, surface, _log) { HelloTimeout = TimeSpan.FromSeconds(5), ShutdownTimeout = TimeSpan.FromSeconds(2) };

		await host.PrewarmAsync(Alice);
		await host.ShutdownAsync();

		Assert.Equal(0, surface.WindowRequests);
		Assert.Empty(surface.Ended);
		Assert.Equal(MpvBackendState.Stopped, host.Status.State);
	}
}

/// <summary>fluentfin-mpv (startup-perf): whether the pre-warm may ask for the connect's window (player controls choice).</summary>
public sealed class MpvVideoSurfaceChoicePrewarmTests
{
	private static MpvVideoSurfaceChoice Choice(MpvPlayerControls controls, out MpvCompositionVideo composition)
	{
		var settings = new Mock<ISettings>();
		settings.SetupGet(x => x.MpvPlayerControls).Returns(controls);
		composition = new MpvCompositionVideo(new ListLogger<MpvCompositionVideo>(), null);
		return new MpvVideoSurfaceChoice(new MpvVideoHost(new ListLogger<MpvVideoHost>()), composition, settings.Object,
			new ListLogger<MpvVideoSurfaceChoice>());
	}

	[Fact]
	public void MpvsControls_ConnectAhead()
	{
		var choice = Choice(MpvPlayerControls.Mpv, out var composition);
		using (composition)
		{
			Assert.True(((IMpvVideoSurface)choice).CanConnectAhead);
		}
	}

	[Theory]
	[InlineData(true, false)]
	[InlineData(false, true)]
	public void ThePrewarmConnectsAhead_ExactlyWhenTheConnectWouldBeEmbedded(bool fluentFinControls, bool connectsAhead)
	{
		// the same choice GetWindowAsync makes (FluentFin's controls when chosen and libmpv can be had: attached)
		var choice = Choice(MpvPlayerControls.FluentFin, out var composition);
		using (composition)
		{
			choice.Choice = () => (fluentFinControls, fluentFinControls ? null : "libmpv-2.dll was not found next to FluentFin");
			Assert.Equal(connectsAhead, ((IMpvVideoSurface)choice).CanConnectAhead);
		}
	}
}
