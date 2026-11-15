using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FluentFin.Tests.Mpv;

/// <summary>
/// docs/BRIDGE.md §6: when the backend dies unexpectedly while a play session is open (shown by a state, not yet closed by
/// its <c>reported</c>), FluentFin sends the one Stopped report the backend no longer can, with that session's ids and the
/// position of the latest position event for its item. Never otherwise.
/// </summary>
public sealed class InterruptedPlaybackTests : IDisposable
{
	private static readonly Guid Item = Guid.Parse("0123456789abcdef0123456789abcdef");
	private static readonly Guid OtherItem = Guid.Parse("fedcba9876543210fedcba9876543210");
	private static readonly Guid AliceUser = Guid.Parse("a11ce000000000000000000000000001");
	private static readonly Guid BobUser = Guid.Parse("b0b00000000000000000000000000002");
	private static readonly BridgeIdentity Alice = new("http://jf:8096", "srv", "Home", AliceUser.ToString("N"), "alice", "token-alice-123", "dev", "PC", "FluentFin", "1.0");
	private const string ServerB = "https://jf-b.example:8920";

	private readonly ListLogger<MpvBackendHost> _log = new();
	private readonly ListLogger<MpvPlaybackService> _serviceLog = new();
	private readonly FakeLauncher _launcher = new(() => new FakeBackendProcess());
	private readonly MpvBackendHost _host;
	private readonly Mock<IJellyfinClient> _client = new();
	private readonly ConcurrentQueue<BridgeEvent> _events = new();
	private readonly IDisposable _eventSubscription;
	private readonly MpvPlaybackService _service;

	public InterruptedPlaybackTests()
	{
		_host = new MpvBackendHost(_launcher, new FakeSurface(), _log) { ShutdownTimeout = TimeSpan.FromSeconds(2) };
		_eventSubscription = _host.Events.Subscribe(_events.Enqueue);
		// FluentFin's client is logged in as the server and user the backend plays as: the crash report goes only there (docs/BRIDGE.md §6)
		_client.SetupGet(x => x.BaseUrl).Returns(Alice.ServerUrl);
		_client.SetupGet(x => x.UserId).Returns(AliceUser);
		_service = new MpvPlaybackService(_host, _client.Object, _serviceLog);
	}

	public void Dispose()
	{
		_eventSubscription.Dispose();
		_host.Dispose();
	}

	private FakeBackendProcess Backend => _launcher.Started[^1];

	/// <summary>Started and connected as FluentFin plays: a play session is played as the server and user of the connect.</summary>
	private async Task Start(BridgeIdentity? identity = null)
	{
		await _host.StartAsync();
		await _host.ConnectAsync(identity ?? Alice);
	}

	private async Task State(string state, Guid? itemId, long positionTicks = 0)
	{
		var count = _events.Count;
		await Backend.Event("state", new JsonObject
		{
			["state"] = state,
			["itemId"] = itemId?.ToString("N"),
			["mediaSourceId"] = itemId?.ToString("N"),
			["playSessionId"] = itemId is null ? null : $"session-{itemId:N}",
			["positionTicks"] = positionTicks,
		});
		await BridgeClientTests.WaitUntil(() => _events.Count > count);
	}

	private async Task Position(long ticks)
	{
		var count = _events.Count;
		await Backend.Event("position", new JsonObject { ["positionTicks"] = ticks, ["durationTicks"] = 72_000_000_000 });
		await BridgeClientTests.WaitUntil(() => _events.Count > count);
	}

	/// <summary>The backend is done with the Stopped report of this item's play session (docs/BRIDGE.md §5).</summary>
	private async Task Reported(Guid itemId, string outcome = "sent")
	{
		var count = _events.Count;
		await Backend.Reported(itemId.ToString("N"), $"session-{itemId:N}", outcome);
		await BridgeClientTests.WaitUntil(() => _events.Count > count);
	}

	private void VerifyStops(Times times) => _client.Verify(x => x.Stop(It.IsAny<PlaybackStopInfo>()), times);

	private void VerifyOneStop(Guid itemId, long? positionTicks)
	{
		// no position known: failed, so the server keeps the item's resume point
		bool? failed = positionTicks.HasValue ? null : true;
		_client.Verify(x => x.Stop(It.Is<PlaybackStopInfo>(info =>
			info.ItemId == itemId &&
			info.MediaSourceId == itemId.ToString("N") &&
			info.PlaySessionId == $"session-{itemId:N}" &&
			info.PositionTicks == positionTicks &&
			info.Failed == failed)), Times.Once);
		VerifyStops(Times.Once());
	}

	private async Task CrashAndWaitForItsHandling(int exitCode = 3)
	{
		Backend.Exit(exitCode);
		await BridgeClientTests.WaitUntil(() => _host.Status.State == MpvBackendState.Crashed);
		await Task.Delay(300);
	}

