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
	private readonly List<UserConfiguration> _posted = [];
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
		_client.Setup(x => x.GetCurrentUser()).ReturnsAsync(() => new UserDto { Id = Guid.NewGuid(), Configuration = _onServer });
		_client.Setup(x => x.GetCultures()).ReturnsAsync(
		[
			Culture("Japanese", "jpn"),
			Culture("English", "eng"),
			Culture("French", "fre"),
		]);
		_client.Setup(x => x.SaveUserConfiguration(It.IsAny<UserConfiguration>())).ReturnsAsync((UserConfiguration c) =>
		{
			_posted.Add(Copy(c));
			return true;
		});
		// the copy FluentFin took at sign-in
		SessionInfo.CurrentUser = new UserDto { Configuration = new UserConfiguration { EnableNextEpisodeAutoPlay = false } };
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
	public async Task AChange_GoesToTheServerAtOnce_WithTheRestOfTheConfigurationKept()
	{
		var vm = Create();
		await vm.OnNavigatedTo(null!);

		vm.SubtitleMode = VideoSubtitleMode(UserConfiguration_SubtitleMode.Always);
		await WaitUntil(() => _posted.Count == 1);
		vm.AudioLanguage = vm.Languages.Single(x => x.Code == "");
		vm.AutoPlayNextEpisode = true;
		await WaitUntil(() => _posted.Count == 3);

		var last = _posted[^1];
		Assert.Equal(UserConfiguration_SubtitleMode.Always, last.SubtitleMode);
		Assert.Equal("", last.AudioLanguagePreference);
		Assert.Equal("eng", last.SubtitleLanguagePreference);
		Assert.True(last.EnableNextEpisodeAutoPlay);
		// the server replaces the whole configuration: what this page does not show goes back as it came
		Assert.True(last.HidePlayedInLatest);
		Assert.Equal(_onServer.OrderedViews, last.OrderedViews);
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
		_client.Setup(x => x.SaveUserConfiguration(It.IsAny<UserConfiguration>())).ReturnsAsync((UserConfiguration c) =>
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
		var answers = new Queue<UserDto?>([null, new UserDto { Configuration = _onServer }]);
		_client.Setup(x => x.GetCurrentUser()).ReturnsAsync(() => answers.Dequeue());
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
	[InlineData(50, 120, 5, 55)]
	[InlineData(50, -120, 5, 45)]
	[InlineData(98, 1, 5, 100)]
	[InlineData(2, -1, 5, 0)]
	[InlineData(50, 0, 5, 50)]
	[InlineData(50, 1, 0, 51)]
	public void AWheelNotchOrArrowKey_MovesTheVolumeByTheStep_Within0And100(double volume, double direction, int step, double expected)
	{
		Assert.Equal(expected, PlaybackPreferences.StepVolume(volume, direction, step));
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

	private SettingsViewModel Create() => new(_settings, _backend.Object, _client.Object);

	private static SubtitleModeChoice VideoSubtitleMode(UserConfiguration_SubtitleMode mode) => SettingsViewModel.SubtitleModes.Single(x => x.Mode == mode);

	private static UserConfiguration Copy(UserConfiguration c) => new()
	{
		AudioLanguagePreference = c.AudioLanguagePreference,
		SubtitleLanguagePreference = c.SubtitleLanguagePreference,
		SubtitleMode = c.SubtitleMode,
		EnableNextEpisodeAutoPlay = c.EnableNextEpisodeAutoPlay,
		HidePlayedInLatest = c.HidePlayedInLatest,
		OrderedViews = c.OrderedViews,
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
