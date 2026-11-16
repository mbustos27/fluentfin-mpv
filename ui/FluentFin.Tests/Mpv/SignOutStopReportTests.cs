using System.Diagnostics;
using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Moq;

namespace FluentFin.Tests.Mpv;

/// <summary>
/// Round 6, item 1, the host's part: a sign-out stops the playback and waits, bounded, until the backend is done with its
/// Stopped report (its <c>reported</c>, and idle: docs/BRIDGE.md §5), so the report goes out before the token is revoked.
/// </summary>
public sealed class SignOutStopReportTests : IDisposable
{
	private static readonly Guid Item = Guid.Parse("0123456789abcdef0123456789abcdef");
	private static readonly Guid AliceUser = Guid.Parse("a11ce000000000000000000000000001");
	private static readonly BridgeIdentity Alice = new("http://jf:8096", "srv", "Home", AliceUser.ToString("N"), "alice", "token-alice-123", "dev", "PC", "FluentFin", "1.0");

	private readonly ListLogger<MpvBackendHost> _log = new();
	private readonly ListLogger<MpvPlaybackService> _serviceLog = new();
	private readonly FakeLauncher _launcher;
	private readonly MpvBackendHost _host;
	private readonly Mock<IJellyfinClient> _client = new();
	private readonly MpvPlaybackService _service;

	/// <summary>What the fake backend does when it gets <c>stop</c> (after answering it).</summary>
	private Func<FakeBackendProcess, Task>? _onStop;

	public SignOutStopReportTests()
	{
		_launcher = new FakeLauncher(() => new FakeBackendProcess((process, request) =>
		{
			if ((string?)request["cmd"] == "stop" && _onStop is { } onStop)
			{
				_ = Task.Run(() => onStop(process));
			}
			return new JsonObject();
		}));
		_host = new MpvBackendHost(_launcher, new FakeSurface(), _log) { ShutdownTimeout = TimeSpan.FromSeconds(2) };
		_client.SetupGet(x => x.BaseUrl).Returns(Alice.ServerUrl);
		_client.SetupGet(x => x.UserId).Returns(AliceUser);
		_service = new MpvPlaybackService(_host, _client.Object, _serviceLog);
	}

	public void Dispose() => _host.Dispose();

	private FakeBackendProcess Backend => _launcher.Started[^1];

	private int StopsSent => Backend.Commands.Count(x => x == "stop");

	private async Task Playing()
	{
		await _host.StartAsync();
		await _host.ConnectAsync(Alice);
		await State("playing");
		await Event("position", new JsonObject { ["positionTicks"] = 5_000_000 });
	}

	private Task State(string state, bool withSession = true) => Event("state", new JsonObject
	{
		["state"] = state,
		["itemId"] = state == "idle" ? null : Item.ToString("N"),
		["mediaSourceId"] = state == "idle" ? null : "ms-1",
		["playSessionId"] = state == "idle" || !withSession ? null : "ps-1",
	});

	/// <summary>Sends an event and waits until the host has handled it.</summary>
	private async Task Event(string name, JsonObject data)
	{
		var seen = 0;
		using var subscription = _host.Events.Subscribe(e =>
		{
			if (e.Name == name)
			{
				Interlocked.Increment(ref seen);
			}
		});
		await Backend.Event(name, data);
		await BridgeClientTests.WaitUntil(() => Volatile.Read(ref seen) > 0);
	}

	[Fact]
	public async Task TheWait_EndsAtTheReported_NotAtIdle()
	{
		// a deferred stop answers and goes idle before its report is sent (docs/BRIDGE.md §6)
		await Playing();

		var waiting = _host.WaitForStopReportsAsync(TimeSpan.FromSeconds(10));
		await State("idle");
		await Task.Delay(200);
		Assert.False(waiting.IsCompleted);
		await Backend.Reported(Item.ToString("N"), "ps-1");

		Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
	}

	[Fact]
	public async Task TheWait_AfterTheReported_LastsUntilIdle()
	{
		// a snapshot sampled before the stop can still show the session after its `reported` (docs/BRIDGE.md §5)
		await Playing();

		var waiting = _host.WaitForStopReportsAsync(TimeSpan.FromSeconds(10));
		await Event("reported", new JsonObject { ["itemId"] = Item.ToString("N"), ["playSessionId"] = "ps-1", ["outcome"] = "sent" });
		await State("paused");
		await Task.Delay(200);
		Assert.False(waiting.IsCompleted);
		await State("idle");

		Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
	}