	[Fact]
	public async Task Crash_WhilePlaying_SendsExactlyOneStop_WithTheStatesIds_AndTheLatestPosition()
	{
		await Start();
		await State("playing", Item, positionTicks: 100);
		await Position(5_000_000);
		await Position(7_000_000);

		Backend.Exit(3);

		await BridgeClientTests.WaitUntil(() => _client.Invocations.Any(x => x.Method.Name == nameof(IJellyfinClient.Stop)));
		await Task.Delay(300);
		_client.Verify(x => x.Stop(It.Is<PlaybackStopInfo>(info =>
			info.ItemId == Item &&
			info.MediaSourceId == Item.ToString("N") &&
			info.PlaySessionId == $"session-{Item:N}" &&
			info.PositionTicks == 7_000_000 &&
			info.Failed == null)), Times.Once);
		VerifyStops(Times.Once());
		_client.Verify(x => x.Stop(), Times.Never);
	}

	[Fact]
	public async Task WatchdogKill_WhilePaused_SendsOneStop()
	{
		await Start();
		await State("playing", Item);
		await Position(9_000_000);
		await State("paused", Item, positionTicks: 8_000_000);

		_host.Kill("mpv stopped responding");

		await BridgeClientTests.WaitUntil(() => _client.Invocations.Count > 0);
		await Task.Delay(300);
		_client.Verify(x => x.Stop(It.Is<PlaybackStopInfo>(info => info.ItemId == Item && info.PositionTicks == 9_000_000 && info.Failed == null)), Times.Once);
		VerifyStops(Times.Once());
	}

	/// <summary>
	/// The backend's real <c>state</c> while an item opens (bridge_state.py _snapshot): position 0; before PlaybackInfo no
	/// media source and no play session, after it both (<paramref name="afterPlaybackInfo"/>).
	/// </summary>
	private async Task OpeningSnapshot(Guid itemId, bool afterPlaybackInfo = false)
	{
		var count = _events.Count;
		await Backend.Event("state", new JsonObject
		{
			["state"] = "opening",
			["connected"] = true,
			["itemId"] = itemId.ToString("N"),
			["mediaSourceId"] = afterPlaybackInfo ? itemId.ToString("N") : null,
			["playSessionId"] = afterPlaybackInfo ? $"session-{itemId:N}" : null,
			["playMethod"] = null,
			["positionTicks"] = 0,
			["durationTicks"] = null,
			["volume"] = 100,
			["muted"] = false,
			["audioStreamIndex"] = null,
			["subtitleStreamIndex"] = null,
			["queue"] = new JsonObject { ["itemIds"] = new JsonArray(itemId.ToString("N")), ["index"] = 0 },
			["fullscreen"] = false,
			["window"] = "embedded",
		});
		await BridgeClientTests.WaitUntil(() => _events.Count > count);
	}

	[Fact]
	public async Task Crash_WhileOpening_ReportsAFailedStop_WithNoPosition_SoTheResumePointStays()
	{
		// the reviewer's R1: the earlier item's position is not this item's, and the opening snapshot's 0 is not a position
		// either; a Stopped at 0 would reset the item's resume point, one without a position would mark it played
		await Start();
		await State("playing", OtherItem);
		await Position(9_000_000);
		await Reported(OtherItem);
		await OpeningSnapshot(Item);
		await OpeningSnapshot(Item, afterPlaybackInfo: true);

		Backend.Exit(1);

		await BridgeClientTests.WaitUntil(() => _client.Invocations.Count > 0);
		await Task.Delay(300);
		VerifyOneStop(Item, positionTicks: null);
	}

	[Fact]
	public async Task Crash_WhileTheNextItemOpensWithoutAPlaySessionYet_ReportsTheSessionStillOpen()
	{
		// the next item has no play session before its PlaybackInfo, so it opens none; the outgoing one was never reported
		await Start();
		await State("playing", OtherItem);
		await Position(9_000_000);
		await OpeningSnapshot(Item);

		await CrashAndWaitForItsHandling(1);

		VerifyOneStop(OtherItem, positionTicks: 9_000_000);
	}

	[Fact]
	public async Task Crash_WhilePlaying_BeforeItsFirstPositionEvent_ReportsAFailedStop_WithNoPosition()
	{
		await Start();
		await State("playing", OtherItem);
		await Position(9_000_000);
		await State("playing", Item, positionTicks: 1_234);

		Backend.Exit(1);

		await BridgeClientTests.WaitUntil(() => _client.Invocations.Count > 0);
		_client.Verify(x => x.Stop(It.Is<PlaybackStopInfo>(info => info.ItemId == Item && info.PositionTicks == null && info.Failed == true)), Times.Once);
	}

