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
