using System.Collections.ObjectModel;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Settings;
using Jellyfin.Sdk.Generated.Models;
using ReactiveUI;

namespace FluentFin.Core.ViewModels;

/// <summary>A player in the Settings list.</summary>
public sealed record MediaPlayerChoice(MediaPlayerType Type, string Name)
{
	public override string ToString() => Name;
}

/// <summary>fluentfin-mpv: a language in the Playback preferences; an empty code is "Any language".</summary>
public sealed record LanguageChoice(string Code, string Name)
{
	public override string ToString() => Name;
}

/// <summary>fluentfin-mpv: a subtitle mode as jellyfin-web names and explains it.</summary>
public sealed record SubtitleModeChoice(UserConfiguration_SubtitleMode Mode, string Name, string Description)
{
	public override string ToString() => Name;
}

public partial class SettingsViewModel(ISettings settings, IMpvBackend mpvBackend, IJellyfinClient jellyfinClient) : ObservableObject, INavigationAware
{
	private IDisposable? _statusSubscription;

	// fluentfin-mpv: the server's copy of the user's configuration, changed and posted back whole
	private UserConfiguration? _configuration;
	private bool _showingPreferences;
	private readonly SemaphoreSlim _saving = new(1, 1);

	public ObservableCollection<SavedServer> Servers { get; } = settings.Servers;

	public static IReadOnlyList<MediaPlayerChoice> MediaPlayers { get; } =
	[
		new(MediaPlayerType.Mpv, "MPV (jellyfin-mpv-shim)"),
		new(MediaPlayerType.Vlc, "VLC"),
		new(MediaPlayerType.Flyleaf, "Flyleaf"),
		new(MediaPlayerType.WindowsMediaPlayer, "Windows Media Player"),
	];

	public MediaPlayerType MediaPlayerType
	{
		get => field;
		set
		{
			if (SetProperty(ref field, value))
			{
				settings.MediaPlayer = value;
				OnPropertyChanged(nameof(SelectedMediaPlayer));
				OnPropertyChanged(nameof(IsMpvSelected));
			}
		}
	}

	public MediaPlayerChoice? SelectedMediaPlayer
	{
		get => MediaPlayers.FirstOrDefault(x => x.Type == MediaPlayerType);
		set
		{
			if (value is not null)
			{
				MediaPlayerType = value.Type;
			}
		}
	}

	public bool IsMpvSelected => MediaPlayerType == MediaPlayerType.Mpv;

	/// <summary>Empty = found automatically; see <see cref="SettingKeys.MpvBackendCommand"/>.</summary>
	public string MpvBackendCommand
	{
		get => field;
		set
		{
			if (SetProperty(ref field, value ?? ""))
			{
				settings.MpvBackendCommand = field.Trim();
			}
		}
	} = "";

	[ObservableProperty]
	public partial string MpvBackendStatus { get; set; } = "";

	// ---- fluentfin-mpv: Settings > Playback (docs/research/ux-review.md T1)

	/// <summary>The server-side preferences: loading, failed with Retry, or shown.</summary>
	public LoadState PlaybackPreferences { get; } = new("your playback preferences");

	public static IReadOnlyList<SubtitleModeChoice> SubtitleModes { get; } =
	[
		new(UserConfiguration_SubtitleMode.Default, "Default", "Subtitles marked default or forced, in your language when there is a choice."),
		new(UserConfiguration_SubtitleMode.Smart, "Smart", "Subtitles in your language when the audio is in another language."),
		new(UserConfiguration_SubtitleMode.Always, "Always", "Subtitles in your language, whatever the audio."),
		new(UserConfiguration_SubtitleMode.OnlyForced, "Only forced", "Only subtitles marked forced (signs, foreign dialogue)."),
		new(UserConfiguration_SubtitleMode.None, "None", "No subtitles unless you turn them on."),
	];