	[Fact]
	public async Task AKillThatLandsAfterTheClosePathBegan_StillReportsTheStop()
	{
		// the watchdog's kill comes first, but its exit is seen only after the close path has begun (the reviewer's R8 race,
		// made deterministic): the backend never sent its final report, so FluentFin must (docs/BRIDGE.md §6)
		var release = new TaskCompletionSource();
		var launcher = new FakeLauncher(() => new FakeBackendProcess((_, request) =>
		{
			if ((string?)request["cmd"] == "shutdown")
			{
				release.Task.Wait();
			}
			return new JsonObject();
		})
		{ KillEnds = false });
		using var host = new MpvBackendHost(launcher, new FakeSurface(), _log) { ShutdownTimeout = TimeSpan.FromSeconds(5) };
		var client = new Mock<IJellyfinClient>();
		client.SetupGet(x => x.BaseUrl).Returns(Alice.ServerUrl);
		client.SetupGet(x => x.UserId).Returns(AliceUser);
		_ = new MpvPlaybackService(host, client.Object, NullLogger<MpvPlaybackService>.Instance);
		await host.StartAsync();
		await host.ConnectAsync(Alice);
		var backend = launcher.Started[0];
		await backend.Event("state", new JsonObject { ["state"] = "playing", ["itemId"] = Item.ToString("N"), ["playSessionId"] = "ps-1" });
		await BridgeClientTests.WaitUntil(() => host.LastState is not null);
		await backend.Event("position", new JsonObject { ["positionTicks"] = 5_000_000 });
		await Task.Delay(100);

		host.Kill("mpv stopped responding");
		var closing = host.ShutdownAsync();
		backend.Exit(-1);
		release.TrySetResult();
		await closing.WaitAsync(TimeSpan.FromSeconds(10));

		client.Verify(x => x.Stop(It.Is<PlaybackStopInfo>(info => info.ItemId == Item && info.PlaySessionId == "ps-1" && info.PositionTicks == 5_000_000)), Times.Once);
		// a backend that is being killed is not asked for a graceful shutdown: its report would race the kill and FluentFin's
		Assert.DoesNotContain("shutdown", backend.Commands);
	}

	[Fact]
	public async Task Shutdown_RightAfterAKill_ReturnsOnlyOnceTheInterruptionWasRaised()
	{
		// the close path goes on as soon as ShutdownAsync returns: the interruption must be out by then, even when raising
		// it takes a while (a slow subscriber stands in for a slow thread pool)
		var delivered = 0;
		using var slow = _host.PlaybackInterrupted.Subscribe(_ =>
		{
			Thread.Sleep(500);
			Interlocked.Increment(ref delivered);
		});
		await Start();
		await State("playing", Item);
		await Position(5_000_000);

		_host.Kill("mpv stopped responding");
		await _host.ShutdownAsync();

		Assert.Equal(1, Volatile.Read(ref delivered));
		VerifyStops(Times.Once());
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(5)]
	public async Task WatchdogKill_ThenClose_NeverLosesTheReport(int gapMs)
	{
		// the reviewer's R8: the watchdog kills from its timer thread, then the close path (freed by the kill) shuts down
		var lost = 0;
		for (var i = 0; i < 30; i++)
		{
			var launcher = new FakeLauncher(() => new FakeBackendProcess());
			using var host = new MpvBackendHost(launcher, new FakeSurface(), new ListLogger<MpvBackendHost>()) { ShutdownTimeout = TimeSpan.FromSeconds(1) };
			var client = new Mock<IJellyfinClient>();
			client.SetupGet(x => x.BaseUrl).Returns(Alice.ServerUrl);
			client.SetupGet(x => x.UserId).Returns(AliceUser);
			var stops = 0;
			client.Setup(x => x.Stop(It.IsAny<PlaybackStopInfo>())).Callback(() => Interlocked.Increment(ref stops)).Returns(Task.CompletedTask);
			var service = new MpvPlaybackService(host, client.Object, NullLogger<MpvPlaybackService>.Instance);
			await host.StartAsync();
			await host.ConnectAsync(Alice);
			await launcher.Started[0].Event("state", new JsonObject { ["state"] = "playing", ["itemId"] = Guid.NewGuid().ToString("N"), ["playSessionId"] = $"ps-{i}" });
			await BridgeClientTests.WaitUntil(() => host.LastState is not null);

			host.Kill("mpv stopped responding");
			if (gapMs > 0)
			{
				Thread.Sleep(gapMs);
			}
			await CloseSequence.RunAsync(client.Object, service, NullLogger.Instance);

			if (Volatile.Read(ref stops) == 0)
			{
				lost++;
			}
		}

		Assert.Equal(0, lost);
	}

	[Fact]
	public async Task Close_AfterAWatchdogKill_WaitsForTheReportToReachTheServer()
	{
		// the report takes a while on the server; FluentFin's process must not end before it is through
		var reported = new TaskCompletionSource();
		_client.Setup(x => x.Stop(It.IsAny<PlaybackStopInfo>())).Returns(async () =>
		{
			await Task.Delay(600);
			reported.TrySetResult();
		});
		await Start();
		await State("playing", Item);
		await Position(5_000_000);

		_host.Kill("mpv stopped responding");
		await CloseSequence.RunAsync(_client.Object, _service, NullLogger.Instance);

		Assert.True(reported.Task.IsCompleted);
	}

