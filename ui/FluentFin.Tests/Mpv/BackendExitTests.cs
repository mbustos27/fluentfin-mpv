using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FluentFin.Tests.Mpv;

/// <summary>
/// docs/BRIDGE.md §6: whether an exit is a crash (FluentFin sends the one Stopped report) or FluentFin's own stop (it never
/// does, whatever the exit code) is decided by what came first, the exit or a kill, or the stop; not by when the exit is
/// handled. The races are made deterministic: an exit seen late, a watchdog kill held between its decision and its kill.
/// </summary>
public sealed class BackendExitTests
{
	private static readonly Guid Item = Guid.Parse("0123456789abcdef0123456789abcdef");
	private static readonly Guid AliceUser = Guid.Parse("a11ce000000000000000000000000001");
	private static readonly BridgeIdentity Alice = new("http://jf:8096", "srv", "Home", AliceUser.ToString("N"), "alice", "token-alice-123", "dev", "PC", "FluentFin", "1.0");

	/// <summary>FluentFin's client, logged in as the server and user the backend plays as (the crash report goes only there, docs/BRIDGE.md §6).</summary>
	private static Mock<IJellyfinClient> ClientOnAlicesServer()
	{
		var client = new Mock<IJellyfinClient>();
		client.SetupGet(x => x.BaseUrl).Returns(Alice.ServerUrl);
		client.SetupGet(x => x.UserId).Returns(AliceUser);
		return client;
	}

	/// <summary>Connected (a play session is played as the server and user of the connect), then one session playing at 5_000_000.</summary>
	private static async Task Playing(FakeBackendProcess backend, MpvBackendHost host, bool connect = true)
	{
		if (connect)
		{
			await host.ConnectAsync(Alice);
		}
		await backend.Event("state", new JsonObject { ["state"] = "playing", ["itemId"] = Item.ToString("N"), ["mediaSourceId"] = "ms-1", ["playSessionId"] = "ps-1" });
		await BridgeClientTests.WaitUntil(() => host.LastState is not null);
		await backend.Event("position", new JsonObject { ["positionTicks"] = 5_000_000 });
		await Task.Delay(100);
	}

	/// <summary>
	/// Until the host's connection to the backend is closed: a request then fails at once. One that is still sent fails on
	/// the dead backend's closed input, which closes the connection too.
	/// </summary>
	private static Task UntilTheConnectionIsClosed(MpvBackendHost host) => BridgeClientTests.WaitUntil(() =>
	{
		var ping = host.SendAsync(Bridge.Commands.Ping, timeout: TimeSpan.FromSeconds(1));
		_ = ping.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
		return ping.IsFaulted;
	});

	private static void VerifyOneStopAtTheLatestPosition(Mock<IJellyfinClient> client) =>
		client.Verify(x => x.Stop(It.Is<PlaybackStopInfo>(i => i.ItemId == Item && i.PlaySessionId == "ps-1" && i.PositionTicks == 5_000_000 && i.Failed == null)), Times.Once);

	// ---- item 1: a crash whose exit is seen only after the close began (the reviewer's P5)

	[Fact]
	public async Task ACrashSeenOnlyAfterTheCloseBegan_StillGetsItsStoppedReport()
	{
		// the backend dies (its output ends), the close begins, and only then is its exit seen: the exit came first
		var inner = new FakeBackendProcess();
		var process = new LateExitProcess(inner);
		using var host = new MpvBackendHost(new OneLauncher(process), new FakeSurface(), new ListLogger<MpvBackendHost>()) { ShutdownTimeout = TimeSpan.FromSeconds(3) };
		var client = ClientOnAlicesServer();
		var service = new MpvPlaybackService(host, client.Object, NullLogger<MpvPlaybackService>.Instance);
		await host.StartAsync();
		await Playing(inner, host);

		inner.Exit(3);
		await UntilTheConnectionIsClosed(host);
		Assert.False(process.Exited.IsCompleted);
		var closing = CloseSequence.RunAsync(client.Object, service, NullLogger.Instance);
		await Task.Delay(100);
		process.SeeTheExit();
		await closing.WaitAsync(TimeSpan.FromSeconds(15));

		VerifyOneStopAtTheLatestPosition(client);
		Assert.DoesNotContain("shutdown", inner.Commands);
	}

