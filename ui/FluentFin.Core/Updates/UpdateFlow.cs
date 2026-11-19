using System.Diagnostics;
using System.Text.Json;

namespace FluentFin.Core.Updates;

/// <summary>Seaglass: whether this copy was put there by the MSI (it can update itself) or is a portable unzipped folder.</summary>
public static class InstallDetector
{
	/// <summary>
	/// The MSI writes <c>HKCU\Software\Nixie\InstallFolder</c> ([INSTALLFOLDER], <see cref="UpdateSource.RegistryKey"/>;
	/// Software\Seaglass before 1.2.0); the app runs from that folder's <c>app\</c>.
	/// A copy running from anywhere else (an unzipped portable folder, a dev build) is not an installed one, even when the
	/// registry says an MSI install exists elsewhere on this PC.
	/// </summary>
	public static bool IsMsiInstall(string appBaseDirectory, string? registeredInstallFolder)
	{
		if (string.IsNullOrWhiteSpace(registeredInstallFolder) || string.IsNullOrWhiteSpace(appBaseDirectory))
		{
			return false;
		}

		try
		{
			var install = Normalize(registeredInstallFolder);
			var app = Normalize(appBaseDirectory);
			return string.Equals(app, install, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(app, Path.Combine(install, "app"), StringComparison.OrdinalIgnoreCase);
		}
		catch (ArgumentException)
		{
			return false;
		}
	}

	private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');
}

/// <summary>Seaglass: what "Download and install" does.</summary>
public static class UpdateFlow
{
	/// <summary>
	/// An installed copy downloads the installer, checks it and runs it (<c>msiexec /i</c>), then has the app close itself; a portable copy, or a release without an installer, opens the release page instead.
	/// Returns what it did, for the caller's message.
	/// </summary>
	public static async Task<UpdateOutcome> InstallAsync(ReleaseInfo release, bool isMsiInstall, Func<ReleaseInfo, CancellationToken, Task<string>> download,
		Action<ProcessStartInfo> start, Action<string> openPage, Action? closeApp = null, CancellationToken cancellation = default)
	{
		if (!isMsiInstall || !release.HasInstaller)
		{
			openPage(release.PageUrl);
			return UpdateOutcome.OpenedReleasePage;
		}

		var msi = await download(release, cancellation).ConfigureAwait(false);
		start(MsiexecInstall(msi));
		// the installer replaces the app's files: the app ends cleanly now (playback, backend, settings), so none are locked
		closeApp?.Invoke();
		return UpdateOutcome.StartedInstaller;
	}

	public static ProcessStartInfo MsiexecInstall(string msiPath)
	{
		if (!msiPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
		{
			throw new UpdateException("Not an installer.");
		}

		var info = new ProcessStartInfo("msiexec.exe") { UseShellExecute = false };
		info.ArgumentList.Add("/i");
		info.ArgumentList.Add(msiPath);
		return info;
	}
}

public enum UpdateOutcome
{
	OpenedReleasePage,
	StartedInstaller,
}

/// <summary>
/// Seaglass: what the update check remembers on this PC (one settings value): when it last asked, the release it found, which
/// one was dismissed; Nixie 1.3.1: until when GitHub asked it to wait (<see cref="RetryAfter"/>).
/// </summary>
public sealed record UpdateState(DateTimeOffset? LastCheck = null, ReleaseInfo? Release = null, string? Dismissed = null, DateTimeOffset? LastAttempt = null,
	DateTimeOffset? RetryAfter = null)
{
	public static UpdateState Parse(string? json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return new UpdateState();
		}

		try
		{
			return JsonSerializer.Deserialize<UpdateState>(json) ?? new UpdateState();
		}
		catch (JsonException)
		{
			return new UpdateState();
		}
	}