	[Fact]
	public async Task Close_WaitsAtMostTwoSecondsForACrashReportTheServerNeverAnswers()
	{
		_client.Setup(x => x.Stop(It.IsAny<PlaybackStopInfo>())).Returns(new TaskCompletionSource().Task);
		await Start();
		await State("playing", Item);
		await Position(5_000_000);

		_host.Kill("mpv stopped responding");
		var watch = System.Diagnostics.Stopwatch.StartNew();
		await CloseSequence.RunAsync(_client.Object, _service, NullLogger.Instance).WaitAsync(TimeSpan.FromSeconds(15));

		Assert.InRange(watch.Elapsed.TotalSeconds, 1.8, 4);
		VerifyStops(Times.Once());
	}

	[Fact]
	public async Task NormalShutdown_WhilePlaying_SendsNoStop()
	{
		await Start();
		await State("playing", Item);
		await Position(5_000_000);

		await _host.ShutdownAsync();
		await Task.Delay(300);

		Assert.Equal(MpvBackendState.Stopped, _host.Status.State);
		VerifyStops(Times.Never());
	}

	[Fact]
	public async Task BackendExitsAfterARestartsShutdown_SendsNoStop()
	{
		// a restart with --dev shuts the running backend down first
		await _host.StartAsync(dev: false);
		await State("playing", Item);

		await _host.StartAsync(dev: true);
		await Task.Delay(300);

		Assert.Equal(2, _launcher.Started.Count);
		VerifyStops(Times.Never());
	}

	[Fact]
	public async Task Crash_AfterTheStopWasReported_SendsNoStop()
	{
		await Start();
		await State("playing", Item);
		await Position(5_000_000);
		await State("idle", null);
		await Reported(Item);

		await CrashAndWaitForItsHandling();

		VerifyStops(Times.Never());
	}

	[Fact]
	public async Task Crash_AfterTheReportedEvent_BeforeIdle_SendsNoStop()
	{
		// `reported` is not tied to `idle` or `ended`; it can come first
		await Start();
		await State("playing", Item);
		await Position(5_000_000);
		await Reported(Item);

		await CrashAndWaitForItsHandling();

		VerifyStops(Times.Never());
	}

	[Fact]
	public async Task Crash_AfterAStopWentIdle_BeforeItsReport_SendsOneStop()
	{
		// a deferred stop: the backend answers `stop` and goes idle before its Stopped report is sent (docs/BRIDGE.md §6);
		// dying in that gap left the session unreported by both sides
		await Start();
		await State("playing", Item);
		await Position(5_000_000);
		await Backend.Event("ended", new JsonObject { ["itemId"] = Item.ToString("N"), ["reason"] = "stopped" });
		await State("idle", null);

		await CrashAndWaitForItsHandling();

		VerifyOneStop(Item, positionTicks: 5_000_000);
	}

	[Theory]
	[InlineData("failed")]          // the report errored, timed out or was abandoned
	[InlineData("none")]            // the session never reached the server: nothing to stop
	[InlineData("deferred-to-v2")]  // an outcome this version does not know
	[InlineData("#number")]
	[InlineData("#object")]
	[InlineData("#null")]
	[InlineData("#missing")]
	public async Task AnyReportOutcome_ClosesTheSession(string outcome)
	{
		// docs/BRIDGE.md §5: the backend is done with the session whatever the outcome; the UI does not validate it
		await Start();
		await State("playing", Item);
		await Position(5_000_000);
		var data = new JsonObject { ["itemId"] = Item.ToString("N"), ["playSessionId"] = $"session-{Item:N}" };
		switch (outcome)
		{
			case "#number": data["outcome"] = 2; break;
			case "#object": data["outcome"] = new JsonObject { ["code"] = "sent" }; break;
			case "#null": data["outcome"] = null; break;
			case "#missing": break;
			default: data["outcome"] = outcome; break;
		}
		var count = _events.Count;
		await Backend.Event("reported", data);
		await BridgeClientTests.WaitUntil(() => _events.Count > count);

		await CrashAndWaitForItsHandling();

		VerifyStops(Times.Never());
	}

	[Fact]
	public async Task AReportForAnotherSession_DoesNotCloseTheOpenOne()
	{
		// next: the outgoing session's `reported` comes after the new one opened
		await Start();
		await State("playing", OtherItem);
		await Position(9_000_000);
		await State("playing", Item);
		await Position(5_000_000);
		await Reported(OtherItem);

		await CrashAndWaitForItsHandling();

		VerifyOneStop(Item, positionTicks: 5_000_000);
	}

