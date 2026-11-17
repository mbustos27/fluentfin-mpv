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

/// <summary>fluentfin-mpv: a "Player controls" choice in the Settings list.</summary>
public sealed record MpvPlayerControlsChoice(MpvPlayerControls Controls, string Name)
{
	public override string ToString() => Name;
}

/// <summary>Seaglass: a "Glass" choice in the Settings list.</summary>
public sealed record GlassChoiceItem(GlassChoice Choice, string Name, string Description)
{
	public override string ToString() => Name;
}

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

public partial class SettingsViewModel(ISettings settings, IMpvBackend mpvBackend, IJellyfinClient jellyfinClient, Services.HomeCache homeCache, KnownFolders? knownFolders = null) : ObservableObject, INavigationAware
{
	private IDisposable? _statusSubscription;

	// fluentfin-mpv: whose preferences the page shows (a save for anyone else is dropped), and the save waiting for the
	// changes to stop (Settings > Playback)
	private Guid? _loadedUserId;
	private bool _showingPreferences;
	private readonly SemaphoreSlim _saving = new(1, 1);
	private CancellationTokenSource? _pendingSave;

	public ObservableCollection<SavedServer> Servers { get; } = settings.Servers;

	public static IReadOnlyList<MediaPlayerChoice> AllMediaPlayers { get; } =
	[
		new(MediaPlayerType.Mpv, "MPV (jellyfin-mpv-shim)"),
		new(MediaPlayerType.Vlc, "VLC"),
		new(MediaPlayerType.Flyleaf, "Flyleaf"),
		new(MediaPlayerType.WindowsMediaPlayer, "Windows Media Player"),
	];

	// fluentfin-mpv (release 1.0): only the players this copy can run (the release has no VLC or Flyleaf libraries)
	public static IReadOnlyList<MediaPlayerChoice> MediaPlayers { get; } = OfferedPlayers(MediaPlayerAvailability.ForThisApp);

	public static IReadOnlyList<MediaPlayerChoice> OfferedPlayers(MediaPlayerAvailability players) => [.. AllMediaPlayers.Where(x => players.IsAvailable(x.Type))];

	private bool _loadingPlayer;