	[Fact]
	public async Task TheWait_WhileOpeningWithoutAPlaySessionYet_LastsUntilIdle()
	{
		await _host.StartAsync();
		await _host.ConnectAsync(Alice);
		await State("opening", withSession: false);

		var waiting = _host.WaitForStopReportsAsync(TimeSpan.FromSeconds(10));
		await Task.Delay(200);
		Assert.False(waiting.IsCompleted);
		await State("idle");

		Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
	}

	[Fact]
	public async Task TheWait_GivesUpAtItsBound_AndSaysWhichSession()
	{
		await Playing();

		var watch = Stopwatch.StartNew();
		Assert.False(await _host.WaitForStopReportsAsync(TimeSpan.FromMilliseconds(500)).WaitAsync(TimeSpan.FromSeconds(5)));

		Assert.InRange(watch.Elapsed.TotalSeconds, 0.45, 3);
		Assert.Contains(_log.Entries, x => x.Level == LogLevel.Warning && x.Message.Contains("ps-1"));
	}

	[Fact]
	public async Task TheWait_WithNothingPlayingOrNoBackend_ReturnsAtOnce()
	{
		Assert.True(await _host.WaitForStopReportsAsync(TimeSpan.FromSeconds(10)).WaitAsync(TimeSpan.FromSeconds(1)));
		await _host.StartAsync();
		await _host.ConnectAsync(Alice);
		await State("idle");

		Assert.True(await _host.WaitForStopReportsAsync(TimeSpan.FromSeconds(10)).WaitAsync(TimeSpan.FromSeconds(1)));
	}

	[Fact]
	public async Task TheWait_EndsWhenTheBackendDies_OnceItsExitIsHandled()
	{
		await Playing();
		var interrupted = 0;
		using var subscription = _host.PlaybackInterrupted.Subscribe(_ => Interlocked.Increment(ref interrupted));

		var waiting = _host.WaitForStopReportsAsync(TimeSpan.FromSeconds(10));
		Backend.Exit(3);

		Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
		Assert.Equal(1, Volatile.Read(ref interrupted));
	}

	[Fact]
	public async Task SigningOut_SendsOneStop_AndReturnsAtItsReport()
	{
		_onStop = async process =>
		{
			await process.Event("state", new JsonObject { ["state"] = "idle" });
			await Task.Delay(400);
			await process.Reported(Item.ToString("N"), "ps-1");
		};
		_service.Begin();
		await Playing();

		var watch = Stopwatch.StartNew();
		await _service.StopForSignOutAsync(TimeSpan.FromSeconds(10)).WaitAsync(TimeSpan.FromSeconds(15));

		// at the report (~0.4 s), not at the bound
		Assert.InRange(watch.Elapsed.TotalSeconds, 0.35, 5);
		Assert.Equal(1, StopsSent);
		_client.Verify(x => x.Stop(It.IsAny<PlaybackStopInfo>()), Times.Never);
		Assert.DoesNotContain(_log.Messages.Concat(_serviceLog.Messages), x => x.Contains(Alice.AccessToken, StringComparison.Ordinal));
	}

	[Fact]
	public async Task SigningOut_WhenTheBackendDiesBeforeItsReport_WaitsForFluentFinsOwnStopped()
	{
		// the backend took the stop and died before its report: FluentFin owes the one Stopped (docs/BRIDGE.md §6), and it
		// goes out before the sign-out revokes the token
		var serverTookIt = new TaskCompletionSource();
		_client.Setup(x => x.Stop(It.IsAny<PlaybackStopInfo>())).Returns(async () =>
		{
			await Task.Delay(300);
			serverTookIt.TrySetResult();
		});
		_onStop = async process =>
		{
			await Task.Delay(100);
			process.Exit(3);
		};
		_service.Begin();
		await Playing();

		await _service.StopForSignOutAsync(TimeSpan.FromSeconds(10)).WaitAsync(TimeSpan.FromSeconds(15));

		Assert.True(serverTookIt.Task.IsCompleted, "the sign-out did not wait for FluentFin's own Stopped report");
		_client.Verify(x => x.Stop(It.Is<PlaybackStopInfo>(i => i.ItemId == Item && i.PlaySessionId == "ps-1" && i.PositionTicks == 5_000_000)), Times.Once);
		Assert.Equal(1, StopsSent);
	}