	[Fact]
	public async Task ANewerSession_ReplacesTheOpenOne()
	{
		// only the latest session is FluentFin's to report; the backend reports the outgoing one itself
		await Start();
		await State("playing", OtherItem);
		await Position(9_000_000);
		await State("playing", Item);
		await Position(5_000_000);
		await State("idle", null);

		await CrashAndWaitForItsHandling();

		VerifyOneStop(Item, positionTicks: 5_000_000);
	}

	[Fact]
	public async Task AStateAfterTheReport_DoesNotReopenTheSession()
	{
		// a session opens once, at the first state that shows it: a late snapshot of a reported session is not a new one
		await Start();
		await State("playing", Item);
		await Position(5_000_000);
		await Reported(Item);
		await State("paused", Item, positionTicks: 5_000_000);

		await CrashAndWaitForItsHandling();

		VerifyStops(Times.Never());
	}

	[Fact]
	public async Task AReportThatComesBeforeAnyStateOfItsSession_KeepsThatSessionClosed()
	{
		// the report and the state snapshots are sent from different backend threads: a snapshot taken before the stop can
		// arrive after the session's `reported`
		await Start();
		await Reported(Item);
		await State("playing", Item);
		await Position(5_000_000);

		await CrashAndWaitForItsHandling();

		VerifyStops(Times.Never());
	}

	[Fact]
	public async Task TheReportCarriesTheMediaSource_WhenOnlyALaterStateOfTheSessionNamesIt()
	{
		await Start();
		await Backend.Event("state", new JsonObject { ["state"] = "opening", ["itemId"] = Item.ToString("N"), ["playSessionId"] = $"session-{Item:N}" });
		await State("playing", Item);
		await Position(5_000_000);

		await CrashAndWaitForItsHandling();

		VerifyOneStop(Item, positionTicks: 5_000_000);
	}

	[Fact]
	public async Task APositionEventWithNoItemPlaying_DoesNotReplaceTheItemsPosition()
	{
		await Start();
		await State("playing", Item);
		await Position(5_000_000);
		await State("idle", null);
		await Position(0);

		await CrashAndWaitForItsHandling();

		VerifyOneStop(Item, positionTicks: 5_000_000);
	}

	[Fact]
	public async Task Crash_OfTheSelfTest_AfterAReportedSession_SendsNoStop()
	{
		// play_file items have no play session: they never open one, and the reported one stays closed
		await _host.StartAsync(dev: true);
		await State("playing", Item);
		await Reported(Item);
		await State("playing", null);
		await Position(1_000);

		await CrashAndWaitForItsHandling();

		VerifyStops(Times.Never());
	}

	[Fact]
	public async Task Shutdown_WithAnUnreportedSession_SendsNoStop()
	{
		// the backend reports it during its shutdown (or its server hangs): FluentFin never does after shutdown/EOF
		await Start();
		await State("playing", Item);
		await Position(5_000_000);
		await State("idle", null);

		await _host.ShutdownAsync();
		await Task.Delay(300);

		Assert.Equal(MpvBackendState.Stopped, _host.Status.State);
		VerifyStops(Times.Never());
	}

	[Fact]
	public async Task Crash_InTheErrorState_SendsNoStop()
	{
		// an item that never opened: the state names it, but there is no play session to close
		await Start();
		await State("error", Item);

		Backend.Exit(3);

		await BridgeClientTests.WaitUntil(() => _host.Status.State == MpvBackendState.Crashed);
		await Task.Delay(300);
		VerifyStops(Times.Never());
	}

	[Fact]
	public async Task Crash_OfTheSelfTest_SendsNoStop()
	{
		// play_file items have no item id: nothing on the server to close
		await _host.StartAsync(dev: true);
		await State("playing", null);

		Backend.Exit(3);

		await BridgeClientTests.WaitUntil(() => _host.Status.State == MpvBackendState.Crashed);
		await Task.Delay(300);
		VerifyStops(Times.Never());
	}

	[Fact]
	public async Task TheHostReportsEachInterruptionOnce_AndNotToLaterSubscribers()
	{
		var seen = new ConcurrentQueue<MpvInterruptedPlayback>();
		using var early = _host.PlaybackInterrupted.Subscribe(seen.Enqueue);
		await Start();
		await State("playing", Item);
		await Position(42);

		Backend.Exit(3);
		await BridgeClientTests.WaitUntil(() => !seen.IsEmpty);

		var late = new ConcurrentQueue<MpvInterruptedPlayback>();
		using var subscription = _host.PlaybackInterrupted.Subscribe(late.Enqueue);
		await Task.Delay(300);

		var interrupted = Assert.Single(seen);
		Assert.Equal(new MpvInterruptedPlayback(Item.ToString("N"), Item.ToString("N"), $"session-{Item:N}", 42, interrupted.Reason, Alice.ServerUrl, Alice.UserId), interrupted);
		Assert.Contains("exit code 3", interrupted.Reason);
		Assert.Empty(late);
	}

	// ---- round 5, item 2: the position is the play session's, not the item's