	public string ToJson() => JsonSerializer.Serialize(this);
}

/// <summary>
/// Seaglass: ties the pieces together for the app. <see cref="TickAsync"/> runs from a timer: nothing happens while the setting
/// is off; with it on, the release found earlier is offered (unless dismissed), and one request goes out: Nixie 1.3.1, at the
/// first tick of every run (the user: "it should check when we open"), then once a day while the app stays open. An answer
/// counts as a check; a failure (offline) does not, and is tried again at most once an hour; GitHub's Retry-After is kept.
/// <see cref="CheckNowAsync"/> is Settings' "Check now": the user's own request, setting on or off.
/// </summary>
public sealed class UpdateCoordinator(Settings.ISettings settings, UpdateChecker checker, Func<DateTimeOffset> clock, string currentVersion)
{
	private ReleaseInfo? _notice;

	/// <summary>Nixie 1.3.1: this run's start check was considered (it goes out at the first tick with the setting on).</summary>
	private bool _startChecked;

	/// <summary>The release to offer now, or null.</summary>
	public ReleaseInfo? Notice
	{
		get => _notice;
		private set
		{
			if (!Equals(_notice, value))
			{
				_notice = value;
				NoticeChanged?.Invoke();
			}
		}
	}

	public event Action? NoticeChanged;

	/// <summary>Nixie 1.3.1: a request went out and this is what it found (the app logs it).</summary>
	public event Action<UpdateCheckResult>? Checked;

	public async Task TickAsync(CancellationToken cancellation = default)
	{
		if (!settings.CheckForUpdates)
		{
			Notice = null;
			return;
		}

		var state = UpdateState.Parse(settings.UpdateState);
		Show(state);
		var now = clock();
		var atStart = !_startChecked;
		_startChecked = true;
		if (!UpdateGate.ShouldCheck(state, now, atStart))
		{
			return;
		}

		await CheckAndRememberAsync(state, now, cancellation).ConfigureAwait(false);
	}

	/// <summary>
	/// Nixie 1.3.1: Settings' "Check now". Asks at once, the setting on or off (the user asked), except while GitHub asked to
	/// wait (answered RateLimited with that time, nothing sent). What it finds is remembered as a check; with the setting on,
	/// a newer release shows in the shell's bar too.
	/// </summary>
	public async Task<UpdateCheckResult> CheckNowAsync(CancellationToken cancellation = default)
	{
		var state = UpdateState.Parse(settings.UpdateState);
		var now = clock();
		if (UpdateGate.Waiting(state, now))
		{
			return new UpdateCheckResult(UpdateCheckStatus.RateLimited, RetryAfter: state.RetryAfter);
		}

		return await CheckAndRememberAsync(state, now, cancellation).ConfigureAwait(false);
	}

	private async Task<UpdateCheckResult> CheckAndRememberAsync(UpdateState state, DateTimeOffset now, CancellationToken cancellation)
	{
		// the attempt is recorded first (a crash or a slow answer never makes it ask twice in an hour); only an answer counts
		// as a check, so being offline at start is tried again at the next one, not a day later
		state = state with { LastAttempt = now };
		settings.UpdateState = state.ToJson();
		var result = await checker.CheckAsync(now, cancellation).ConfigureAwait(false);
		switch (result.Status)
		{
			case UpdateCheckStatus.Available:
				state = state with { Release = result.Release, LastCheck = now, RetryAfter = null };
				break;
			case UpdateCheckStatus.UpToDate:
				state = state with { Release = null, LastCheck = now, RetryAfter = null };
				break;
			case UpdateCheckStatus.RateLimited:
				// GitHub's word on when to ask again is kept (Retry-After, or the limit's reset): nothing goes out before it
				state = state with { LastCheck = now, RetryAfter = UpdateGate.CapWait(result.RetryAfter ?? now + UpdateGate.RetryInterval, now) };
				break;
		}

		settings.UpdateState = state.ToJson();
		Checked?.Invoke(result);
		if (settings.CheckForUpdates)
		{
			Show(state);
		}

		return result;
	}

	/// <summary>"Dismiss": this version is not offered again.</summary>
	public void Dismiss()
	{
		if (Notice is { } shown)
		{
			settings.UpdateState = (UpdateState.Parse(settings.UpdateState) with { Dismissed = shown.Version }).ToJson();
			Notice = null;
		}
	}

