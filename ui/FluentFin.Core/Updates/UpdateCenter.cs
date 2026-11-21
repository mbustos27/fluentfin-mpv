namespace FluentFin.Core.Updates;

/// <summary>Nixie 1.5: where an update is, step by step (the notice and Settings show the same one).</summary>
public enum UpdatePhase
{
	/// <summary>Nothing found this run (the last check's time, if any, is in <see cref="UpdateStatus.CheckedAt"/>).</summary>
	Idle,

	/// <summary>Check now is asking GitHub.</summary>
	Checking,

	/// <summary>This is the latest version.</summary>
	UpToDate,

	/// <summary>Check now could not ask (offline, GitHub refused, rate-limited).</summary>
	CheckFailed,

	/// <summary>A newer version: not downloading (a portable copy, a release without an installer, a metered connection, or cancelled).</summary>
	Available,

	/// <summary>The installer is downloading (<see cref="UpdateStatus.Progress"/>).</summary>
	Downloading,

	/// <summary>The whole file is checked against its .sha256.</summary>
	Verifying,

	/// <summary>Checked and ready: "Restart to update".</summary>
	Ready,

	/// <summary>Handed to the installer: Nixie is closing.</summary>
	Installing,

	/// <summary>The download failed (<see cref="UpdateStatus.Error"/>).</summary>
	DownloadFailed,

	/// <summary>The install handed over before this start did not happen (<see cref="UpdateStatus.Error"/>).</summary>
	InstallFailed,

	/// <summary>
	/// Review m8: the installer finished with "restart required" (files were in use): Windows completes it at its restart, and
	/// the same install is not offered again meanwhile.
	/// </summary>
	RestartPending,
}

/// <param name="CanSelfUpdate">This copy was installed by the MSI: it downloads and installs; a portable copy shows the release page.</param>
/// <param name="JustUpdated">This start is the version an install brought: "Updated to 1.x · What's new" (its notes).</param>
public sealed record UpdateStatus(UpdatePhase Phase, ReleaseInfo? Release = null, DownloadProgress? Progress = null, string? Error = null,
	DateTimeOffset? CheckedAt = null, DateTimeOffset? RetryAfter = null, bool CanSelfUpdate = false, PendingInstall? JustUpdated = null);

/// <summary>
/// Nixie 1.5: the updater's one state for the app (the owner, 2026-11-20: "download progress time and other things otherwise now
/// you just click check update and wait"). It follows the <see cref="UpdateCoordinator"/>'s checks and, on an installed copy,
/// downloads a newer release as soon as one is found (not on a metered connection, nor after the user cancelled it this run),
/// with its progress, then checks it, and says Ready; a portable copy says Available with the release page. Each step is a
/// <see cref="Status"/>, raised through <see cref="Changed"/> (on any thread). Installing is the app's (<see cref="BeginInstall"/>).
/// </summary>
public sealed class UpdateCenter
{
	private readonly UpdateCoordinator _coordinator;
	private readonly UpdateDownloader _downloader;
	private readonly string _currentVersion;
	private readonly Func<bool> _metered;
	private readonly Func<DateTimeOffset> _clock;
	private readonly object _lock = new();
	private UpdateStatus _status;
	private CancellationTokenSource? _download;
	private Task? _downloadTask;
	private string? _cancelledVersion;

	/// <summary>Review m5: Restart to update found its installer gone: install once the new download is ready.</summary>
	private volatile bool _installWhenReady;

	/// <summary>Review m5: the download Restart to update had to make again is ready: the install can go on.</summary>
	public event Action? ReadyToInstall;

	/// <summary>Review m5: Restart to update waits for its installer's download.</summary>
	public bool WaitingToInstall => _installWhenReady;

	public UpdateCenter(UpdateCoordinator coordinator, UpdateDownloader downloader, string currentVersion, bool canSelfUpdate, Func<bool>? metered = null,
		Func<DateTimeOffset>? clock = null, UpdateState? stored = null)
	{
		_coordinator = coordinator;
		_downloader = downloader;
		_currentVersion = currentVersion;
		_metered = metered ?? (() => false);
		_clock = clock ?? (() => DateTimeOffset.UtcNow);
		CanSelfUpdate = canSelfUpdate;
		var known = stored?.Release is { } r && UpdateVersion.IsNewer(r.Version, currentVersion) ? r.Validated() : null;
		_status = new UpdateStatus(known is null ? UpdatePhase.Idle : UpdatePhase.Available, known, CheckedAt: stored?.LastCheck, CanSelfUpdate: canSelfUpdate);
		coordinator.Checked += OnChecked;
	}