	private async Task SessionState(string state, Guid itemId, string playSessionId)
	{
		var count = _events.Count;
		await Backend.Event("state", new JsonObject
		{
			["state"] = state,
			["itemId"] = itemId.ToString("N"),
			["mediaSourceId"] = itemId.ToString("N"),
			["playSessionId"] = playSessionId,
			["positionTicks"] = 0,
		});
		await BridgeClientTests.WaitUntil(() => _events.Count > count);
	}

	private async Task SessionReported(Guid itemId, string playSessionId, string outcome = "sent")
	{
		var count = _events.Count;
		await Backend.Reported(itemId.ToString("N"), playSessionId, outcome);
		await BridgeClientTests.WaitUntil(() => _events.Count > count);
	}

	[Fact]
	public async Task Crash_WhileAReplayOfTheSameItemOpens_ReportsAFailedStop_NotTheEarlierSessionsPosition()
	{
		// the reviewer's rr5 item 2: the item played (session 1, reported), then plays again (session 2) and the backend dies
		// while that one opens: session 1's position is not session 2's, a Stopped carrying it would set the resume point
		await Start();
		await SessionState("playing", Item, "ps-first");
		await Position(9_000_000);
		await SessionReported(Item, "ps-first");
		await State("idle", null);
		await SessionState("opening", Item, "ps-replay");

		await CrashAndWaitForItsHandling(1);

		_client.Verify(x => x.Stop(It.Is<PlaybackStopInfo>(info =>
			info.ItemId == Item && info.PlaySessionId == "ps-replay" && info.PositionTicks == null && info.Failed == true)), Times.Once);
		VerifyStops(Times.Once());
	}

	[Fact]
	public async Task Crash_AfterARestartOfTheSameItem_BeforeTheNewSessionsFirstPosition_ReportsAFailedStop()
	{
		// a track or quality change renegotiates the stream: a new play session of the same item replaces the old one (whose
		// `reported` says failed); dying before the new one's first position leaves no position for it
		await Start();
		await SessionState("playing", Item, "ps-before-restart");
		await Position(9_000_000);
		await SessionState("playing", Item, "ps-after-restart");
		await SessionReported(Item, "ps-before-restart", "failed");

		await CrashAndWaitForItsHandling(1);

		_client.Verify(x => x.Stop(It.Is<PlaybackStopInfo>(info =>
			info.ItemId == Item && info.PlaySessionId == "ps-after-restart" && info.PositionTicks == null && info.Failed == true)), Times.Once);
		VerifyStops(Times.Once());
	}

	[Fact]
	public async Task Crash_AfterARestart_ReportsTheNewSessionsOwnPosition()
	{
		await Start();
		await SessionState("playing", Item, "ps-before-restart");
		await Position(9_000_000);
		await SessionState("playing", Item, "ps-after-restart");
		await Position(9_500_000);

		await CrashAndWaitForItsHandling(1);

		_client.Verify(x => x.Stop(It.Is<PlaybackStopInfo>(info =>
			info.PlaySessionId == "ps-after-restart" && info.PositionTicks == 9_500_000 && info.Failed == null)), Times.Once);
		VerifyStops(Times.Once());
	}

	[Fact]
	public async Task APositionWhileTheNextItemOpensWithoutAPlaySession_KeepsTheOpenSessionsPosition()
	{
		// a position event read after the next item's first snapshot (no play session before its PlaybackInfo) belongs to
		// no session FluentFin knows: the open session keeps the position it had
		await Start();
		await State("playing", OtherItem);
		await Position(9_000_000);
		await OpeningSnapshot(Item);
		await Position(9_200_000);

		await CrashAndWaitForItsHandling(1);

		VerifyOneStop(OtherItem, positionTicks: 9_000_000);
	}

	// ---- round 5, item 3: the report goes only to the server and user the session was played as

	[Fact]
	public async Task Crash_AfterFluentFinSwitchedServers_SendsNoStop_AndSaysSoWithoutCredentials()
	{
		// the reviewer's rr5 item 3: FluentFin logged in to server B, the next play connected the backend to B, and the old
		// server's `reported` comes after the connect answer (docs/BRIDGE.md §5); the backend dies first. FluentFin's client is
		// on B now: the old session's Stopped must not go there, and B is not where it was played
		const string serverA = "https://alice:pa55word@jf-a.example:8920/jellyfin?api_key=key-in-url";
		_client.SetupGet(x => x.BaseUrl).Returns(serverA);
		await Start(Alice with { ServerUrl = serverA });
		await State("playing", Item);
		await Position(5_000_000);

		_client.SetupGet(x => x.BaseUrl).Returns(ServerB);
		await _host.ConnectAsync(Alice with { ServerUrl = ServerB, ServerId = "srv-b", AccessToken = "token-on-b-789" });

		await CrashAndWaitForItsHandling();

		VerifyStops(Times.Never());
		var skipped = Assert.Single(_serviceLog.Messages, x => x.Contains("not reporting its stop"));
		Assert.Contains("https://jf-a.example:8920", skipped);
		Assert.Contains("https://jf-b.example:8920", skipped);
		Assert.Contains(_serviceLog.Entries, x => x.Level == Microsoft.Extensions.Logging.LogLevel.Warning && x.Message == skipped);
		foreach (var secret in new[] { "pa55word", "key-in-url", Alice.AccessToken, "token-on-b-789" })
		{
			Assert.DoesNotContain(_serviceLog.Messages.Concat(_log.Messages), x => x.Contains(secret, StringComparison.Ordinal));
		}
	}