	/// <summary>"Any language" first, then the server's languages by name.</summary>
	[ObservableProperty]
	public partial IReadOnlyList<LanguageChoice> Languages { get; private set; } = [AnyLanguage];

	[ObservableProperty]
	public partial LanguageChoice? AudioLanguage { get; set; }

	[ObservableProperty]
	public partial LanguageChoice? SubtitleLanguage { get; set; }

	[ObservableProperty]
	public partial SubtitleModeChoice? SubtitleMode { get; set; }

	[ObservableProperty]
	public partial bool AutoPlayNextEpisode { get; set; }

	/// <summary>Why the last change did not reach the server (empty: saved).</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasSaveError))]
	public partial string SaveError { get; private set; } = "";

	public bool HasSaveError => !string.IsNullOrEmpty(SaveError);

	public static LanguageChoice AnyLanguage { get; } = new("", "Any language");

	/// <summary>Kept on this PC: see <see cref="SettingKeys.ShowSkipPrompt"/>.</summary>
	public bool ShowSkipPrompt
	{
		get => settings.ShowSkipPrompt;
		set
		{
			if (settings.ShowSkipPrompt != value)
			{
				settings.ShowSkipPrompt = value;
				OnPropertyChanged();
			}
		}
	}

	/// <summary>Kept on this PC: see <see cref="SettingKeys.MouseWheelVolume"/>.</summary>
	public bool MouseWheelVolume
	{
		get => settings.MouseWheelVolume;
		set
		{
			if (settings.MouseWheelVolume != value)
			{
				settings.MouseWheelVolume = value;
				OnPropertyChanged();
			}
		}
	}

	/// <summary>Kept on this PC: see <see cref="SettingKeys.VolumeStep"/>; a double for the NumberBox, saved whole, 1 to 20.</summary>
	public double VolumeStep
	{
		get => settings.VolumeStep;
		set
		{
			var step = double.IsNaN(value) ? settings.VolumeStep : (int)Math.Clamp(Math.Round(value), 1, 20);
			if (settings.VolumeStep != step)
			{
				settings.VolumeStep = step;
			}

			// also when the box held something out of range: it shows the value that was kept
			OnPropertyChanged();
		}
	}

	public Task OnNavigatedFrom()
	{
		_statusSubscription?.Dispose();
		_statusSubscription = null;
		PlaybackPreferences.Reset();
		return Task.CompletedTask;
	}

	public async Task OnNavigatedTo(object parameter)
	{
		MediaPlayerType = settings.MediaPlayer;
		MpvBackendCommand = settings.MpvBackendCommand;

		_statusSubscription?.Dispose();
		_statusSubscription = mpvBackend.StatusChanged
			.ObserveOn(RxSchedulers.MainThreadScheduler)
			.Subscribe(status => MpvBackendStatus = status.Describe());

		await PlaybackPreferences.RunAsync(LoadPlaybackPreferences);
	}

