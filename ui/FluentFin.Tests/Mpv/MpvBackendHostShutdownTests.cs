using System.Diagnostics;
using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FluentFin.Tests.Mpv;

/// <summary>Killing and stopping the backend never throws into FluentFin and never takes more than a few seconds.</summary>
public sealed class MpvBackendHostShutdownTests
{
	private static readonly BridgeIdentity Alice = new("http://jf:8096", "srv", "Home", "aaaa", "alice", "token-alice-123", "dev", "PC", "FluentFin", "1.0");

	private readonly ListLogger<MpvBackendHost> _log = new();

	private static JsonObject? Ok(FakeBackendProcess process, JsonObject request) => (string?)request["cmd"] == "connect"
		? new JsonObject { ["mpv"] = "0.41.0", ["mpvBackend"] = "external" }
		: new JsonObject();

	/// <summary>A play request big enough to fill the stdin pipe of a backend that does not read it.</summary>
	private static JsonObject HugePlay() => new()
	{
		["itemIds"] = new JsonArray([.. Enumerable.Range(0, 20_000).Select(i => (JsonNode)JsonValue.Create($"{i:D32}"))]),
	};

	[Fact]
	public async Task Kill_ThatThrowsAggregateException_DoesNotEscape_AndIsReportedAsAKill()
	{
		// what Process.Kill(entireProcessTree) did on the watchdog's timer thread: the job close had already ended the
		// tree, then the tree walk threw "Not all processes in process tree could be terminated"
		var launcher = new FakeLauncher(() => new FakeBackendProcess(Ok)
		{
			KillFailure = new AggregateException("Not all processes in process tree could be terminated.", new System.ComponentModel.Win32Exception(6)),
		});
		using var host = new MpvBackendHost(launcher, new FakeSurface(), _log);
		await host.StartAsync();

		var error = Record.Exception(() => host.Kill("mpv stopped responding"));

		Assert.Null(error);
		await BridgeClientTests.WaitUntil(() => host.Status.State == MpvBackendState.Crashed);
		Assert.Contains("mpv stopped responding", host.Status.LastError);
		Assert.True(launcher.Started[0].Killed);
	}

