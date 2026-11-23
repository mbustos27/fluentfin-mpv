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

public partial class SettingsViewModel(ISettings settings, IMpvBackend mpvBackend, IJellyfinClient jellyfinClient, Services.HomeCache homeCache, KnownFolders? knownFolders = null, MpvPlaybackService? mpvPlayback = null) : ObservableObject, INavigationAware
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
		// Seaglass 1.1.3: the player's name, not the backend's (UX review, Settings)
		new(MediaPlayerType.Mpv, "mpv"),
		new(MediaPlayerType.Vlc, "VLC"),
		new(MediaPlayerType.Flyleaf, "Flyleaf"),
		new(MediaPlayerType.WindowsMediaPlayer, "Windows Media Player"),
	];

	// fluentfin-mpv (release 1.0): only the players this copy can run (the release has no VLC or Flyleaf libraries)
	public static IReadOnlyList<MediaPlayerChoice> MediaPlayers { get; } = OfferedPlayers(MediaPlayerAvailability.ForThisApp);

	public static IReadOnlyList<MediaPlayerChoice> OfferedPlayers(MediaPlayerAvailability players) => [.. AllMediaPlayers.Where(x => players.IsAvailable(x.Type))];

	/// <summary>Seaglass 1.1.3: the Player list is shown only when there is a choice (a copy that can run one player has none).</summary>
	public static bool HasPlayerChoice { get; } = OffersAChoice(MediaPlayerAvailability.ForThisApp);

	/// <summary>Whether <paramref name="players"/> leaves more than one player to choose from.</summary>
	public static bool OffersAChoice(MediaPlayerAvailability players) => OfferedPlayers(players).Count > 1;

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
					// Seaglass 1.1.4 (interaction audit, item 17): remote control follows at once, not at the next sign-in
					PlayerChanged = jellyfinClient.UpdateCapabilities();
				}
				OnPropertyChanged(nameof(SelectedMediaPlayer));
				OnPropertyChanged(nameof(IsMpvSelected));
				OnPropertyChanged(nameof(IsSyncPlayAvailable));
				OnPropertyChanged(nameof(SyncPlayDescription));
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

	// Seaglass 1.1.4 (interaction audit, item 7): "Play the next episode automatically" is shown for every player again (it was
	// hidden with mpv, FollowsAutoPlay): the mpv player sends the user's choice to the backend before every play (set_auto_play)

	// ---- Seaglass 1.1.4 (UX review, prioritised fix 15): Settings > Other devices, from the old profile flyout

	/// <summary>
	/// SyncPlay plays in the other players only (the mpv player has no group playback; DialogCommands says so too), and only
	/// for an account the server allows it (its policy's SyncPlayAccess). Otherwise its button is disabled and the card says
	/// why rather than vanishing (layout audit, item 18: choosing it used to do nothing at all).
	/// </summary>
	public bool IsSyncPlayAvailable => !IsMpvSelected && SyncPlayAllowed(CurrentUser());

	/// <summary>The signed-in user, whose policy says whether SyncPlay is allowed (tests set it).</summary>
	internal Func<UserDto?> CurrentUser { get; set; } = () => SessionInfo.CurrentUser;

	/// <summary>Whether the server lets this account use SyncPlay (no policy known: assume it does, the server decides).</summary>
	public static bool SyncPlayAllowed(UserDto? user) => user?.Policy?.SyncPlayAccess is not UserPolicy_SyncPlayAccess.None;

	public string SyncPlayDescription =>
		IsMpvSelected ? "Watch in step with other people on this server. Not available with the mpv player: choose another player under Playback to use it."
		: !SyncPlayAllowed(CurrentUser()) ? "Watch in step with other people on this server. Not available on this server: it doesn't allow SyncPlay for your account."
		// review: a join-only account was offered "New group", which did nothing
		: CurrentUser()?.Policy?.SyncPlayAccess == UserPolicy_SyncPlayAccess.JoinGroups ? "Watch in step with other people on this server: join a group. Your account can't start one here."
		: "Watch in step with other people on this server: join a group or start one.";

	/// <summary>
	/// The server's administration (the old Dashboard: users, libraries, playback, activity, scheduled tasks) for an
	/// administrator only, from Settings > Servers and accounts.
	/// </summary>
	public static bool ShowsServerAdministration(UserDto? user) => user?.Policy?.IsAdministrator == true;

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
		new(MpvPlayerControls.FluentFin, AppIdentity.Name),
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

	/// <summary>The server being told of a picked player (<see cref="IJellyfinClient.UpdateCapabilities"/>); for tests.</summary>
	internal Task PlayerChanged { get; private set; } = Task.CompletedTask;

	/// <summary>
	/// Empty = found automatically; see <see cref="SettingKeys.MpvBackendCommand"/>. Seaglass 1.1.4 (interaction audit, item 17):
	/// a change takes effect at once: an idle backend is shut down, so the next playback starts one with the new command (it
	/// used to wait for the next time the backend started, often the next run of Seaglass).
	/// </summary>
	public string MpvBackendCommand
	{
		get => field;
		set
		{
			if (SetProperty(ref field, value ?? ""))
			{
				var command = field.Trim();
				if (settings.MpvBackendCommand != command)
				{
					settings.MpvBackendCommand = command;
					BackendRestarted = RestartIdleBackendAsync();
				}
			}
		}
	} = "";

	/// <summary>The idle backend's shutdown after a command change (<see cref="MpvBackendCommand"/>); for tests.</summary>
	internal Task BackendRestarted { get; private set; } = Task.CompletedTask;

	/// <summary>Shuts a running backend down when nothing plays; a playing one keeps its command until it next starts. Never throws.</summary>
	private async Task RestartIdleBackendAsync()
	{
		try
		{
			if (mpvBackend.Status.State is MpvBackendState.Running or MpvBackendState.Starting && mpvPlayback?.HasPlayback != true)
			{
				await mpvBackend.ShutdownAsync();
			}
		}
		catch (Exception)
		{
			// the next start uses the new command anyway
		}
	}

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

	/// <summary>Seaglass: Settings > Check for updates, off by default: see <see cref="SettingKeys.CheckForUpdates"/>.</summary>
	public bool CheckForUpdates
	{
		get => settings.CheckForUpdates;
		set
		{
			if (settings.CheckForUpdates != value)
			{
				settings.CheckForUpdates = value;
				OnPropertyChanged();
			}
		}
	}

	// 1.5 fixes
	/// <summary>
	/// Kept on this PC: the switch over <see cref="SettingKeys.LeaveOutRewatching"/>. Home and a show's page follow it on their
	/// next visit: the saved Home page is marked stale (the review's M1: Home served it for up to 5 minutes), so the next visit
	/// asks the server again with the new choice.
	/// </summary>
	public bool IncludeRewatching
	{
		get => !settings.LeaveOutRewatching;
		set
		{
			if (settings.LeaveOutRewatching == value)
			{
				settings.LeaveOutRewatching = !value;
				homeCache?.MarkStale();
				OnPropertyChanged();
			}
		}
	}

	/// <summary>Kept on this PC: Home's N and C keys (the switch over <see cref="SettingKeys.HomeLetterKeysOff"/>); at once.</summary>
	public bool HomeLetterKeys
	{
		get => !settings.HomeLetterKeysOff;
		set
		{
			if (settings.HomeLetterKeysOff == value)
			{
				settings.HomeLetterKeysOff = !value;
				OnPropertyChanged();
			}
		}
	}
	// END 1.5 fixes

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

	// 1.6 skip: skip rules (docs/BRIDGE.md §4). "Forget learned intros" became the list's Forget all: the backend keeps
	// the learned intros with the rules now, and is the one that forgets them.

	/// <summary>"Ads and previews the server marks": Off, Show a Skip button, Skip them by itself.</summary>
	public static IReadOnlyList<SkipChoice> AdsAndPreviewsChoices { get; } =
	[
		new("off", "Off"),
		new("ask", "Show a Skip button"),
		new("always", "Skip them by itself"),
	];

	/// <summary>"When a show has a skip rule": Show a Skip button, Skip it by itself.</summary>
	public static IReadOnlyList<SkipChoice> SkipRuleChoices { get; } =
	[
		new("ask", "Show a Skip button"),
		new("always", "Skip it by itself"),
	];

	/// <summary>Kept on this PC: see <see cref="SettingKeys.AdsAndPreviewsAction"/>. Reaches the backend with the next play.</summary>
	public SkipChoice? SelectedAdsAndPreviews
	{
		get => AdsAndPreviewsChoices.FirstOrDefault(x => x.Value == BridgeSkipCommands.SegmentAction(settings.AdsAndPreviewsAction)) ?? AdsAndPreviewsChoices[1];
		set
		{
			if (value is not null && settings.AdsAndPreviewsAction != value.Value)
			{
				settings.AdsAndPreviewsAction = value.Value;
				OnPropertyChanged();
			}
		}
	}

	/// <summary>Kept on this PC: see <see cref="SettingKeys.SkipRuleAction"/>. A rule's own choice wins.</summary>
	public SkipChoice? SelectedSkipRuleAction
	{
		get => SkipRuleChoices.FirstOrDefault(x => x.Value == BridgeSkipCommands.RuleAction(settings.SkipRuleAction)) ?? SkipRuleChoices[0];
		set
		{
			if (value is not null && settings.SkipRuleAction != value.Value)
			{
				settings.SkipRuleAction = value.Value;
				OnPropertyChanged();
			}
		}
	}

	/// <summary>The "Skip rules" list, read from the backend when its expander opens (<see cref="LoadSkipRulesCommand"/>).</summary>
	public SkipRulesList SkipRules => field ??= new SkipRulesList(mpvBackend, () =>
		MpvIdentity.FromSession(settings) is { } identity ? (identity.ServerId, identity.UserId) : (null, null));

	[RelayCommand]
	private Task LoadSkipRules() => SkipRules.LoadAsync();

	/// <summary>A row's Forget: one rule or learned intro, or a show with everything of it.</summary>
	[RelayCommand]
	private Task ForgetSkipRule(SkipRuleRow? row) => row is null ? Task.CompletedTask : SkipRules.ForgetAsync(row);

	/// <summary>Forget all, after a yes to <see cref="ForgetAllSkipRulesPrompt"/> (button review A39's rule: nothing goes unasked).</summary>
	[RelayCommand]
	private async Task ForgetAllSkipRules()
	{
		if (!await Asked(ForgetAllSkipRulesPrompt))
		{
			return;
		}

		await SkipRules.ForgetAllAsync();
	}
	// end 1.6 skip

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

	/// <summary>1.6.1 (BH-151-m2): Playback preferences' failed Save, in words that name an ended sign-in.</summary>
	internal static string ChangeDidNotReach() => LoadMessages.ActionFailed($"Your change didn't reach {LoadMessages.ServerName()}",
		$"Your change didn't reach {LoadMessages.ServerName()}. Check this PC's connection, then try again."); // 1.7.3 (rule 11): it said "Check that the server is running"

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
				SaveError = ChangeDidNotReach();
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
				SaveError = ChangeDidNotReach();
			}
		}
		finally
		{
			_saving.Release();
		}
	}

	/// <summary>
	/// Seaglass 1.1.3 (UX review, prioritised fix 2): asks before a saved server or sign-in goes; set by the page (a dialog
	/// whose default button is Cancel). Without it nothing is removed.
	/// </summary>
	public Func<ConfirmPrompt, Task<bool>>? Confirm { get; set; }

	private async Task<bool> Asked(ConfirmPrompt prompt) => Confirm is { } confirm && await confirm(prompt);

	private static string Named(SavedServer server) =>
		!string.IsNullOrWhiteSpace(server.DisplayName) ? server.DisplayName.Trim()
		: !string.IsNullOrWhiteSpace(server.PublicUrl) ? server.PublicUrl.Trim()
		: "this server";

	/// <summary>What Remove server asks: the server is named, and what goes (on this PC only).</summary>
	public static ConfirmPrompt RemoveServerPrompt(SavedServer server) => new(
		$"Remove {Named(server)}?",
		server.Users.Count switch
		{
			0 => $"{AppIdentity.Name} forgets this server. Nothing changes on the server; sign in to add it again.",
			1 => $"{AppIdentity.Name} forgets this server and the sign-in saved for {server.Users[0].Username}, with its saved home page. Nothing changes on the server; sign in to add it again.",
			var n => $"{AppIdentity.Name} forgets this server and its {n} saved sign-ins, with their saved home pages. Nothing changes on the server; sign in to add it again.",
		},
		"Remove");

	/// <summary>What Remove asks for one saved sign-in: the user and the server are named.</summary>
	public static ConfirmPrompt RemoveUserPrompt(SavedServer server, SavedUser user) => new(
		$"Remove {user.Username} from {Named(server)}?",
		$"{AppIdentity.Name} forgets the sign-in saved for {user.Username} on this PC, with its saved home page. The account on the server is not changed.",
		"Remove");

	/// <summary>1.6 skip: what the skip rules' "Forget all" asks.</summary>
	public static ConfirmPrompt ForgetAllSkipRulesPrompt { get; } = new(
		"Forget all skip rules?",
		"Every part you set to skip, and every intro learned from your skips, stops being skipped on this PC. The player learns again from your next skips.",
		"Forget all");

	/// <summary>
	/// Seaglass 1.1.3: the accessible name of a saved sign-in's button; 1.1.4 (button review A48): "Remove sign-in", as it
	/// reads ("Remove the sign-in for alice").
	/// </summary>
	public static string RemoveUserLabel(string username) => $"Remove the sign-in for {username}";

	/// <summary>Seaglass 1.1.3: the accessible name of a saved server's Remove button ("Remove server Home").</summary>
	public static string RemoveServerLabel(string displayName) => string.IsNullOrWhiteSpace(displayName) ? "Remove server" : $"Remove server {displayName.Trim()}";

	// fluentfin-mpv: a saved login that goes takes its saved home page (watch history) with it (HomeCache.ForgetLogin)
	// Seaglass 1.1.3: only after the user said yes to RemoveServerPrompt
	[RelayCommand]
	private async Task DeleteServer(SavedServer server)
	{
		if (!await Asked(RemoveServerPrompt(server)))
		{
			return;
		}

		Servers.Remove(server);
		foreach (var user in server.Users.ToList())
		{
			await homeCache.ForgetLogin(server.Id, user.Username);
		}
	}

	/// <summary>
	/// fluentfin-mpv: Delete User on a saved server: the saved login goes, and the home page saved for it. Seaglass 1.1.3: only
	/// after the user said yes to <see cref="RemoveUserPrompt"/>; true when it went.
	/// </summary>
	public async Task<bool> RemoveSavedUser(SavedServer server, SavedUser user)
	{
		if (!await Asked(RemoveUserPrompt(server, user)))
		{
			return false;
		}

		server.Users.Remove(user);
		await homeCache.ForgetLogin(server.Id, user.Username);
		return true;
	}

	// ---- Seaglass 1.1.3: Settings > About (UX review, prioritised fixes 1, 6 and 9)

	/// <summary>"Seaglass 1.1.3", the first line of About.</summary>
	public static string AboutTitle { get; } = AboutTitleFor(AppVersion.Current);

	public static string AboutTitleFor(string version) => $"{AppIdentity.Name} {version}";

	/// <summary>This project on GitHub: Contribute.</summary>
	public const string ContributeUrl = Updates.UpdateSource.RepositoryUrl;

	/// <summary>A new issue on this project: Report a problem.</summary>
	public const string ReportProblemUrl = ContributeUrl + "/issues/new";

	/// <summary>The GNU GPL 3.0, as this project ships it.</summary>
	public const string LicenseUrl = ContributeUrl + "/blob/main/LICENSE";

	/// <summary>The two projects Seaglass is built from (docs/UPSTREAM.md).</summary>
	public const string FluentFinUrl = "https://github.com/insomniachi/FluentFin";

	public const string ShimUrl = "https://github.com/jellyfin/jellyfin-mpv-shim";

	// for the page's HyperlinkButtons (x:Bind does not turn a string into a Uri)
	public static Uri LicenseUri { get; } = new(LicenseUrl);

	public static Uri FluentFinUri { get; } = new(FluentFinUrl);

	public static Uri ShimUri { get; } = new(ShimUrl);
}
