using FluentFin.Contracts.Services;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Settings;
using FluentFin.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Tests;

/// <summary>
/// 1.1.4 (states, review of wip/114-states): Use Quick Connect. An approved code signs in like a password does (progress,
/// the page waits, a failure said on the page); a code the server forgot, or one past its 10 minutes, closes the dialog
/// and says so; Cancel stops the asking; Use Quick Connect waits while a sign-in is on its way. The dialog is replaced by
/// <see cref="FakeDialog"/>.
/// </summary>
public sealed class QuickConnectTests
{
	private readonly Mock<IJellyfinAuthenticationService> _auth = new();
	private readonly SavedServer _server = new() { Id = "s1", DisplayName = "Harness Jellyfin", LocalUrl = "https://jf.example:8920" };
	private readonly QuickConnectResult _code = new() { Code = "123456", Secret = "s3cret", Authenticated = false };
	private readonly FakeDialog _dialog = new();
	private readonly StepClock _clock = new();

	static QuickConnectTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	private async Task<LoginViewModel> SignInPage()
	{
		_auth.Setup(x => x.GetQuickConnectCode(_server)).ReturnsAsync(_code);
		var vm = new LoginViewModel(_auth.Object, new Mock<INavigationService>().Object, new Mock<ILocalSettingsService>().Object)
		{
			QuickConnectPollInterval = TimeSpan.FromMilliseconds(1),
			Time = _clock,
		};
		vm.ShowQuickConnectCode = _dialog.Show;
		await vm.OnNavigatedTo(_server);
		return vm;
	}

	private void Approve() =>
		_auth.Setup(x => x.CheckQuickConnectStatus(_server, _code)).ReturnsAsync(new QuickConnectResult { Code = "123456", Secret = "s3cret", Authenticated = true });

	[Fact]
	public async Task AnApprovedCode_SignsInWithProgress_AndAFailedSignIn_IsSaidOnThePage()
	{
		Approve();
		var signingIn = new TaskCompletionSource<bool>();
		_auth.Setup(x => x.Authenticate(_server, It.Is<QuickConnectResult>(r => r.Authenticated == true), It.IsAny<bool>())).Returns(signingIn.Task);
		var vm = await SignInPage();
		vm.Username = "alice";
		var failed = 0;
		vm.SignInFailed += (_, _) => failed++;

		var running = vm.QuickConnectCommand.ExecuteAsync(null);
		await WaitFor(() => vm.IsSigningIn);

		// the dialog is closed, the page waits: no second sign-in from the Sign in button or Use Quick Connect
		Assert.True(_dialog.Closed);
		Assert.False(vm.LoginCommand.CanExecute(null));
		Assert.False(vm.CanLogin);
		signingIn.SetResult(false);
		await running.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.False(vm.IsSigningIn);
		Assert.Equal(1, failed);
		Assert.Contains("The code was approved, but signing in to Harness Jellyfin didn't work", vm.ErrorMessage, StringComparison.Ordinal);
		Assert.True(vm.CanLogin);
	}

