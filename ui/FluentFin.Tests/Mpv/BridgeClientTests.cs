using System.Reactive.Linq;
using System.Text.Json.Nodes;
using FluentFin.Core.Mpv;

namespace FluentFin.Tests.Mpv;

public sealed class BridgeClientTests : IDisposable
{
	private readonly PipePair _pipes = new();
	private readonly BridgeClient _client;

	public BridgeClientTests()
	{
		_client = new BridgeClient(_pipes.UiReader, _pipes.UiWriter) { DefaultTimeout = TimeSpan.FromSeconds(5) };
		_client.Start();
	}

	public void Dispose()
	{
		_client.Dispose();
		_pipes.Dispose();
	}

	[Fact]
	public async Task Request_IsOneJsonLineWithAnIdTheCommandAndTheArgs()
	{
		var pending = _client.SendAsync(Bridge.Commands.Seek, BridgeCommands.Seek(6000000000));

		var raw = await _pipes.ReadRawRequest();
		Assert.Equal("""{"id":1,"cmd":"seek","args":{"positionTicks":6000000000}}""", raw);

		await _pipes.SendFromBackend("""{"id":1,"ok":true,"result":{}}""");
		var result = await pending;
		Assert.NotNull(result);
	}

	[Fact]
	public async Task Responses_AreMatchedByIdEvenOutOfOrder()
	{
		var ping = _client.SendAsync(Bridge.Commands.Ping);
		var state = _client.SendAsync(Bridge.Commands.GetState);
		var first = await _pipes.ReadRequest();
		var second = await _pipes.ReadRequest();

		// answer the second request first
		await _pipes.SendFromBackend($$$"""{"id":{{{second["id"]}}},"ok":true,"result":{"state":"idle"}}""");
		await _pipes.SendFromBackend($$$"""{"id":{{{first["id"]}}},"ok":true,"result":{"pong":1}}""");

		Assert.Equal("ping", (string?)first["cmd"]);
		Assert.Equal("get_state", (string?)second["cmd"]);
		Assert.Equal(1, (int?)(await ping)!["pong"]);
		Assert.Equal("idle", (string?)(await state)!["state"]);
	}

	[Fact]
	public async Task ErrorResponse_ThrowsWithTheCode()
	{
		var pending = _client.SendAsync(Bridge.Commands.Pause);
		var request = await _pipes.ReadRequest();
		await _pipes.SendFromBackend($$$"""{"id":{{{request["id"]}}},"ok":false,"error":{"code":"no_playback","message":"Nothing is playing"}}""");

		var error = await Assert.ThrowsAsync<BridgeCommandException>(() => pending);
		Assert.Equal("no_playback", error.Code);
		Assert.Equal("pause", error.Command);
		Assert.Contains("Nothing is playing", error.Message);
	}

	[Fact]
	public async Task NoResponseInTime_TimesOut_AndALateResponseIsIgnored()
	{
		// the write itself must finish within the timeout too: 0.2 s failed a loaded run with "did not read its input"
		var pending = _client.SendAsync(Bridge.Commands.Ping, timeout: TimeSpan.FromSeconds(1));
		var request = await _pipes.ReadRequest();

		await Assert.ThrowsAsync<BridgeTimeoutException>(() => pending);

		// the late answer must not complete anything else or break the reader
		await _pipes.SendFromBackend($$$"""{"id":{{{request["id"]}}},"ok":true,"result":{}}""");
		var next = _client.SendAsync(Bridge.Commands.Ping);
		var nextRequest = await _pipes.ReadRequest();
		Assert.NotEqual((long)request["id"]!, (long)nextRequest["id"]!);
		await _pipes.SendFromBackend($$$"""{"id":{{{nextRequest["id"]}}},"ok":true,"result":{"n":2}}""");
		Assert.Equal(2, (int?)(await next)!["n"]);
	}

