using System.Diagnostics;
using System.Text.Json;

namespace FluentFin.Core.Updates;

/// <summary>Seaglass: whether this copy was put there by the MSI (it can update itself) or is a portable unzipped folder.</summary>
public static class InstallDetector
{
	/// <summary>
	/// The MSI writes <c>HKCU\Software\Seaglass\InstallFolder</c> ([INSTALLFOLDER]); the app runs from that folder's <c>app\</c>.
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
	/// An installed copy downloads the installer, checks it and runs it (<c>msiexec /i</c>: the installer closes or replaces
	/// the app); a portable copy, or a release without an installer, opens the release page instead.
	/// Returns what it did, for the caller's message.
	/// </summary>
	public static async Task<UpdateOutcome> InstallAsync(ReleaseInfo release, bool isMsiInstall, Func<ReleaseInfo, CancellationToken, Task<string>> download,
		Action<ProcessStartInfo> start, Action<string> openPage, CancellationToken cancellation = default)
	{
		if (!isMsiInstall || !release.HasInstaller)
		{
			openPage(release.PageUrl);
			return UpdateOutcome.OpenedReleasePage;
		}

		var msi = await download(release, cancellation).ConfigureAwait(false);
		start(MsiexecInstall(msi));
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

/// <summary>Seaglass: what the update check remembers on this PC (one settings value): when it last asked, the release it found, which one was dismissed.</summary>
public sealed record UpdateState(DateTimeOffset? LastCheck = null, ReleaseInfo? Release = null, string? Dismissed = null, DateTimeOffset? LastAttempt = null)
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
/// is off; with it on, the release found earlier is offered (unless dismissed), and when a day has passed since the last
/// check, one request goes out. An answer counts as a check, so a day is the most often it asks; a failure (offline) does not, and is retried at the next start, at most once an hour.
/// </summary>
public sealed class UpdateCoordinator(Settings.ISettings settings, UpdateChecker checker, Func<DateTimeOffset> clock, string currentVersion)
{
	private ReleaseInfo? _notice;

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
		if (!UpdateGate.IsDue(state.LastCheck, now) || !UpdateGate.AttemptAllowed(state.LastAttempt, now))
		{
			return;
		}

		// the attempt is recorded first (a crash or a slow answer never makes it ask twice in an hour); only an answer counts
		// as a check, so being offline at start is tried again at the next one, not a day later
		state = state with { LastAttempt = now };
		settings.UpdateState = state.ToJson();
		var result = await checker.CheckAsync(now, cancellation).ConfigureAwait(false);
		switch (result.Status)
		{
			case UpdateCheckStatus.Available:
				state = state with { Release = result.Release, LastCheck = now };
				break;
			case UpdateCheckStatus.UpToDate:
				state = state with { Release = null, LastCheck = now };
				break;
			case UpdateCheckStatus.RateLimited when result.RetryAfter is { } until && until > now.AddDays(1):
				// told to wait longer than a day: ask again only after that
				state = state with { LastCheck = until - UpdateGate.Interval };
				break;
			case UpdateCheckStatus.RateLimited:
				state = state with { LastCheck = now };
				break;
		}

		settings.UpdateState = state.ToJson();
		Show(state);
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
