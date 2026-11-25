using System.Diagnostics;
using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.Updates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace FluentFin.Services;

/// <summary>
/// Seaglass: the update notice (Settings > Check for updates; Nixie 1.3.1: on by default). A timer ticks every minute, the first
/// tick about 20 s after the start; the work is in <see cref="UpdateCoordinator"/> (nothing goes out while the setting is off).
/// The notice is in the shell (ShellPage). This replaces upstream FluentFin's WindowsUpdateService, which fetched upstream's
/// installer; nothing here can name any release but this repository's (<see cref="UpdateSource"/>).
/// Nixie 1.5 (the updater stream): a check when Nixie opens, every 4 hours while it stays open, and when the window comes back
/// after an hour or more away (it was inactive, or the PC slept: a minute's tick that comes much later than a minute). An
/// installed copy downloads a found update in the background (<see cref="UpdateCenter"/>: progress, its .sha256 and GitHub's
/// digest, resume) and installs it on "Restart to update" through the launcher's helper, which reopens Nixie
/// (<see cref="UpdateHandoff"/>), or as Nixie closes when the user turned that on. The next start says "Updated to" or why not.
/// </summary>
public sealed class UpdateNoticeService : IHostedService, IDisposable
{
	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
	private readonly ILogger<UpdateNoticeService> _logger;
	private readonly ILocalSettingsService _local;
	private readonly string _updates;
	private readonly string? _customDataDir;
	private readonly bool _harness;
	private CancellationTokenSource? _stop;
	private Task? _loop;
	private bool _handedOver;
	private DateTimeOffset? _inactiveSince;

	/// <summary>Nixie 1.5: a tick this late after the one before means the PC slept (or the process was suspended) meanwhile.</summary>
	internal static readonly TimeSpan SleptAfter = TimeSpan.FromMinutes(3);

	public UpdateNoticeService(ISettings settings, ILocalSettingsService local, KnownFolders folders, ILogger<UpdateNoticeService> logger)
	{
		_logger = logger;
		_local = local;
		_updates = folders.Updates;
		_customDataDir = folders.IsCustom ? folders.ApplicationData : null;
		// Nixie 1.3.1: the harness's dev runs ask its fake server (NIXIE_UPDATE_API), never GitHub; a release always asks GitHub
		_harness = Environment.GetEnvironmentVariable(GlassActivity.AlwaysActiveVariable) == "1" && !AppVersion.IsReleasePackage;
		var api = UpdateSource.ApiFor(AppIdentity.ReadEnvironment(UpdateSource.ApiVariable), harnessOn: _harness, releasePackage: AppVersion.IsReleasePackage);
		var harnessOrigin = api.IsLoopback ? new Uri(api.GetLeftPart(UriPartial.Authority)) : null;
		Coordinator = new UpdateCoordinator(settings, new UpdateChecker(Http, AppVersion.Current, api), () => DateTimeOffset.UtcNow, AppVersion.Current);
		Coordinator.Checked += result => _logger.LogInformation("[update] checked {Host}: {Status}{Version}{Reason}", api.Host, result.Status,
			result.Release is { } r ? $" {r.Version}" : "", result.Reason is { } why ? $" ({why})" : "");
		var downloader = new UpdateDownloader(Http, _updates, AppVersion.Current, harnessOrigin);
		downloader.Noted += note => _logger.LogInformation("[update] {Note}", note);
		CanSelfUpdate = IsMsiInstall() || (_harness && harnessOrigin is not null && AppIdentity.ReadEnvironment(PretendInstalledVariable) == "1");
		Center = new UpdateCenter(Coordinator, downloader, AppVersion.Current, CanSelfUpdate, IsMetered, stored: UpdateState.Parse(settings.UpdateState));
		Center.Changed += status => _logger.LogDebug("[update] {Phase} {Version}{Error}", status.Phase, status.Release?.Version, status.Error is { } e ? $": {e}" : "");
		Settings = new UpdateSettings(local, CanSelfUpdate);
		Center.ReadyToInstall += OnReadyToInstall;   // review m5
	}

