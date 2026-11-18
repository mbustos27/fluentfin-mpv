using System.Reactive.Linq;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// fluentfin-mpv: Settings > Playback (docs/research/ux-review.md T1). Languages, subtitle mode and auto-play are the
/// user's configuration on the server (as in jellyfin-web); skip button, wheel volume and the volume step are kept on this
/// PC and used by FluentFin's own players (the bridge has no field for them yet).
/// </summary>
[Collection(SessionInfoCollection.Name)]
public sealed class PlaybackSettingsTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "FluentFinTests", Guid.NewGuid().ToString("N"));
	private readonly Mock<IJellyfinClient> _client = new();
	private readonly Mock<IMpvBackend> _backend = new();
	private readonly Guid _userId = Guid.NewGuid();
	private readonly List<UserConfiguration> _posted = [];
	private readonly List<Guid> _postedFor = [];
	private readonly Settings.Settings _settings;
	private UserConfiguration _onServer = new()
	{
		AudioLanguagePreference = "jpn",
		SubtitleLanguagePreference = "eng",
		SubtitleMode = UserConfiguration_SubtitleMode.Smart,
		EnableNextEpisodeAutoPlay = false,
		HidePlayedInLatest = true,
		OrderedViews = [Guid.NewGuid()],
	};

	static PlaybackSettingsTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public PlaybackSettingsTests()
	{
		_settings = new Settings.Settings(new LocalSettingsService(new KnownFolders(_root)));
		_backend.SetupGet(x => x.StatusChanged).Returns(Observable.Never<MpvBackendStatus>());
		// a fresh copy each time, as from the server: the page must not get by on changing the one it loaded in place
		_client.Setup(x => x.GetCurrentUser()).ReturnsAsync(() => new UserDto { Id = _userId, Configuration = Copy(_onServer) });
		_client.Setup(x => x.GetCultures()).ReturnsAsync(
		[
			Culture("Japanese", "jpn"),
			Culture("English", "eng"),
			Culture("French", "fre"),
		]);
		_client.Setup(x => x.SaveUserConfiguration(It.IsAny<Guid>(), It.IsAny<UserConfiguration>())).ReturnsAsync((Guid user, UserConfiguration c) =>
		{
			_postedFor.Add(user);
			_posted.Add(Copy(c));
			_onServer = Copy(c);
			return true;
		});
		// the copy FluentFin took at sign-in
		SessionInfo.CurrentUser = new UserDto { Id = _userId, Configuration = new UserConfiguration { EnableNextEpisodeAutoPlay = false } };
	}

	public void Dispose()
	{
		SessionInfo.CurrentUser = null!;
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, true);
		}
	}

	[Fact]
	public async Task TheServersPreferences_AreShown_AnyLanguageFirst_AndLoadingPostsNothing()
	{
		var vm = Create();

		await vm.OnNavigatedTo(null!);

		Assert.True(vm.PlaybackPreferences.IsLoaded);
		Assert.Equal(["Any language", "English", "French", "Japanese"], vm.Languages.Select(x => x.Name));
		Assert.Equal("jpn", vm.AudioLanguage?.Code);
		Assert.Equal("eng", vm.SubtitleLanguage?.Code);
		Assert.Equal(UserConfiguration_SubtitleMode.Smart, vm.SubtitleMode?.Mode);
		Assert.False(vm.AutoPlayNextEpisode);
		Assert.Empty(_posted);
	}

	[Fact]
	public async Task AChange_GoesToTheServer_ForTheUserShown_WithTheRestOfTheConfigurationKept()
	{
		var views = _onServer.OrderedViews!.ToList();
		var vm = Create();
		await vm.OnNavigatedTo(null!);

		vm.SubtitleMode = VideoSubtitleMode(UserConfiguration_SubtitleMode.Always);
		await WaitUntil(() => _posted.Count == 1);
		vm.AudioLanguage = vm.Languages.Single(x => x.Code == "");
		vm.AutoPlayNextEpisode = true;
		await WaitUntil(() => _posted.Count == 2);

		Assert.Equal([_userId, _userId], _postedFor);
		var last = _posted[^1];
		Assert.Equal(UserConfiguration_SubtitleMode.Always, last.SubtitleMode);
		Assert.Equal("", last.AudioLanguagePreference);
		Assert.Equal("eng", last.SubtitleLanguagePreference);
		Assert.True(last.EnableNextEpisodeAutoPlay);
		// the server replaces the whole configuration: what this page does not show goes back as it came
		Assert.True(last.HidePlayedInLatest);
		Assert.Equal(views, last.OrderedViews);
		// FluentFin's own players follow at once
		Assert.True(PlaybackPreferences.PlaysNextItem());
	}

	[Fact]
	public async Task AListThatEmptiesItsSelection_IsNotAChoice_AndSavesNothing()
	{
		// a ComboBox whose items are replaced sets its selection to nothing for a moment
		var vm = Create();
		await vm.OnNavigatedTo(null!);

		vm.AudioLanguage = null;
		vm.SubtitleMode = null;
		await Task.Delay(50);

		Assert.Empty(_posted);
	}

	[Fact]
	public async Task ASaveThatFails_IsSaid_AndTryAgainSendsItOnceMore()
	{
		var vm = Create();
		await vm.OnNavigatedTo(null!);
		var accept = false;
		_client.Setup(x => x.SaveUserConfiguration(It.IsAny<Guid>(), It.IsAny<UserConfiguration>())).ReturnsAsync((Guid _, UserConfiguration c) =>
		{
			_posted.Add(Copy(c));
			return accept;
		});

		vm.AutoPlayNextEpisode = true;
		await WaitUntil(() => vm.HasSaveError);
		Assert.Contains("didn't reach", vm.SaveError, StringComparison.Ordinal);
		Assert.True(vm.AutoPlayNextEpisode);
		// not saved, so FluentFin's own players do not act on it yet
		Assert.False(PlaybackPreferences.PlaysNextItem());

		accept = true;
		await vm.SavePlaybackPreferencesCommand.ExecuteAsync(null);

		Assert.False(vm.HasSaveError);
		Assert.Equal(2, _posted.Count);
		Assert.True(_posted[^1].EnableNextEpisodeAutoPlay);
	}

	[Fact]
	public async Task PreferencesThatCannotBeLoaded_AreSaidWithRetry_AndNothingIsPosted()
	{
		var answers = new Queue<UserDto?>([null, new UserDto { Id = _userId, Configuration = Copy(_onServer) }]);
		_client.Setup(x => x.GetCurrentUser()).ReturnsAsync(() => answers.Count > 0 ? answers.Dequeue() : new UserDto { Id = _userId, Configuration = Copy(_onServer) });
		var vm = Create();

		await vm.OnNavigatedTo(null!);
		Assert.True(vm.PlaybackPreferences.IsFailed);
		Assert.Equal("Couldn't load your playback preferences", vm.PlaybackPreferences.Title);
		vm.AutoPlayNextEpisode = true;
		await Task.Delay(50);
		Assert.Empty(_posted);

		await vm.PlaybackPreferences.RetryCommand.ExecuteAsync(null);
		Assert.True(vm.PlaybackPreferences.IsLoaded);
		Assert.False(vm.AutoPlayNextEpisode);
	}

	[Fact]
	public async Task ASavedLanguageTheServerDoesNotList_IsKept_NotTurnedIntoAnyLanguage()
	{
		_onServer.AudioLanguagePreference = "tlh";
		_client.Setup(x => x.GetCultures()).ReturnsAsync([]);
		var vm = Create();

		await vm.OnNavigatedTo(null!);
		vm.AutoPlayNextEpisode = true;
		await WaitUntil(() => _posted.Count == 1);

		Assert.Equal("tlh", vm.AudioLanguage?.Code);
		Assert.Equal("tlh", _posted[0].AudioLanguagePreference);
	}

	[Fact]
	public async Task AChangeMadeElsewhereWhileThePageIsOpen_IsKept_NotUndoneByTheNextSave()
	{
		// the reviewer's R2: jellyfin-web, or another device, changes the configuration while Settings is open here
		var vm = Create();
		await vm.OnNavigatedTo(null!);

		_onServer.HidePlayedInLatest = false;
		_onServer.SubtitleLanguagePreference = "fre";
		vm.AutoPlayNextEpisode = true;
		await WaitUntil(() => _posted.Count == 1);

		Assert.False(_posted[0].HidePlayedInLatest);
		Assert.True(_posted[0].EnableNextEpisodeAutoPlay);
		// the four fields are the page's: what it shows is what is saved
		Assert.Equal("eng", _posted[0].SubtitleLanguagePreference);
	}

	[Fact]
	public async Task ASaveAfterASwitchOfUser_IsDropped_NotPostedForTheNewUser()
	{
		var vm = Create();
		await vm.OnNavigatedTo(null!);
		var other = Guid.NewGuid();
		_client.Setup(x => x.GetCurrentUser()).ReturnsAsync(() => new UserDto { Id = other, Configuration = new UserConfiguration() });

		vm.AutoPlayNextEpisode = true;
		await vm.SavePlaybackPreferencesCommand.ExecuteAsync(null);
		await Task.Delay(100);

		Assert.Empty(_posted);
		Assert.False(vm.HasSaveError);
	}

	[Fact]
	public async Task ChangesInQuickSuccession_AreOneSave_WithTheLastValues()
	{
		// the reviewer's R3: arrowing through a closed list passes every item on the way
		var vm = Create();
		vm.SaveDelay = TimeSpan.FromMilliseconds(500);
		await vm.OnNavigatedTo(null!);

		foreach (var mode in SettingsViewModel.SubtitleModes)
		{
			vm.SubtitleMode = mode;
		}

		await WaitUntil(() => _posted.Count == 1);
		await Task.Delay(700);

		var posted = Assert.Single(_posted);
		Assert.Equal(SettingsViewModel.SubtitleModes[^1].Mode, posted.SubtitleMode);
	}

	[Fact]
	public async Task LeavingThePage_SavesAChangeStillWaiting()
	{
		var vm = Create();
		vm.SaveDelay = TimeSpan.FromSeconds(30);
		await vm.OnNavigatedTo(null!);

		vm.AutoPlayNextEpisode = true;
		await Task.Delay(50);
		Assert.Empty(_posted);
		await vm.OnNavigatedFrom();
		await WaitUntil(() => _posted.Count == 1);

		Assert.True(_posted[0].EnableNextEpisodeAutoPlay);
	}

	/// <summary>The user's choice (2026-11-15): FluentFin's own controls unless mpv's are chosen; a choice is kept on this PC.</summary>
	[Fact]
	public async Task FluentFinsPlayerControls_AreTheDefault_AndAChoiceIsKept()
	{
		Assert.Equal(MpvPlayerControls.FluentFin, SettingKeys.MpvPlayerControls.Default.Value);
		var vm = Create();
		await vm.OnNavigatedTo(null!);
		Assert.Equal(MpvPlayerControls.FluentFin, _settings.MpvPlayerControls);
		Assert.Equal(MpvPlayerControls.FluentFin, vm.PlayerControls);
		Assert.Equal("Nixie", vm.SelectedPlayerControls?.Name);

		vm.SelectedPlayerControls = SettingsViewModel.PlayerControlsChoices.Single(x => x.Controls == MpvPlayerControls.Mpv);
		Assert.Equal(MpvPlayerControls.Mpv, new Settings.Settings(new LocalSettingsService(new KnownFolders(_root))).MpvPlayerControls);
	}

	[Fact]
	public void TheLocalSettings_AreKeptOnThisPC_AndTheStepStaysBetween1And20()
	{
		var vm = Create();
		Assert.True(vm.ShowSkipPrompt);
		Assert.True(vm.MouseWheelVolume);
		Assert.Equal(5, vm.VolumeStep);

		vm.ShowSkipPrompt = false;
		vm.MouseWheelVolume = false;
		vm.VolumeStep = 12.4;
		var reread = new Settings.Settings(new LocalSettingsService(new KnownFolders(_root)));
		Assert.False(reread.ShowSkipPrompt);
		Assert.False(reread.MouseWheelVolume);
		Assert.Equal(12, reread.VolumeStep);

		vm.VolumeStep = 400;
		Assert.Equal(20, _settings.VolumeStep);
		vm.VolumeStep = double.NaN;
		Assert.Equal(20, vm.VolumeStep);
		vm.VolumeStep = 0;
		Assert.Equal(1, _settings.VolumeStep);
	}

	[Theory]
	[InlineData(50, 120, 5, 100, 55)]
	[InlineData(50, -120, 5, 100, 45)]
	[InlineData(98, 1, 5, 100, 100)]
	[InlineData(2, -1, 5, 100, 0)]
	[InlineData(50, 0, 5, 100, 50)]
	[InlineData(50, 1, 0, 100, 51)]
	// Windows' player counts 0 to 1: a notch is 5 % of that, not 5 (which muted or went out of range)
	[InlineData(0.5, 120, 5, 1, 0.55)]
	[InlineData(0.5, -120, 5, 1, 0.45)]
	[InlineData(0.98, 1, 5, 1, 1)]
	[InlineData(0.02, -1, 5, 1, 0)]
	public void AWheelNotchOrArrowKey_MovesTheVolumeByTheStep_WithinThePlayersScale(double volume, double direction, int step, double full, double expected)
	{
		Assert.Equal(expected, PlaybackPreferences.StepVolume(volume, direction, step, full), precision: 9);
	}

	[Fact]
	public void TheNextItemPlaysByItself_UnlessTheUserTurnedItOff()
	{
		SessionInfo.CurrentUser = null!;
		Assert.True(PlaybackPreferences.PlaysNextItem());
		SessionInfo.CurrentUser = new UserDto { Configuration = new UserConfiguration { EnableNextEpisodeAutoPlay = false } };
		Assert.False(PlaybackPreferences.PlaysNextItem());
	}

	/// <summary>The SDK's culture has a read-only code (the server fills it in): set as the app sets MediaStream.DisplayTitle.</summary>
	private static CultureDto Culture(string name, string code)
	{
		var culture = new CultureDto { DisplayName = name };
		typeof(CultureDto).GetProperty(nameof(CultureDto.ThreeLetterISOLanguageName))!.SetValue(culture, code);
		return culture;
	}

	private SettingsViewModel Create() => new(_settings, _backend.Object, _client.Object, TestHomeCache.Unused()) { SaveDelay = TimeSpan.FromMilliseconds(20) };

	private static SubtitleModeChoice VideoSubtitleMode(UserConfiguration_SubtitleMode mode) => SettingsViewModel.SubtitleModes.Single(x => x.Mode == mode);

	private static UserConfiguration Copy(UserConfiguration c) => new()
	{
		AudioLanguagePreference = c.AudioLanguagePreference,
		SubtitleLanguagePreference = c.SubtitleLanguagePreference,
		SubtitleMode = c.SubtitleMode,
		EnableNextEpisodeAutoPlay = c.EnableNextEpisodeAutoPlay,
		HidePlayedInLatest = c.HidePlayedInLatest,
		OrderedViews = c.OrderedViews is null ? null : [.. c.OrderedViews],
	};

	private static async Task WaitUntil(Func<bool> condition)
	{
		var watch = System.Diagnostics.Stopwatch.StartNew();
		while (!condition())
		{
			Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "timed out");
			await Task.Delay(10);
		}
	}
}
