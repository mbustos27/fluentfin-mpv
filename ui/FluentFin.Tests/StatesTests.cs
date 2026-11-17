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
using Windows.UI;

namespace FluentFin.Tests;

/// <summary>
/// 1.1.4 (states): the sign-in page says on the page why a sign-in failed and what to do, shows progress while the
/// server answers and gives the keyboard back; the words of a failed request; what Narrator is told when a load state
/// appears; the server folder picker's paths and words; the harness's contrast-theme preview.
/// </summary>
public sealed class StatesTests
{
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
	public void QuickConnect_ShowsTheCodeLarge_AndSaysWhereToEnterIt()
	{
		Assert.Equal("123 456", LoginViewModel.SpacedCode("123456"));
		Assert.Equal("12345", LoginViewModel.SpacedCode("12345"));
		var words = LoginViewModel.QuickConnectInstructions("123456", "Harness Jellyfin");
		// the harness finds the code by "Enter code NNNNNN" (tools/ui-harness/offscreen/drive.ps1)
		Assert.Contains("Enter code 123456", words, StringComparison.Ordinal);
		Assert.Contains("already signed in to Harness Jellyfin", words, StringComparison.Ordinal);
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

		Assert.Equal("Couldn't reach http://127.0.0.1:9: No connection could be made (127.0.0.1:9). Check the address and port, and that the server is running, then try again.", refused);
		Assert.Contains("isn't trusted by Windows", certificate, StringComparison.Ordinal);
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
		Assert.Contains("try again", ServerFolders.Message(ServerFolderStatus.Empty), StringComparison.Ordinal);
		Assert.Equal("Couldn't read this folder", ServerFolders.Title(ServerFolderStatus.Failed));
		Assert.Equal("", ServerFolders.Title(ServerFolderStatus.Loaded));
		Assert.Equal("Current folder: /media", ServerFolders.PathLabel("/media"));
	}

	[Theory]
	[InlineData("HighContrast", "Aquatic")]
	[InlineData("highcontrast:desert", "Desert")]
	[InlineData("HighContrast:Dusk", "Dusk")]
	[InlineData("HighContrast:NightSky", "NightSky")]
	[InlineData("HighContrast:Unknown", null)]
	[InlineData("Dark", null)]
	[InlineData("Light", null)]
	[InlineData(null, null)]
	public void TheContrastPreview_IsAskedForOnlyByName(string? value, string? scheme) =>
		Assert.Equal(scheme, HighContrastPreview.Parse(value)?.Name);

	[Fact]
	public void TheContrastPreview_MapsTheOrdinarySystemColours_ToTheScheme_KeepingAlphaAndOtherColours()
	{
		// this PC's ordinary colours (Window and HighlightText both white, WindowText and ButtonText both black)
		var ordinary = new Dictionary<int, Color>
		{
			[5] = Rgb(0xFFFFFF), [8] = Rgb(0x000000), [13] = Rgb(0x0078D7), [14] = Rgb(0xFFFFFF),
			[15] = Rgb(0xF0F0F0), [18] = Rgb(0x000000), [17] = Rgb(0x6D6D6D), [26] = Rgb(0x0066CC),
		};
		var map = HighContrastPreview.ColorMap(HighContrastPreview.NightSky, i => ordinary[i]);

		Assert.Equal(Rgb(0x000000), HighContrastPreview.Map(Rgb(0xFFFFFF), map));   // Window (first of the two whites)
		Assert.Equal(Rgb(0xFFFFFF), HighContrastPreview.Map(Rgb(0x000000), map));   // WindowText
		Assert.Equal(Rgb(0xFFFF00), HighContrastPreview.Map(Rgb(0x0078D7), map));   // Highlight
		Assert.Equal(Rgb(0xD6B4FD), HighContrastPreview.Map(Rgb(0x6D6D6D), map));   // GrayText
		Assert.Equal(Color.FromArgb(0x80, 0, 0, 0), HighContrastPreview.Map(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF), map));
		Assert.Equal(Rgb(0x123456), HighContrastPreview.Map(Rgb(0x123456), map));
	}

	private static Color Rgb(uint rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
