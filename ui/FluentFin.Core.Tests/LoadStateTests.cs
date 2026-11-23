using FluentFin.Core.ViewModels;

namespace FluentFin.Core.Tests;

/// <summary>fluentfin-mpv: the loading, empty and error state every page uses (docs/research/ux-review.md §3.11).</summary>
[Collection(SessionInfoCollection.Name)]
public sealed class LoadStateTests
{
	[Fact]
	public async Task ALoad_ShowsLoading_ThenItsOutcome()
	{
		var state = new LoadState("Home");
		var answer = new TaskCompletionSource<LoadOutcome>();

		var running = state.RunAsync(() => answer.Task);

		Assert.True(state.IsLoading);
		Assert.False(state.HasMessage);
		Assert.False(state.RetryCommand.CanExecute(null));
		answer.SetResult(LoadOutcome.Empty("Nothing here yet", "Add media"));
		await running;

		Assert.True(state.IsEmpty);
		Assert.True(state.HasMessage);
		Assert.Equal("Nothing here yet", state.Title);
		Assert.Equal("Add media", state.Message);
		Assert.Equal("Refresh", state.RetryText);
		Assert.True(state.RetryCommand.CanExecute(null));
	}

	[Fact]
	public async Task ALoadThatThrows_Fails_WithTheDefaultWords_NeverStaysLoading()
	{
		SessionInfo.ServerName = "Home server";
		try
		{
			var state = new LoadState("this movie");

			await state.RunAsync(() => throw new HttpRequestException("connection refused"));

			Assert.True(state.IsFailed);
			Assert.Equal("Couldn't load this movie", state.Title);
			Assert.Contains("Home server", state.Message, StringComparison.Ordinal);
			Assert.DoesNotContain("connection refused", state.Message, StringComparison.Ordinal);
			Assert.Equal("Try again", state.RetryText);
		}
		finally
		{
			SessionInfo.ServerName = null!;
		}
	}

	// ---- 1.7.3 (rule 11): whose words a throw gets. 1.7.1's Collections page said "Check that the server is running" for a
	// title listed twice in its own saved index; the server had answered every request.