	[Fact]
	public async Task Crash_OfASessionPlayedAfterTheSwitch_IsReportedToTheNewServer()
	{
		// a session is played as the server the backend was connected to when it opened, not the first one it had
		await Start();
		await State("playing", OtherItem);
		await Position(9_000_000);
		await Reported(OtherItem);
		_client.SetupGet(x => x.BaseUrl).Returns(ServerB);
		await _host.ConnectAsync(Alice with { ServerUrl = ServerB, ServerId = "srv-b" });
		await State("playing", Item);
		await Position(5_000_000);

		await CrashAndWaitForItsHandling();

		VerifyOneStop(Item, positionTicks: 5_000_000);
	}

	[Fact]
	public async Task Crash_WhenFluentFinIsOnTheSameServerUnderAnotherSpelling_IsReported()
	{
		_client.SetupGet(x => x.BaseUrl).Returns("HTTP://JF:8096/");
		await Start();
		await State("playing", Item);
		await Position(5_000_000);

		await CrashAndWaitForItsHandling();

		VerifyOneStop(Item, positionTicks: 5_000_000);
	}

	[Fact]
	public async Task Crash_OfASessionWithNoKnownServer_SendsNoStop()
	{
		// a play session needs a connected server, so this does not happen; if it did, the report could go anywhere
		await _host.StartAsync();
		await State("playing", Item);
		await Position(5_000_000);

		await CrashAndWaitForItsHandling();

		VerifyStops(Times.Never());
		Assert.Contains(_serviceLog.Messages, x => x.Contains("not reporting its stop"));
	}

	// ---- round 5 follow-up: and only as the user it was played as (docs/BRIDGE.md §6)

	private static readonly BridgeIdentity Bob = Alice with { UserId = BobUser.ToString("N"), UserName = "bob", AccessToken = "token-bob-456" };