	[Fact]
	public async Task Events_ArePublishedInOrder_BetweenResponses()
	{
		var events = new List<BridgeEvent>();
		using var subscription = _client.Events.Subscribe(events.Add);

		var pending = _client.SendAsync(Bridge.Commands.Play, BridgeCommands.Play(new BridgePlayRequest(["a"])));
		var request = await _pipes.ReadRequest();
		await _pipes.SendFromBackend("""{"event":"state","data":{"state":"opening","connected":true}}""");
		await _pipes.SendFromBackend($$$"""{"id":{{{request["id"]}}},"ok":true,"result":{}}""");
		await _pipes.SendFromBackend("""{"event":"position","data":{"positionTicks":10,"durationTicks":100}}""");
		await _pipes.SendFromBackend("""{"event":"some_future_event","data":{"x":1}}""");
		await pending;

		await WaitUntil(() => events.Count == 3);
		Assert.Equal(["state", "position", "some_future_event"], events.Select(x => x.Name));
		Assert.Equal("opening", events[0].DataAs<BridgeState>()!.State);
		Assert.Equal(100, (long?)events[1].Data!["durationTicks"]);
	}

	[Fact]
	public async Task MalformedLines_AreSkipped_AndTheConnectionKeepsWorking()
	{
		var events = new List<BridgeEvent>();
		using var subscription = _client.Events.Subscribe(events.Add);
		var pending = _client.SendAsync(Bridge.Commands.Ping);
		var request = await _pipes.ReadRequest();

		await _pipes.SendFromBackend("this is not json");
		await _pipes.SendFromBackend("{\"id\": 1, \"ok\": tru");
		await _pipes.SendFromBackend("[1,2,3]");
		await _pipes.SendFromBackend("""{"hello":"neither a response nor an event"}""");
		await _pipes.SendFromBackend("""{"id":"one","ok":true}""");
		await _pipes.SendFromBackend("""{"event":42}""");
		await _pipes.SendFromBackend("");
		await _pipes.SendFromBackend($$$"""{"id":{{{request["id"]}}},"ok":true,"result":{"still":"fine"}}""");

		Assert.Equal("fine", (string?)(await pending)!["still"]);
		Assert.Empty(events);
		Assert.False(_client.IsClosed);
	}

	[Fact]
	public async Task BackendExit_FailsPendingRequests_CompletesEvents_AndRefusesNewOnes()
	{
		var completed = false;
		using var subscription = _client.Events.Subscribe(_ => { }, () => completed = true);
		var pending = _client.SendAsync(Bridge.Commands.Play, BridgeCommands.Play(new BridgePlayRequest(["a"])));
		await _pipes.ReadRequest();

		_pipes.CloseBackendOutput();

		await Assert.ThrowsAsync<BridgeClosedException>(() => pending);
		await _client.Closed.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.True(completed);
		Assert.True(_client.IsClosed);
		await Assert.ThrowsAsync<BridgeClosedException>(() => _client.SendAsync(Bridge.Commands.Ping));
	}

	[Fact]
	public async Task DisposeWhileTheBackendExits_IsQuiet()
	{
		// shutdown: the host disposes the client while the reader sees the end of the backend's output
		using var pipes = new PipePair();
		var log = new ListLogger<BridgeClient>();
		var client = new BridgeClient(pipes.UiReader, pipes.UiWriter, log);
		client.Start();

		pipes.CloseBackendOutput();
		client.Dispose();
		await client.Closed.WaitAsync(TimeSpan.FromSeconds(5));
		await Task.Delay(100);

		Assert.Empty(log.Messages);
		await Assert.ThrowsAsync<BridgeClosedException>(() => client.SendAsync(Bridge.Commands.Ping));
	}

	[Fact]
	public async Task Args_CanBeReusedAcrossRequests()
	{
		var args = BridgeCommands.Volume(40);
		var first = _client.SendAsync(Bridge.Commands.SetVolume, args);
		var firstRequest = await _pipes.ReadRequest();
		var second = _client.SendAsync(Bridge.Commands.SetVolume, args);
		var secondRequest = await _pipes.ReadRequest();

		Assert.Equal(40, (int?)firstRequest["args"]!["volume"]);
		Assert.Equal(40, (int?)secondRequest["args"]!["volume"]);
		await _pipes.SendFromBackend($$$"""{"id":{{{firstRequest["id"]}}},"ok":true,"result":{}}""");
		await _pipes.SendFromBackend($$$"""{"id":{{{secondRequest["id"]}}},"ok":true,"result":{}}""");
		await Task.WhenAll(first, second);
	}

