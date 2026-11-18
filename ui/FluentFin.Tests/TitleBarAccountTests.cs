using System.Reactive.Linq;
using FluentFin.Contracts.Services;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using FluentFin.Dialogs.ViewModels;
using FluentFin.UI.Core.Contracts.Services;
using FluentFin.ViewModels;
using FluentFin.Views;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FluentFin.Tests;

/// <summary>
/// Seaglass 1.1.4 (UX review, prioritised fixes 5 and 15): the title bar's Back and Account hide while the shell shows the
/// player page (it has its own Back; a Sign out over a playing video is a hazard), driven by the page's type from the
/// navigation, and come back with any other page, after full screen in and out, and when the shell goes. The Account menu
/// names the user and the server and opens Settings.
/// </summary>
public sealed class TitleBarAccountTests
{
	private readonly Mock<INavigationService> _navigation = new();
	private readonly Mock<ISettings> _settings = new();

	private TitleBarViewModel Create(ISettings? settings = null, string? libmpvUnavailable = null)
	{
		var backend = new Mock<IMpvBackend>();
		backend.Setup(x => x.Events).Returns(Observable.Never<BridgeEvent>());
		backend.Setup(x => x.PlaybackInterrupted).Returns(Observable.Never<MpvInterruptedPlayback>());
		var client = new Mock<IJellyfinClient>();
		var playback = new MpvPlaybackService(backend.Object, client.Object, NullLogger<MpvPlaybackService>.Instance);
		return new TitleBarViewModel(_navigation.Object, Mock.Of<INavigationService>(), Mock.Of<INavigationViewService>(), client.Object, playback, settings)
		{
			LibmpvUnavailable = () => libmpvUnavailable,
			User = new UserDto { Name = "alice" },
		};
	}

	private static (bool Back, bool Account) Shown(TitleBarViewModel vm) => (vm.IsBackButtonShown, vm.IsAccountShown);

	[Fact]
	public void ThePlayerPage_HidesBackAndAccount_AndAnyOtherPageShowsThem()
	{
		var vm = Create();
		vm.OnPageShown(typeof(SeasonPage));
		Assert.Equal((true, true), Shown(vm));

		vm.OnPageShown(typeof(VideoPlayerPage));
		Assert.True(vm.IsPlayerPageShown);
		Assert.Equal((false, false), Shown(vm));

		// Back from the player: the season page again
		vm.OnPageShown(typeof(SeasonPage));
		Assert.Equal((true, true), Shown(vm));
	}

	[Fact]
	public void FullScreenInAndOut_LeavesThemHidden_UntilThePlayerPageGoes()
	{
		var vm = Create();
		vm.OnPageShown(typeof(VideoPlayerPage));

		// the player page hides the whole title bar in full screen and shows it again after (VideoPlayerPage.SetTitleBar)
		vm.IsVisible = false;
		vm.IsVisible = true;
		Assert.Equal((false, false), Shown(vm));

		// left from full screen: the page goes, the title bar comes back with Back and Account
		vm.IsVisible = false;
		vm.OnPageShown(typeof(EpisodePage));
		vm.IsVisible = true;
		Assert.Equal((true, true), Shown(vm));
	}