	[Fact]
	public async Task ACrashBeforeTheClose_NeitherSeenNorItsOutputEnded_WhenTheCloseBegins_GetsItsStopped()
	{
		// round 6, item 2 (the reviewer's rr6 P1): the process has ended, as Windows knows, before the close begins, but
		// FluentFin has seen neither its exit nor the end of its output (a busy pool; a pipe something it started still holds).
		// What happened first decides (docs/BRIDGE.md §6): a crash, not the close's own exit
		var dead = new ManualResetEventSlim();
		var never = new ManualResetEventSlim();
		var inner = new FakeBackendProcess((_, request) =>
		{
			// a dead process answers nothing
			if (dead.IsSet)
			{
				never.Wait(TimeSpan.FromSeconds(20));
			}
			return new JsonObject();
		});
		var process = new DiesUnseenProcess(inner);
		using var host = new MpvBackendHost(new OneLauncher(process), new FakeSurface(), new ListLogger<MpvBackendHost>()) { ShutdownTimeout = TimeSpan.FromSeconds(2) };
		var client = ClientOnAlicesServer();
		var service = new MpvPlaybackService(host, client.Object, NullLogger<MpvPlaybackService>.Instance);
		try
		{
			await host.StartAsync();
			await Playing(inner, host);

			dead.Set();
			process.Die();
			await Task.Delay(30);
			var closing = CloseSequence.RunAsync(client.Object, service, NullLogger.Instance);
			await Task.Delay(200);
			inner.Exit(3);
			process.SeeTheExit();
			await closing.WaitAsync(TimeSpan.FromSeconds(20));
			await Task.Delay(300);

			VerifyOneStopAtTheLatestPosition(client);
			Assert.DoesNotContain("shutdown", inner.Commands);
		}
		finally
		{
			never.Set();
		}
	}

	[Fact]
	public async Task AnExitAlreadySeen_ButNotYetHandled_WhenTheCloseBegan_IsACrash()
	{
		// the exit is known (its output stays open: something it started holds the pipe) but its handling has not run yet
		// when the close begins: the exit came first, so it is a crash, and the dead backend gets no shutdown
		var inner = new FakeBackendProcess();
		var process = new LateExitProcess(inner, onlyTheFirstWaiter: true);
		using var host = new MpvBackendHost(new OneLauncher(process), new FakeSurface(), new ListLogger<MpvBackendHost>())
		{
			ShutdownTimeout = TimeSpan.FromSeconds(3),
			ExitHandlingTimeout = TimeSpan.FromSeconds(5),
		};
		var client = ClientOnAlicesServer();
		var service = new MpvPlaybackService(host, client.Object, NullLogger<MpvPlaybackService>.Instance);
		await host.StartAsync();
		await Playing(inner, host);

		inner.ExitLeavingThePipesOpen(3);
		var closing = CloseSequence.RunAsync(client.Object, service, NullLogger.Instance, interruptedReportTimeout: TimeSpan.FromSeconds(5));
		await Task.Delay(100);
		process.SeeTheExit();
		await closing.WaitAsync(TimeSpan.FromSeconds(20));

		VerifyOneStopAtTheLatestPosition(client);
		Assert.DoesNotContain("shutdown", inner.Commands);
	}

	// ---- item 1: a watchdog kill decided just before the close (the reviewer's P2b): one outcome, not two

	[Fact]
	public async Task AKillDecidedJustBeforeTheClose_GetsNoShutdown_AndOneStoppedFromFluentFin()
	{
		// the watchdog has decided to kill (and is held in its log line) when the close begins: the close must not ask the
		// backend for a graceful shutdown (its own final report would race the kill, and FluentFin reports this crash too)
		var log = new GateLogger<MpvBackendHost>("Killing the mpv backend");
		var launcher = new FakeLauncher(() => new FakeBackendProcess());
		using var host = new MpvBackendHost(launcher, new FakeSurface(), log);
		var client = ClientOnAlicesServer();
		_ = new MpvPlaybackService(host, client.Object, NullLogger<MpvPlaybackService>.Instance);
		await host.StartAsync();
		var backend = launcher.Started[0];
		await Playing(backend, host);

		var watchdog = Task.Run(() => host.Kill("mpv stopped responding"));
		Assert.True(log.Entered.Wait(TimeSpan.FromSeconds(5)));
		var closing = host.ShutdownAsync();
		await Task.Delay(300);
		log.Release.Set();
		await watchdog.WaitAsync(TimeSpan.FromSeconds(5));
		await closing.WaitAsync(TimeSpan.FromSeconds(15));
		await Task.Delay(300);

		Assert.DoesNotContain("shutdown", backend.Commands);
		Assert.False(backend.StdinClosed);
		Assert.True(backend.Killed);
		VerifyOneStopAtTheLatestPosition(client);
	}

