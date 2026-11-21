namespace FluentFin.Core.Updates;

/// <summary>Nixie 1.5: what the updater's buttons do.</summary>
public enum UpdateAction
{
	None,
	Download,
	Cancel,
	RestartToUpdate,
	Retry,
	RetryCheck,
	OpenReleasePage,
	ReleaseNotes,
	WhatsNew,
	Dismiss,
}

/// <summary>
/// Nixie 1.5: what the notice or Settings shows for an <see cref="UpdateStatus"/>: a title (Narrator hears it when it changes:
/// it names the step, never the progress), a detail line (the progress, a reason), the progress bar's value (0 to 1; null for
/// none; <see cref="Busy"/> for a bar that only says "working"), one Primary action and the others.
/// </summary>
public sealed record UpdateView(string Title, string? Detail, double? Progress, bool Busy, UpdateAction Primary, IReadOnlyList<UpdateAction> Others, bool IsError)
{
	/// <summary>The button's words for <paramref name="action"/>.</summary>
	public static string Label(UpdateAction action) => action switch
	{
		UpdateAction.Download => "Download",
		UpdateAction.Cancel => "Cancel",
		UpdateAction.RestartToUpdate => "Restart to update",
		UpdateAction.Retry or UpdateAction.RetryCheck => "Retry",
		UpdateAction.OpenReleasePage => "Open the release page",
		UpdateAction.ReleaseNotes => "Release notes",
		UpdateAction.WhatsNew => "What's new",
		UpdateAction.Dismiss => "Dismiss",
		_ => "",
	};

	private static string Name => AppIdentity.Name;

	/// <summary>Settings' Updates group: every step, from "Checking for updates…" to "Ready to install".</summary>
	public static UpdateView ForSettings(UpdateStatus status, DateTimeOffset now, TimeZoneInfo zone) => status.Phase switch
	{
		// right after an update, Settings says so as the notice does (the shell hides the notice on Settings: one Primary)
		UpdatePhase.Idle or UpdatePhase.UpToDate when status.JustUpdated is { } done =>
			new($"Updated to {Name} {done.Version}", status.CheckedAt is null ? null : UpToDate(status, now, zone), null, false, UpdateAction.WhatsNew, [], false),
		UpdatePhase.Checking => new("Checking for updates…", null, null, true, UpdateAction.None, [], false),
		UpdatePhase.UpToDate => new(UpToDate(status, now, zone), null, null, false, UpdateAction.None, [], false),
		UpdatePhase.CheckFailed => new(status.Error ?? "Couldn't check", null, null, false, UpdateAction.RetryCheck, [UpdateAction.OpenReleasePage], true),
		UpdatePhase.Idle => new(status.CheckedAt is null ? "Not checked yet" : UpToDate(status, now, zone), null, null, false, UpdateAction.None, [], false),
		_ when status.Release is null => new(UpToDate(status, now, zone), null, null, false, UpdateAction.None, [], false),
		_ => Release(status, settings: true),
	};

	/// <summary>
	/// The shell's notice: only what is worth interrupting for (a newer version, its download and its install, a failure of
	/// those, and "Updated to"); null when it should not show. Checks, their failures and "up to date" stay in Settings.
	/// </summary>
	public static UpdateView? ForNotice(UpdateStatus status)
	{
		if (status.JustUpdated is { } done)
		{
			return new($"Updated to {Name} {done.Version}", null, null, false, UpdateAction.WhatsNew, [UpdateAction.Dismiss], false);
		}

		return status.Release is null || status.Phase is UpdatePhase.Idle or UpdatePhase.Checking or UpdatePhase.UpToDate or UpdatePhase.CheckFailed
			? null
			: Release(status, settings: false);
	}

	private static string UpToDate(UpdateStatus status, DateTimeOffset now, TimeZoneInfo zone) =>
		status.CheckedAt is { } at ? $"You're up to date{UpdateText.Separator}checked {UpdateText.Ago(at, now, zone)}" : "You're up to date";

	private static UpdateView Release(UpdateStatus status, bool settings)
	{
		var release = status.Release!;
		var version = $"{Name} {release.Version}";
		// the notice ends with Dismiss (this version is not offered again); Settings has no notice to dismiss
		IReadOnlyList<UpdateAction> With(params UpdateAction[] actions) => settings ? actions : [.. actions, UpdateAction.Dismiss];
		var installable = status.CanSelfUpdate && release.HasInstaller;
		return status.Phase switch
		{
			UpdatePhase.Downloading => new($"Downloading {version}",
				status.Progress is { } p ? UpdateText.Progress(p) : null,
				status.Progress?.Fraction ?? 0, status.Progress?.Fraction is null, UpdateAction.None, [UpdateAction.Cancel], false),
			UpdatePhase.Verifying => new("Checking the download…", null, null, true, UpdateAction.None, [], false),
			UpdatePhase.Ready => new($"{version} is ready to install",
				$"{Name} closes, installs it and opens again where you were.", null, false, UpdateAction.RestartToUpdate, With(UpdateAction.ReleaseNotes), false),
			UpdatePhase.Installing => new($"Installing {version}…", $"{Name} closes now and opens again when it's done.", null, true, UpdateAction.None, [], false),
			UpdatePhase.DownloadFailed => new($"Couldn't download {version}", status.Error, null, false, UpdateAction.Retry, With(UpdateAction.OpenReleasePage), true),
			// review m8: installed with "restart required": nothing to click, the same install is not offered again
			UpdatePhase.RestartPending => new($"Restart Windows to finish updating to {version}",
				"Some files were in use, so Windows finishes the install when it restarts.", null, false, UpdateAction.None, With(), false),
			UpdatePhase.InstallFailed => new($"{version} didn't install", Sentence(status.Error), null, false,
				installable ? UpdateAction.Retry : UpdateAction.OpenReleasePage, With(installable ? UpdateAction.OpenReleasePage : UpdateAction.ReleaseNotes), true),
			// review m3: a check that failed while this update was known is said beside it (status.Error), the update kept
			_ when !status.CanSelfUpdate => new($"{version} is available",
				status.Error ?? $"This copy of {Name} is portable, so it updates from the release page.", null, false, UpdateAction.OpenReleasePage, With(UpdateAction.ReleaseNotes), status.Error is not null),
			_ when !release.HasInstaller => new($"{version} is available", status.Error, null, false, UpdateAction.OpenReleasePage, With(UpdateAction.ReleaseNotes), status.Error is not null),
			_ => new(release.MsiSize is { } size ? $"{version} is available{UpdateText.Separator}{UpdateText.Size(size)}" : $"{version} is available",
				status.Error, null, false, UpdateAction.Download, With(UpdateAction.ReleaseNotes), status.Error is not null),
		};
	}

	/// <summary>A reason as a sentence: its first letter up, a full stop at the end.</summary>
	private static string? Sentence(string? reason) =>
		string.IsNullOrWhiteSpace(reason) ? null : char.ToUpperInvariant(reason[0]) + reason[1..] + (reason.EndsWith('.') ? "" : ".");
}
