using System.Reactive.Linq;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// fluentfin-mpv (release 1.0, review): the Settings page shows the player in use (MPV for a saved VLC or Flyleaf choice the
/// release cannot run) without saving it; only a pick saves. Opening the page saved the fallback over the saved choice.
/// From the reviewer's probe (ReviewProbeSettingsPageTests).
/// </summary>
[Collection(SessionInfoCollection.Name)]
public sealed class SettingsPagePlayerTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "FluentFinTests", Guid.NewGuid().ToString("N"));
	private readonly LocalSettingsService _service;
	private readonly MediaPlayerAvailability _release = MediaPlayerAvailability.InFolder(@"C:\nowhere-ffm", _ => false, (f, _) => throw new DirectoryNotFoundException(f));

	static SettingsPagePlayerTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public SettingsPagePlayerTests() => _service = new LocalSettingsService(new KnownFolders(_root));

	private async Task<SettingsViewModel> OpenSettings()
	{
		var backend = new Mock<IMpvBackend>();
		backend.SetupGet(x => x.StatusChanged).Returns(Observable.Never<MpvBackendStatus>());
		var vm = new SettingsViewModel(new Settings.Settings(_service, _release), backend.Object, Mock.Of<IJellyfinClient>(), null!);
		try
		{
			await vm.OnNavigatedTo(null!);
		}
		catch (Exception)
		{
			// the rest of the page (the server's preferences) has no server here; the player is loaded before it
		}

		return vm;
	}

	[Theory]
	[InlineData(MediaPlayerType.Vlc)]
	[InlineData(MediaPlayerType.Flyleaf)]
	public async Task OpeningSettings_ShowsMpv_AndKeepsTheSavedChoice(MediaPlayerType saved)
	{
		_service.SaveSetting(SettingKeys.MediaPlayerType, saved);

		var vm = await OpenSettings();

		Assert.Equal(MediaPlayerType.Mpv, vm.MediaPlayerType);
		Assert.Equal(MediaPlayerType.Mpv, vm.SelectedMediaPlayer?.Type);
		Assert.Equal(saved, _service.ReadSetting(SettingKeys.MediaPlayerType));
	}

	[Fact]
	public async Task APick_IsSaved()
	{
		_service.SaveSetting(SettingKeys.MediaPlayerType, MediaPlayerType.Vlc);
		var vm = await OpenSettings();

		vm.SelectedMediaPlayer = SettingsViewModel.OfferedPlayers(_release).Single(x => x.Type == MediaPlayerType.WindowsMediaPlayer);

		Assert.Equal(MediaPlayerType.WindowsMediaPlayer, _service.ReadSetting(SettingKeys.MediaPlayerType));
	}

	/// <summary>
	/// Seaglass 1.1.3 (review): "Play the next episode automatically" shows only for a player that follows it, and the page
	/// hears the change when the Player list changes (the card's Visibility is bound one way to FollowsAutoPlay).
	/// </summary>
	[Fact]
	public async Task TheAutoplayCard_FollowsThePlayerPick()
	{
		var vm = await OpenSettings();
		Assert.False(vm.FollowsAutoPlay);
		List<string?> changed = [];
		vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

		vm.SelectedMediaPlayer = SettingsViewModel.OfferedPlayers(_release).Single(x => x.Type == MediaPlayerType.WindowsMediaPlayer);

		Assert.True(vm.FollowsAutoPlay);
		Assert.Contains(nameof(SettingsViewModel.FollowsAutoPlay), changed);

		changed.Clear();
		vm.SelectedMediaPlayer = SettingsViewModel.OfferedPlayers(_release).Single(x => x.Type == MediaPlayerType.Mpv);

		Assert.False(vm.FollowsAutoPlay);
		Assert.Contains(nameof(SettingsViewModel.FollowsAutoPlay), changed);
	}

	/// <summary>Seaglass 1.1.3 (review): the Player list hides for a copy that can run one player only; the release has two.</summary>
	[Fact]
	public void ThePlayerList_IsAChoiceOnlyWithTwoPlayers()
	{
		Assert.False(SettingsViewModel.OffersAChoice(new MediaPlayerAvailability(type => type == MediaPlayerType.Mpv)));
		Assert.True(SettingsViewModel.OffersAChoice(_release));
		Assert.True(SettingsViewModel.OffersAChoice(new MediaPlayerAvailability(_ => true)));
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, true);
		}
	}
}
