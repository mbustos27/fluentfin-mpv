using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// fluentfin-mpv (startup-perf): the capabilities posted at sign-in carry a profile that is ready (docs/research/perf-ui.md
/// O13: the Windows player's probe used to run after them), and the [perf] timeline of a play in the log.
/// </summary>
[Collection(SessionInfoCollection.Name)]
public sealed class StartupPerfTests : IAsyncLifetime
{
	private readonly FakeJellyfin _server = new();

	public Task InitializeAsync()
	{
		_server.Start();
		return Task.CompletedTask;
	}

	public Task DisposeAsync()
	{
		_server.Dispose();
		return Task.CompletedTask;
	}

	[Fact]
	public async Task SignIn_PreparesTheDeviceProfile_BeforeItPostsTheCapabilities()
	{
		var prepared = new TaskCompletionSource();
		var readBeforeReady = false;
		var profiles = new Mock<IDeviceProfileFactory>();
		profiles.Setup(x => x.PrepareAsync()).Returns(prepared.Task);
		profiles.Setup(x => x.GetDeviceProfile()).Returns(() =>
		{
			readBeforeReady |= !prepared.Task.IsCompleted;
			return DeviceProfiles.Flyleaf;
		});
		var settings = new Mock<ISettings>();
		settings.SetupGet(x => x.MediaPlayer).Returns(MediaPlayerType.WindowsMediaPlayer);
		var client = new JellyfinClient(new CoreListLogger<JellyfinClient>(), Mock.Of<IObserver<IOutboundWebSocketMessage>>(), Mock.Of<IJumpListService>(), profiles.Object, settings.Object);

		var signIn = client.Initialize(_server.BaseUrl, new AuthenticationResult
		{
			AccessToken = "fake-token-0123456789",
			ServerId = "fake-server",
			User = new UserDto { Id = Guid.NewGuid(), Name = "alice" },
		});
		await Task.Delay(200);

		Assert.DoesNotContain(_server.ApiRequests, x => x.Path.Equals("/Sessions/Capabilities/Full", StringComparison.OrdinalIgnoreCase));
		Assert.False(signIn.IsCompleted);

		prepared.SetResult();
		await signIn.WaitAsync(TimeSpan.FromSeconds(10));

		Assert.Contains(_server.ApiRequests, x => x.Path.Equals("/Sessions/Capabilities/Full", StringComparison.OrdinalIgnoreCase));
		profiles.Verify(x => x.GetDeviceProfile(), Times.AtLeastOnce);
		Assert.False(readBeforeReady, "the profile was read before it was ready");
	}

	[Fact]
	public async Task AProbeThatFails_DoesNotFailTheSignIn()
	{
		var profiles = new Mock<IDeviceProfileFactory>();
		profiles.Setup(x => x.PrepareAsync()).ThrowsAsync(new InvalidOperationException("no audio device"));
		profiles.Setup(x => x.GetDeviceProfile()).Returns(DeviceProfiles.Flyleaf);
		var settings = new Mock<ISettings>();
		settings.SetupGet(x => x.MediaPlayer).Returns(MediaPlayerType.WindowsMediaPlayer);
		var log = new CoreListLogger<JellyfinClient>();
		var client = new JellyfinClient(log, Mock.Of<IObserver<IOutboundWebSocketMessage>>(), Mock.Of<IJumpListService>(), profiles.Object, settings.Object);

		await client.Initialize(_server.BaseUrl, new AuthenticationResult
		{
			AccessToken = "fake-token-0123456789",
			ServerId = "fake-server",
			User = new UserDto { Id = Guid.NewGuid(), Name = "alice" },
		}).WaitAsync(TimeSpan.FromSeconds(10));

		Assert.Contains(_server.ApiRequests, x => x.Path.Equals("/Sessions/Capabilities/Full", StringComparison.OrdinalIgnoreCase));
		Assert.Contains(log.Entries, x => x.Message.Contains("could not be prepared", StringComparison.Ordinal));
	}
}

/// <summary>fluentfin-mpv (startup-perf): what a play's [perf] lines say, and that each step is logged once.</summary>
public sealed class MpvPlayTimelineTests
{
	private static BridgeEvent State(string state, string? itemId) =>
		new(Bridge.Events.State, new JsonObject { ["state"] = state, ["itemId"] = itemId });

	private static BridgeEvent Position(long ticks) => new(Bridge.Events.Position, new JsonObject { ["positionTicks"] = ticks });

	[Fact]
	public void APlay_FromThePress_ToTheFirstPosition()
	{
		var log = new CoreListLogger<MpvPlayTimelineTests>();
		PerfLog.PlayPressed();

		var timeline = MpvPlayTimeline.Begin(log, 7);
		timeline.Step("backend already running");
		// before play is sent: the page before's states are not this play's
		timeline.OnEvent(State(Bridge.States.Playing, "aaaa"));
		timeline.Sending("AAAA");
		timeline.OnEvent(State(Bridge.States.Opening, "bbbb"));
		// the first opening before the answer (docs/BRIDGE.md §3)
		timeline.OnEvent(State(Bridge.States.Opening, "aaaa"));
		timeline.Step("play sent and accepted");
		foreach (var _ in Enumerable.Range(0, 3))
		{
			timeline.OnEvent(State(Bridge.States.Opening, "aaaa"));
			timeline.OnEvent(Position(0));
			timeline.OnEvent(State(Bridge.States.Playing, "aaaa"));
			timeline.OnEvent(Position(10_000));
			timeline.OnEvent(State(Bridge.States.Paused, "aaaa"));
		}

		var lines = log.Entries.Select(x => x.Message).ToList();
		Assert.All(lines, x => Assert.StartsWith("[perf] play (generation 7): ", x));
		Assert.All(lines, x => Assert.Contains("ms after Play was pressed", x));
		Assert.Equal(["player page opened", "backend already running", "opening", "play sent and accepted", "playing", "first position"],
			lines.Select(x => x["[perf] play (generation 7): ".Length..x.IndexOf(" at ", StringComparison.Ordinal)].Split(" (")[0]));
	}

	[Fact]
	public void WithoutAPress_ItCountsFromThePage()
	{
		var log = new CoreListLogger<MpvPlayTimelineTests>();
		_ = PerfLog.TakePlayPressed(TimeSpan.FromHours(1));

		MpvPlayTimeline.Begin(log, 1);

		Assert.Contains("ms after the player page opened", Assert.Single(log.Entries).Message);
	}

	[Fact]
	public void APressIsTakenOnce_AndAnOldOneIsNotThisPlays()
	{
		PerfLog.PlayPressed();
		Assert.NotNull(PerfLog.TakePlayPressed(TimeSpan.FromMinutes(1)));
		Assert.Null(PerfLog.TakePlayPressed(TimeSpan.FromMinutes(1)));

		PerfLog.PlayPressed();
		Thread.Sleep(30);
		Assert.Null(PerfLog.TakePlayPressed(TimeSpan.FromMilliseconds(1)));
	}

	[Fact]
	public void TheStartupLine_SaysHowLongAfterTheProcessStarted()
	{
		var log = new CoreListLogger<MpvPlayTimelineTests>();

		PerfLog.Startup(log, "window shown");

		var message = Assert.Single(log.Entries).Message;
		Assert.Matches(@"^\[perf\] startup: window shown at \d+ ms after the process started$", message);
	}
}