	[Fact]
	public async Task AnApprovedCode_ThatSignsIn_SaysNothing()
	{
		Approve();
		_auth.Setup(x => x.Authenticate(_server, It.IsAny<QuickConnectResult>(), It.IsAny<bool>())).ReturnsAsync(true);
		var vm = await SignInPage();

		await vm.QuickConnectCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));

		Assert.False(vm.HasError);
		_auth.Verify(x => x.Authenticate(_server, It.IsAny<QuickConnectResult>(), It.IsAny<bool>()), Times.Once);
	}

	/// <summary>174-signin: "Remember me" counts for Quick Connect too (a Quick Connect sign-in was never kept).</summary>
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task AnApprovedCode_SignsIn_RememberedAsRememberMeSays(bool remember)
	{
		Approve();
		_auth.Setup(x => x.Authenticate(_server, It.IsAny<QuickConnectResult>(), It.IsAny<bool>())).ReturnsAsync(true);
		var vm = await SignInPage();
		vm.KeepMeLoggedIn = remember;

		await vm.QuickConnectCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));

		_auth.Verify(x => x.Authenticate(_server, It.IsAny<QuickConnectResult>(), remember), Times.Once);
	}

	[Fact]
	public async Task ACodeTheServerStopsKnowing_ClosesTheDialog_AndSaysItExpired()
	{
		// Jellyfin answers "not found" for a code it forgot; CheckQuickConnectStatus turns every failure into null
		_auth.Setup(x => x.CheckQuickConnectStatus(_server, _code)).ReturnsAsync((QuickConnectResult?)null);
		var vm = await SignInPage();
		var failed = 0;
		vm.SignInFailed += (_, _) => failed++;

		await vm.QuickConnectCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));

		Assert.True(_dialog.Closed);
		Assert.Equal("This code expired. Try again.", vm.ErrorMessage);
		Assert.Equal(1, failed);
		_auth.Verify(x => x.CheckQuickConnectStatus(_server, _code), Times.Exactly(LoginViewModel.QuickConnectFailuresAllowed));
		_auth.Verify(x => x.Authenticate(_server, It.IsAny<QuickConnectResult>(), It.IsAny<bool>()), Times.Never);
	}

	[Fact]
	public async Task ACodeNotApprovedInTenMinutes_ClosesTheDialog_AndSaysItExpired()
	{
		// the server keeps answering "not yet"; three minutes pass between checks
		var checks = 0;
		_auth.Setup(x => x.CheckQuickConnectStatus(_server, _code)).ReturnsAsync(() =>
		{
			checks++;
			_clock.Advance(TimeSpan.FromMinutes(3));
			return new QuickConnectResult { Code = "123456", Secret = "s3cret", Authenticated = false };
		});
		var vm = await SignInPage();

		await vm.QuickConnectCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));

		Assert.True(_dialog.Closed);
		Assert.Equal(LoginViewModel.QuickConnectExpired, vm.ErrorMessage);
		Assert.Equal(4, checks); // 3, 6, 9, 12 minutes: given up at the first check past 10
	}

	[Fact]
	public async Task Cancel_StopsTheAsking_AndSaysNothing()
	{
		var checks = 0;
		_auth.Setup(x => x.CheckQuickConnectStatus(_server, _code)).ReturnsAsync(() =>
		{
			checks++;
			return new QuickConnectResult { Authenticated = false };
		});
		var vm = await SignInPage();

		var running = vm.QuickConnectCommand.ExecuteAsync(null);
		await WaitFor(() => checks >= 2);
		_dialog.Cancel();
		await running.WaitAsync(TimeSpan.FromSeconds(5));
		var atCancel = checks;
		await Task.Delay(100);

		Assert.InRange(checks, atCancel, atCancel + 1); // at most the one already on its way
		Assert.False(vm.HasError);
		_auth.Verify(x => x.Authenticate(_server, It.IsAny<QuickConnectResult>(), It.IsAny<bool>()), Times.Never);
	}

	[Fact]
	public async Task UseQuickConnect_WaitsWhileAPasswordSignInIsOnItsWay()
	{
		var answer = new TaskCompletionSource<string?>();
		_auth.Setup(x => x.Authenticate(_server, "alice", "", false)).Returns(answer.Task);
		var vm = await SignInPage();
		vm.Username = "alice";

		var signingIn = vm.LoginCommand.ExecuteAsync(null);
		Assert.True(vm.IsSigningIn);

		Assert.False(vm.QuickConnectCommand.CanExecute(null));
		await vm.QuickConnectCommand.ExecuteAsync(null); // past CanExecute, as a stray call would be
		_auth.Verify(x => x.GetQuickConnectCode(It.IsAny<SavedServer>()), Times.Never);

		answer.SetResult(null);
		await signingIn;
		Assert.True(vm.QuickConnectCommand.CanExecute(null));
	}

	private static async Task WaitFor(Func<bool> condition)
	{
		var deadline = DateTime.UtcNow.AddSeconds(5);
		while (!condition())
		{
			Assert.True(DateTime.UtcNow < deadline, "timed out");
			await Task.Delay(5);
		}
	}

	/// <summary>The Quick Connect dialog: open until the view model closes it (its token) or the test cancels it.</summary>
	private sealed class FakeDialog
	{
		private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public bool Closed { get; private set; }

		public Task Show(string code, string serverName, CancellationToken close)
		{
			close.Register(() =>
			{
				Closed = true;
				_open.TrySetResult();
			});
			return _open.Task;
		}

		public void Cancel() => _open.TrySetResult();
	}

	/// <summary>A clock that moves only when told.</summary>
	private sealed class StepClock : TimeProvider
	{
		private DateTimeOffset _now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

		public override DateTimeOffset GetUtcNow() => _now;

		public void Advance(TimeSpan by) => _now += by;
	}
}