	[Fact]
	public async Task Crash_AfterAUserSwitchOnTheSameServer_SendsNoStop_AndSaysSoWithoutAToken()
	{
		// FluentFin logged in as Bob on the same server and the next play connected the backend as Bob; Alice's session's
		// `reported` has not come yet when the backend dies: its Stopped is not FluentFin's to send, and never as Bob
		await Start();
		await State("playing", Item);
		await Position(5_000_000);

		_client.SetupGet(x => x.UserId).Returns(BobUser);
		await _host.ConnectAsync(Bob);

		await CrashAndWaitForItsHandling();

		VerifyStops(Times.Never());
		var skipped = Assert.Single(_serviceLog.Messages, x => x.Contains("not reporting its stop"));
		Assert.Contains(AliceUser.ToString("N"), skipped);
		Assert.Contains(BobUser.ToString("N"), skipped);
		Assert.Contains("another user", skipped);
		foreach (var token in new[] { Alice.AccessToken, Bob.AccessToken })
		{
			Assert.DoesNotContain(_serviceLog.Messages.Concat(_log.Messages), x => x.Contains(token, StringComparison.Ordinal));
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Crash_AfterTheSameUserLoggedInAgain_IsReported(bool backendReconnected)
	{
		// a new login as the same user on the same server (a new token): still the user the session was played as
		await Start();
		await State("playing", Item);
		await Position(5_000_000);
		if (backendReconnected)
		{
			await _host.ConnectAsync(Alice with { AccessToken = "token-alice-again-789" });
		}

		await CrashAndWaitForItsHandling();

		VerifyOneStop(Item, positionTicks: 5_000_000);
	}

	[Fact]
	public async Task Crash_OfASessionPlayedAfterTheUserSwitch_IsReportedAsTheNewUser()
	{
		await Start();
		await State("playing", OtherItem);
		await Position(9_000_000);
		await Reported(OtherItem);
		_client.SetupGet(x => x.UserId).Returns(BobUser);
		await _host.ConnectAsync(Bob);
		await State("playing", Item);
		await Position(5_000_000);

		await CrashAndWaitForItsHandling();

		VerifyOneStop(Item, positionTicks: 5_000_000);
	}

	[Fact]
	public async Task Crash_AfterAUserSwitch_StillChecksTheServerFirst()
	{
		// a switch to another server as another user: the server decides (the user ids of two servers are unrelated)
		await Start();
		await State("playing", Item);
		await Position(5_000_000);
		_client.SetupGet(x => x.BaseUrl).Returns(ServerB);
		_client.SetupGet(x => x.UserId).Returns(BobUser);
		await _host.ConnectAsync(Bob with { ServerUrl = ServerB });

		await CrashAndWaitForItsHandling();

		VerifyStops(Times.Never());
		Assert.Contains(_serviceLog.Messages, x => x.Contains("not FluentFin's server any more"));
	}

	// ---- round 6, item 5: a session is played as the connection its `play` was sent on, not the one current at its first state

	private Task SendPlay(Guid itemId) => _host.SendAsync(Bridge.Commands.Play, BridgeCommands.Play(new BridgePlayRequest([itemId.ToString("N")])));

	[Fact]
	public async Task Crash_OfASessionPlayedAsAlice_FirstShownAfterTheSwitchToBobWasAnswered_SendsNoStopAsBob()
	{
		// Alice's play is sent; the switch's connect as Bob stops it before it answers, but a snapshot sampled before that
		// stop, the first to show the session, is read after the answer (docs/BRIDGE.md §5): the session is still Alice's.
		// Bob's page sends set_fullscreen before its play (MpvPlaybackService.PlayAsync): that is not a play
		await Start();
		await SendPlay(Item);
		_client.SetupGet(x => x.UserId).Returns(BobUser);
		await _host.ConnectAsync(Bob);
		await _host.SendAsync(Bridge.Commands.SetFullscreen, BridgeCommands.Fullscreen(false));
		await State("playing", Item);
		await Position(5_000_000);

		await CrashAndWaitForItsHandling();

		VerifyStops(Times.Never());
		var skipped = Assert.Single(_serviceLog.Messages, x => x.Contains("not reporting its stop"));
		Assert.Contains(AliceUser.ToString("N"), skipped);
	}

	[Fact]
	public async Task Crash_OfASessionPlayedOnServerA_FirstShownAfterTheSwitchToBWasAnswered_SendsNoStopToB()
	{
		await Start();
		await SendPlay(Item);
		_client.SetupGet(x => x.BaseUrl).Returns(ServerB);
		await _host.ConnectAsync(Alice with { ServerUrl = ServerB, ServerId = "srv-b" });
		await State("playing", Item);
		await Position(5_000_000);

		await CrashAndWaitForItsHandling();

		VerifyStops(Times.Never());
		Assert.Contains(_serviceLog.Messages, x => x.Contains("not FluentFin's server any more"));
	}

	[Fact]
	public async Task Crash_OfASessionPlayedByAPlaySentAfterTheSwitch_IsReportedAsTheNewUser()
	{
		await Start();
		await SendPlay(OtherItem);
		await State("playing", OtherItem);
		await Position(9_000_000);
		await Reported(OtherItem);
		_client.SetupGet(x => x.UserId).Returns(BobUser);
		await _host.ConnectAsync(Bob);
		await SendPlay(Item);
		await State("playing", Item);
		await Position(5_000_000);

		await CrashAndWaitForItsHandling();

		VerifyOneStop(Item, positionTicks: 5_000_000);
	}

	[Theory]
	[InlineData("a11ce000000000000000000000000001", true)]
	[InlineData("a11ce000-0000-0000-0000-000000000001", true)]
	[InlineData("A11CE000000000000000000000000001", true)]
	[InlineData("b0b00000000000000000000000000002", false)]
	[InlineData("00000000000000000000000000000000", false)]
	[InlineData("aaaa", false)]
	[InlineData("", false)]
	[InlineData(null, false)]
	public void SameUser_ComparesIds(string? sessionUser, bool same)
	{
		Assert.Equal(same, MpvPlaybackService.SameUser(sessionUser, AliceUser));
	}

	[Fact]
	public void SameUser_NobodyLoggedIn_IsNeverTheSame()
	{
		Assert.False(MpvPlaybackService.SameUser(Guid.Empty.ToString("N"), Guid.Empty));
	}

	[Theory]
	[InlineData("http://jf:8096", "http://jf:8096", true)]
	[InlineData("http://jf:8096/", "HTTP://JF:8096", true)]
	[InlineData("https://jf.example/jellyfin", "https://jf.example:443/jellyfin/", true)]
	[InlineData("https://u:p@jf.example/", "https://jf.example", true)]
	[InlineData("http://jf:8096", "http://jf:8097", false)]
	[InlineData("http://jf:8096", "https://jf:8096", false)]
	[InlineData("https://jf.example/a", "https://jf.example/b", false)]
	[InlineData("http://jf-a:8096", "http://jf-b:8096", false)]
	[InlineData(null, "http://jf:8096", false)]
	[InlineData("http://jf:8096", null, false)]
	[InlineData("http://jf:8096", "", false)]
	[InlineData("not a url", "not a url", false)]
	public void SameServer_ComparesSchemeHostPortAndPath(string? sessionServer, string? currentServer, bool same)
	{
		Assert.Equal(same, MpvPlaybackService.SameServer(sessionServer, currentServer));
	}
}
