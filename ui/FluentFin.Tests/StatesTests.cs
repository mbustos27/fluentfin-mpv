using System.Net.Http;
using System.Security.Authentication;
using FluentFin.Contracts.Services;
using FluentFin.Controls;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using FluentFin.Services;
using FluentFin.ViewModels;
using Microsoft.Kiota.Abstractions;
using Moq;

namespace FluentFin.Tests;

/// <summary>
/// 1.1.4 (states): the sign-in page says on the page why a sign-in failed and what to do, shows progress while the
/// server answers and gives the keyboard back; the words of a failed request; what Narrator is told when a load state
/// appears; the server folder picker's paths and words.
/// </summary>
public sealed class StatesTests
{
	[Theory]
	[InlineData(true, LoadStatus.Empty, true)]     // an empty page: the full dusk scene behind its message
	[InlineData(true, LoadStatus.Failed, true)]    // a failure: the same
	[InlineData(true, LoadStatus.Loading, false)]  // loading: the page's own backdrop (the hill strip)
	[InlineData(true, LoadStatus.Loaded, false)]
	[InlineData(true, null, false)]
	[InlineData(false, LoadStatus.Failed, false)]  // unloaded: given back
	public void ALoadStateMessage_HoldsTheFullDuskScene_WhileItShows(bool loaded, LoadStatus? status, bool full) =>
		// the 1.2.0 merge: the glass stream's hook (AmbientBackdrop.RequestFullScene) in the states stream's view
		Assert.Equal(full, LoadStateView.WantsFullScene(loaded, status));

	private readonly Mock<IJellyfinAuthenticationService> _auth = new();
	private readonly SavedServer _server = new() { Id = "s1", DisplayName = "Harness Jellyfin", LocalUrl = "https://jf.example:8920", PublicUrl = "https://jf.example:8920" };

	static StatesTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	private async Task<LoginViewModel> SignInPage()
	{
		var vm = new LoginViewModel(_auth.Object, new Mock<INavigationService>().Object, new Mock<ILocalSettingsService>().Object);
		await vm.OnNavigatedTo(_server);
		return vm;
	}

	[Fact]
	public async Task AWrongPassword_IsSaidOnThePage_AndTheKeyboardGoesBack()
	{
		_auth.Setup(x => x.Authenticate(_server, "alice", "nope", false)).ReturnsAsync("Wrong username or password.");
		var vm = await SignInPage();
		var failed = 0;
		vm.SignInFailed += (_, _) => failed++;
		vm.Username = "alice";
		vm.Password = "nope";

		Assert.True(vm.LoginCommand.CanExecute(null));
		await vm.LoginCommand.ExecuteAsync(null);

		Assert.True(vm.HasError);
		Assert.Equal("Wrong username or password.", vm.ErrorMessage);
		Assert.Equal(1, failed);
		Assert.False(vm.IsSigningIn);
		Assert.True(vm.IsIdle);
	}

	[Fact]
	public async Task WhileSigningIn_ThePageShowsProgress_AndSignInWaits_ThenAnEarlierErrorIsCleared()
	{
		var answer = new TaskCompletionSource<string?>();
		_auth.Setup(x => x.Authenticate(_server, "alice", "", false)).Returns(answer.Task);
		var vm = await SignInPage();
		vm.ErrorMessage = "an earlier failure";
		vm.Username = "alice";

		var signingIn = vm.LoginCommand.ExecuteAsync(null);

		Assert.True(vm.IsSigningIn);
		Assert.False(vm.IsIdle);
		Assert.False(vm.CanLogin);
		Assert.False(vm.HasError);
		Assert.Equal("Signing in to Harness Jellyfin…", vm.StatusText);
		answer.SetResult(null);
		await signingIn;
		Assert.False(vm.IsSigningIn);
		Assert.Equal("", vm.StatusText);
		Assert.False(vm.HasError);
		Assert.True(vm.CanLogin);
	}

