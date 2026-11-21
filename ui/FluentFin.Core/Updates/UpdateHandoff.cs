using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace FluentFin.Core.Updates;

/// <summary>
/// Nixie 1.5: "Restart to update". Nixie cannot replace its own files while it runs, so it hands the install to a helper: a copy
/// of the release's launcher (<c>&lt;install&gt;\Nixie.exe</c>, <c>tools\release\launcher\Launcher.cs</c>) put in Nixie's data
/// folder (<see cref="KnownFolders.Updates"/>, so the installer never finds it in use), started with <see cref="Switch"/>. The
/// helper waits for this Nixie to end (its process id and start time: a reused id is never waited on), runs
/// <c>msiexec /i &lt;msi&gt; /passive /norestart /l*v &lt;log&gt;</c> (<c>/qn</c> when it installs as Nixie closes), writes
/// msiexec's exit code to <see cref="ResultFile"/>, and opens Nixie again with the same arguments (unless it installs as Nixie
/// closes). The next start reads what happened (<see cref="UpdateAftermath"/>). This class and Launcher.cs speak the same
/// arguments; a test runs the real launcher against a stand-in msiexec.
/// </summary>
public static class UpdateHandoff
{
	/// <summary>The launcher's argument that makes it the install helper.</summary>
	public const string Switch = "--nixie-install-update";

	/// <summary>The helper's copy in the updates folder.</summary>
	public const string HelperName = AppIdentity.Name + "-updater.exe";

	/// <summary>What Nixie wrote before handing over: the version, where to come back to, the notes (<see cref="PendingInstall"/>).</summary>
	public const string PendingFile = "pending.json";

	/// <summary>The helper's word on the install: "exit=&lt;msiexec's exit code&gt;".</summary>
	public const string ResultFile = "install-result.txt";

	/// <summary>
	/// Written by a Nixie started while the helper waits or installs: that Nixie ends at once (its files are being replaced) and
	/// the helper opens Nixie when the install is done.
	/// </summary>
	public const string ReopenRequestFile = "reopen.request";