	[Fact]
	public async Task AKillHeldPastTheStopsWait_IsFinishedByTheStop_AndStillReportedOnce()
	{
		// the same race with the watchdog held longer than the half second the stop waits: the stop kills it itself
		var log = new GateLogger<MpvBackendHost>("Killing the mpv backend");
		var launcher = new FakeLauncher(() => new FakeBackendProcess());
		using var host = new MpvBackendHost(launcher, new FakeSurface(), log);
		var client = ClientOnAlicesServer();
		var service = new MpvPlaybackService(host, client.Object, NullLogger<MpvPlaybackService>.Instance);
		await host.StartAsync();
		var backend = launcher.Started[0];
		await Playing(backend, host);

		var watchdog = Task.Run(() => host.Kill("mpv stopped responding"));
		Assert.True(log.Entered.Wait(TimeSpan.FromSeconds(5)));
		var watch = System.Diagnostics.Stopwatch.StartNew();
		await CloseSequence.RunAsync(client.Object, service, NullLogger.Instance).WaitAsync(TimeSpan.FromSeconds(15));
		var closeTook = watch.Elapsed;
		log.Release.Set();
		await watchdog.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.DoesNotContain("shutdown", backend.Commands);
		Assert.True(backend.Killed);
		Assert.True(closeTook < TimeSpan.FromSeconds(3), $"close took {closeTook}");
		VerifyOneStopAtTheLatestPosition(client);
	}

	// ---- round 5, item 1: the exit of a kill the close makes is seen a moment after the kill (the real launcher)

	private static int StopReports(Mock<IJellyfinClient> client) =>
		client.Invocations.Count(i => i.Method.Name == nameof(IJellyfinClient.Stop) && i.Arguments.Count == 1);

	[Theory]
	[InlineData(0)]
	[InlineData(50)]
	public async Task AKillHeldPastTheStopsWait_WhoseExitIsSeenAfterTheKill_IsReportedBeforeTheCloseReturns(int exitSeenAfterMs)
	{
		// the reviewer's rr5 probe: the watchdog decided to kill and is held (in its log line) past the half second the stop
		// waits, so the stop kills it itself; the real launcher sees that exit a moment after Kill returns (TerminateJobObject,
		// then the registered wait's callback). Once the close returns, the Stopped FluentFin owes has been asked for
		var log = new GateLogger<MpvBackendHost>("Killing the mpv backend");
		var launcher = new FakeLauncher(() => new FakeBackendProcess { KillExitSeenAfter = TimeSpan.FromMilliseconds(exitSeenAfterMs) });
		using var host = new MpvBackendHost(launcher, new FakeSurface(), log);
		var client = ClientOnAlicesServer();
		var service = new MpvPlaybackService(host, client.Object, NullLogger<MpvPlaybackService>.Instance);
		var interrupted = 0;
		using var subscription = host.PlaybackInterrupted.Subscribe(_ => Interlocked.Increment(ref interrupted));
		await host.StartAsync();
		var backend = launcher.Started[0];
		await Playing(backend, host);

		var watchdog = Task.Run(() => host.Kill("mpv stopped responding"));
		Assert.True(log.Entered.Wait(TimeSpan.FromSeconds(5)));
		await CloseSequence.RunAsync(client.Object, service, NullLogger.Instance).WaitAsync(TimeSpan.FromSeconds(15));
		var interruptedWhenTheCloseReturned = Volatile.Read(ref interrupted);
		var stopsWhenTheCloseReturned = StopReports(client);
		log.Release.Set();
		await watchdog.WaitAsync(TimeSpan.FromSeconds(5));
		await Task.Delay(500);

		Assert.Equal(1, interruptedWhenTheCloseReturned);
		Assert.Equal(1, stopsWhenTheCloseReturned);
		VerifyOneStopAtTheLatestPosition(client);
		Assert.DoesNotContain("shutdown", backend.Commands);
	}

