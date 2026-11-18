using System.Reactive.Linq;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Seaglass 1.1.4 (interaction audit, item 17): every setting takes effect at once, with no restart, sign-in or Save. The
/// player used to reach remote control at the next sign-in and the backend command at the backend's next start (often the next
/// run of Seaglass); the page said so in grey text.
/// </summary>
public sealed class SettingsTakeEffectTests
{
	private readonly Mock<ISettings> _settings = new();
	private readonly Mock<IMpvBackend> _backend = new();
	private readonly Mock<IJellyfinClient> _client = new();

	static SettingsTakeEffectTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public SettingsTakeEffectTests()
	{
		_settings.SetupProperty(x => x.MpvBackendCommand, "");
		_settings.SetupProperty(x => x.MediaPlayer, MediaPlayerType.Mpv);
		_backend.SetupGet(x => x.StatusChanged).Returns(Observable.Never<MpvBackendStatus>());
		_backend.SetupGet(x => x.Events).Returns(Observable.Never<BridgeEvent>());
		_backend.SetupGet(x => x.PlaybackInterrupted).Returns(Observable.Never<MpvInterruptedPlayback>());
		_backend.Setup(x => x.ShutdownAsync()).Returns(Task.CompletedTask);
		_client.Setup(x => x.UpdateCapabilities()).ReturnsAsync(true);
	}

	private SettingsViewModel Create(MpvPlaybackService? playback = null) =>
		new(_settings.Object, _backend.Object, _client.Object, null!, null, playback);

	private void BackendIs(MpvBackendState state) => _backend.SetupGet(x => x.Status).Returns(new MpvBackendStatus(state));

	// ---- the player

	[Fact]
	public async Task PickingAPlayer_TellsTheServerAtOnce()
	{
		var vm = Create();

		vm.MediaPlayerType = MediaPlayerType.WindowsMediaPlayer;
		await vm.PlayerChanged;

		_client.Verify(x => x.UpdateCapabilities(), Times.Once);
		Assert.Equal(MediaPlayerType.WindowsMediaPlayer, _settings.Object.MediaPlayer);
	}

	[Fact]
	public async Task OpeningSettings_TellsTheServerNothing()
	{
		var vm = Create();

		try
		{
			await vm.OnNavigatedTo(null!);
		}
		catch (Exception)
		{
			// the server's preferences: no server here; the player is loaded before them
		}

		_client.Verify(x => x.UpdateCapabilities(), Times.Never);
		_backend.Verify(x => x.ShutdownAsync(), Times.Never);
	}

	// ---- the backend command

	[Theory]
	[InlineData(MpvBackendState.Running)]
	[InlineData(MpvBackendState.Starting)]
	public async Task ANewBackendCommand_RestartsAnIdleBackend(MpvBackendState state)
	{
		BackendIs(state);
		var vm = Create();

		vm.MpvBackendCommand = @"C:\shim\.venv\Scripts\python.exe -I -m jellyfin_mpv_shim.bridge ";
		await vm.BackendRestarted;

		Assert.Equal(@"C:\shim\.venv\Scripts\python.exe -I -m jellyfin_mpv_shim.bridge", _settings.Object.MpvBackendCommand);
		_backend.Verify(x => x.ShutdownAsync(), Times.Once);

		// the same command again (spaces aside) is no change
		vm.MpvBackendCommand = @"C:\shim\.venv\Scripts\python.exe -I -m jellyfin_mpv_shim.bridge";
		await vm.BackendRestarted;
		_backend.Verify(x => x.ShutdownAsync(), Times.Once);
	}

	[Theory]
	[InlineData(MpvBackendState.NotStarted)]
	[InlineData(MpvBackendState.Stopped)]
	[InlineData(MpvBackendState.Crashed)]
	public async Task ANewBackendCommand_StartsNothing_WhenNoBackendRuns(MpvBackendState state)
	{
		BackendIs(state);
		var vm = Create();

		vm.MpvBackendCommand = "python -m jellyfin_mpv_shim.bridge";
		await vm.BackendRestarted;

		_backend.Verify(x => x.ShutdownAsync(), Times.Never);
		_backend.Verify(x => x.StartAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task ANewBackendCommand_NeverStopsAPlayback()
	{
		BackendIs(MpvBackendState.Running);
		_backend.SetupGet(x => x.LastState).Returns(new BridgeState { State = Bridge.States.Playing });
		var playback = new MpvPlaybackService(_backend.Object, _client.Object, NullLogger<MpvPlaybackService>.Instance);
		Assert.True(playback.HasPlayback);
		var vm = Create(playback);

		vm.MpvBackendCommand = "python -m jellyfin_mpv_shim.bridge";
		await vm.BackendRestarted;

		_backend.Verify(x => x.ShutdownAsync(), Times.Never);
	}

	// ---- what the page says

	[Fact]
	public void NoSetting_SaysItWaitsForASignInOrARestart()
	{
		var page = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Views/SettingsPage.xaml"));

		Assert.DoesNotContain("after you sign in again", page, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("the next time the backend starts", page, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("Used from the next playback", page, StringComparison.OrdinalIgnoreCase);
	}

	private static string RepoRoot { get; } = FindRepoRoot();

	private static string FindRepoRoot()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FluentFin.sln")))
		{
			dir = dir.Parent;
		}

		return dir?.FullName ?? throw new DirectoryNotFoundException("FluentFin.sln not found above " + AppContext.BaseDirectory);
	}
}