	[Fact]
	public async Task SigningOut_IsBoundedInAll_WhenTheBackendNeverAnswersTheStop()
	{
		// a hung backend: the stop itself is not answered (its own timeout is 5 s); the sign-out goes on at its bound
		var release = new ManualResetEventSlim();
		using var host = new MpvBackendHost(new FakeLauncher(() => new FakeBackendProcess((_, request) =>
		{
			if ((string?)request["cmd"] == "stop")
			{
				release.Wait(TimeSpan.FromSeconds(20));
			}
			return new JsonObject();
		})), new FakeSurface(), _log) { ShutdownTimeout = TimeSpan.FromSeconds(1) };
		var service = new MpvPlaybackService(host, _client.Object, _serviceLog);
		try
		{
			service.Begin();
			await host.StartAsync();
			await host.ConnectAsync(Alice);

			var watch = Stopwatch.StartNew();
			await service.StopForSignOutAsync(TimeSpan.FromSeconds(1)).WaitAsync(TimeSpan.FromSeconds(10));

			// the stop's own timeout is 5 s
			Assert.InRange(watch.Elapsed.TotalSeconds, 0.9, 3.5);
			Assert.Contains(_serviceLog.Entries, x => x.Level == LogLevel.Warning && x.Message.Contains("sign-out"));
		}
		finally
		{
			release.Set();
		}
	}

	[Fact]
	public async Task ACastDuringTheWait_PlaysNothing_SoTheSignOutEndsAtTheReportOfWhatItStopped()
	{
		// signout-ux item 2: before, the cast's play went out during the wait: the backend played it (the wait then lasted
		// until its bound) and kept playing it past the revoke, so its Stopped got a 401
		var y = Guid.Parse("22222222222222222222222222222222").ToString("N");
		_onStop = async process =>
		{
			await process.Event("state", new JsonObject { ["state"] = "idle" });
			await Task.Delay(600);
			await process.Reported(Item.ToString("N"), "ps-1");
		};
		_service.Begin();
		await Playing();
		Backend.Respond = (process, request) =>
		{
			var cmd = (string?)request["cmd"];
			if (cmd == "stop" && _onStop is { } onStop)
			{
				_ = Task.Run(() => onStop(process));
			}
			else if (cmd == "play")
			{
				_ = Task.Run(() => process.Event("state", new JsonObject { ["state"] = "playing", ["itemId"] = y, ["mediaSourceId"] = y, ["playSessionId"] = "ps-2" }));
			}
			return new JsonObject();
		};

		var watch = Stopwatch.StartNew();
		var signingOut = _service.StopForSignOutAsync(TimeSpan.FromSeconds(10));
		await BridgeClientTests.WaitUntil(() => StopsSent == 1);
		var cast = _service.Begin("a remote play (cast)");
		var played = await _service.PlayAsync(cast, Alice, new BridgePlayRequest([y]));
		await signingOut.WaitAsync(TimeSpan.FromSeconds(15));

		Assert.False(played);
		Assert.DoesNotContain("play", Backend.Commands.SkipWhile(x => x != "stop"));
		// at the report (~0.6 s), not at the bound
		Assert.InRange(watch.Elapsed.TotalSeconds, 0.5, 5);
		Assert.Contains(_serviceLog.Messages, x => x.Contains("FluentFin is signing out", StringComparison.Ordinal));
	}

	[Fact]
	public async Task SigningOut_IsBoundedInAll_WhenTheBackendNeverReports()
	{
		_service.Begin();
		await Playing();

		var watch = Stopwatch.StartNew();
		await _service.StopForSignOutAsync(TimeSpan.FromSeconds(1)).WaitAsync(TimeSpan.FromSeconds(5));

		Assert.InRange(watch.Elapsed.TotalSeconds, 0.9, 3);
		Assert.Equal(1, StopsSent);
	}
}