	[Fact]
	public async Task EnterWithNoUsername_SendsNothing()
	{
		// the Enter key runs the command past CanExecute (LoginPage.InputFieldKeyDown)
		var vm = await SignInPage();
		vm.Password = "secret";

		await vm.LoginCommand.ExecuteAsync(null);

		_auth.Verify(x => x.Authenticate(It.IsAny<SavedServer>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
		Assert.False(vm.IsSigningIn);
	}

	[Fact]
	public async Task SignIn_NeedsAUsername()
	{
		var vm = await SignInPage();

		Assert.False(vm.CanLogin);
		vm.Username = "  ";
		Assert.False(vm.CanLogin);
		vm.Username = "alice";
		Assert.True(vm.CanLogin);
	}

	[Fact]
	public async Task QuickConnectSwitchedOff_IsSaidOnThePage_NotInADialog()
	{
		_auth.Setup(x => x.GetQuickConnectCode(_server)).ReturnsAsync((Jellyfin.Sdk.Generated.Models.QuickConnectResult?)null);
		var vm = await SignInPage();

		await vm.QuickConnectCommand.ExecuteAsync(null);

		Assert.StartsWith("Quick Connect isn't available on Harness Jellyfin", vm.ErrorMessage, StringComparison.Ordinal);
		Assert.Contains("username and password instead", vm.ErrorMessage, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ThePage_SaysWhichServerItSignsInTo()
	{
		var vm = await SignInPage();

		Assert.Equal("Sign in to Harness Jellyfin", vm.Heading);
		Assert.Equal("jf.example:8920", LoginViewModel.ServerNameOf(new SavedServer { LocalUrl = "https://jf.example:8920" }));
		Assert.Equal("", LoginViewModel.ServerNameOf(null));
	}

	[Fact]
	public void QuickConnect_ShowsTheCodeLarge_ReadsItDigitByDigit_AndSaysWhereToEnterIt()
	{
		Assert.Equal("123 456", LoginViewModel.SpacedCode("123456"));
		Assert.Equal("12345", LoginViewModel.SpacedCode("12345"));
		// Narrator, and the harness (drive.ps1 reads the QuickConnectCode text's name)
		Assert.Equal("1 2 3 4 5 6", LoginViewModel.SpokenCode("123 456"));
		var words = LoginViewModel.QuickConnectInstructions("Harness Jellyfin");
		Assert.Contains("already signed in to Harness Jellyfin", words, StringComparison.Ordinal);
		Assert.Contains("Settings > Quick Connect", words, StringComparison.Ordinal);
		Assert.DoesNotContain("login", words, StringComparison.OrdinalIgnoreCase);
	}

	[Theory]
	[InlineData(401, "Wrong username or password")]
	[InlineData(403, "doesn't let this account sign in")]
	[InlineData(500, "answered with an error (500)")]
	[InlineData(503, "Wait a moment and try again")]
	[InlineData(404, "Check that this is your Jellyfin server's address")]
	public void AnErrorStatus_IsSaidInWords_WithWhatToDo(int status, string expected)
	{
		var text = JellyfinAuthenticationService.DescribeFailure(new ApiException("The server returned an unexpected status code") { ResponseStatusCode = status }, "https://jf.example:8920");

		Assert.Contains(expected, text, StringComparison.Ordinal);
		Assert.DoesNotContain("unexpected status code", text, StringComparison.Ordinal);
	}

	[Fact]
	public void AnUnreachableServer_AndAnUntrustedCertificate_SayWhatToDo()
	{
		var refused = JellyfinAuthenticationService.DescribeFailure(new HttpRequestException("No connection could be made (127.0.0.1:9)."), "http://127.0.0.1:9");
		var certificate = JellyfinAuthenticationService.DescribeFailure(new HttpRequestException("ssl", new AuthenticationException()), "https://jf.example");

		// the exception's own words stay in the log (layout audit #11)
		Assert.Equal("Can't reach http://127.0.0.1:9. Check that the server is running, that the address and port are right, and that this PC can reach it (VPN, firewall), then try again.", refused);
		Assert.Contains("isn't trusted by Windows", certificate, StringComparison.Ordinal);
	}

	[Fact]
	public void AnUnexpectedFailure_IsSaidInPlainWords_NotItsExceptionText()
	{
		var text = JellyfinAuthenticationService.DescribeFailure(new InvalidOperationException("Object reference not set to an instance"), "https://jf.example");

		Assert.DoesNotContain("Object reference", text, StringComparison.Ordinal);
		Assert.Contains("Try again", text, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(LoadStatus.Failed, "Couldn't load Home", "Check the server.", false, "Couldn't load Home. Check the server.")]
	[InlineData(LoadStatus.Empty, "This library is empty.", "Add movies.", false, "This library is empty. Add movies.")]
	[InlineData(LoadStatus.Empty, "Nothing here", "", false, "Nothing here")]
	[InlineData(LoadStatus.Loading, "", "", true, "Loading your playback preferences…")]
	[InlineData(LoadStatus.Loading, "", "", false, null)]
	[InlineData(LoadStatus.Loaded, "Some of Home couldn't be loaded", "Check the server.", false, null)]
	[InlineData(LoadStatus.Idle, "", "", true, null)]
	public void ALoadState_IsAnnouncedWithWhatToDo(LoadStatus status, string title, string message, bool showsLoading, string? expected) =>
		Assert.Equal(expected, LoadStateAnnouncement.For(status, title, message, showsLoading, "Loading your playback preferences…"));

	[Theory]
	[InlineData("/media/movies", "/media")]
	[InlineData("/media", "/")]
	[InlineData("/media/movies/", "/media")]
	[InlineData("/", null)]
	[InlineData("", null)]
	public void TheFolderPicker_GoesUpOneFolder(string path, string? parent) => Assert.Equal(parent, ServerFolders.ParentOf(path));

	[Fact]
	public void TheFolderPicker_SaysWhyAFolderShowsNothing()
	{
		Assert.Equal("No folders here", ServerFolders.Title(ServerFolderStatus.Empty));
		Assert.Contains("refresh", ServerFolders.Message(ServerFolderStatus.Empty), StringComparison.Ordinal); // 1.5 (S19)
		Assert.Contains("try again", ServerFolders.Message(ServerFolderStatus.Failed), StringComparison.Ordinal);
		Assert.Equal("Couldn't read this folder", ServerFolders.Title(ServerFolderStatus.Failed));
		Assert.Equal("", ServerFolders.Title(ServerFolderStatus.Loaded));
		Assert.Equal("Current folder: /media", ServerFolders.PathLabel("/media"));
	}

	// 1.5 (the button review's S19, S20): an empty folder's button is Refresh (nothing failed); Select this folder only on a
	// folder the server listed (empty included), never while it is listed or after the listing failed
	[Theory]
	[InlineData(ServerFolderStatus.Loading, "Try again", false)]
	[InlineData(ServerFolderStatus.Loaded, "Try again", true)]
	[InlineData(ServerFolderStatus.Empty, "Refresh", true)]
	[InlineData(ServerFolderStatus.Failed, "Try again", false)]
	public void TheFolderPicker_SaysRefreshForAnEmptyFolder_AndSelectsOnlyAListedOne(ServerFolderStatus status, string retry, bool canSelect)
	{
		Assert.Equal(retry, ServerFolders.RetryLabel(status));
		Assert.Equal(canSelect, ServerFolders.CanSelect("/media", status));
		Assert.False(ServerFolders.CanSelect("", status));
	}

	// 1.5 (the button review's S2): the disabled Sign in says why, on screen
	[Theory]
	[InlineData("", false, Microsoft.UI.Xaml.Visibility.Visible)]
	[InlineData("   ", false, Microsoft.UI.Xaml.Visibility.Visible)]
	[InlineData("alice", false, Microsoft.UI.Xaml.Visibility.Collapsed)]
	[InlineData("", true, Microsoft.UI.Xaml.Visibility.Collapsed)]
	public void TheSignInHint_ShowsWhileThereIsNoUsername(string username, bool signingIn, Microsoft.UI.Xaml.Visibility shown) =>
		Assert.Equal(shown, FluentFin.Views.LoginPage.SignInHintVisibility(username, signingIn));

	// 1.5 (the button review's L1, L2): Home's N and C do nothing when Settings turns them off
	[Fact]
	public void HomesLetterKeys_FollowTheSwitch()
	{
		Assert.True(FluentFin.Services.HomeLetterKeys.Ignore(switchedOff: true, focused: null));
		Assert.False(FluentFin.Services.HomeLetterKeys.Ignore(switchedOff: false, focused: null));
		Assert.False(FluentFin.Services.HomeLetterKeys.Ignore(switchedOff: false, focused: "not a text box"));
	}
}
