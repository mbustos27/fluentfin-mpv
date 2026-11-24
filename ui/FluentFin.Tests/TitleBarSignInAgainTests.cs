using System.Reactive.Linq;
using FluentFin.Contracts.Services;
using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Settings;
using FluentFin.UI.Core.Contracts.Services;
using FluentFin.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FluentFin.Tests;

/// <summary>1.6.1: tests that set the saved sign-in (<see cref="SessionInfo.CurrentUserCrendentials"/>, static) run one at a time.</summary>
[CollectionDefinition(Name)]
public sealed class SavedSignInCollection
{
	public const string Name = "1.6.1: SessionInfo's saved sign-in (static)";
}

/// <summary>
/// 1.6.1 (the 1.5.1 review, TitleBarViewModel.cs:220): every page's and the player's Sign in again, once the server no longer
/// takes this sign-in. With a saved password it signs in again in place, saying so in the title bar meanwhile, and the page
/// stays (no sign-in page); refused, or with no saved password (Quick Connect), it goes to the server's sign-in page as Switch
/// user does.
/// </summary>
[Collection(SavedSignInCollection.Name)]
public sealed class TitleBarSignInAgainTests : IDisposable
{
	private readonly Mock<INavigationService> _setup = new();
	private readonly Mock<IJellyfinAuthenticationService> _authentication = new();
	private readonly SavedServer _server = new() { DisplayName = "Den", PublicUrl = "http://den:8096", LocalUrl = "http://den:8096", Id = "den" };
	private readonly SavedUser _saved = new() { Username = "alice", Password = [1, 2, 3] };

	public void Dispose() => SessionInfo.CurrentUserCrendentials = null!;

	private TitleBarViewModel Create()
	{
		var backend = new Mock<IMpvBackend>();
		backend.Setup(x => x.Events).Returns(Observable.Never<BridgeEvent>());
		backend.Setup(x => x.PlaybackInterrupted).Returns(Observable.Never<MpvInterruptedPlayback>());
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.Logout()).Returns(Task.CompletedTask);
		var playback = new MpvPlaybackService(backend.Object, client.Object, NullLogger<MpvPlaybackService>.Instance);
		return new TitleBarViewModel(Mock.Of<INavigationService>(), _setup.Object, Mock.Of<INavigationViewService>(), client.Object, playback)
		{
			LibmpvUnavailable = () => null,
			User = new UserDto { Name = "alice" },
			CurrentServer = _server,
		};
	}

	private void SignInPageNever() => _setup.Verify(x => x.NavigateTo(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<bool>()), Times.Never);

	// 174-sessions: the sign-in page of this server, opening with this user's name (the server's last remembered one could be another's)
	private void SignInPageOnce() => _setup.Verify(x => x.NavigateTo("FluentFin.ViewModels.LoginViewModel",
		It.Is<SetupNotice>(n => n.Server == _server && n.Username == "alice" && n.Message == ""), It.IsAny<bool>()), Times.Once);

	[Fact]
	public async Task ASavedPassword_SignsInAgainInPlace_SayingSoMeanwhile()
	{
		SessionInfo.CurrentUserCrendentials = _saved;
		var vm = Create();
		var answer = new TaskCompletionSource<bool>();
		_authentication.Setup(x => x.Authenticate(_server, _saved)).Returns(answer.Task);

		var signingIn = vm.SignInAgain(_authentication.Object);

		Assert.True(vm.IsSigningOut);
		Assert.Equal("Signing in to Den again…", vm.SignOutStatus);
		Assert.False(vm.LogoutCommand.CanExecute(null)); // no Sign out meanwhile
		answer.SetResult(true);
		Assert.True(await signingIn.WaitAsync(TimeSpan.FromSeconds(5)));
		Assert.False(vm.IsSigningOut);
		Assert.Equal("", vm.SignOutStatus);
		_authentication.Verify(x => x.Authenticate(_server, _saved), Times.Once);
		SignInPageNever();
		Assert.Equal("alice", vm.User?.Name); // still signed in as the same user
	}

	[Fact]
	public async Task ASavedPasswordTheServerRefuses_GoesToTheSignInPage()
	{
		SessionInfo.CurrentUserCrendentials = _saved;
		var vm = Create();
		_authentication.Setup(x => x.Authenticate(_server, _saved)).ReturnsAsync(false);

		Assert.False(await vm.SignInAgain(_authentication.Object));

		SignInPageOnce();
		Assert.False(vm.IsSigningOut);
	}

	[Fact]
	public async Task ASignInThatThrows_GoesToTheSignInPage()
	{
		SessionInfo.CurrentUserCrendentials = _saved;
		var vm = Create();
		_authentication.Setup(x => x.Authenticate(_server, _saved)).ThrowsAsync(new HttpRequestException("gone"));

		Assert.False(await vm.SignInAgain(_authentication.Object));

		SignInPageOnce();
	}

	[Fact]
	public async Task NoSavedPassword_GoesToTheSignInPage_WithoutTrying()
	{
		SessionInfo.CurrentUserCrendentials = null!; // Quick Connect: nothing saved
		var vm = Create();

		Assert.False(await vm.SignInAgain(_authentication.Object));

		_authentication.Verify(x => x.Authenticate(It.IsAny<SavedServer>(), It.IsAny<SavedUser>()), Times.Never);
		SignInPageOnce();
	}

	[Fact]
	public async Task ASecondPress_WhileSigningIn_DoesNothing()
	{
		SessionInfo.CurrentUserCrendentials = _saved;
		var vm = Create();
		var answer = new TaskCompletionSource<bool>();
		_authentication.Setup(x => x.Authenticate(_server, _saved)).Returns(answer.Task);

		var first = vm.SignInAgain(_authentication.Object);
		Assert.False(await vm.SignInAgain(_authentication.Object));
		answer.SetResult(true);
		Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));

		_authentication.Verify(x => x.Authenticate(_server, _saved), Times.Once);
		SignInPageNever();
	}
}
