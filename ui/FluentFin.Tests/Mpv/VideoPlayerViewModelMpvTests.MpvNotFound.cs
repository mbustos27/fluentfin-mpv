using FluentFin.Core.Mpv;

namespace FluentFin.Tests.Mpv;

/// <summary>
/// fluentfin-mpv (release 1.0): with mpv's controls and no mpv.exe, the backend's <c>connect</c> answers <c>mpv_not_found</c>
/// (docs/BRIDGE.md section 2) and the page says plainly what to do, instead of "Connecting failed: ... FileNotFoundError".
/// </summary>
public sealed partial class VideoPlayerViewModelMpvTests
{
	[Fact]
	public async Task NoMpvForMpvsControls_SaysToSwitchControlsOrInstallMpv()
	{
		var movie = new FakeLibrary(_client).AddMovie();
		_backend.ConnectFailure = new BridgeCommandException("connect", "mpv_not_found", "mpv.exe was not found (not set in mpv_ext_path, not on PATH, not installed with WinGet)");
		var vm = CreateViewModel();

		await vm.OnNavigatedTo(movie);

		Assert.Equal(MpvNotFoundMessage.Text, vm.MpvMessage);
		Assert.True(vm.MpvMessageIsError);
		Assert.Contains("FluentFin's controls", vm.MpvMessage);
		Assert.Contains("winget install mpv-player.mpv-CI.MSVC", vm.MpvMessage);
		Assert.DoesNotContain("play", _backend.Commands);
	}

	[Theory]
	[InlineData("connect", "mpv_failed", "Connecting failed: The backend refused 'connect': mpv_failed (mpv could not be started: boom)")]
	public async Task OtherMpvFailures_KeepTheirOwnText(string command, string code, string expected)
	{
		var movie = new FakeLibrary(_client).AddMovie();
		_backend.ConnectFailure = new BridgeCommandException(command, code, "mpv could not be started: boom");
		var vm = CreateViewModel();

		await vm.OnNavigatedTo(movie);

		Assert.Equal(expected, vm.MpvMessage);
	}

	[Fact]
	public void TheCodeOnlyCountsForConnect()
	{
		Assert.Equal(MpvNotFoundMessage.Text, MpvNotFoundMessage.For(new BridgeCommandException("connect", "mpv_not_found", null)));
		Assert.Null(MpvNotFoundMessage.For(new BridgeCommandException("play", "mpv_not_found", null)));
		Assert.Null(MpvNotFoundMessage.For(new BridgeCommandException("connect", "mpv_failed", null)));
		Assert.Null(MpvNotFoundMessage.For(new InvalidOperationException("mpv_not_found")));
	}
}