	public bool CanSelfUpdate { get; }

	public UpdateCoordinator Coordinator => _coordinator;

	public UpdateDownloader Downloader => _downloader;

	public UpdateStatus Status
	{
		get
		{
			lock (_lock)
			{
				return _status;
			}
		}
	}

	/// <summary>A new <see cref="Status"/> (from any thread).</summary>
	public event Action<UpdateStatus>? Changed;

	private void Set(Func<UpdateStatus, UpdateStatus> change)
	{
		UpdateStatus next;
		lock (_lock)
		{
			next = change(_status);
			if (next == _status)
			{
				return;
			}

			_status = next;
		}

		Changed?.Invoke(next);
	}

	/// <summary>Whether this release can be downloaded and installed here.</summary>
	private bool Installable(ReleaseInfo release) => CanSelfUpdate && release.HasInstaller;

	/// <summary>
	/// At the start: what the install handed over before it became (Updated: the notes; Failed: why, with the installer kept for
	/// Retry), and an installer downloaded in an earlier run is Ready again without downloading it.
	/// </summary>
	public void Restore(UpdateAftermath aftermath)
	{
		if (aftermath.Kind == AftermathKind.Updated)
		{
			Set(s => s with { JustUpdated = aftermath.Pending });
		}

		var release = Status.Release;
		if (release is null || !Installable(release))
		{
			return;
		}

		// review M1: a failed install is remembered beside its installer, so no later start offers it as ready (and Install
		// when I close Nixie never runs it again quietly) until the user retries or a newer version comes
		if (aftermath.Kind == AftermathKind.Failed && aftermath.Pending?.Version == release.Version)
		{
			WriteMark(release, MarkFailed, aftermath.Reason);
		}

		// review m8: "restart required" is remembered too: not offered again until Windows restarts (which finishes it)
		if (aftermath.Kind == AftermathKind.RestartNeeded && aftermath.Pending?.Version == release.Version)
		{
			WriteMark(release, MarkRestart, aftermath.Reason);
		}

		var mark = ReadMark(release);
		if (mark is { Kind: MarkRestart } restart)
		{
			// Windows restarted since, and this is still the old version: the install did not finish after all
			var restarted = restart.At is { } at && BootTime() > at;
			if (restarted)
			{
				WriteMark(release, MarkFailed, "Windows restarted, but the install didn't finish");
				mark = ReadMark(release);
			}
			else
			{
				Set(s => s with { Phase = UpdatePhase.RestartPending, Progress = null, Error = restart.Reason });
				return;
			}
		}

		if (mark is { Kind: MarkFailed } failed && _downloader.Downloaded(release) is not null)
		{
			Set(s => s with { Phase = UpdatePhase.InstallFailed, Progress = null, Error = failed.Reason });
		}
		else if (mark is { Kind: MarkRefused } refused)
		{
			Set(s => s with { Phase = UpdatePhase.DownloadFailed, Progress = null, Error = refused.Reason });
		}
		else if (_downloader.Downloaded(release) is not null && !_coordinator.IsDismissed(release.Version))
		{
			// review m2: a dismissed version is not Ready by itself (Install when I close Nixie would install it): Available,
			// and the user's Download makes it Ready at once
			Set(s => s with { Phase = UpdatePhase.Ready, Progress = null, Error = null });
		}
	}

	/// <summary>
	/// Review m2: "Dismiss": the version is neither downloaded nor installed by itself any more (a download on its way stops,
	/// a ready one goes back to Available) until a newer one appears or the user asks (Download, Restart to update in
	/// Settings).
	/// </summary>
	public void Dismissed()
	{
		if (Status.Release is not { } release)
		{
			return;
		}

		_cancelledVersion = release.Version;
		CancelDownload();
		Set(s => s.Release?.Version == release.Version && s.Phase is UpdatePhase.Ready ? s with { Phase = UpdatePhase.Available } : s);
	}

	// ---------- what is remembered about the release in hand, beside its installer (<msi>.state): kind, then the reason

	internal const string MarkFailed = "install-failed";

	/// <summary>Review m4: the release's files refused it (checksum, digest, host): not fetched again by itself.</summary>
	internal const string MarkRefused = "download-refused";

	/// <summary>Review m8: installed with "restart required": Windows finishes it at its restart; not offered again meanwhile.</summary>
	internal const string MarkRestart = "restart-required";