	/// <summary>
	/// fluentfin-mpv: the user's playback preferences as the server keeps them (the same ones jellyfin-web shows). They
	/// reach every player: the server picks each item's default audio and subtitle tracks from them, and the MPV backend
	/// plays those defaults when no rule of its own applies.
	/// </summary>
	private async Task<LoadOutcome> LoadPlaybackPreferences()
	{
		var userTask = jellyfinClient.GetCurrentUser();
		var culturesTask = jellyfinClient.GetCultures();
		var user = await userTask;
		if (user?.Configuration is not { } configuration)
		{
			return LoadMessages.CouldNotLoad("your playback preferences");
		}

		List<CultureDto> cultures;
		try
		{
			cultures = await culturesTask ?? [];
		}
		catch (Exception)
		{
			// the list of languages is a nicety: the saved ones are still shown
			cultures = [];
		}

		var languages = cultures
			.Where(x => !string.IsNullOrEmpty(x.ThreeLetterISOLanguageName) && !string.IsNullOrEmpty(x.DisplayName))
			.GroupBy(x => x.ThreeLetterISOLanguageName!, StringComparer.OrdinalIgnoreCase)
			.Select(x => new LanguageChoice(x.Key, x.First().DisplayName!))
			.OrderBy(x => x.Name, StringComparer.CurrentCulture)
			.ToList();

		_showingPreferences = false;
		try
		{
			_configuration = configuration;
			Languages = [AnyLanguage, .. languages, .. Missing(languages, configuration.AudioLanguagePreference, configuration.SubtitleLanguagePreference)];
			AudioLanguage = Find(configuration.AudioLanguagePreference);
			SubtitleLanguage = Find(configuration.SubtitleLanguagePreference);
			SubtitleMode = SubtitleModes.FirstOrDefault(x => x.Mode == configuration.SubtitleMode) ?? SubtitleModes[0];
			AutoPlayNextEpisode = configuration.EnableNextEpisodeAutoPlay ?? true;
			SaveError = "";
		}
		finally
		{
			_showingPreferences = true;
		}

		return LoadOutcome.Loaded;
	}

	/// <summary>A saved language the server's list lacks is kept, under its code, rather than lost on the next save.</summary>
	private static IEnumerable<LanguageChoice> Missing(List<LanguageChoice> known, params string?[] codes) => codes
		.Where(code => !string.IsNullOrEmpty(code) && !known.Any(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase)))
		.Distinct(StringComparer.OrdinalIgnoreCase)
		.Select(code => new LanguageChoice(code!, code!));

	private LanguageChoice Find(string? code) =>
		Languages.FirstOrDefault(x => string.Equals(x.Code, code ?? "", StringComparison.OrdinalIgnoreCase)) ?? AnyLanguage;

	// a list that empties its selection (its items replaced) is not a choice: only a picked value is saved
	partial void OnAudioLanguageChanged(LanguageChoice? value) => SaveIfPicked(value);

	partial void OnSubtitleLanguageChanged(LanguageChoice? value) => SaveIfPicked(value);

	partial void OnSubtitleModeChanged(SubtitleModeChoice? value) => SaveIfPicked(value);

	private void SaveIfPicked(object? value)
	{
		if (value is not null)
		{
			_ = SavePlaybackPreferences();
		}
	}

	partial void OnAutoPlayNextEpisodeChanged(bool value) => _ = SavePlaybackPreferences();

	/// <summary>
	/// fluentfin-mpv: a change goes to the server at once, the whole configuration as loaded with this page's four fields
	/// changed (the server replaces it). One save at a time, each with the latest values. A failure is said on the page with
	/// a Retry; the values stay as picked. The session's copy is updated, so FluentFin's own players follow at once.
	/// </summary>
	[RelayCommand]
	private async Task SavePlaybackPreferences()
	{
		if (!_showingPreferences || _configuration is not { } configuration)
		{
			return;
		}

		await _saving.WaitAsync();
		try
		{
			configuration.AudioLanguagePreference = AudioLanguage?.Code ?? "";
			configuration.SubtitleLanguagePreference = SubtitleLanguage?.Code ?? "";
			configuration.SubtitleMode = SubtitleMode?.Mode ?? UserConfiguration_SubtitleMode.Default;
			configuration.EnableNextEpisodeAutoPlay = AutoPlayNextEpisode;

			if (await jellyfinClient.SaveUserConfiguration(configuration))
			{
				SaveError = "";
				if (SessionInfo.CurrentUser is { } user)
				{
					user.Configuration = configuration;
				}
			}
			else
			{
				SaveError = $"Your change didn't reach {LoadMessages.ServerName()}. Check that the server is running and that this PC can reach it, then try again.";
			}
		}
		finally
		{
			_saving.Release();
		}
	}

	[RelayCommand]
	private void DeleteServer(SavedServer server) => Servers.Remove(server);
}