	private void Show(UpdateState state) =>
		Notice = state.Release is { } r && UpdateVersion.IsNewer(r.Version, currentVersion) && r.Version != state.Dismissed ? r.Validated() : null;
}

/// <summary>Nixie 1.3.1: what Settings' "Check now" says (and Narrator reads), from what the check found.</summary>
/// <param name="CanRetry">The check could not be made (offline, rate-limited): a Retry button is offered.</param>
public sealed record UpdateCheckMessage(string Text, bool IsError, bool CanRetry, ReleaseInfo? Release = null)
{
	public const string Checking = "Checking\u2026";

	public static UpdateCheckMessage For(UpdateCheckResult result, string currentVersion, TimeZoneInfo? zone = null, DateTimeOffset? now = null) => result.Status switch
	{
		UpdateCheckStatus.Available when result.Release is { } release =>
			new($"{AppIdentity.Name} {release.Version} is available", false, false, release.Validated()),
		UpdateCheckStatus.UpToDate => new($"{AppIdentity.Name} is up to date ({currentVersion})", false, false),
		UpdateCheckStatus.RateLimited when result.RetryAfter is { } until =>
			new($"Couldn't check: GitHub asked {AppIdentity.Name} to wait until {When(until, now ?? DateTimeOffset.Now, zone ?? TimeZoneInfo.Local)}", true, true),
		UpdateCheckStatus.RateLimited => new($"Couldn't check: GitHub asked {AppIdentity.Name} to wait a while", true, true),
		_ => new($"Couldn't check: {result.Reason ?? "no answer from GitHub"}", true, true),
	};

	/// <summary>
	/// Nixie 1.4.0 (bug hunt m7: "wait until 09:00" did not say which day): the time alone today, "tomorrow at" the time, else
	/// the day and date at the time, in the user's time zone.
	/// </summary>
	public static string When(DateTimeOffset until, DateTimeOffset now, TimeZoneInfo zone)
	{
		var at = TimeZoneInfo.ConvertTime(until, zone);
		var today = TimeZoneInfo.ConvertTime(now, zone).Date;
		var time = at.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
		return at.Date == today ? time
			: at.Date == today.AddDays(1) ? $"tomorrow at {time}"
			: $"{at.ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture)} at {time}";
	}
}

/// <summary>
/// Nixie 1.3.1: update checks are on by default (the user confirmed). Older versions wrote the old default (off) into the
/// settings the first time they read it, so a saved "off" does not say the user chose it. It is read as the user's choice
/// only when the check ever ran (the remembered update state holds something: the user had turned it on); otherwise the
/// saved off is the old default and becomes on, once (<see cref="MarkerKey"/>; a later off is the user's and stays).
/// </summary>
public static class UpdateDefaults
{
	/// <summary>Set once the 1.3.1 default was applied to this settings file.</summary>
	public const string MarkerKey = "CheckForUpdatesDefaultOn";

	/// <summary>The value to write: true when a saved off is the old default; null to leave the setting as it is.</summary>
	public static bool? Decide(System.Text.Json.Nodes.JsonNode? savedCheck, System.Text.Json.Nodes.JsonNode? savedState)
	{
		if (savedCheck is null || savedCheck.GetValueKind() != JsonValueKind.False)
		{
			return null;   // never saved (the new default, on, applies), or on
		}

		var state = savedState?.GetValueKind() == JsonValueKind.String ? savedState.GetValue<string>() : null;
		return string.IsNullOrWhiteSpace(state) ? true : null;   // the check never ran: off was never the user's choice
	}

	/// <summary>Applies the new default once to <paramref name="settings"/>.</summary>
	public static void Apply(Contracts.Services.ILocalSettingsService settings)
	{
		if (settings.ReadSettingRaw(MarkerKey) is not null)
		{
			return;
		}

		if (Decide(settings.ReadSettingRaw(Settings.SettingKeys.CheckForUpdates.Name), settings.ReadSettingRaw(Settings.SettingKeys.UpdateState.Name)) is { } value)
		{
			settings.SaveSetting(Settings.SettingKeys.CheckForUpdates.Name, value);
		}

		settings.SaveSetting(MarkerKey, true);
	}
}