	/// <summary>Review m8: when Windows last started (a restart since the install said "restart required" finished it, or not).</summary>
	internal Func<DateTimeOffset> BootTime { get; set; } = () => DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

	private string MarkFile(ReleaseInfo release) => _downloader.TargetFor(release) + ".state";

	private (string Kind, string? Reason, DateTimeOffset? At)? ReadMark(ReleaseInfo release)
	{
		try
		{
			var path = MarkFile(release);
			if (!File.Exists(path))
			{
				return null;
			}

			var lines = File.ReadAllLines(path);
			DateTimeOffset? at = lines.Length > 2 && DateTimeOffset.TryParse(lines[2], System.Globalization.CultureInfo.InvariantCulture,
				System.Globalization.DateTimeStyles.RoundtripKind, out var when) ? when : null;
			return lines.Length == 0 ? null : (lines[0].Trim(), lines.Length > 1 ? lines[1] : null, at);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	private void WriteMark(ReleaseInfo release, string kind, string? reason)
	{
		try
		{
			Directory.CreateDirectory(_downloader.Folder);
			File.WriteAllLines(MarkFile(release), [kind, reason ?? "", _clock().ToString("O", System.Globalization.CultureInfo.InvariantCulture)]);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}

	private void ClearMark(ReleaseInfo release)
	{
		try
		{
			File.Delete(MarkFile(release));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}

	/// <summary>"What's new" was seen (or dismissed): the notice no longer says "Updated to".</summary>
	public void ForgetJustUpdated() => Set(s => s with { JustUpdated = null });

	/// <summary>
	/// Settings' "Check now": says it's checking, then what it found; a newer release on an installed copy starts downloading
	/// at once (as a found update does).
	/// </summary>
	public async Task<UpdateCheckResult> CheckNowAsync(CancellationToken cancellation = default)
	{
		var before = Status;
		// a download on its way, or one in hand (ready, or failed with Retry): the check goes out, but that step stays on
		// screen (a failed check never hides a Restart to update)
		var busy = before.Phase is UpdatePhase.Downloading or UpdatePhase.Verifying or UpdatePhase.Installing
			or UpdatePhase.Ready or UpdatePhase.DownloadFailed or UpdatePhase.InstallFailed or UpdatePhase.RestartPending;
		// review m3: a known update stays on screen through a check, with its buttons; a failure is said beside it
		var known = before.Phase == UpdatePhase.Available && before.Release is not null;
		busy |= known;
		if (!busy)
		{
			Set(s => s with { Phase = UpdatePhase.Checking, Error = null, RetryAfter = null });
		}

		UpdateCheckResult result;
		try
		{
			result = await _coordinator.CheckNowAsync(cancellation).ConfigureAwait(false);
		}
		catch (Exception) when (!cancellation.IsCancellationRequested)
		{
			result = new UpdateCheckResult(UpdateCheckStatus.Failed, Reason: "the check did not finish");
		}

		// Available and UpToDate were taken in by OnChecked (the coordinator raised Checked), unless the answer was reused
		switch (result.Status)
		{
			case UpdateCheckStatus.Available or UpdateCheckStatus.UpToDate:
				OnChecked(result);
				break;
			case UpdateCheckStatus.RateLimited or UpdateCheckStatus.Failed when known:
				Set(s => s.Phase == UpdatePhase.Available && s.Release?.Version == before.Release!.Version
					? s with { Error = UpdateCheckMessage.For(result, _currentVersion).Text, RetryAfter = result.RetryAfter }
					: s);
				break;
			case UpdateCheckStatus.RateLimited or UpdateCheckStatus.Failed when !busy:
				Set(s => s with
				{
					Phase = s.Phase == UpdatePhase.Checking ? UpdatePhase.CheckFailed : s.Phase,
					Error = UpdateCheckMessage.For(result, _currentVersion).Text,
					RetryAfter = result.RetryAfter,
				});
				break;
		}

		return result;
	}

	/// <summary>What a check found (a timer's, a return's or Check now's). Failures stay silent here: Check now says them.</summary>
	private void OnChecked(UpdateCheckResult result)
	{
		var now = _clock();
		if (result.Status == UpdateCheckStatus.UpToDate)
		{
			Set(s => s.Phase is UpdatePhase.Downloading or UpdatePhase.Verifying or UpdatePhase.Installing or UpdatePhase.RestartPending
				? s with { CheckedAt = now }
				: s with { Phase = UpdatePhase.UpToDate, Release = null, Progress = null, Error = null, CheckedAt = now, RetryAfter = null });
			return;
		}

		// a failure: silent here (a timer's stays silent; Check now, which may share the same request, says why itself)
		if (result.Status != UpdateCheckStatus.Available || result.Release?.Validated() is not { } release)
		{
			return;
		}

		var current = Status;
		var same = current.Release?.Version == release.Version;
		// the steps a re-check of the same version leaves alone (review M1: a failed install stays failed, with Retry)
		if (same && current.Phase is UpdatePhase.Downloading or UpdatePhase.Verifying or UpdatePhase.Ready or UpdatePhase.Installing
			or UpdatePhase.InstallFailed or UpdatePhase.RestartPending)
		{
			Set(s => s with { CheckedAt = now });
			return;
		}

		if (!same)
		{
			CancelDownload();
		}

		var dismissed = _coordinator.IsDismissed(release.Version);   // review m2: not downloaded, nor Ready, by itself
		if (Installable(release) && !dismissed && _downloader.Downloaded(release) is not null)
		{
			Set(s => s with { Phase = UpdatePhase.Ready, Release = release, Progress = null, Error = null, CheckedAt = now, RetryAfter = null });
			return;
		}

		// review m4: refused by its own files before: said again, not fetched again (the user's Retry fetches it)
		if (Installable(release) && ReadMark(release) is { Kind: MarkRefused } refused)
		{
			Set(s => s with { Phase = UpdatePhase.DownloadFailed, Release = release, Progress = null, Error = refused.Reason, CheckedAt = now, RetryAfter = null });
			return;
		}

		Set(s => s with { Phase = UpdatePhase.Available, Release = release, Progress = null, Error = null, CheckedAt = now, RetryAfter = null });
		if (Installable(release) && !dismissed && _cancelledVersion != release.Version && !_metered())
		{
			StartDownload();
		}
	}

	/// <summary>Whether a found update downloads by itself now: an installed copy, an installer, not cancelled, not metered.</summary>
	public bool DownloadsByItself(ReleaseInfo release) => Installable(release) && !_coordinator.IsDismissed(release.Version) && _cancelledVersion != release.Version && !_metered();

	/// <summary>"Download" or "Retry": downloads the release found (an installed copy only); the same download is never started twice.</summary>
	public Task StartDownload()
	{
		lock (_lock)
		{
			if (_downloadTask is { IsCompleted: false } running)
			{
				return running;
			}

			if (_status.Release is not { } release || !Installable(release))
			{
				return Task.CompletedTask;
			}

			_cancelledVersion = null;
			ClearMark(release);   // Download or Retry: the user asks again (a refused release is fetched once more)
			_download?.Dispose();
			_download = new CancellationTokenSource();
			var token = _download.Token;
			_status = _status with { Phase = UpdatePhase.Downloading, Progress = new DownloadProgress(0, release.MsiSize, null, null), Error = null };
			var started = _status;
			_downloadTask = Task.Run(async () =>
			{
				Changed?.Invoke(started);
				await DownloadAsync(release, token).ConfigureAwait(false);
			});
			return _downloadTask;
		}
	}

	private async Task DownloadAsync(ReleaseInfo release, CancellationToken token)
	{
		var progress = new Progress(p => Set(s => s.Phase == UpdatePhase.Downloading && s.Release?.Version == release.Version ? s with { Progress = p } : s));
		try
		{
			await _downloader.DownloadAsync(release, progress, () => Set(s => s.Release?.Version == release.Version ? s with { Phase = UpdatePhase.Verifying } : s), token)
				.ConfigureAwait(false);
			Set(s => s.Release?.Version == release.Version ? s with { Phase = UpdatePhase.Ready, Progress = null, Error = null } : s);
			if (_installWhenReady && Status is { Phase: UpdatePhase.Ready } ready && ready.Release?.Version == release.Version)
			{
				_installWhenReady = false;
				ReadyToInstall?.Invoke();
			}
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			_installWhenReady = false;
			Set(s => s.Release?.Version == release.Version && s.Phase is UpdatePhase.Downloading or UpdatePhase.Verifying
				? s with { Phase = UpdatePhase.Available, Progress = null } : s);
		}
		catch (UpdateException ex)
		{
			_installWhenReady = false;
			// review m4: a release its own files refuse is not fetched again by itself (every check fetched it twice more)
			if (ex.Integrity)
			{
				WriteMark(release, MarkRefused, ex.Message);
			}

			Set(s => s.Release?.Version == release.Version ? s with { Phase = UpdatePhase.DownloadFailed, Progress = null, Error = ex.Message } : s);
		}
		catch (Exception)
		{
			_installWhenReady = false;
			Set(s => s.Release?.Version == release.Version ? s with { Phase = UpdatePhase.DownloadFailed, Progress = null, Error = "The download stopped." } : s);
		}
	}

	/// <summary>"Cancel": the download stops (its part stays, so a later Download resumes it) and does not start again by itself this run.</summary>
	public void Cancel()
	{
		if (Status.Release is { } release)
		{
			_cancelledVersion = release.Version;
		}

		CancelDownload();
	}

	private void CancelDownload()
	{
		lock (_lock)
		{
			_download?.Cancel();
		}
	}

	/// <summary>
	/// "Restart to update": the checked installer, checked once more, and the phase Installing; null (and the phase as it was)
	/// when there is none ready.
	/// </summary>
	public string? BeginInstall()
	{
		if (Status is not { Phase: UpdatePhase.Ready or UpdatePhase.InstallFailed, Release: { } release } || !Installable(release))
		{
			return null;
		}

		var msi = _downloader.Downloaded(release);
		if (msi is null)
		{
			// review m5: the installer went (a cleanup, an antivirus): it is downloaded again, shown as any download, and
			// ReadyToInstall says when the install can go on
			_installWhenReady = true;
			Set(s => s with { Phase = UpdatePhase.Available });
			StartDownload();
			return null;
		}

		ClearMark(release);   // the user retries a failed install: it is no longer remembered as failed
		Set(s => s with { Phase = UpdatePhase.Installing, Error = null });
		return msi;
	}

	/// <summary>
	/// <see cref="UpdateHandoff.InstallSwitch"/>: an installer given on the command line (checked by
	/// <see cref="UpdateHandoff.LocalInstaller"/>) is copied into the updates folder with its checksum and is Ready, as a
	/// download would be. False when this copy cannot install (portable) or the copy fails.
	/// </summary>
	public bool UseLocalInstaller(ReleaseInfo release, string msiPath)
	{
		if (!Installable(release) || release.MsiName is not { } name || release.MsiDigest is not { } hash)
		{
			return false;
		}

		try
		{
			Directory.CreateDirectory(_downloader.Folder);
			var target = _downloader.TargetFor(release);
			if (!string.Equals(Path.GetFullPath(msiPath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
			{
				File.Copy(msiPath, target, overwrite: true);
			}

			File.WriteAllText(target + UpdateDownloader.HashSuffix, $"{hash} *{name}\n");
			if (_downloader.Downloaded(release) is null)
			{
				return false;
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return false;
		}

		Set(s => s with { Phase = UpdatePhase.Ready, Release = release, Progress = null, Error = null });
		return true;
	}

	/// <summary>The install could not be handed over (the helper did not start): back to Ready, with why.</summary>
	public void InstallNotStarted(string reason) => Set(s => s with { Phase = UpdatePhase.InstallFailed, Error = reason });

	/// <summary>
	/// The updates folder holds only what the release in hand needs (its installer, part and checksum) and the handoff's own
	/// files: installers of other versions go.
	/// </summary>
	public void Tidy()
	{
		try
		{
			var keep = Status.Release is { MsiName: { } name } ? name : null;
			foreach (var file in Directory.EnumerateFiles(_downloader.Folder))
			{
				var fileName = Path.GetFileName(file);
				var ours = fileName is UpdateHandoff.PendingFile or UpdateHandoff.ResultFile or UpdateHandoff.LogFile or UpdateHandoff.HelperName or UpdateHandoff.ReopenRequestFile;
				var current = keep is not null && fileName.StartsWith(keep, StringComparison.OrdinalIgnoreCase);
				if (!ours && !current && (fileName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(UpdateDownloader.PartSuffix, StringComparison.OrdinalIgnoreCase)
					|| fileName.EndsWith(UpdateDownloader.HashSuffix, StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".msi.state", StringComparison.OrdinalIgnoreCase)))
				{
					File.Delete(file);
				}
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}

	/// <summary>A synchronous IProgress: Progress&lt;T&gt; would post to a context that may not be there.</summary>
	private sealed class Progress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
	{
		public void Report(DownloadProgress value) => report(value);
	}
}