	[Fact]
	public async Task Shutdown_WhenTheBackendIgnoresIt_KillsIt_EvenIfTheKillThrows()
	{
		// answers nothing, never exits by itself, and its kill throws
		var hang = new TaskCompletionSource();
		var launcher = new FakeLauncher(() => new FakeBackendProcess((_, _) =>
		{
			hang.Task.Wait();
			return new JsonObject();
		})
		{ KillFailure = new AggregateException("tree walk failed") });
		var host = new MpvBackendHost(launcher, new FakeSurface(), _log) { ShutdownTimeout = TimeSpan.FromMilliseconds(500) };
		try
		{
			await host.StartAsync();
			var process = launcher.Started[0];

			var watch = Stopwatch.StartNew();
			await host.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));

			Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"took {watch.Elapsed}");
			Assert.True(process.Killed);
			Assert.Equal(MpvBackendState.Stopped, host.Status.State);
		}
		finally
		{
			hang.TrySetResult();
			host.Dispose();
		}
	}

	[Fact]
	public async Task Shutdown_WaitsForASlowFinalReport_AnsweredAt8_9s_WithoutClosingStdinFirst()
	{
		// the backend answers shutdown after its final Stopped report, within its own 8 s deadline plus a little: ~9 s at
		// worst on a hung server (docs/BRIDGE.md §3). The UI's 10 s must cover that with the default settings.
		var stdinClosedBeforeTheAnswer = false;
		var launcher = new FakeLauncher(() => new FakeBackendProcess((process, request) =>
		{
			if ((string?)request["cmd"] == "shutdown")
			{
				Thread.Sleep(TimeSpan.FromSeconds(8.9));
				stdinClosedBeforeTheAnswer = !((StreamWriter)process.StandardInput).BaseStream.CanWrite;
			}
			return new JsonObject();
		}));
		using var host = new MpvBackendHost(launcher, new FakeSurface(), _log);
		await host.StartAsync();
		var backend = launcher.Started[0];

		var watch = Stopwatch.StartNew();
		await host.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(30));

		Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(8.8), $"took {watch.Elapsed}");
		Assert.False(stdinClosedBeforeTheAnswer);
		Assert.False(backend.Killed);
		Assert.Equal(0, await backend.Exited);
		Assert.Equal(MpvBackendState.Stopped, host.Status.State);
	}

	[Fact]
	public async Task Shutdown_ThatIsNeverAnswered_KillsTheBackendTenSecondsAfterSendingIt()
	{
		// docs/BRIDGE.md §3: the UI waits up to 10 s for the exit, then kills the job; not 10 s plus a grace period
		var hang = new TaskCompletionSource();
		long shutdownSentAt = 0;
		var launcher = new FakeLauncher(() => new FakeBackendProcess((_, request) =>
		{
			if ((string?)request["cmd"] == "shutdown")
			{
				shutdownSentAt = Stopwatch.GetTimestamp();
				hang.Task.Wait();
			}
			return new JsonObject();
		}));
		var host = new MpvBackendHost(launcher, new FakeSurface(), _log);
		try
		{
			await host.StartAsync();
			var backend = launcher.Started[0];

			await host.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(30));

			Assert.True(backend.Killed);
			var killedAfter = Stopwatch.GetElapsedTime(shutdownSentAt, backend.KilledAt);
			Assert.InRange(killedAfter.TotalSeconds, 9.8, 10.4);
			Assert.Equal(MpvBackendState.Stopped, host.Status.State);
		}
		finally
		{
			hang.TrySetResult();
			host.Dispose();
		}
	}

	[Fact]
	public async Task Shutdown_AnsweredJustBeforeTheDeadline_ByABackendThatDoesNotExit_IsKilledAtTheDeadline()
	{
		// docs/BRIDGE.md §3: the kill comes no later than the deadline after `shutdown` was sent, however late the answer
		// came; no half second more for an answer in the last half second (scaled down: a 2 s deadline, answered at 1.9 s)
		long shutdownSentAt = 0;
		var launcher = new FakeLauncher(() => new FakeBackendProcess((_, request) =>
		{
			if ((string?)request["cmd"] == "shutdown")
			{
				shutdownSentAt = Stopwatch.GetTimestamp();
				Thread.Sleep(TimeSpan.FromSeconds(1.9));
			}
			return new JsonObject();
		})
		{ ShutdownExitCode = null });
		using var host = new MpvBackendHost(launcher, new FakeSurface(), _log) { ShutdownTimeout = TimeSpan.FromSeconds(2) };
		await host.StartAsync();
		var backend = launcher.Started[0];

		await host.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(30));

		Assert.True(backend.Killed);
		var killedAfter = Stopwatch.GetElapsedTime(shutdownSentAt, backend.KilledAt);
		Assert.InRange(killedAfter.TotalSeconds, 1.85, 2.2);
		Assert.Equal(MpvBackendState.Stopped, host.Status.State);
	}

	[Fact]
	public async Task AWatchdogKill_DuringAGracefulShutdown_IsIgnored()
	{
		// the backend is sending its final report and answers shutdown after it: a hang check that fires meanwhile must not
		// kill it, nor make FluentFin report a crash Stopped for what the backend reports itself (docs/BRIDGE.md §6)
		var answering = new TaskCompletionSource();
		var release = new TaskCompletionSource();
		var launcher = new FakeLauncher(() => new FakeBackendProcess((_, request) =>
		{
			if ((string?)request["cmd"] == "shutdown")
			{
				answering.TrySetResult();
				release.Task.Wait();
			}
			return new JsonObject();
		}));
		using var host = new MpvBackendHost(launcher, new FakeSurface(), _log);
		var client = new Mock<IJellyfinClient>();
		_ = new MpvPlaybackService(host, client.Object, NullLogger<MpvPlaybackService>.Instance);
		await host.StartAsync();
		var backend = launcher.Started[0];
		await backend.Event("state", new JsonObject { ["state"] = "playing", ["itemId"] = Guid.NewGuid().ToString("N"), ["playSessionId"] = "ps-1" });
		await BridgeClientTests.WaitUntil(() => host.LastState is not null);

		var closing = host.ShutdownAsync();
		await answering.Task.WaitAsync(TimeSpan.FromSeconds(5));
		host.Kill("mpv stopped responding");
		release.TrySetResult();
		await closing.WaitAsync(TimeSpan.FromSeconds(15));

		Assert.False(backend.Killed);
		Assert.Equal(0, await backend.Exited);
		client.Verify(x => x.Stop(It.IsAny<PlaybackStopInfo>()), Times.Never);
		Assert.Equal(MpvBackendState.Stopped, host.Status.State);
	}

	[Fact]
	public async Task Dispose_WhenTheKillThrows_DoesNotThrow()
	{
		var launcher = new FakeLauncher(() => new FakeBackendProcess(Ok) { KillFailure = new AggregateException("tree walk failed") });
		var host = new MpvBackendHost(launcher, new FakeSurface(), _log);
		await host.StartAsync();

		var error = Record.Exception(host.Dispose);

		Assert.Null(error);
		Assert.True(launcher.Started[0].Killed);
	}

	[Fact]
	public async Task Shutdown_WhenTheBackendStopsReadingItsInput_KillsItWithinSeconds()
	{
		var launcher = new FakeLauncher(() => new FakeBackendProcess(readsInput: false));
		var host = new MpvBackendHost(launcher, new FakeSurface(), _log)
		{
			CommandTimeout = TimeSpan.FromSeconds(1),
			ShutdownTimeout = TimeSpan.FromSeconds(1),
			LifecycleTimeout = TimeSpan.FromSeconds(1),
		};
		try
		{
			await host.StartAsync();
			var process = launcher.Started[0];

			// fills the pipe: this write never finishes while the backend does not read
			var stuck = host.SendAsync(Bridge.Commands.Play, HugePlay(), timeout: TimeSpan.FromSeconds(30));

			var watch = Stopwatch.StartNew();
			await host.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(15));

			Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
			Assert.True(process.Killed);
			// the kill broke the pipe, so the stuck write fails now instead of after its 30 s
			await Assert.ThrowsAnyAsync<BridgeException>(() => stuck.WaitAsync(TimeSpan.FromSeconds(5)));
		}
		finally
		{
			host.Dispose();
			foreach (var process in launcher.Started)
			{
				process.Dispose();
			}
		}
	}

	[Fact]
	public async Task AWriteTheBackendNeverTakes_ClosesTheConnection_AndKillsTheBackend()
	{
		var launcher = new FakeLauncher(() => new FakeBackendProcess(readsInput: false));
		var host = new MpvBackendHost(launcher, new FakeSurface(), _log) { CommandTimeout = TimeSpan.FromMilliseconds(500) };
		try
		{
			await host.StartAsync();

			var error = await Assert.ThrowsAsync<BridgeClosedException>(() => host.SendAsync(Bridge.Commands.Play, HugePlay()).WaitAsync(TimeSpan.FromSeconds(10)));

			Assert.Contains("stopped reading its input", error.Message);
			await BridgeClientTests.WaitUntil(() => host.Status.State == MpvBackendState.Crashed);
			Assert.True(launcher.Started[0].Killed);
			Assert.Contains("stopped reading its input", host.Status.LastError);
		}
		finally
		{
			host.Dispose();
			foreach (var process in launcher.Started)
			{
				process.Dispose();
			}
		}
	}

	[Fact]
	public async Task Shutdown_DoesNotWaitForAConnectThatHangs()
	{
		// connect may wait 45 s for the server; closing FluentFin must not
		var release = new TaskCompletionSource();
		var connectArrived = new TaskCompletionSource();
		var launcher = new FakeLauncher(() => new FakeBackendProcess((_, request) =>
		{
			if ((string?)request["cmd"] == "connect")
			{
				connectArrived.TrySetResult();
				release.Task.Wait();
			}
			return new JsonObject();
		}));
		var host = new MpvBackendHost(launcher, new FakeSurface(), _log)
		{
			ConnectTimeout = TimeSpan.FromSeconds(30),
			LifecycleTimeout = TimeSpan.FromMilliseconds(500),
			ShutdownTimeout = TimeSpan.FromSeconds(1),
		};
		try
		{
			await host.StartAsync();
			var connecting = host.ConnectAsync(Alice);
			await connectArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));

			var watch = Stopwatch.StartNew();
			await host.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(15));

			Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"took {watch.Elapsed}");
			Assert.True(launcher.Started[0].Killed);
			await Assert.ThrowsAnyAsync<BridgeException>(() => connecting.WaitAsync(TimeSpan.FromSeconds(5)));
			Assert.DoesNotContain(_log.Messages, x => x.Contains("token-alice-123"));
		}
		finally
		{
			release.TrySetResult();
			host.Dispose();
		}
	}

	[Fact]
	public async Task Shutdown_DoesNotWaitForAStartThatHangs()
	{
		// a backend that never says hello holds the start for 30 s
		var launcher = new FakeLauncher(() => new FakeBackendProcess(sayHello: false));
		var host = new MpvBackendHost(launcher, new FakeSurface(), _log)
		{
			HelloTimeout = TimeSpan.FromSeconds(30),
			LifecycleTimeout = TimeSpan.FromMilliseconds(500),
		};
		try
		{
			var starting = host.StartAsync();
			await BridgeClientTests.WaitUntil(() => launcher.Started.Count == 1);

			var watch = Stopwatch.StartNew();
			await host.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(15));

			Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"took {watch.Elapsed}");
			Assert.True(launcher.Started[0].Killed);
			await Assert.ThrowsAsync<BridgeException>(() => starting.WaitAsync(TimeSpan.FromSeconds(5)));
		}
		finally
		{
			host.Dispose();
		}
	}

	[Fact]
	public async Task ErrorEvents_AreLoggedAndShownWithoutTokens()
	{
		var launcher = new FakeLauncher(() => new FakeBackendProcess(Ok));
		using var host = new MpvBackendHost(launcher, new FakeSurface(), _log);
		await host.StartAsync();

		await launcher.Started[0].Event("error", new JsonObject
		{
			["code"] = "server_unreachable",
			["message"] = "GET http://jf:8096/Items?api_key=token-alice-123 failed",
			["fatal"] = false,
		});

		await BridgeClientTests.WaitUntil(() => _log.Messages.Any(x => x.Contains("server_unreachable")));
		await BridgeClientTests.WaitUntil(() => host.Status.LastError is not null);
		Assert.DoesNotContain(_log.Messages, x => x.Contains("token-alice-123"));
		Assert.DoesNotContain("token-alice-123", host.Status.LastError);
		Assert.Contains("api_key=***", host.Status.LastError);
	}
}