	public MediaPlayerType MediaPlayerType
	{
		get => field;
		set
		{
			if (SetProperty(ref field, value))
			{
				if (!_loadingPlayer)
				{
					settings.MediaPlayer = value;
				}
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

	/// <summary>Seaglass: the choices for Settings > Appearance > Glass.</summary>
	public static IReadOnlyList<GlassChoiceItem> GlassChoices { get; } =
	[
		new(GlassChoice.Automatic, "Automatic", "Follows Windows: transparency effects, animation effects and high contrast."),
		new(GlassChoice.Full, "Full", "Frosted glass and motion."),
		new(GlassChoice.Reduced, "Reduced", "Frosted glass, with fades only."),
		new(GlassChoice.Solid, "Solid", "Opaque surfaces, no transparency."),
	];

	/// <summary>Saved on this PC when it changes; the app applies it (FluentFin.Services.GlassSetting) as the page hears the change.</summary>
	public GlassChoice Glass
	{
		get => field;
		set
		{
			if (SetProperty(ref field, value))
			{
				settings.Glass = value;
				OnPropertyChanged(nameof(SelectedGlass));
			}
		}
	}

	public GlassChoiceItem SelectedGlass
	{
		get => GlassChoices.First(x => x.Choice == Glass);
		set
		{
			if (value is not null)
			{
				Glass = value.Choice;
			}
		}
	}

	/// <summary>fluentfin-mpv: the choices for who draws the player controls (docs/research/player-controls.md).</summary>
	public static IReadOnlyList<MpvPlayerControlsChoice> PlayerControlsChoices { get; } =
	[
		new(MpvPlayerControls.FluentFin, "Seaglass"),
		new(MpvPlayerControls.Mpv, "mpv"),
	];

	/// <summary>Used from the next playback on (a change restarts the backend then, docs/BRIDGE.md §3 window_locked).</summary>
	public MpvPlayerControls PlayerControls
	{
		get => field;
		set
		{
			if (SetProperty(ref field, value))
			{
				settings.MpvPlayerControls = value;
				OnPropertyChanged(nameof(SelectedPlayerControls));
			}
		}
	}

	public MpvPlayerControlsChoice? SelectedPlayerControls
	{
		get => PlayerControlsChoices.FirstOrDefault(x => x.Controls == PlayerControls);
		set
		{
			if (value is not null)
			{
				PlayerControls = value.Controls;
			}
		}
	}

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

	/// <summary>
	/// How long the preferences must stay unchanged before they are saved: arrowing through a closed list is one save, not
	/// one for each item passed. Leaving the page saves at once.
	/// </summary>
	public TimeSpan SaveDelay { get; set; } = TimeSpan.FromMilliseconds(500);

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

	/// <summary>Kept on this PC: see <see cref="SettingKeys.LookUpIntrosOnline"/>. Reaches the backend with the next play.</summary>
	public bool LookUpIntrosOnline
	{
		get => settings.LookUpIntrosOnline;
		set
		{
			if (settings.LookUpIntrosOnline != value)
			{
				settings.LookUpIntrosOnline = value;
				OnPropertyChanged();
			}
		}
	}

	/// <summary>Kept on this PC: see <see cref="SettingKeys.LearnIntros"/>. Reaches the backend with the next play.</summary>
	public bool LearnIntros
	{
		get => settings.LearnIntros;
		set
		{
			if (settings.LearnIntros != value)
			{
				settings.LearnIntros = value;
				OnPropertyChanged();
			}
		}
	}

	/// <summary>What "Forget learned intros" said last ("" before it was pressed).</summary>
	[ObservableProperty]
	public partial string ForgetLearnedStatus { get; set; } = "";

	/// <summary>Deletes the intros learned from the user's skips (<see cref="LearnedIntros"/>).</summary>
	[RelayCommand]
	private void ForgetLearnedIntros()
	{
		if (knownFolders is null)
		{
			return;
		}

		ForgetLearnedStatus = LearnedIntros.Forget(LearnedIntros.ConfigDirectory(knownFolders))
			? "Learned intros forgotten."
			: "Could not forget them. Close the player and try again.";
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

		// a change still waiting for its save goes now
		if (Interlocked.Exchange(ref _pendingSave, null) is { } pending)
		{
			pending.Cancel();
			_ = SavePlaybackPreferences();
		}

		return Task.CompletedTask;
	}

	public async Task OnNavigatedTo(object parameter)
	{
		// fluentfin-mpv (release 1.0): shown, not saved. The effective player can be the fallback for a saved choice this
		// copy cannot run (VLC, Flyleaf in the release), and only a pick may replace the saved one.
		_loadingPlayer = true;
		try
		{
			MediaPlayerType = settings.MediaPlayer;
		}
		finally
		{
			_loadingPlayer = false;
		}
		MpvBackendCommand = settings.MpvBackendCommand;
		PlayerControls = settings.MpvPlayerControls;
		Glass = settings.Glass;

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
			_loadedUserId = user.Id;
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
			SaveSoon();
		}
	}

	partial void OnAutoPlayNextEpisodeChanged(bool value) => SaveSoon();

	/// <summary>Saves once the preferences have stayed unchanged for <see cref="SaveDelay"/> (each change restarts the wait).</summary>
	private void SaveSoon()
	{
		if (!_showingPreferences || _loadedUserId is null)
		{
			return;
		}

		var pending = new CancellationTokenSource();
		Interlocked.Exchange(ref _pendingSave, pending)?.Cancel();
		_ = SaveAfterDelay(pending);
	}

	private async Task SaveAfterDelay(CancellationTokenSource pending)
	{
		try
		{
			await Task.Delay(SaveDelay, pending.Token);
		}
		catch (OperationCanceledException)
		{
			return;
		}

		if (Interlocked.CompareExchange(ref _pendingSave, null, pending) == pending)
		{
			await SavePlaybackPreferences();
		}
	}

	/// <summary>
	/// fluentfin-mpv: saves this page's four fields into the user's configuration as the server has it now, read just
	/// before (as jellyfin-web does): a change made elsewhere while the page was open is kept, not undone by an old copy.
	/// Only for the user the page was loaded for: after a switch of user the save is dropped. One save at a time, each with
	/// the latest values. A failure is said on the page with Try again; the values stay as picked. The session's copy is
	/// updated, so FluentFin's own players follow at once.
	/// </summary>
	[RelayCommand]
	private async Task SavePlaybackPreferences()
	{
		if (!_showingPreferences || _loadedUserId is not { } loadedUser)
		{
			return;
		}

		await _saving.WaitAsync();
		try
		{
			var fresh = await jellyfinClient.GetCurrentUser();
			if (fresh?.Configuration is not { } configuration || fresh.Id is not { } userId)
			{
				SaveError = $"Your change didn't reach {LoadMessages.ServerName()}. Check that the server is running and that this PC can reach it, then try again.";
				return;
			}

			if (userId != loadedUser)
			{
				// signed in as someone else since the page was loaded: these were the other user's preferences
				SaveError = "";
				return;
			}

			configuration.AudioLanguagePreference = AudioLanguage?.Code ?? "";
			configuration.SubtitleLanguagePreference = SubtitleLanguage?.Code ?? "";
			configuration.SubtitleMode = SubtitleMode?.Mode ?? UserConfiguration_SubtitleMode.Default;
			configuration.EnableNextEpisodeAutoPlay = AutoPlayNextEpisode;

			if (await jellyfinClient.SaveUserConfiguration(userId, configuration))
			{
				SaveError = "";
				if (SessionInfo.CurrentUser is { } user && user.Id == userId)
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

	// fluentfin-mpv: a saved login that goes takes its saved home page (watch history) with it (HomeCache.ForgetLogin)
	[RelayCommand]
	private async Task DeleteServer(SavedServer server)
	{
		Servers.Remove(server);
		foreach (var user in server.Users.ToList())
		{
			await homeCache.ForgetLogin(server.Id, user.Username);
		}
	}

	/// <summary>fluentfin-mpv: Delete User on a saved server: the saved login goes, and the home page saved for it.</summary>
	public async Task RemoveSavedUser(SavedServer server, SavedUser user)
	{
		server.Users.Remove(user);
		await homeCache.ForgetLogin(server.Id, user.Username);
	}
}