	[Fact]
	public void APlayerPageReplacingAnother_NeverShowsThemInBetween()
	{
		// a remote Play while playing: the frame shows a new player page in the old one's place
		var vm = Create();
		vm.OnPageShown(typeof(VideoPlayerPage));
		var seen = new List<(string, bool)>();
		vm.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName is nameof(TitleBarViewModel.IsBackButtonShown) or nameof(TitleBarViewModel.IsAccountShown))
			{
				seen.Add((e.PropertyName, e.PropertyName == nameof(TitleBarViewModel.IsBackButtonShown) ? vm.IsBackButtonShown : vm.IsAccountShown));
			}
		};

		vm.OnPageShown(typeof(VideoPlayerPage));

		Assert.DoesNotContain(seen, x => x.Item2);
		Assert.Equal((false, false), Shown(vm));
	}

	[Fact]
	public void TheShellGoing_ShowsThemAgain_ForTheNextSignIn()
	{
		// Log out / Switch user take the shell away with the player page on it: no navigation of its frame says so
		var vm = Create();
		vm.OnPageShown(typeof(VideoPlayerPage));

		vm.OnPageShown(null);

		Assert.False(vm.IsPlayerPageShown);
		Assert.Equal((true, true), Shown(vm));
	}

	[Fact]
	public void SignedOut_NeitherShows()
	{
		var vm = Create();
		vm.OnPageShown(typeof(HomePage));
		vm.User = null;

		Assert.Equal((false, false), Shown(vm));
	}

	[Fact]
	public void TheChangesAreAnnounced_SoTheBindingsFollow()
	{
		var vm = Create();
		vm.OnPageShown(typeof(HomePage));
		var changed = new List<string?>();
		vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

		vm.OnPageShown(typeof(VideoPlayerPage));

		Assert.Contains(nameof(TitleBarViewModel.IsBackButtonShown), changed);
		Assert.Contains(nameof(TitleBarViewModel.IsAccountShown), changed);
	}

	[Theory]
	// Seaglass's controls (and libmpv there): the player page has its own Back, the title bar's goes
	[InlineData(MediaPlayerType.Mpv, MpvPlayerControls.FluentFin, null, false)]
	// mpv's own controls, or Seaglass's chosen but libmpv missing (the page falls back to mpv's): no Back on the page
	[InlineData(MediaPlayerType.Mpv, MpvPlayerControls.Mpv, null, true)]
	[InlineData(MediaPlayerType.Mpv, MpvPlayerControls.FluentFin, "libmpv-2.dll not found", true)]
	// another player: its controls have no Back
	[InlineData(MediaPlayerType.WindowsMediaPlayer, MpvPlayerControls.FluentFin, null, true)]
	public void ThePlayersOwnBack_DecidesTheTitleBarsBack_ButAccountAlwaysGoes(MediaPlayerType player, MpvPlayerControls controls, string? libmpvUnavailable, bool backShown)
	{
		_settings.Setup(x => x.MediaPlayer).Returns(player);
		_settings.Setup(x => x.MpvPlayerControls).Returns(controls);
		var vm = Create(_settings.Object, libmpvUnavailable);

		vm.OnPageShown(typeof(VideoPlayerPage));

		Assert.Equal((backShown, false), Shown(vm));
	}

	[Fact]
	public void TheAccountMenu_NamesTheServer()
	{
		// (not through CurrentServer: setting it also sets the static SessionInfo.ServerName other tests read)
		Assert.Equal("", Create().ServerName);
		Assert.Equal("", TitleBarViewModel.ServerNameOf(null));
		Assert.Equal("Living room", TitleBarViewModel.ServerNameOf(new SavedServer { DisplayName = " Living room " }));
		Assert.Equal("http://192.168.1.5:8096", TitleBarViewModel.ServerNameOf(new SavedServer { DisplayName = " ", PublicUrl = "http://192.168.1.5:8096" }));
	}

	[Fact]
	public void TheAccountMenusSettings_OpensSettings()
	{
		var vm = Create();

		vm.GoToSettingsCommand.Execute(null);

		_navigation.Verify(x => x.NavigateTo(typeof(SettingsViewModel).FullName!, null, false), Times.Once);
	}

	// ---- Seaglass 1.1.4 (interaction audit, item 2): the search's text clears after you leave

	[Fact]
	public void TheSearchText_StaysOnItsResultsPage_AndClearsOnAnyOther()
	{
		var vm = Create();
		vm.OnPageShown(typeof(HomePage));
		vm.SearchText = "quiet";

		// Enter: the results page for it
		vm.OnPageShown(typeof(SearchResultsPage));
		Assert.Equal("quiet", vm.SearchText);

		// a result opened from there, or anywhere else
		vm.OnPageShown(typeof(SeriesPage));
		Assert.Equal("", vm.SearchText);

		vm.SearchText = "the";
		vm.OnPageShown(typeof(LibraryPage));
		Assert.Equal("", vm.SearchText);
	}

	// ---- Seaglass 1.1.4 (layout audit): the title bar

	[Fact]
	public void SigningOut_HidesTheSearch()
	{
		// item 12: the box stayed on the server and sign-in pages after a sign-out
		var vm = Create();
		vm.OnPageShown(typeof(HomePage));
		Assert.True(vm.IsSearchShown);
		var changed = new List<string?>();
		vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

		vm.User = null;

		Assert.False(vm.IsSearchShown);
		Assert.Contains(nameof(TitleBarViewModel.IsSearchShown), changed);
	}

	[Fact]
	public void ThePlayerPage_HidesTheSearchToo()
	{
		var vm = Create();
		vm.OnPageShown(typeof(VideoPlayerPage));

		Assert.False(vm.IsSearchShown);
	}

	[Theory]
	[InlineData("1.1.4", "")]
	[InlineData("1.1.4-beta.1", "Preview")]
	public void TheSubtitle_IsEmptyOnARelease_AndPreviewOnATestBuild(string version, string subtitle)
	{
		// item 20: "Seaglass 1.1.3" on every page; the version is in Settings > About
		Assert.Equal(subtitle, TitleBarViewModel.SubtitleFor(version));
	}

	[Theory]
	[InlineData(Microsoft.UI.Xaml.ElementTheme.Light, false, Microsoft.UI.Windowing.TitleBarTheme.Light)]
	[InlineData(Microsoft.UI.Xaml.ElementTheme.Dark, false, Microsoft.UI.Windowing.TitleBarTheme.Dark)]
	[InlineData(Microsoft.UI.Xaml.ElementTheme.Light, true, Microsoft.UI.Windowing.TitleBarTheme.UseDefaultAppMode)]
	public void TheCaptionButtons_TakeTheAppsTheme(Microsoft.UI.Xaml.ElementTheme theme, bool highContrast, Microsoft.UI.Windowing.TitleBarTheme expected)
	{
		// item 17: pale grey caption buttons on the light title bar (Seaglass light, Windows dark)
		Assert.Equal(expected, TitleBarControl.CaptionThemeFor(theme, highContrast));
	}

	[Fact]
	public void TheCaptionButtons_HaveTheThemesForegroundAndHoverColours()
	{
		var light = TitleBarControl.CaptionColorsFor(Microsoft.UI.Xaml.ElementTheme.Light, false)!;
		var dark = TitleBarControl.CaptionColorsFor(Microsoft.UI.Xaml.ElementTheme.Dark, false)!;

		// dark glyphs on the light title bar, white on the dark one (WinUI's TextFillColorPrimary in each)
		Assert.Equal(Windows.UI.Color.FromArgb(0xE4, 0, 0, 0), light.Foreground);
		Assert.Equal(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), dark.Foreground);
		// a hover fill of the glyph's own colour, faint (SubtleFillColorSecondary)
		Assert.Equal((byte)0, light.HoverBackground.R);
		Assert.Equal((byte)0xFF, dark.HoverBackground.R);
		Assert.InRange(light.HoverBackground.A, (byte)1, (byte)0x20);
		// high contrast: Windows' own colours
		Assert.Null(TitleBarControl.CaptionColorsFor(Microsoft.UI.Xaml.ElementTheme.Light, true));
	}

	// ---- SyncPlay's dialog (layout audit, item 18)

	[Fact]
	public void SyncPlaysDialog_JoinsOnlyAChosenGroup_InSentenceCase()
	{
		var vm = new SyncPlayGroupPickerViewModel(Mock.Of<IJellyfinClient>());
		Assert.False(vm.CanJoin);

		vm.SelectedGroup = new GroupInfoDto { GroupId = Guid.NewGuid(), GroupName = "Friday" };
		Assert.True(vm.CanJoin);

		Assert.Equal("New group", SyncPlayGroupPickerViewModel.SecondaryText(null));
		Assert.Equal("Leave group", SyncPlayGroupPickerViewModel.SecondaryText(new GroupInfoDto()));
	}

	// ---- Settings > Other devices > Quick Connect: the dialog says what Approve did

	[Theory]
	[InlineData("123456", true, QuickConnectViewModel.ApprovedStatus)]
	[InlineData(" 123456 ", false, QuickConnectViewModel.FailedStatus)]
	[InlineData("   ", false, QuickConnectViewModel.NoCodeStatus)]
	public async Task QuickConnect_SaysWhetherTheCodeWasApproved(string code, bool approved, string status)
	{
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.Authenticate("123456")).ReturnsAsync(approved);
		var vm = new QuickConnectViewModel(client.Object) { QuickConnectCode = code };

		await vm.VerifyQuickConnectCommand.ExecuteAsync(null);

		Assert.Equal(status, vm.Status);
		// Cancel until approved, then Close
		Assert.Equal(approved ? "Close" : "Cancel", vm.CloseText);
		Assert.Equal(!approved, vm.CanApprove);
		client.Verify(x => x.Authenticate(It.IsAny<string>()), string.IsNullOrWhiteSpace(code) ? Times.Never() : Times.Once());
	}
}