	/// <summary>
	/// Nixie 1.5: <c>NIXIE_UPDATE_INSTALLED=1</c> makes a harness run (a dev build pointed at the fake server) behave as an
	/// installed copy, so its notice and Settings show the download, Ready and Restart to update. A release ignores it.
	/// </summary>
	public const string PretendInstalledVariable = "UPDATE_INSTALLED";

	public UpdateCoordinator Coordinator { get; }

	/// <summary>Nixie 1.5: the updater's one state (the notice and Settings show it).</summary>
	public UpdateCenter Center { get; }

	/// <summary>Nixie 1.5: Settings > Install when I close Nixie.</summary>
	public UpdateSettings Settings { get; }

	/// <summary>Nixie 1.5: an installed copy (it downloads and installs) rather than a portable one (the release page).</summary>
	public bool CanSelfUpdate { get; }

	/// <summary>Whether this copy was installed by the MSI (it can update itself) rather than unzipped (portable).</summary>
	public static bool IsMsiInstall()
	{
		try
		{
			using var key = Registry.CurrentUser.OpenSubKey(UpdateSource.RegistryKey);
			return InstallDetector.IsMsiInstall(AppContext.BaseDirectory, key?.GetValue("InstallFolder") as string);
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>Nixie 1.5: a metered connection (a phone's hotspot, a data plan): a found update waits for the Download click.</summary>
	private static bool IsMetered()
	{
		try
		{
			var cost = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile()?.GetConnectionCost();
			return cost is not null && (cost.NetworkCostType is Windows.Networking.Connectivity.NetworkCostType.Fixed or Windows.Networking.Connectivity.NetworkCostType.Variable
				|| cost.Roaming || cost.OverDataLimit);
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>A release page (of this repository, or of the one a rename led to) in the browser; nothing else is ever opened.</summary>
	public static void OpenPage(string url)
	{
		if (UpdateSource.RepositoryOf(url) is not null)
		{
			Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
		}
	}

	// ---------- "Restart to update" and "Install when I close Nixie"

	/// <summary>
	/// Nixie 1.5: "Restart to update": the checked installer goes to the helper, which waits for this Nixie to end, installs it
	/// with Windows' progress bar and opens Nixie again (<paramref name="resume"/>: where to come back to); then this window
	/// closes cleanly (the stop report, the backend, the settings). Without the helper (a copy with no launcher beside it), the
	/// installer runs as before 1.5: its own window, and Nixie is opened from its last page.
	/// </summary>
	public void RestartToUpdate(ResumePlace? resume, Action? beforeClose = null)
	{
		if (Hand(reopen: true, quiet: false, resume, out var waiting) is { } failure)
		{
			Center.InstallNotStarted(failure);
			return;
		}

		if (waiting)
		{
			// review m5: the installer had gone: its download shows (the notice, Settings), and the restart follows it
			_restartWhenReady = true;
			_restartResume = resume;
			return;
		}

		CloseForUpdate(beforeClose);
	}

	private volatile bool _restartWhenReady;
	private ResumePlace? _restartResume;

	/// <summary>Review m5: the download Restart to update had to make again is ready: the restart goes on.</summary>
	private void OnReadyToInstall()
	{
		if (_restartWhenReady)
		{
			_restartWhenReady = false;
			var resume = _restartResume;
			App.MainWindow.DispatcherQueue.TryEnqueue(() => RestartToUpdate(resume));
		}
	}

	/// <summary>
	/// The handoff; null when the helper (or the installer) started, or when <paramref name="waiting"/> (review m5: the
	/// installer was gone and downloads again first), else why it didn't.
	/// </summary>
	private string? Hand(bool reopen, bool quiet, ResumePlace? resume, out bool waiting)
	{
		waiting = false;
		if (_handedOver)
		{
			return null;
		}

		var release = Center.Status.Release;
		// review m8 (the second window): another Nixie of this install keeps its files in use, and the installer would leave the
		// update to a Windows restart: it is closed first (only read here: nothing else is ever ended)
		// (not in a harness run, which installs nothing and may share its build with parallel runs on other data folders)
		var pretend = _harness && AppIdentity.ReadEnvironment(PretendInstalledVariable) == "1";
		if (!pretend && OtherNixieWindows() is { Length: > 0 } others)
		{
			_logger.LogInformation("[update] not handed over: {Count} other Nixie process(es) of this install are running", others.Length);
			return others.Length == 1
				? $"another {AppIdentity.Name} window is open; close it, then try again"
				: $"{others.Length} other {AppIdentity.Name} windows are open; close them, then try again";
		}

		if (Center.BeginInstall() is not { } msi || release is null)
		{
			waiting = Center.WaitingToInstall;
			return waiting ? null : $"the download isn't ready; {AppIdentity.Name} is getting it again";
		}

		try
		{
			UpdateHandoff.WritePending(_updates, new PendingInstall(release.Version, AppVersion.Current, DateTimeOffset.UtcNow, release.PageUrl, release.Notes, reopen, resume));
			using var self = Process.GetCurrentProcess();
			var app = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, AppIdentity.Name + ".exe");
			// the reopened Nixie gets this one's arguments, without a --install-update (it must not install again)
			var arguments = UpdateHandoff.ArgumentsForReopen([.. Environment.GetCommandLineArgs().Skip(1)], _customDataDir);   // review m7: its data folder in full
			if (_harness && AppIdentity.ReadEnvironment(PretendInstalledVariable) == "1")
			{
				// a harness run never installs: the handoff it would make is written down for the harness to check
				var plan = UpdateHandoff.HelperStart(Path.Combine(_updates, UpdateHandoff.HelperName), msi, _updates, self.Id, UpdateHandoff.CreationTime(self), app, arguments, reopen, quiet);
				File.WriteAllLines(Path.Combine(_updates, "harness-handoff.txt"), plan.ArgumentList);
			}
			else if (UpdateHandoff.LauncherFor(AppContext.BaseDirectory) is { } launcher)
			{
				var helper = Path.Combine(_updates, UpdateHandoff.HelperName);
				File.Copy(launcher, helper, overwrite: true);   // the installer replaces the launcher: the helper runs from a copy
				using var started = Process.Start(UpdateHandoff.HelperStart(helper, msi, _updates, self.Id, UpdateHandoff.CreationTime(self), app, arguments, reopen, quiet));
				if (started is null)
				{
					throw new InvalidOperationException("the helper did not start");
				}

				AllowSetForegroundWindow((uint)started.Id);
			}
			else
			{
				using var _ = Process.Start(UpdateFlow.MsiexecInstall(msi));
			}

			_handedOver = true;
			_logger.LogInformation("[update] {Version} handed to the installer (reopen {Reopen}, quiet {Quiet})", release.Version, reopen, quiet);
			return null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or UpdateException)
		{
			_logger.LogWarning("[update] the installer could not be started: {Message}", ex.Message);
			return "the installer couldn't be started";
		}
	}

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern bool AllowSetForegroundWindow(uint processId);

	/// <summary>Review m8: the other running processes of this very Nixie.exe (read only; a harness run has none to look for).</summary>
	private static int[] OtherNixieWindows()
	{
		var app = Environment.ProcessPath;
		if (app is null)
		{
			return [];
		}

		var running = new List<(int, string?)>();
		foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(app)))
		{
			using (process)
			{
				string? path = null;
				try
				{
					path = process.MainModule?.FileName;
				}
				catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
				{
				}

				running.Add((process.Id, path));
			}
		}

		return UpdateHandoff.OtherInstances(running, Environment.ProcessId, app);
	}

	/// <summary>
	/// Once the installer is handed over: tell the user (the bar), then close the window, which runs the app's own clean close (the
	/// stop report, the backend and its mpv window, the settings flushed) so the installer finds no file locked.
	/// </summary>
	private static void CloseForUpdate(Action? beforeClose)
	{
		var window = App.MainWindow;
		window.DispatcherQueue.TryEnqueue(() =>
		{
			beforeClose?.Invoke();
			window.Close();
		});
	}

	// ---------- the loop

	public Task StartAsync(CancellationToken cancellationToken)
	{
		// Nixie 1.5: a helper is waiting or installing (Nixie was opened again meanwhile): this start ends at once, before any
		// window, and the helper opens Nixie when the install is done. 1.7.5: only for an install of this copy (another copy's
		// helper never reads this one's request: it ended and never came back): one over this program folder, or, from a helper
		// before 1.8's, the installed copy's (the MSI's, not a harness run pretending) or the one this data folder handed over
		if (UpdateHandoff.StartEndsForInstall(_updates, AppContext.BaseDirectory, IsMsiInstall(), out var anotherCopys))
		{
			try
			{
				UpdateHandoff.RequestReopen(_updates);
				_logger.LogInformation("[update] an install is under way: this start ends, and Nixie opens when it is done");
				Serilog.Log.CloseAndFlush();
				Environment.Exit(0);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				_logger.LogWarning("[update] an install is under way, and its reopen could not be asked for: {Message}", ex.Message);
			}
		}
		else if (anotherCopys)
		{
			_logger.LogInformation("[update] another copy's install is under way (not this data folder's): this start goes on");
		}

		ReadAftermath();
		Center.Tidy();
		_stop = new CancellationTokenSource();
		_loop = Task.Run(() => RunAsync(_stop.Token));
		App.MainWindow.Activated += OnWindowActivated;
		InstallFromCommandLine(_stop.Token);
		return Task.CompletedTask;
	}

	/// <summary>
	/// Nixie 1.5: <c>--install-update &lt;msi&gt;</c> (the upgrade test in Windows Sandbox drives "Restart to update" with it): an
	/// installed copy takes that installer, checked against the .sha256 beside it, as a ready update and restarts to install
	/// it, once the window is up. Refused (and logged) on a portable copy, in a release package without the test's marker
	/// (BH-150-m6), or for anything else.
	/// </summary>
	private void InstallFromCommandLine(CancellationToken token)
	{
		if (UpdateHandoff.LocalInstaller([.. Environment.GetCommandLineArgs().Skip(1)], AppVersion.Current, AppVersion.IsReleasePackage,
			Environment.GetEnvironmentVariable(UpdateHandoff.TestMarkerVariable), out var msi, out var error) is not { } release)
		{
			if (error is not null)
			{
				_logger.LogWarning("[update] {Switch} refused: {Error}", UpdateHandoff.InstallSwitch, error);
			}

			return;
		}

		if (!Center.UseLocalInstaller(release, msi!))
		{
			_logger.LogWarning("[update] {Switch} {Version} refused: this copy can't install updates", UpdateHandoff.InstallSwitch, release.Version);
			return;
		}

		_logger.LogInformation("[update] {Switch}: {Version} is ready; restarting to update", UpdateHandoff.InstallSwitch, release.Version);
		_ = Task.Run(async () =>
		{
			// after the window is up and the first page shown, as a click would come
			await Task.Delay(TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
			App.MainWindow.DispatcherQueue.TryEnqueue(() => RestartToUpdate(new ResumePlace(typeof(Core.ViewModels.HomeViewModel).FullName!)));
		}, token);
	}

	/// <summary>Nixie 1.5: what the install handed over before this start became ("Updated to", or why not), said once.</summary>
	private void ReadAftermath()
	{
		var pending = UpdateHandoff.ReadPending(_updates);
		if (pending is null)
		{
			return;
		}

		var aftermath = UpdateAftermath.Read(pending, UpdateHandoff.ReadResult(_updates), AppVersion.Current, DateTimeOffset.UtcNow, UpdateHandoff.InstallRunning());
		_logger.LogInformation("[update] the install of {Version} from {From}: {Kind}{Reason} (installer exit {Exit})", pending.Version, pending.FromVersion, aftermath.Kind,
			aftermath.Reason is { } why ? $" ({why})" : "", UpdateHandoff.ReadResult(_updates)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none");
		Resume = aftermath.Kind == AftermathKind.Updated || aftermath.Kind == AftermathKind.Failed ? pending.Resume : null;
		Center.Restore(aftermath);
		if (aftermath.Kind != AftermathKind.Installing)
		{
			foreach (var file in new[] { UpdateHandoff.PendingFile, UpdateHandoff.ResultFile, UpdateHandoff.ReopenRequestFile })
			{
				TryDelete(Path.Combine(_updates, file));
			}
		}

		if (aftermath.Kind == AftermathKind.Updated)
		{
			// the installer did its work: it is not kept
			foreach (var file in Directory.EnumerateFiles(_updates, $"{AppIdentity.Name}-{pending.Version}-win-x64.msi*"))
			{
				TryDelete(file);
			}
		}
	}

	/// <summary>Nixie 1.5: where to come back to after the update (the shell takes it once).</summary>
	public ResumePlace? Resume { get; private set; }

	public ResumePlace? TakeResume()
	{
		var resume = Resume;
		Resume = null;
		return resume;
	}

	private static void TryDelete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}

	/// <summary>Nixie 1.5: the window coming back after an hour or more inactive asks again (the PC's sleep: <see cref="RunAsync"/>).</summary>
	private void OnWindowActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
	{
		if (args.WindowActivationState == Microsoft.UI.Xaml.WindowActivationState.Deactivated)
		{
			_inactiveSince ??= DateTimeOffset.UtcNow;
			return;
		}

		if (_inactiveSince is { } since)
		{
			_inactiveSince = null;
			Returned(DateTimeOffset.UtcNow - since, "the window came back");
		}
	}

	private void Returned(TimeSpan away, string why)
	{
		if (away < UpdateGate.ReturnAfter || _stop is not { } stop)
		{
			return;
		}

		_ = Task.Run(async () =>
		{
			try
			{
				if (await Coordinator.ReturnedAsync(away, stop.Token).ConfigureAwait(false) is not null)
				{
					_logger.LogInformation("[update] {Why} after {Minutes} min: checked", why, (int)away.TotalMinutes);
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				_logger.LogDebug("Update check on return: {Message}", ex.Message);
			}
			catch (OperationCanceledException)
			{
			}
		});
	}

	private async Task RunAsync(CancellationToken token)
	{
		try
		{
			// not while the app starts: the first tick comes after the window is up
			await Task.Delay(TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
			using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
			var last = DateTimeOffset.UtcNow;
			do
			{
				// Nixie 1.5: a tick that comes long after the one before: the PC slept (resume), a return like the window's
				var now = DateTimeOffset.UtcNow;
				if (now - last >= SleptAfter)
				{
					Returned(now - last, "the PC woke");
				}

				last = now;
				try
				{
					await Coordinator.TickAsync(token).ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (token.IsCancellationRequested)
				{
					throw;
				}
				catch (Exception ex)
				{
					_logger.LogDebug("Update check: {Message}", ex.Message);   // silent for the user
				}
			}
			while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
		}
		catch (OperationCanceledException)
		{
		}
	}

	public async Task StopAsync(CancellationToken cancellationToken)
	{
		App.MainWindow.Activated -= OnWindowActivated;
		// Nixie 1.5: "Install when I close Nixie" (off by default): a checked installer is handed over quietly, nothing reopens
		if (!_handedOver && Settings.InstallOnClose && Center.Status.Phase == UpdatePhase.Ready && !Coordinator.IsDismissed(Center.Status.Release?.Version))
		{
			if (Hand(reopen: false, quiet: true, resume: null, out _) is { } failure)
			{
				_logger.LogWarning("[update] not installed as Nixie closes: {Failure}", failure);
			}
		}

		Center.Cancel();
		_stop?.Cancel();
		if (_loop is not null)
		{
			await Task.WhenAny(_loop, Task.Delay(1000, cancellationToken)).ConfigureAwait(false);
		}
	}

	public void Dispose() => _stop?.Dispose();
}
