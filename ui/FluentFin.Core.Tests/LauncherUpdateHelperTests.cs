using System.Diagnostics;
using FluentFin.Core.Updates;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.5, "Restart to update": the real launcher (tools\release\launcher\Launcher.cs, compiled here with Windows' own C#
/// compiler as the release build does, plus the tests' define that lets a stand-in take msiexec's place) run as the install
/// helper, against a stand-in msiexec and a stand-in Nixie (one tiny program under two names that write down how they were
/// started). Nothing is installed: the release build has no way to run anything but Windows' msiexec. The programs have no
/// window. Every process here is one this test started (its Process object), and only those are ever ended.
/// </summary>
[Collection(nameof(LauncherUpdateHelperTests))]   // the helper's mutex is machine-wide: one test at a time
public sealed class LauncherUpdateHelperTests : IDisposable
{
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "nixie-helper-tests-" + Guid.NewGuid().ToString("N"));
	private readonly List<Process> _started = [];

	/// <summary>The helper's mutex in these tests: never the real one, which would make a Nixie starting meanwhile end at once.</summary>
	private readonly string _mutex = @"Local\Nixie-update-install-test-" + Guid.NewGuid().ToString("N");

	private bool MutexHeld()
	{
		if (!Mutex.TryOpenExisting(_mutex, out var m))
		{
			return false;
		}

		m.Dispose();
		return true;
	}

	private static readonly Lazy<string> s_built = new(Build);

	public LauncherUpdateHelperTests() => Directory.CreateDirectory(Path.Combine(_dir, "Updates folder"));

	public void Dispose()
	{
		foreach (var process in _started)
		{
			try
			{
				if (!process.HasExited)
				{
					process.Kill();
				}

				process.WaitForExit(5000);
			}
			catch (InvalidOperationException)
			{
			}

			process.Dispose();
		}

		// a program that just ended (the reopened stand-in too) keeps its file for a moment
		for (var attempt = 0; attempt < 20 && Directory.Exists(_dir); attempt++)
		{
			try
			{
				Directory.Delete(_dir, recursive: true);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				Thread.Sleep(250);
			}
		}
	}

	private const string StandIn = """
		using System;
		using System.IO;
		using System.Threading;
		static class StandIn
		{
		    static int Main(string[] args)
		    {
		        string exe = System.Reflection.Assembly.GetEntryAssembly().Location;
		        string dir = Path.GetDirectoryName(exe);
		        string name = Path.GetFileNameWithoutExtension(exe);
		        if (args.Length == 2 && args[0] == "--sleep") { Thread.Sleep(int.Parse(args[1])); return 0; }
		        string mx = Environment.GetEnvironmentVariable("NIXIE_TEST_MUTEX");
		        if (!string.IsNullOrEmpty(mx))
		        {
		            Mutex held;
		            bool exists = Mutex.TryOpenExisting(mx, out held);
		            if (exists) held.Dispose();
		            File.WriteAllText(Path.Combine(dir, name + "-mutex.txt"), "exists=" + exists);
		        }
		        File.WriteAllText(Path.Combine(dir, name + ".txt"), DateTime.UtcNow.Ticks + "\n" + Environment.CommandLine + "\n" + string.Join("\n", args));
		        string exit = Path.Combine(dir, name + "-exit.txt");
		        return File.Exists(exit) ? int.Parse(File.ReadAllText(exit).Trim()) : 0;
		    }
		}
		""";

	/// <summary>Compiles the helper (test build and release build) and the stand-ins once per run, into a folder of their own.</summary>
	private static string Build()
	{
		var csc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Microsoft.NET\Framework64\v4.0.30319\csc.exe");
		var bin = Path.Combine(Path.GetTempPath(), "nixie-helper-bin-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(bin);
		AppDomain.CurrentDomain.ProcessExit += (_, _) =>
		{
			try
			{
				Directory.Delete(bin, recursive: true);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		};
		var launcher = Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "..", "tools", "release", "launcher", "Launcher.cs");
		var standIn = Path.Combine(bin, "StandIn.cs");
		File.WriteAllText(standIn, StandIn);
		Compile(csc, "/define:NIXIE_LAUNCHER_TEST", Path.Combine(bin, "helper.exe"), launcher);
		Compile(csc, "/optimize+", Path.Combine(bin, "release-helper.exe"), launcher);
		Directory.CreateDirectory(Path.Combine(bin, "msiexec"));
		Directory.CreateDirectory(Path.Combine(bin, "app"));
		Compile(csc, "/optimize+", Path.Combine(bin, "msiexec", "msiexec.exe"), standIn);
		Compile(csc, "/optimize+", Path.Combine(bin, "app", "Nixie.exe"), standIn);
		return bin;
	}

	private static void Compile(string csc, string option, string output, string source)
	{
		var info = new ProcessStartInfo(csc) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
		foreach (var a in new[] { "/nologo", "/target:winexe", "/platform:x64", option, "/out:" + output, source })
		{
			info.ArgumentList.Add(a);
		}

		using var process = Process.Start(info)!;
		var said = process.StandardOutput.ReadToEnd();
		process.WaitForExit();
		Assert.True(process.ExitCode == 0, $"csc {Path.GetFileName(source)}: {said}");
	}

	private string Bin => s_built.Value;

	/// <summary>A copy of the stand-ins in this test's folder (each test reads its own records).</summary>
	private (string msiexec, string app) StandIns()
	{
		var msiexec = Path.Combine(_dir, "msiexec", "msiexec.exe");
		var app = Path.Combine(_dir, "Programs", "Nixie", "app", "Nixie.exe");
		Directory.CreateDirectory(Path.GetDirectoryName(msiexec)!);
		Directory.CreateDirectory(Path.GetDirectoryName(app)!);
		File.Copy(Path.Combine(Bin, "msiexec", "msiexec.exe"), msiexec);
		File.Copy(Path.Combine(Bin, "app", "Nixie.exe"), app);
		return (msiexec, app);
	}

	private Process Start(ProcessStartInfo info)
	{
		var process = Process.Start(info)!;
		_started.Add(process);
		return process;
	}

	/// <summary>The stand-in Nixie, running for <paramref name="ms"/>: the Nixie the helper waits for.</summary>
	private Process Sleeper(string app, int ms)
	{
		var info = new ProcessStartInfo(app) { UseShellExecute = false, CreateNoWindow = true };
		info.ArgumentList.Add("--sleep");
		info.ArgumentList.Add(ms.ToString(System.Globalization.CultureInfo.InvariantCulture));
		return Start(info);
	}

	private ProcessStartInfo Helper(string msiexec, string app, Process waitFor, bool reopen, bool quiet, IReadOnlyList<string> appArguments, long? created = null,
		string helper = "helper.exe")
	{
		var updates = Path.Combine(_dir, "Updates folder");
		var msi = Path.Combine(updates, "Nixie-1.5.1-win-x64.msi");
		File.WriteAllText(msi, "not really an installer");
		var info = UpdateHandoff.HelperStart(Path.Combine(Bin, helper), msi, updates, waitFor.Id, created ?? UpdateHandoff.CreationTime(waitFor), app, appArguments, reopen, quiet);
		info.ArgumentList.Insert(1, "--msiexec");
		info.ArgumentList.Insert(2, msiexec);
		info.ArgumentList.Insert(3, "--mutex");
		info.ArgumentList.Insert(4, _mutex);
		return info;
	}

	private static string[] Record(string file) => File.Exists(file) ? File.ReadAllText(file).Split('\n') : [];

	[Fact]
	public void TheHelper_WaitsForNixieToEnd_InstallsWithALog_WritesTheResult_AndReopensNixieWithItsArguments()
	{
		var (msiexec, app) = StandIns();
		var nixie = Sleeper(app, 1500);
		string[] arguments = ["--data-dir", @"D:\my data", "say \"hi\"", @"ends\"];

		var info = Helper(msiexec, app, nixie, reopen: true, quiet: false, arguments);
		info.Environment["NIXIE_TEST_MUTEX"] = _mutex;   // the reopened stand-in says whether the helper's mutex is still there
		var helper = Start(info);
		Thread.Sleep(600);
		Assert.False(File.Exists(Path.ChangeExtension(msiexec, ".txt")), "msiexec ran while Nixie was still open");
		Assert.True(helper.WaitForExit(30_000), "the helper did not finish");
		Assert.True(nixie.HasExited);

		Assert.Equal(0, helper.ExitCode);
		var updates = Path.Combine(_dir, "Updates folder");
		var ran = Record(Path.ChangeExtension(msiexec, ".txt"));
		Assert.True(long.Parse(ran[0]) >= nixie.ExitTime.ToUniversalTime().Ticks, "msiexec ran before Nixie ended");
		Assert.Equal(["/i", Path.Combine(updates, "Nixie-1.5.1-win-x64.msi"), "/passive", "/norestart", "/l*v", Path.Combine(updates, "install.log")], ran[2..]);
		Assert.Equal(0, UpdateHandoff.ReadResult(updates));
		Assert.True(SpinWait.SpinUntil(() => File.Exists(Path.ChangeExtension(app, ".txt")), 10_000), "Nixie was not opened again");
		Assert.Equal(arguments, Record(Path.ChangeExtension(app, ".txt"))[2..]);   // the same arguments, quoting and all
		Assert.False(MutexHeld());
		// review m6: the helper closed its mutex before opening Nixie again (a Nixie that finds it ends at once)
		Assert.Equal("exists=False", File.ReadAllText(Path.Combine(Path.GetDirectoryName(app)!, "Nixie-mutex.txt")));
	}

	[Fact]
	public void InstallingAsNixieCloses_IsQuiet_AndOpensNothing_UnlessANixieStartedMeanwhileAskedForIt()
	{
		var (msiexec, app) = StandIns();
		File.WriteAllText(Path.Combine(Path.GetDirectoryName(msiexec)!, "msiexec-exit.txt"), "1603");
		var updates = Path.Combine(_dir, "Updates folder");

		var quiet = Start(Helper(msiexec, app, Sleeper(app, 200), reopen: false, quiet: true, []));
		Assert.True(quiet.WaitForExit(30_000));
		Assert.Equal(1603, quiet.ExitCode);
		Assert.Contains("/qn", Record(Path.ChangeExtension(msiexec, ".txt")));
		Assert.Equal(1603, UpdateHandoff.ReadResult(updates));
		Thread.Sleep(500);
		Assert.False(File.Exists(Path.ChangeExtension(app, ".txt")), "it opened Nixie though nobody asked");

		UpdateHandoff.RequestReopen(updates);   // a Nixie started during the install asked, and ended
		var asked = Start(Helper(msiexec, app, Sleeper(app, 200), reopen: false, quiet: true, ["--x"]));
		Assert.True(asked.WaitForExit(30_000));
		Assert.True(SpinWait.SpinUntil(() => File.Exists(Path.ChangeExtension(app, ".txt")), 10_000), "the request was not honoured");
		Assert.False(File.Exists(Path.Combine(updates, UpdateHandoff.ReopenRequestFile)));
	}

	[Fact]
	public void AReusedProcessId_IsNotWaitedOn()
	{
		var (msiexec, app) = StandIns();
		var stranger = Sleeper(app, 20_000);    // the same id, but not the Nixie that asked (another start time)

		var helper = Start(Helper(msiexec, app, stranger, reopen: false, quiet: true, [], created: UpdateHandoff.CreationTime(stranger) - 10_000_000));

		Assert.True(helper.WaitForExit(15_000), "the helper waited on a process that was not the Nixie that asked");
		Assert.False(stranger.HasExited);       // and it was left alone
		Assert.True(File.Exists(Path.ChangeExtension(msiexec, ".txt")));
	}

	[Fact]
	public void ANixieThatDoesNotClose_GetsNothingInstalled()
	{
		var (msiexec, app) = StandIns();
		var stuck = Sleeper(app, 20_000);
		var info = Helper(msiexec, app, stuck, reopen: true, quiet: false, []);
		info.ArgumentList.Insert(1, "--wait-ms");
		info.ArgumentList.Insert(2, "500");

		var helper = Start(info);

		Assert.True(helper.WaitForExit(15_000));
		Assert.Equal(4, helper.ExitCode);
		Assert.False(File.Exists(Path.ChangeExtension(msiexec, ".txt")));
		var updates = Path.Combine(_dir, "Updates folder");
		Assert.Equal(-1, UpdateHandoff.ReadResult(updates));
		Assert.Equal("Nixie didn't close in time, so nothing was installed", UpdateHandoff.InstallerError(-1));
	}

	[Fact]
	public void OneHelperAtATime_AndBadArguments_InstallNothing()
	{
		var (msiexec, app) = StandIns();
		using (var held = new Mutex(true, _mutex, out var mine))
		{
			try
			{
				Assert.True(mine);
				Assert.True(MutexHeld());
				var second = Start(Helper(msiexec, app, Sleeper(app, 100), reopen: false, quiet: true, []));
				Assert.True(second.WaitForExit(15_000));
				Assert.Equal(5, second.ExitCode);
			}
			finally
			{
				held.ReleaseMutex();
			}
		}

		var bad = new ProcessStartInfo(Path.Combine(Bin, "helper.exe")) { UseShellExecute = false };
		foreach (var a in new[] { UpdateHandoff.Switch, "--msi", @"C:\x\evil.exe", "--log", "l", "--result", "r", "--wait-pid", "1", "--wait-started", "1" })
		{
			bad.ArgumentList.Add(a);
		}

		var refused = Start(bad);
		Assert.True(refused.WaitForExit(15_000));
		Assert.Equal(2, refused.ExitCode);
		Assert.False(File.Exists(Path.ChangeExtension(msiexec, ".txt")));
	}

	/// <summary>
	/// Review m6: an install is under way only while a helper OWNS the mutex: a mutex that merely exists (a handle left open
	/// after its release) or was abandoned (its helper died) must not make a starting Nixie end at once.
	/// </summary>
	[Fact]
	public void AnInstallIsUnderWay_OnlyWhileAHelperOwnsTheMutex()
	{
		Assert.False(UpdateHandoff.InstallRunning(_mutex));                 // none
		using (var held = new Mutex(true, _mutex, out var mine))
		{
			var released = false;
			try
			{
				Assert.True(mine);
				var seen = false;
				var other = new Thread(() => seen = UpdateHandoff.InstallRunning(_mutex));   // another thread: as another process sees it
				other.Start();
				other.Join();
				Assert.True(seen);                                               // owned
			}
			finally
			{
				held.ReleaseMutex();
				released = true;
			}

			var after = true;
			var again = new Thread(() => after = UpdateHandoff.InstallRunning(_mutex));
			again.Start();
			again.Join();
			Assert.True(released);
			Assert.False(after);                                                 // released, its handle still open
		}

		Mutex? kept = null;
		var dies = new Thread(() => kept = new Mutex(true, _mutex, out _));  // owned by a thread that ends without releasing
		dies.Start();
		dies.Join();
		try
		{
			Assert.False(UpdateHandoff.InstallRunning(_mutex));             // abandoned
		}
		finally
		{
			kept?.Dispose();
		}
	}

	/// <summary>The release build has no stand-in switch: asked for one, it refuses (2) and runs nothing.</summary>
	[Fact]
	public void TheReleaseBuild_RunsOnlyWindowsMsiexec()
	{
		var (msiexec, app) = StandIns();

		var helper = Start(Helper(msiexec, app, Sleeper(app, 100), reopen: false, quiet: true, [], helper: "release-helper.exe"));

		Assert.True(helper.WaitForExit(15_000));
		Assert.Equal(2, helper.ExitCode);
		Assert.False(File.Exists(Path.ChangeExtension(msiexec, ".txt")));
		var launcher = File.ReadAllText(Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "..", "tools", "release", "launcher", "Launcher.cs"));
		Assert.Contains("Path.Combine(Environment.SystemDirectory, \"msiexec.exe\")", launcher);
	}
}