	/// <summary>A Nixie started during an install asks the helper to open it afterwards (<see cref="ReopenRequestFile"/>).</summary>
	public static void RequestReopen(string updatesFolder) =>
		File.WriteAllText(Path.Combine(updatesFolder, ReopenRequestFile), DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));

	/// <summary>msiexec's verbose log of the install.</summary>
	public const string LogFile = "install.log";

	/// <summary>Held by the helper while it waits and installs: a Nixie started meanwhile knows an install is under way.</summary>
	public const string MutexName = @"Local\Nixie-update-install";

	/// <summary>The launcher of an installed copy: <c>Nixie.exe</c> in the folder above the app's (<c>&lt;install&gt;\app\</c>); null when there is none.</summary>
	public static string? LauncherFor(string appBaseDirectory)
	{
		try
		{
			var launcher = Path.GetFullPath(Path.Combine(appBaseDirectory, "..", AppIdentity.Name + ".exe"));
			return File.Exists(launcher) ? launcher : null;
		}
		catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
		{
			return null;
		}
	}

	/// <summary>
	/// How to start the helper: install <paramref name="msi"/> once the process <paramref name="processId"/> (created at
	/// <paramref name="processCreated"/>, a FILETIME as <see cref="CreationTime"/> reads it) has ended, writing the log and the result into <paramref name="updatesFolder"/>; then
	/// open <paramref name="app"/> with <paramref name="appArguments"/> when <paramref name="reopen"/>, or when a Nixie started
	/// during the install asked for it (<see cref="ReopenRequestFile"/>). <paramref name="quiet"/> installs with no window at all
	/// (<c>/qn</c>, as Nixie closes); otherwise Windows' own progress bar shows (<c>/passive</c>).
	/// </summary>
	public static ProcessStartInfo HelperStart(string helper, string msi, string updatesFolder, int processId, long processCreated,
		string? app, IReadOnlyList<string> appArguments, bool reopen, bool quiet)
	{
		if (!msi.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
		{
			throw new UpdateException("Not an installer.");
		}

		var info = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = updatesFolder };
		info.ArgumentList.Add(Switch);
		info.ArgumentList.Add("--msi");
		info.ArgumentList.Add(msi);
		info.ArgumentList.Add("--log");
		info.ArgumentList.Add(Path.Combine(updatesFolder, LogFile));
		info.ArgumentList.Add("--result");
		info.ArgumentList.Add(Path.Combine(updatesFolder, ResultFile));
		info.ArgumentList.Add("--wait-pid");
		info.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
		info.ArgumentList.Add("--wait-started");
		info.ArgumentList.Add(processCreated.ToString(CultureInfo.InvariantCulture));
		if (quiet)
		{
			info.ArgumentList.Add("--quiet");
		}

		if (app is not null)
		{
			if (reopen)
			{
				info.ArgumentList.Add("--reopen");
			}

			info.ArgumentList.Add("--app");
			info.ArgumentList.Add(app);
			info.ArgumentList.Add("--");
			foreach (var argument in appArguments)
			{
				info.ArgumentList.Add(argument);
			}
		}

		return info;
	}

	[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

	/// <summary>
	/// The process's creation time exactly as Windows keeps it (a FILETIME, UTC), as the helper compares it: Process.StartTime is
	/// local time, and its round trip can be an hour off in the autumn's repeated hour. 0 when it cannot be read.
	/// </summary>
	public static long CreationTime(Process process)
	{
		try
		{
			return GetProcessTimes(process.Handle, out var creation, out _, out _, out _) ? creation : 0;
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
		{
			return 0;
		}
	}

	/// <summary>
	/// <c>Nixie.exe --install-update &lt;msi&gt;</c>: the upgrade test in Windows Sandbox (tools\release\upgrade-test-sandbox.ps1)
	/// drives "Restart to update" with it, never with clicks: an installed copy takes the installer named, with its .sha256 beside
	/// it, as a downloaded update and restarts to install it. It grants nothing a user could not do with msiexec.
	/// </summary>
	public const string InstallSwitch = "--install-update";

	/// <summary>The arguments without <see cref="InstallSwitch"/> and its installer (the reopened Nixie must not install again).</summary>
	public static string[] WithoutInstallSwitch(IReadOnlyList<string> arguments)
	{
		var rest = new List<string>();
		for (var i = 0; i < arguments.Count; i++)
		{
			if (string.Equals(arguments[i], InstallSwitch, StringComparison.OrdinalIgnoreCase))
			{
				i++;
				continue;
			}

			rest.Add(arguments[i]);
		}

		return [.. rest];
	}

	/// <summary>
	/// Review m7: the arguments the reopened Nixie gets: this one's, without <see cref="InstallSwitch"/>, and with its data folder
	/// (<paramref name="customDataDir"/>, the full path this run resolved; null for the default folder) given as a full path.
	/// The helper opens Nixie from the app's folder, so a relative --data-dir (or FLUENTFIN_MPV_DATA_DIR, which the reopened
	/// Nixie inherits) would name another folder there; the switch wins over the variable.
	/// </summary>
	public static string[] ArgumentsForReopen(IReadOnlyList<string> arguments, string? customDataDir)
	{
		var rest = KnownFolders.WithoutDataDir(WithoutInstallSwitch(arguments));
		return customDataDir is null ? rest : [KnownFolders.DataDirSwitch, Path.GetFullPath(customDataDir), .. rest];
	}

	/// <summary>
	/// The installer <see cref="InstallSwitch"/> names, as a release ready to install: its name must be
	/// <c>Nixie-&lt;version&gt;-win-x64.msi</c> of a version newer than <paramref name="currentVersion"/>, and the
	/// <c>.sha256</c> beside it must name it and match it. Null with no switch; <paramref name="error"/> says why one is refused.
	/// </summary>
	public static ReleaseInfo? LocalInstaller(IReadOnlyList<string> arguments, string currentVersion, out string? msiPath, out string? error)
	{
		msiPath = null;
		error = null;
		var at = -1;
		for (var i = 0; i < arguments.Count; i++)
		{
			if (string.Equals(arguments[i], InstallSwitch, StringComparison.OrdinalIgnoreCase))
			{
				at = i;
			}
		}

		if (at < 0)
		{
			return null;
		}

		if (at + 1 >= arguments.Count)
		{
			error = $"{InstallSwitch} needs an installer";
			return null;
		}

		try
		{
			var path = Path.GetFullPath(arguments[at + 1]);
			var name = Path.GetFileName(path);
			var match = System.Text.RegularExpressions.Regex.Match(name, $@"^{AppIdentity.Name}-(\d+\.\d+\.\d+)-win-x64\.msi$");
			if (!match.Success || !File.Exists(path))
			{
				error = $"{name} is not a {AppIdentity.Name} installer here";
				return null;
			}

			var version = match.Groups[1].Value;
			if (!UpdateVersion.IsNewer(version, currentVersion))
			{
				error = $"{version} is not newer than {currentVersion}";
				return null;
			}

			var hashFile = path + UpdateDownloader.HashSuffix;
			if (!File.Exists(hashFile) || Sha256File.Parse(File.ReadAllText(hashFile), name) is not { } expected || !UpdateDownloader.Matches(path, expected))
			{
				error = $"{name} has no .sha256 beside it that matches it";
				return null;
			}

			msiPath = path;
			var download = $"{UpdateSource.RepositoryUrl}/releases/download/v{version}/{name}";
			return new ReleaseInfo(version, $"{UpdateSource.ReleasesPage}/tag/v{version}", "", name, download, download + UpdateDownloader.HashSuffix) { MsiDigest = expected };
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			error = ex.Message;
			return null;
		}
	}

	/// <summary>Writes <paramref name="pending"/> for the next start (before the helper starts).</summary>
	public static void WritePending(string updatesFolder, PendingInstall pending)
	{
		Directory.CreateDirectory(updatesFolder);
		File.Delete(Path.Combine(updatesFolder, ResultFile));
		File.Delete(Path.Combine(updatesFolder, ReopenRequestFile));
		File.WriteAllText(Path.Combine(updatesFolder, PendingFile), JsonSerializer.Serialize(pending));
	}

	/// <summary>The pending install, or null (none, or not readable).</summary>
	public static PendingInstall? ReadPending(string updatesFolder)
	{
		try
		{
			var path = Path.Combine(updatesFolder, PendingFile);
			return File.Exists(path) ? JsonSerializer.Deserialize<PendingInstall>(File.ReadAllText(path)) : null;
		}
		catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
		{
			return null;
		}
	}

	/// <summary>msiexec's exit code from <see cref="ResultFile"/>, or null when the helper wrote none.</summary>
	public static int? ReadResult(string updatesFolder)
	{
		try
		{
			var path = Path.Combine(updatesFolder, ResultFile);
			if (!File.Exists(path))
			{
				return null;
			}

			foreach (var line in File.ReadAllLines(path))
			{
				if (line.StartsWith("exit=", StringComparison.Ordinal) && int.TryParse(line[5..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code))
				{
					return code;
				}
			}

			return null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>Whether a helper holds <see cref="MutexName"/> now (it waits for Nixie to end, or installs).</summary>
	public static bool InstallRunning() => InstallRunning(MutexName);

	/// <summary>
	/// Review m6: whether a helper OWNS the mutex <paramref name="name"/> now. A mutex that only exists (a handle still open
	/// after its release) or was abandoned (its helper ended without releasing it) is no install under way.
	/// </summary>
	internal static bool InstallRunning(string name)
	{
		try
		{
			if (!Mutex.TryOpenExisting(name, out var mutex))
			{
				return false;
			}

			using (mutex)
			{
				var acquired = false;
				try
				{
					try
					{
						acquired = mutex.WaitOne(0);   // nobody owned it: ours for a moment, given back below
					}
					catch (AbandonedMutexException)
					{
						acquired = true;               // its owner ended without releasing it: the same, no install under way
					}

					return !acquired;                  // not acquired: a helper owns it
				}
				finally
				{
					if (acquired)
					{
						mutex.ReleaseMutex();
					}
				}
			}
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>
	/// What an msiexec exit code means, in plain words; null for success (0, or 3010/1641: done, Windows wants a restart).
	/// </summary>
	/// <summary>
	/// Review m8: ERROR_SUCCESS_REBOOT_REQUIRED (3010) and ERROR_SUCCESS_REBOOT_INITIATED (1641): files were in use (another
	/// Nixie window, a file locked by another program), and Windows completes the install at its next restart.
	/// </summary>
	public static bool NeedsWindowsRestart(int exitCode) => exitCode is 3010 or 1641;

	/// <summary>
	/// Review m8 (the second-window case): other processes running this app's own Nixie.exe (<paramref name="appPath"/>)
	/// besides this one: the installer would find their files in use and leave the install to a Windows restart.
	/// <paramref name="running"/> are (process id, its program's path), as read from the processes named like the app.
	/// </summary>
	public static int[] OtherInstances(IEnumerable<(int Id, string? Path)> running, int selfId, string appPath) =>
		[.. running.Where(p => p.Id != selfId && p.Path is not null && string.Equals(System.IO.Path.GetFullPath(p.Path), System.IO.Path.GetFullPath(appPath), StringComparison.OrdinalIgnoreCase))
			.Select(p => p.Id)];

	public static string? InstallerError(int exitCode) => exitCode switch
	{
		0 or 3010 or 1641 => null,
		-1 => "Nixie didn't close in time, so nothing was installed",
		1602 => "the installation was cancelled",
		1618 => "another installation was running; try again when it's done",
		1625 or 1643 => "Windows' policy doesn't allow this installation",
		1638 => "another copy of this version is already installed",
		1619 or 1620 => "the downloaded installer couldn't be opened",
		1601 => "the Windows Installer service couldn't be reached",
		1603 => "Windows Installer stopped with an error (1603)",
		_ => $"Windows Installer stopped with error {exitCode}",
	};
}

/// <summary>Nixie 1.5: the page to come back to after the update: the page's view model and, for an item's page, the item.</summary>
public sealed record ResumePlace(string PageKey, Guid? ItemId = null);

/// <summary>
/// Nixie 1.5: an install handed to the helper (<see cref="UpdateHandoff.PendingFile"/>): the version it installs, the version
/// that handed it over, when, the release's page and notes (for "What's new" afterwards), and where to come back to.
/// </summary>
public sealed record PendingInstall(string Version, string FromVersion, DateTimeOffset StartedAt, string PageUrl, string Notes, bool Reopen,
	ResumePlace? Resume = null);

public enum AftermathKind
{
	/// <summary>No install was handed over (or it is too old to talk about).</summary>
	None,

	/// <summary>This run is the version that was installed: "Updated to 1.x · What's new".</summary>
	Updated,

	/// <summary>The install did not happen: <see cref="UpdateAftermath.Reason"/> says why.</summary>
	Failed,

	/// <summary>A helper is still waiting or installing (Nixie was opened again meanwhile).</summary>
	Installing,

	/// <summary>
	/// Review m8: Windows Installer finished with 3010 or 1641: files were in use, and Windows completes the install when it
	/// restarts. This run is still the old version; the same install is not offered again meanwhile.
	/// </summary>
	RestartNeeded,
}

/// <summary>Nixie 1.5: what became of the install handed over before this start.</summary>
public sealed record UpdateAftermath(AftermathKind Kind, PendingInstall? Pending = null, string? Reason = null, int? ExitCode = null)
{
	public static UpdateAftermath Nothing { get; } = new(AftermathKind.None);

	/// <summary>An install handed over this long ago without a word from the helper did not happen.</summary>
	public static readonly TimeSpan HelperSilentAfter = TimeSpan.FromMinutes(10);

	/// <summary>A pending install older than this is no longer news.</summary>
	public static readonly TimeSpan Forgotten = TimeSpan.FromDays(7);

	/// <summary>
	/// Reads it: this run's <paramref name="currentVersion"/> is the pending version (or newer): Updated. Still the version that
	/// handed over: Installing while a helper runs (<paramref name="installRunning"/>), else Failed with msiexec's reason, or
	/// with "didn't finish" when the helper never wrote one. A successful exit with the old version still running is a copy
	/// started from elsewhere (a portable folder sharing the data folder): nothing to say.
	/// </summary>
	public static UpdateAftermath Read(PendingInstall? pending, int? exitCode, string currentVersion, DateTimeOffset now, bool installRunning)
	{
		if (pending is null || now - pending.StartedAt > Forgotten || pending.StartedAt - now > TimeSpan.FromDays(1))
		{
			return Nothing;
		}

		if (!UpdateVersion.IsNewer(pending.Version, currentVersion))
		{
			return UpdateVersion.TryParse(currentVersion, out _) ? new UpdateAftermath(AftermathKind.Updated, pending) : Nothing;
		}

		if (installRunning)
		{
			return new UpdateAftermath(AftermathKind.Installing, pending);
		}

		if (exitCode is { } code)
		{
			// review m8: 3010/1641 with the old version still running is not success: Windows finishes it at its restart
			if (UpdateHandoff.NeedsWindowsRestart(code))
			{
				return new UpdateAftermath(AftermathKind.RestartNeeded, pending, "Windows finishes installing it when it restarts", code);
			}

			return UpdateHandoff.InstallerError(code) is { } reason ? new UpdateAftermath(AftermathKind.Failed, pending, reason, code) : Nothing;
		}

		return now - pending.StartedAt >= HelperSilentAfter
			? new UpdateAftermath(AftermathKind.Failed, pending, "the installer didn't finish")
			: new UpdateAftermath(AftermathKind.Failed, pending, "the installer didn't start");
	}
}