	[Fact]
	public async Task ErrorResponse_DetailsAreRedacted()
	{
		var pending = _client.SendAsync(Bridge.Commands.Connect);
		var request = await _pipes.ReadRequest();
		await _pipes.SendFromBackend($$$"""{"id":{{{request["id"]}}},"ok":false,"error":{"code":"auth_failed","message":"GET http://jf/Users/aaaa?api_key=secret-token-1 -> 401, X-Emby-Token: secret-token-2"}}""");

		var error = await Assert.ThrowsAsync<BridgeCommandException>(() => pending);

		Assert.DoesNotContain("secret-token", error.Message);
		Assert.DoesNotContain("secret-token", error.Detail);
		Assert.Contains("api_key=***", error.Detail);
	}

	[Fact]
	public async Task MalformedLines_AreLoggedWithoutTokens()
	{
		using var pipes = new PipePair();
		var log = new ListLogger<BridgeClient>();
		using var client = new BridgeClient(pipes.UiReader, pipes.UiWriter, log);
		client.Start();

		await pipes.SendFromBackend("""not json, but GET /Items?api_key=secret-token-3 failed""");
		await pipes.SendFromBackend("""{"neither":"response nor event","accessToken":"secret-token-4"}""");

		await WaitUntil(() => log.Messages.Count == 2);
		Assert.DoesNotContain(log.Messages, x => x.Contains("secret-token"));
		Assert.All(log.Messages, x => Assert.Contains("***", x));
	}