	public static TheoryData<string, Exception> ServerFailures => new()
	{
		{ "refused", new HttpRequestException("No connection could be made because the target machine actively refused it.") },
		{ "429", new Microsoft.Kiota.Abstractions.ApiException("The server returned an unexpected status code") { ResponseStatusCode = 429 } }, // fix round: 404 has its own words
		{ "client timeout", new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.", new TimeoutException("The operation was canceled.")) },
		{ "timeout", new TimeoutException() },
		{ "flurl timeout", new Flurl.Http.FlurlHttpTimeoutException(null!, null) },
		{ "socket, wrapped", new InvalidOperationException("reading the page", new System.Net.Sockets.SocketException(10054)) },
	};

	[Theory]
	[MemberData(nameof(ServerFailures))]
	public async Task AnHttpErrorOrATimeout_SaysTheServersWords(string what, Exception thrown)
	{
		SessionInfo.ServerName = "Home server";
		try
		{
			var state = new LoadState("this movie");

			await state.RunAsync(() => throw thrown);

			Assert.True(state.IsFailed, what);
			Assert.Equal("Couldn't load this movie", state.Title);
			Assert.Equal("Nixie couldn't get it from Home server just now. Check this PC's connection, then select Try again.", state.Message);
			Assert.Equal("Try again", state.RetryText);
		}
		finally
		{
			SessionInfo.ServerName = null!;
		}
	}

	public static TheoryData<string, Exception> ServerFaults => new()
	{
		{ "Jellyfin's API 500", new Microsoft.Kiota.Abstractions.ApiException("The server returned an unexpected status code") { ResponseStatusCode = 500 } },
		{ "HttpClient 503", new HttpRequestException("Service Unavailable", null, System.Net.HttpStatusCode.ServiceUnavailable) },
		{ "wrapped 502", new AggregateException(new Microsoft.Kiota.Abstractions.ApiException("x") { ResponseStatusCode = 502 }) },
	};

	[Theory]
	[MemberData(nameof(ServerFaults))]
	public async Task AServerError_NamesItsOwner_AndNoStepOnThisPC(string what, Exception thrown)
	{
		SessionInfo.ServerName = "Home server";
		try
		{
			var state = new LoadState("your collections");

			await state.RunAsync(() => throw thrown);

			Assert.True(state.IsFailed, what);
			Assert.Equal("Couldn't load your collections", state.Title);
			Assert.Equal("Home server had a problem answering just now. Select Try again; if it keeps happening, its owner may need to know.", state.Message);
			Assert.DoesNotContain("connection", state.Message, StringComparison.OrdinalIgnoreCase);
			Assert.Equal("Try again", state.RetryText);
		}
		finally
		{
			SessionInfo.ServerName = null!;
		}

		// no name saved: the sentence still starts with a capital
		Assert.StartsWith("The server had a problem answering", LoadMessages.ServerHadAProblem(), StringComparison.Ordinal);
	}

	// 1.7.3 fix round (BH-173-q2): every 4xx but 401 said "Check this PC's connection"; a 404 (a title removed) or a 403 (the
	// account's rules) isn't the connection, and a 400 is a request Nixie made that the server couldn't take
	public static TheoryData<string, Exception, string> RefusalsOfTheirOwn => new()
	{
		{ "403", new Microsoft.Kiota.Abstractions.ApiException("x") { ResponseStatusCode = 403 }, "Home server doesn't let your account open this. Only the server's owner can change that." },
		// the re-review: a 404 that reaches a page's load is an address the server doesn't have (an older server's endpoint, a
		// proxy's path), not its item (pages ask for that through calls that answer null): the server's words, never "removed"
		{ "404", new Microsoft.Kiota.Abstractions.ApiException("x") { ResponseStatusCode = 404 }, "Home server had a problem answering just now. Select Try again; if it keeps happening, its owner may need to know." },
		{ "410, HttpClient", new HttpRequestException("Gone", null, System.Net.HttpStatusCode.Gone), "Home server had a problem answering just now. Select Try again; if it keeps happening, its owner may need to know." },
		{ "400", new Microsoft.Kiota.Abstractions.ApiException("x") { ResponseStatusCode = 400 }, "Nixie couldn't show this page. Select Try again; if it keeps happening, restart Nixie, or use Report a problem in Settings." },
	};

	[Theory]
	[MemberData(nameof(RefusalsOfTheirOwn))]
	public async Task A403Or404_IsNotTheConnection(string what, Exception thrown, string words)
	{
		SessionInfo.ServerName = "Home server";
		try
		{
			var state = new LoadState("this movie");

			await state.RunAsync(() => throw thrown);

			Assert.True(state.IsFailed, what);
			Assert.Equal(words, state.Message);
			Assert.DoesNotContain("connection", state.Message, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("any more", state.Message, StringComparison.OrdinalIgnoreCase); // the re-review: never "removed"
		}
		finally
		{
			SessionInfo.ServerName = null!;
		}

		// no name saved: the sentence still starts with a capital
		Assert.StartsWith("The server doesn't let your account", LoadMessages.NotAllowed(), StringComparison.Ordinal);
	}

	public static TheoryData<string, Exception> LocalFailures => new()
	{
		{ "a title listed twice (1.7.1)", new ArgumentException("An item with the same key has already been added. Key: 0b1c") },
		{ "a bug", new NullReferenceException() },
		{ "an unreadable file", new System.Text.Json.JsonException("'<' is an invalid start of a value.") },
		{ "a disk", new IOException("There is not enough space on the disk.") },
		{ "wrapped", new InvalidOperationException("a builder threw", new KeyNotFoundException()) },
	};

	[Theory]
	[MemberData(nameof(LocalFailures))]
	public async Task NixiesOwnFailure_SaysSo_WithNoWordAboutTheServer(string what, Exception thrown)
	{
		SessionInfo.ServerName = "Home server";
		try
		{
			var state = new LoadState("your collections");

			await state.RunAsync(() => throw thrown);

			Assert.True(state.IsFailed, what);
			Assert.Equal("Couldn't load your collections", state.Title);
			Assert.Equal("Nixie couldn't show this page. Select Try again; if it keeps happening, restart Nixie, or use Report a problem in Settings.", state.Message);
			Assert.DoesNotContain("server", state.Message, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("Home server", state.Message, StringComparison.Ordinal);
			Assert.DoesNotContain("connection", state.Message, StringComparison.OrdinalIgnoreCase);
			Assert.Equal("Try again", state.RetryText);
			Assert.True(state.RetryCommand.CanExecute(null));
		}
		finally
		{
			SessionInfo.ServerName = null!;
		}
	}

	[Fact]
	public async Task APageThatStopsItsOwnLoad_IsNotAFailure_OnlyTheClientsTimeoutIs()
	{
		var state = new LoadState("Home");
		using var stopped = new CancellationTokenSource();
		await stopped.CancelAsync();

		await state.RunAsync(() => throw new TaskCanceledException("left the page", null, stopped.Token));

		Assert.Equal(LoadStatus.Idle, state.Status);
		Assert.False(state.HasMessage);
		Assert.True(LoadMessages.IsTimeout(new TaskCanceledException("x", new TimeoutException())));
		Assert.False(LoadMessages.IsTimeout(new OperationCanceledException(stopped.Token)));
	}

	[Fact]
	public async Task Retry_RunsTheSameLoadAgain_AndShowsTheNewOutcome()
	{
		var state = new LoadState();
		var calls = 0;

		await state.RunAsync(() => Task.FromResult(++calls == 1 ? LoadOutcome.Failed("Couldn't load", "Try again") : LoadOutcome.Loaded));
		Assert.True(state.IsFailed);

		await state.RetryCommand.ExecuteAsync(null);

		Assert.Equal(2, calls);
		Assert.True(state.IsLoaded);
		Assert.False(state.HasMessage);
		Assert.False(state.HasProblem);
		Assert.Equal("", state.Title);
	}

	[Fact]
	public async Task AnOlderLoadThatEndsLate_DoesNotOverwriteTheNewerOne()
	{
		var state = new LoadState();
		var slow = new TaskCompletionSource<LoadOutcome>();

		var first = state.RunAsync(() => slow.Task);
		await state.RunAsync(() => Task.FromResult(LoadOutcome.Loaded));
		slow.SetResult(LoadOutcome.Failed("Old failure", "x"));
		await first;

		Assert.True(state.IsLoaded);
		Assert.Equal("", state.Title);
	}

	[Fact]
	public async Task Reset_ForgetsTheState_AndALoadStillRunningEndsUnseen()
	{
		var state = new LoadState();
		var slow = new TaskCompletionSource<LoadOutcome>();

		var running = state.RunAsync(() => slow.Task);
		state.Reset();
		slow.SetResult(LoadOutcome.Failed("Late", "x"));
		await running;

		Assert.Equal(LoadStatus.Idle, state.Status);
		Assert.False(state.HasMessage);
	}

	[Fact]
	public async Task APartialLoad_IsShownWithAWarning_NotInsteadOfTheContent()
	{
		var state = new LoadState();

		await state.RunAsync(() => Task.FromResult(LoadOutcome.Partly("Some sections couldn't be loaded", "Try again")));

		Assert.True(state.IsLoaded);
		Assert.True(state.HasProblem);
		Assert.False(state.HasMessage);
	}

	[Fact]
	public void RetryBeforeAnyLoad_CannotRun()
	{
		Assert.False(new LoadState().RetryCommand.CanExecute(null));
	}

	[Theory]
	[InlineData(null, "https://jf.example.com:8920/", "jf.example.com:8920")]
	[InlineData(null, "https://jf.example.com/", "jf.example.com")]
	[InlineData("Home", "https://jf.example.com/", "Home")]
	[InlineData(null, null, "the server")]
	public void TheServer_IsNamedAsTheUserKnowsIt(string? name, string? url, string expected)
	{
		SessionInfo.ServerName = name!;
		SessionInfo.BaseUrl = url!;
		try
		{
			Assert.Equal(expected, LoadMessages.ServerName());
		}
		finally
		{
			SessionInfo.ServerName = null!;
			SessionInfo.BaseUrl = null!;
		}
	}
}