	[Fact]
	public async Task ShutdownAsync_AfterAHeldKill_WhoseExitIsSeenLater_ReturnsOnlyOnceTheInterruptionWasRaised()
	{
		// the host's own promise (IMpvBackend.ShutdownAsync), without the close sequence around it
		var log = new GateLogger<MpvBackendHost>("Killing the mpv backend");
		var launcher = new FakeLauncher(() => new FakeBackendProcess { KillExitSeenAfter = TimeSpan.FromMilliseconds(300) });
		using var host = new MpvBackendHost(launcher, new FakeSurface(), log);
		var interrupted = 0;
		using var subscription = host.PlaybackInterrupted.Subscribe(_ => Interlocked.Increment(ref interrupted));
		await host.StartAsync();
		await Playing(launcher.Started[0], host);

		var watchdog = Task.Run(() => host.Kill("mpv stopped responding"));
		Assert.True(log.Entered.Wait(TimeSpan.FromSeconds(5)));
		var watch = System.Diagnostics.Stopwatch.StartNew();
		await host.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(15));
		var took = watch.Elapsed;
		var interruptedWhenItReturned = Volatile.Read(ref interrupted);
		log.Release.Set();
		await watchdog.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(1, interruptedWhenItReturned);
		// half a second for the dying backend, then its exit 0.3 s after the stop's own kill; well inside the 2 s bound
		Assert.True(took < TimeSpan.FromSeconds(2.5), $"shutdown took {took}");
	}

	[Fact]
	public async Task AKillThatDoesNotTake_DelaysTheCloseByAtMostTheExitHandlingBound()
	{
		// the watchdog decided to kill, and the kill does nothing (the process stays until its job is released): the close
		// waits for that exit, but bounded (half a second for the dying backend, then at most ExitHandlingTimeout; here the
		// release of the process ends the wait, see AKillThatNeverTakes_IsWaitedForOnce for one that does not)
		var log = new GateLogger<MpvBackendHost>("Killing the mpv backend");
		var launcher = new FakeLauncher(() => new FakeBackendProcess { KillEnds = false });
		var host = new MpvBackendHost(launcher, new FakeSurface(), log);
		var client = ClientOnAlicesServer();
		var service = new MpvPlaybackService(host, client.Object, NullLogger<MpvPlaybackService>.Instance);
		try
		{
			await host.StartAsync();
			await Playing(launcher.Started[0], host);

			var watchdog = Task.Run(() => host.Kill("mpv stopped responding"));
			Assert.True(log.Entered.Wait(TimeSpan.FromSeconds(5)));
			var watch = System.Diagnostics.Stopwatch.StartNew();
			await CloseSequence.RunAsync(client.Object, service, NullLogger.Instance).WaitAsync(TimeSpan.FromSeconds(20));
			var took = watch.Elapsed;
			log.Release.Set();
			await watchdog.WaitAsync(TimeSpan.FromSeconds(5));

			Assert.True(took < TimeSpan.FromSeconds(3.5), $"close took {took}");
			Assert.True(took >= TimeSpan.FromSeconds(2.4), $"close took {took}: it did not wait for the exit");
		}
		finally
		{
			log.Release.Set();
			host.Dispose();
		}
	}

	[Fact]
	public async Task AKillThatNeverTakes_IsWaitedForOnce_NotByTheStopAndAgainByTheShutdown()
	{
		// round 6, item 4: the watchdog decided to kill and the kill never takes, and releasing the process does not end the
		// wait for its exit either (the real launcher waits on a duplicate of the handle). The stop waits for that exit (at
		// most ExitHandlingTimeout), and the shutdown must not wait for the same exit again: ~2.5 s in all, not ~4.5 s
		var log = new GateLogger<MpvBackendHost>("Killing the mpv backend");
		var inner = new FakeBackendProcess { KillEnds = false };
		var process = new ExitNeverSeenProcess(inner);
		var host = new MpvBackendHost(new OneLauncher(process), new FakeSurface(), log);
		var client = ClientOnAlicesServer();
		var service = new MpvPlaybackService(host, client.Object, NullLogger<MpvPlaybackService>.Instance);
		try
		{
			await host.StartAsync();
			await Playing(inner, host);

			var watchdog = Task.Run(() => host.Kill("mpv stopped responding"));
			Assert.True(log.Entered.Wait(TimeSpan.FromSeconds(5)));
			var watch = System.Diagnostics.Stopwatch.StartNew();
			await CloseSequence.RunAsync(client.Object, service, NullLogger.Instance).WaitAsync(TimeSpan.FromSeconds(20));
			var took = watch.Elapsed;
			log.Release.Set();
			await watchdog.WaitAsync(TimeSpan.FromSeconds(5));

			Assert.True(took >= TimeSpan.FromSeconds(2.4), $"close took {took}: it did not wait for the exit");
			Assert.True(took < TimeSpan.FromSeconds(3.8), $"close took {took}: it waited for the same exit twice");
		}
		finally
		{
			log.Release.Set();
			process.SeeTheExit();
			host.Dispose();
		}
	}

	[Fact]
	public async Task AKillTheCloseDecides_IsNotWaitedFor_AndNotReported()
	{
		// the shutdown is never answered, so the close kills the backend at its deadline: that kill is the close's own stop
		// (docs/BRIDGE.md §6), nothing can be owed for it, so the close does not wait for its exit to be seen
		var hang = new TaskCompletionSource();
		var launcher = new FakeLauncher(() => new FakeBackendProcess((_, request) =>
		{
			if ((string?)request["cmd"] == "shutdown")
			{
				hang.Task.Wait();
			}
			return new JsonObject();
		})
		{ KillExitSeenAfter = TimeSpan.FromSeconds(3) });
		var host = new MpvBackendHost(launcher, new FakeSurface(), new ListLogger<MpvBackendHost>()) { ShutdownTimeout = TimeSpan.FromSeconds(1) };
		var client = ClientOnAlicesServer();
		var service = new MpvPlaybackService(host, client.Object, NullLogger<MpvPlaybackService>.Instance);
		try
		{
			await host.StartAsync();
			var backend = launcher.Started[0];
			await Playing(backend, host);

			var watch = System.Diagnostics.Stopwatch.StartNew();
			await CloseSequence.RunAsync(client.Object, service, NullLogger.Instance).WaitAsync(TimeSpan.FromSeconds(15));
			var took = watch.Elapsed;
			hang.TrySetResult();
			await backend.Exited.WaitAsync(TimeSpan.FromSeconds(10));
			await Task.Delay(300);

			Assert.True(backend.Killed);
			Assert.True(took < TimeSpan.FromSeconds(2), $"close took {took}");
			client.Verify(x => x.Stop(It.IsAny<PlaybackStopInfo>()), Times.Never);
		}
		finally
		{
			hang.TrySetResult();
			host.Dispose();
		}
	}

	[Fact]
	public async Task AConnectThatHangsAtTheClose_IsKilledAsTheClosesOwnStop_NotReportedAsACrash()
	{
		// the close kills a backend whose connect holds it (docs/BRIDGE.md §3 cap), then stops it as usual: the stop began
		// with that kill, so the later look at the (now dead) backend must not turn the close's own kill into a crash
		var release = new TaskCompletionSource();
		var connectArrived = new TaskCompletionSource();
		var connects = 0;
		var inner = new FakeBackendProcess((_, request) =>
		{
			// the first connect (Alice's, the session's server) is answered; the second (a user switch) hangs
			if ((string?)request["cmd"] == "connect" && Interlocked.Increment(ref connects) > 1)
			{
				connectArrived.TrySetResult();
				release.Task.Wait();
			}
			return new JsonObject();
		});
		var process = new LateExitProcess(inner);
		using var host = new MpvBackendHost(new OneLauncher(process), new FakeSurface(), new ListLogger<MpvBackendHost>())
		{
			ConnectTimeout = TimeSpan.FromSeconds(30),
			LifecycleTimeout = TimeSpan.FromMilliseconds(500),
			ShutdownTimeout = TimeSpan.FromSeconds(1),
		};
		var client = ClientOnAlicesServer();
		var service = new MpvPlaybackService(host, client.Object, NullLogger<MpvPlaybackService>.Instance);
		var interrupted = 0;
		using var subscription = host.PlaybackInterrupted.Subscribe(_ => Interlocked.Increment(ref interrupted));
		try
		{
			await host.StartAsync();
			await Playing(inner, host);
			var connecting = host.ConnectAsync(Alice with { UserId = "b0b00000000000000000000000000002", UserName = "bob", AccessToken = "token-bob-456" });
			await connectArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));

			await CloseSequence.RunAsync(client.Object, service, NullLogger.Instance).WaitAsync(TimeSpan.FromSeconds(15));
			await Assert.ThrowsAnyAsync<BridgeException>(() => connecting.WaitAsync(TimeSpan.FromSeconds(5)));
			process.SeeTheExit();
			await Task.Delay(500);

			Assert.True(inner.Killed);
			Assert.Equal(0, Volatile.Read(ref interrupted));
			client.Verify(x => x.Stop(It.IsAny<PlaybackStopInfo>()), Times.Never);
		}
		finally
		{
			release.TrySetResult();
		}
	}

	[Fact]
	public async Task AReportedEventStillInThePipe_WhenACrashIsStopped_IsStillRead()
	{
		// the backend reported its session, then died; FluentFin's reader is slow (a handler holds it) and has not read the
		// `reported` yet when the close stops the dead backend: the stop must let the reader finish before it releases the
		// connection, or the session looks unreported and FluentFin sends a second Stopped
		var log = new ListLogger<MpvBackendHost>();
		var launcher = new FakeLauncher(() => new FakeBackendProcess());
		using var host = new MpvBackendHost(launcher, new FakeSurface(), log);
		var client = ClientOnAlicesServer();
		_ = new MpvPlaybackService(host, client.Object, NullLogger<MpvPlaybackService>.Instance);
		var holding = new ManualResetEventSlim();
		var letGo = new ManualResetEventSlim();
		using var slowHandler = host.Events.Subscribe(e =>
		{
			if (e.Name == Bridge.Events.Ended)
			{
				holding.Set();
				letGo.Wait(TimeSpan.FromSeconds(10));
			}
		});
		await host.StartAsync();
		var backend = launcher.Started[0];
		await Playing(backend, host);

		await backend.Event("ended", new JsonObject { ["itemId"] = Item.ToString("N"), ["reason"] = "stopped" });
		await backend.Reported(Item.ToString("N"), "ps-1");
		Assert.True(holding.Wait(TimeSpan.FromSeconds(5)));
		backend.Exit(3);
		var closing = host.ShutdownAsync();
		await Task.Delay(100);
		letGo.Set();
		await closing.WaitAsync(TimeSpan.FromSeconds(15));
		await Task.Delay(300);

		Assert.Contains(log.Messages, x => x.Contains("stopped unexpectedly"));
		client.Verify(x => x.Stop(It.IsAny<PlaybackStopInfo>()), Times.Never);
	}

	// ---- item 3: after shutdown or stdin EOF FluentFin never reports, whatever the exit code (the reviewer's P3, P3b)

	[Fact]
	public async Task Exit1_AfterAnsweringShutdown_SendsNoStop_AndWarnsThatTheFinalReportMayBeMissing()
	{
		// exit 1 = the backend's 8 s shutdown deadline ran out (docs/BRIDGE.md §3): its report may still have gone out
		var log = new ListLogger<MpvBackendHost>();
		var launcher = new FakeLauncher(() => new FakeBackendProcess { ShutdownExitCode = 1 });
		using var host = new MpvBackendHost(launcher, new FakeSurface(), log);
		var client = ClientOnAlicesServer();
		var service = new MpvPlaybackService(host, client.Object, NullLogger<MpvPlaybackService>.Instance);
		await host.StartAsync();
		await Playing(launcher.Started[0], host);

		await CloseSequence.RunAsync(client.Object, service, NullLogger.Instance);
		await Task.Delay(300);

		Assert.Equal(1, await launcher.Started[0].Exited);
		Assert.Contains("shutdown", launcher.Started[0].Commands);
		Assert.Equal(MpvBackendState.Stopped, host.Status.State);
		client.Verify(x => x.Stop(It.IsAny<PlaybackStopInfo>()), Times.Never);
		Assert.Contains(log.Entries, x => x.Level == LogLevel.Warning && x.Message.Contains("exited with code 1") && x.Message.Contains("may not have its final playback report"));
		Assert.DoesNotContain(log.Entries, x => x.Level >= LogLevel.Error);
	}

	[Fact]
	public async Task Exit0_AfterShutdown_IsLoggedAsInformation()
	{
		var log = new ListLogger<MpvBackendHost>();
		var launcher = new FakeLauncher(() => new FakeBackendProcess());
		using var host = new MpvBackendHost(launcher, new FakeSurface(), log);
		await host.StartAsync();

		await host.ShutdownAsync();
		await BridgeClientTests.WaitUntil(() => log.Messages.Any(x => x.Contains("exited with code 0")));

		Assert.Contains(log.Entries, x => x.Level == LogLevel.Information && x.Message.Contains("exited with code 0"));
		Assert.DoesNotContain(log.Entries, x => x.Level >= LogLevel.Warning);
	}

	[Fact]
	public async Task Exit1_AfterStdinEof_SendsNoStop()
	{
		// stdin EOF is a shutdown request too (docs/BRIDGE.md §1): the UI closes stdin when the backend did not take
		// `shutdown`, and the backend exits 1 when its deadline runs out
		var launcher = new FakeLauncher(() => new FakeBackendProcess((_, request) => (string?)request["cmd"] == "shutdown" ? null : new JsonObject())
		{
			ShutdownExitCode = null,
			EofExitCode = 1,
		});
		using var host = new MpvBackendHost(launcher, new FakeSurface(), new ListLogger<MpvBackendHost>());
		var client = ClientOnAlicesServer();
		var service = new MpvPlaybackService(host, client.Object, NullLogger<MpvPlaybackService>.Instance);
		await host.StartAsync();
		var backend = launcher.Started[0];
		await Playing(backend, host);

		await CloseSequence.RunAsync(client.Object, service, NullLogger.Instance);
		await Task.Delay(300);

		Assert.True(backend.StdinClosed);
		Assert.False(backend.Killed);
		Assert.Equal(1, await backend.Exited);
		client.Verify(x => x.Stop(It.IsAny<PlaybackStopInfo>()), Times.Never);
	}

	[Fact]
	public async Task Exit1_WithoutAnsweringShutdown_SendsNoStop()
	{
		// the backend took `shutdown` and exited 1 before answering it: still the stop's exit
		var launcher = new FakeLauncher(() => new FakeBackendProcess((p, request) =>
		{
			if ((string?)request["cmd"] == "shutdown")
			{
				p.Exit(1);
			}
			return new JsonObject();
		}));
		using var host = new MpvBackendHost(launcher, new FakeSurface(), new ListLogger<MpvBackendHost>());
		var client = ClientOnAlicesServer();
		var service = new MpvPlaybackService(host, client.Object, NullLogger<MpvPlaybackService>.Instance);
		await host.StartAsync();
		await Playing(launcher.Started[0], host);

		await CloseSequence.RunAsync(client.Object, service, NullLogger.Instance);
		await Task.Delay(300);

		Assert.Equal(1, await launcher.Started[0].Exited);
		client.Verify(x => x.Stop(It.IsAny<PlaybackStopInfo>()), Times.Never);
	}

	/// <summary>
	/// The process's exit is seen (the <see cref="Exited"/> task completes) only when the test says, like a late wait
	/// callback. <c>onlyTheFirstWaiter</c>: only the first reader of <see cref="Exited"/> (the host's exit handler) is held,
	/// later readers see the real exit: the exit is known, its handling has not run yet. One held task for all readers, made
	/// once (a new one per read could still be incomplete just after <see cref="SeeTheExit"/>, and failed a test 1 in 13
	/// loaded runs).
	/// </summary>
	private sealed class LateExitProcess : IMpvBackendProcess
	{
		private readonly FakeBackendProcess _inner;
		private readonly bool _onlyTheFirstWaiter;
		private readonly TaskCompletionSource _seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly Task<int> _held;
		private int _reads;

		public LateExitProcess(FakeBackendProcess inner, bool onlyTheFirstWaiter = false)
		{
			_inner = inner;
			_onlyTheFirstWaiter = onlyTheFirstWaiter;
			_held = _seen.Task.ContinueWith(_ => inner.Exited, TaskScheduler.Default).Unwrap();
		}

		public void SeeTheExit() => _seen.TrySetResult();

		public int Id => _inner.Id;
		public TextReader StandardOutput => _inner.StandardOutput;
		public TextWriter StandardInput => _inner.StandardInput;
		public TextReader StandardError => _inner.StandardError;

		public Task<int> Exited => _onlyTheFirstWaiter && Interlocked.Increment(ref _reads) > 1 ? _inner.Exited : _held;

		public void Kill() => _inner.Kill();
		public void Dispose() => _inner.Dispose();
	}

	/// <summary>
	/// A process that dies (<see cref="Die"/>: Windows knows, <see cref="HasExited"/> and <see cref="ExitedAt"/> say so) before
	/// FluentFin sees its exit (<see cref="SeeTheExit"/>). The reviewer's rr6 P1 process.
	/// </summary>
	private sealed class DiesUnseenProcess(FakeBackendProcess inner) : IMpvBackendProcess
	{
		private readonly TaskCompletionSource _seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private long _diedAt;
		private Task<int>? _exited;

		public void Die() => Interlocked.CompareExchange(ref _diedAt, Environment.TickCount64, 0);
		public void SeeTheExit() => _seen.TrySetResult();

		public int Id => inner.Id;
		public TextReader StandardOutput => inner.StandardOutput;
		public TextWriter StandardInput => inner.StandardInput;
		public TextReader StandardError => inner.StandardError;
		public Task<int> Exited => _exited ??= _seen.Task.ContinueWith(_ => inner.Exited, TaskScheduler.Default).Unwrap();
		public long? ExitedAt => Volatile.Read(ref _diedAt) is var at and not 0 ? at : null;
		public bool HasExited => ExitedAt is not null;
		public void Kill() { Die(); inner.Kill(); SeeTheExit(); }
		public void Dispose() => inner.Dispose();
	}

	/// <summary>
	/// A process whose exit is never seen until the test says so: its kill does nothing and releasing it does not end the wait
	/// for its exit (like the real launcher, which waits on a duplicate of the process handle).
	/// </summary>
	private sealed class ExitNeverSeenProcess(FakeBackendProcess inner) : IMpvBackendProcess
	{
		private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public void SeeTheExit() => _exit.TrySetResult(-1);

		public int Id => inner.Id;
		public TextReader StandardOutput => inner.StandardOutput;
		public TextWriter StandardInput => inner.StandardInput;
		public TextReader StandardError => inner.StandardError;
		public Task<int> Exited => _exit.Task;
		public bool HasExited => false;
		public void Kill() => inner.Kill();
		public void Dispose() => inner.Dispose();
	}

	private sealed class OneLauncher(IMpvBackendProcess process) : IMpvBackendLauncher
	{
		public MpvBackendCommand? Resolve(bool dev, out string searched)
		{
			searched = "fake";
			return new MpvBackendCommand("python.exe", ["-I", "-m", MpvBackendCommand.BridgeModule], "test");
		}

		public IMpvBackendProcess Start(MpvBackendCommand command) => process;
	}
}

/// <summary>Holds the logging thread inside the first message that contains the needle, until <see cref="Release"/>.</summary>
internal sealed class GateLogger<T>(string needle) : ILogger<T>
{
	private int _once;

	public ManualResetEventSlim Entered { get; } = new();
	public ManualResetEventSlim Release { get; } = new();
	public ConcurrentQueue<string> Messages { get; } = new();

	public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

	public bool IsEnabled(LogLevel logLevel) => true;

	public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
	{
		var text = formatter(state, exception);
		Messages.Enqueue(text);
		if (text.Contains(needle, StringComparison.Ordinal) && Interlocked.Exchange(ref _once, 1) == 0)
		{
			Entered.Set();
			Release.Wait(TimeSpan.FromSeconds(10));
		}
	}
}