	[Fact]
	public async Task AWriteTheBackendNeverTakes_TimesOut_ClosesTheConnection_AndLaterSendsFailFast()
	{
		// nobody reads the backend's stdin: the pipe fills and the write blocks
		using var pipes = new PipePair();
		using var client = new BridgeClient(pipes.UiReader, pipes.UiWriter) { DefaultTimeout = TimeSpan.FromMilliseconds(300) };
		client.Start();
		var huge = new JsonObject { ["itemIds"] = new JsonArray([.. Enumerable.Range(0, 20_000).Select(i => (JsonNode)JsonValue.Create($"{i:D32}"))]) };

		var error = await Assert.ThrowsAsync<BridgeClosedException>(() => client.SendAsync(Bridge.Commands.Play, huge).WaitAsync(TimeSpan.FromSeconds(10)));

		Assert.Contains("stopped reading its input", error.Message);
		Assert.True(client.IsClosed);
		Assert.IsType<TimeoutException>(client.CloseReason);
		var watch = System.Diagnostics.Stopwatch.StartNew();
		await Assert.ThrowsAsync<BridgeClosedException>(() => client.SendAsync(Bridge.Commands.Ping, timeout: TimeSpan.FromSeconds(10)).WaitAsync(TimeSpan.FromSeconds(10)));
		Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"took {watch.Elapsed}");
	}

	[Fact]
	public async Task ARequestQueuedBehindAStuckWrite_TimesOut()
	{
		using var pipes = new PipePair();
		using var client = new BridgeClient(pipes.UiReader, pipes.UiWriter);
		client.Start();
		var huge = new JsonObject { ["itemIds"] = new JsonArray([.. Enumerable.Range(0, 20_000).Select(i => (JsonNode)JsonValue.Create($"{i:D32}"))]) };
		var stuck = client.SendAsync(Bridge.Commands.Play, huge, timeout: TimeSpan.FromSeconds(30));

		await Assert.ThrowsAsync<BridgeTimeoutException>(() => client.SendAsync(Bridge.Commands.Ping, timeout: TimeSpan.FromMilliseconds(300)).WaitAsync(TimeSpan.FromSeconds(10)));

		// the backend goes away: the stuck write fails instead of waiting out its 30 s
		pipes.CloseBackendInput();
		await Assert.ThrowsAsync<BridgeClosedException>(() => stuck.WaitAsync(TimeSpan.FromSeconds(10)));
	}

	[Fact]
	public async Task AWriteTornDownByTheRelease_FailsAsAClosedConnection_WhateverItThrows()
	{
		// MpvBackendHost releases a stopped backend (closes the client, then disposes the process and its pipes) while a
		// request may still be half written: the torn-down writer can fail with anything (a disposed StreamWriter threw
		// ArgumentOutOfRangeException), and callers only expect BridgeException
		using var pipes = new PipePair();
		var writer = new BlockingWriter();
		using var client = new BridgeClient(pipes.UiReader, writer);
		client.Start();
		var pending = client.SendAsync(Bridge.Commands.Play, timeout: TimeSpan.FromSeconds(30));
		await writer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

		client.Dispose();
		writer.Fail(new ArgumentOutOfRangeException("count"));

		var error = await Assert.ThrowsAsync<BridgeClosedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
		Assert.IsType<ArgumentOutOfRangeException>(error.InnerException);
	}

	[Fact]
	public async Task AWriteThatFailsOddlyOnAnOpenConnection_IsNotHidden()
	{
		// only a closed connection turns any failure into BridgeClosedException; a bug while open still surfaces
		using var pipes = new PipePair();
		var writer = new BlockingWriter();
		using var client = new BridgeClient(pipes.UiReader, writer);
		client.Start();
		var pending = client.SendAsync(Bridge.Commands.Play, timeout: TimeSpan.FromSeconds(30));
		await writer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

		writer.Fail(new ArgumentOutOfRangeException("count"));

		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
	}

	// The caller of SendAsync may be the UI thread. A write holds the write lock until it is done, so it must not need the
	// caller's SynchronizationContext to finish: with a busy or frozen UI thread the lock stayed taken, the next requests
	// timed out as if the backend had stopped reading its input, and the host killed it. NeverRunsContext stands in for
	// a UI thread that does not get to the posted work.

	private static JsonObject Big() => new() { ["blob"] = new string('x', 100_000) };

	private static T On<T>(SynchronizationContext context, Func<T> start)
	{
		var previous = SynchronizationContext.Current;
		SynchronizationContext.SetSynchronizationContext(context);
		try
		{
			return start();
		}
		finally
		{
			SynchronizationContext.SetSynchronizationContext(previous);
		}
	}

	[Fact]
	public async Task AWrite_FinishesWithoutTheCallersContext()
	{
		_ = On(new NeverRunsContext(), () => _client.SendAsync(Bridge.Commands.Play, Big(), TimeSpan.FromSeconds(30)));

		// bigger than the pipe, so the write goes asynchronous; its end (with the newline) arrives only if it finishes
		var request = await _pipes.ReadRequest(TimeSpan.FromSeconds(10));
		Assert.Equal(100_000, ((string?)request["args"]!["blob"])!.Length);

		// and the write lock is free again for the next request
		_ = _client.SendAsync(Bridge.Commands.Ping, timeout: TimeSpan.FromSeconds(30));
		Assert.Equal("ping", (string?)(await _pipes.ReadRequest(TimeSpan.FromSeconds(10)))["cmd"]);
	}

	[Fact]
	public async Task ARequestThatWaitedForTheWriteLock_GoesOnWithoutTheCallersContext()
	{
		var first = _client.SendAsync(Bridge.Commands.Play, Big(), TimeSpan.FromSeconds(30));
		_ = On(new NeverRunsContext(), () => _client.SendAsync(Bridge.Commands.Ping, timeout: TimeSpan.FromSeconds(30)));

		Assert.Equal("play", (string?)(await _pipes.ReadRequest(TimeSpan.FromSeconds(10)))["cmd"]);
		Assert.Equal("ping", (string?)(await _pipes.ReadRequest(TimeSpan.FromSeconds(10)))["cmd"]);
	}

	[Fact]
	public async Task CloseOutput_ThatWaitedForTheWriteLock_GoesOnWithoutTheCallersContext()
	{
		var first = _client.SendAsync(Bridge.Commands.Play, Big(), TimeSpan.FromSeconds(30));
		_ = On(new NeverRunsContext(), () => _client.CloseOutputAsync(TimeSpan.FromSeconds(30)));

		await _pipes.ReadRequest(TimeSpan.FromSeconds(10));
		Assert.Null(await _pipes.BackendReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
	}

	[Fact]
	public async Task ASmallWrite_LetsGoOfTheWriteLockWithoutTheCallersContext()
	{
		// a line that fits the writer's buffer: only its flush goes asynchronous
		_ = On(new NeverRunsContext(), () => _client.SendAsync(Bridge.Commands.Ping, timeout: TimeSpan.FromSeconds(30)));
		Assert.Equal("ping", (string?)(await _pipes.ReadRequest(TimeSpan.FromSeconds(10)))["cmd"]);

		_ = _client.SendAsync(Bridge.Commands.Stop, timeout: TimeSpan.FromSeconds(30));
		Assert.Equal("stop", (string?)(await _pipes.ReadRequest(TimeSpan.FromSeconds(10)))["cmd"]);
	}

	[Fact]
	public async Task CloseOutput_LetsGoOfTheWriteLockWithoutTheCallersContext()
	{
		// a close that takes a moment, so the wait for it really goes asynchronous
		using var pipes = new PipePair();
		using var client = new BridgeClient(pipes.UiReader, new SlowClosingWriter());
		client.Start();

		_ = On(new NeverRunsContext(), () => client.CloseOutputAsync(TimeSpan.FromSeconds(30)));
		await Task.Delay(500);

		// the input is closed, so a request fails at once; behind a lock never let go it would wait out its timeout
		await Assert.ThrowsAsync<BridgeClosedException>(() => client.SendAsync(Bridge.Commands.Ping, timeout: TimeSpan.FromSeconds(2)).WaitAsync(TimeSpan.FromSeconds(10)));
	}

	private sealed class SlowClosingWriter : StringWriter
	{
		protected override void Dispose(bool disposing)
		{
			Thread.Sleep(300);
			base.Dispose(disposing);
		}
	}

	private sealed class NeverRunsContext : SynchronizationContext
	{
		public override void Post(SendOrPostCallback d, object? state)
		{
		}
	}

	/// <summary>A backend stdin whose first write hangs until the test makes it fail.</summary>
	private sealed class BlockingWriter : TextWriter
	{
		private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private Exception? _failure;

		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

		public void Fail(Exception failure)
		{
			_failure = failure;
			_release.TrySetResult();
		}

		public override async Task WriteAsync(string? value)
		{
			Started.TrySetResult();
			await _release.Task;
			throw _failure!;
		}
	}

	[Fact]
	public async Task TheTestPipes_LinesSentFromManyThreadsAtOnce_AllArriveWhole()
	{
		// the fake backend's hello (unawaited), its responses and a test's events can be sent at the same moment
		var events = new System.Collections.Concurrent.ConcurrentQueue<BridgeEvent>();
		using var subscription = _client.Events.Subscribe(events.Enqueue);

		await Task.WhenAll(Enumerable.Range(0, 400).Select(i => Task.Run(() => _pipes.SendFromBackend("{\"event\":\"n\",\"data\":{\"i\":" + i + "}}"))));

		await WaitUntil(() => events.Count == 400);
		Assert.Equal(Enumerable.Range(0, 400), events.Select(e => (int)e.Data!["i"]!).Order());
	}

	internal static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
	{
		var deadline = Environment.TickCount64 + timeoutMs;
		while (!condition())
		{
			if (Environment.TickCount64 > deadline)
			{
				throw new TimeoutException("condition not met in time");
			}

			await Task.Delay(10);
		}
	}
}
