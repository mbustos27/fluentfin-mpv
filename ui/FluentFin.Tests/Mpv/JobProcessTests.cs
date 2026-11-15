using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FluentFin.Mpv;
using Microsoft.Extensions.Logging.Abstractions;

namespace FluentFin.Tests.Mpv;

/// <summary>
/// The backend is started inside its job from its first instruction (docs/BRIDGE.md, "Embedded input and hangs"), with its
/// stdio on pipes. These start real processes: the backend checkout's venv python.exe, which is a launcher that starts the
/// real interpreter, exactly like the backend in development.
/// </summary>
public sealed class JobProcessTests
{
	private static readonly string[] TrickyArguments =
	[
		"", "plain", "two words", "tab\there", "quote\"inside", "\"", "trailing\\", "C:\\Program Files\\x\\",
		"back\\\\\"slash quote", "\\\\server\\share", "ünïcødé ✓", "--config-dir", "a\\b c\\",
	];

	/// <summary>The interpreter's pid and a grandchild's (PING, started by the interpreter like the backend starts mpv).</summary>
	private const string TreeScript =
		"import os, subprocess, sys, time\n" +
		"g = subprocess.Popen([os.path.join(os.environ['SystemRoot'], 'System32', 'PING.EXE'), '-n', '120', '127.0.0.1'], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)\n" +
		"print(os.getpid(), g.pid, flush=True)\n" +
		"time.sleep(120)\n";

	internal static string VenvPython()
	{
		// a copy of ui\ outside the repo (tools\ui-harness\mutate_ui.py) has no backend above it: it names the venv's python
		if (Environment.GetEnvironmentVariable("FLUENTFIN_MPV_TEST_PYTHON") is { Length: > 0 } named && File.Exists(named))
		{
			return named;
		}

		for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
		{
			var python = Path.Combine(directory.FullName, "backend", ".venv", "Scripts", "python.exe");
			if (File.Exists(python))
			{
				return python;
			}
		}

		throw new FileNotFoundException($"backend\\.venv\\Scripts\\python.exe was not found above {AppContext.BaseDirectory} (README: pip install -e backend[all])");
	}

	[Fact]
	public void CommandLine_ReadsBackAsTheSameArguments()
	{
		var commandLine = JobProcess.BuildCommandLine(@"C:\Program Files\Python\python.exe", TrickyArguments);

		var argv = CommandLineToArgv(commandLine);

		Assert.Equal(@"C:\Program Files\Python\python.exe", argv[0]);
		Assert.Equal(TrickyArguments, argv[1..]);
	}

	[Fact]
	public async Task Arguments_ArriveUnchangedInPython()
	{
		using var backend = WindowsMpvBackendLauncher.StartInJob(VenvPython(),
			["-I", "-c", "import json, sys; print(json.dumps(sys.argv[1:]), flush=True)", .. TrickyArguments], Path.GetTempPath());

		var line = await backend.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));

		Assert.Equal(TrickyArguments, JsonSerializer.Deserialize<string[]>(line!));
		Assert.Equal(0, await backend.Exited.WaitAsync(TimeSpan.FromSeconds(30)));
	}

	[Fact]
	public async Task Stdio_IsPipedBothWays_AndStdinEofEndsTheChild()
	{
		using var backend = WindowsMpvBackendLauncher.StartInJob(VenvPython(),
			["-I", "-X", "utf8", "-c", "import sys\nfor line in sys.stdin:\n    sys.stdout.write(line.upper()); sys.stdout.flush()\nsys.stderr.write('bye\\n')\n"], Path.GetTempPath());

		await backend.StandardInput.WriteAsync("hello bridge ✓\n");
		await backend.StandardInput.FlushAsync();
		var echoed = await backend.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
		backend.StandardInput.Close();
		var stderr = await backend.StandardError.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));

		Assert.Equal("HELLO BRIDGE ✓", echoed);
		Assert.Equal("bye", stderr);
		Assert.Equal(0, await backend.Exited.WaitAsync(TimeSpan.FromSeconds(30)));
	}

	[Fact]
	public async Task ExitedAt_IsWhenTheProcessEnded_KnownWhenItsExitIsSeen()
	{
		// the crash message counts its second from the exit itself (MpvBackendHost.OnExited), not from when it was seen
		using var process = WindowsMpvBackendLauncher.StartInJob(VenvPython(), ["-I", "-c", "import sys, time; print('up', flush=True); time.sleep(0.6)"], Path.GetTempPath());
		Assert.Null(process.ExitedAt);

		Assert.Equal("up", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
		var upAt = Environment.TickCount64;
		await process.Exited.WaitAsync(TimeSpan.FromSeconds(30));
		var seenAt = Environment.TickCount64;

		Assert.NotNull(process.ExitedAt);
		Assert.InRange(process.ExitedAt!.Value, upAt + 400, seenAt);
	}

	[Fact]
	public async Task HasExited_IsAskedOfWindows_FalseWhileRunning_TrueOnceEnded_AlsoAfterDispose()
	{
		// round 6, item 2: whether the backend died before FluentFin's own stop (MpvBackendHost's mark) reads this
		var backend = WindowsMpvBackendLauncher.StartInJob(Path.Combine(Environment.SystemDirectory, "PING.EXE"), ["-n", "30", "127.0.0.1"], null);
		try
		{
			Assert.False(backend.HasExited);

			backend.Kill();
			await backend.Exited.WaitAsync(TimeSpan.FromSeconds(10));

			Assert.True(backend.HasExited);
		}
		finally
		{
			backend.Dispose();
		}

		Assert.True(backend.HasExited);
	}

	[Fact]
	public void HasExited_IsTrueBeforeTheExitIsSeen()
	{
		// the wait behind Exited runs its callback on the thread pool, a moment (on a busy pool, seconds) after the end; the
		// process handle says it at once. Looked at right after the kill, in turns: at least once the end is known first
		var knownFirst = 0;
		for (var turn = 0; turn < 10 && knownFirst == 0; turn++)
		{
			using var backend = WindowsMpvBackendLauncher.StartInJob(Path.Combine(Environment.SystemDirectory, "PING.EXE"), ["-n", "30", "127.0.0.1"], null);
			backend.Kill();
			var watch = Stopwatch.StartNew();
			while (!backend.HasExited && watch.Elapsed < TimeSpan.FromSeconds(10))
			{
			}

			if (!backend.Exited.IsCompleted)
			{
				knownFirst++;
			}

			Assert.True(backend.HasExited);
		}

		Assert.True(knownFirst > 0, "HasExited never knew the end before Exited did: it does not ask Windows");
	}

	[Fact]
	public void IsSignalled_ReadsTheProcessHandle()
	{
		using var job = new JobObject();
		using var process = JobProcess.Start(Path.Combine(Environment.SystemDirectory, "PING.EXE"), ["-n", "30", "127.0.0.1"], null, job);

		Assert.False(JobProcess.IsSignalled(process.Handle));
		Assert.False(process.HasExited);
		job.Terminate(1);
		var watch = Stopwatch.StartNew();
		while (JobProcess.IsSignalled(process.Handle) != true && watch.Elapsed < TimeSpan.FromSeconds(10))
		{
			Thread.Sleep(5);
		}

		Assert.True(JobProcess.IsSignalled(process.Handle));
		Assert.True(process.HasExited);
	}

	[Fact]
	public void TheExitTime_IsAsLongAgoAsWindowsRecorded_NotWhenTheWaitSawIt()
	{
		var now = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

		Assert.Equal(100_000 - 2_500, JobProcess.TickCountOf(now.AddSeconds(-2.5).ToFileTimeUtc(), 100_000, now));
		Assert.Equal(100_000, JobProcess.TickCountOf(now.AddMilliseconds(20).ToFileTimeUtc(), 100_000, now));
		Assert.Equal(100_000, JobProcess.TickCountOf(0, 100_000, now));
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void TheProcess_IsInTheJob_BeforeItsFirstInstruction(bool useJobList)
	{
		using var job = new JobObject();
		var inJobWhileSuspended = false;

		using var process = JobProcess.Start(Path.Combine(Environment.SystemDirectory, "PING.EXE"), ["-n", "30", "127.0.0.1"], null, job,
			whileSuspended: handle => inJobWhileSuspended = job.Contains(handle), useJobList: useJobList);

		Assert.True(inJobWhileSuspended);
		Assert.Equal(useJobList, process.StartedWithJobList);
		job.Terminate(1);
	}

	[Fact]
	public async Task KillingTheBackend_EndsTheVenvInterpreterAndItsGrandchild()
	{
		// python.exe of a venv is a launcher: the interpreter is its child and mpv the interpreter's. JobObjectTests'
		// cmd -> PING cannot show this: it never had the launcher's own job in between.
		var backend = (WindowsMpvBackendLauncher.BackendProcess)WindowsMpvBackendLauncher.StartInJob(VenvPython(), ["-I", "-c", TreeScript], Path.GetTempPath());
		var (interpreter, grandchild) = await ReadTree(backend);
		try
		{
			Assert.True(backend.Job.Contains(interpreter.SafeHandle), "the interpreter is outside the job");
			Assert.True(backend.Job.Contains(grandchild.SafeHandle), "the grandchild is outside the job");

			backend.Kill();

			await interpreter.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
			await grandchild.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
			Assert.Equal((int)WindowsMpvBackendLauncher.BackendProcess.KilledExitCode, await backend.Exited.WaitAsync(TimeSpan.FromSeconds(10)));
		}
		finally
		{
			Cleanup(backend, interpreter, grandchild);
		}
	}

	[Fact]
	public async Task ClosingTheJob_AsWindowsDoesWhenFluentFinDies_EndsTheWholeTree()
	{
		var backend = (WindowsMpvBackendLauncher.BackendProcess)WindowsMpvBackendLauncher.StartInJob(VenvPython(), ["-I", "-c", TreeScript], Path.GetTempPath());
		var (interpreter, grandchild) = await ReadTree(backend);
		try
		{
			backend.Job.Dispose();

			await interpreter.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
			await grandchild.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
			await backend.Exited.WaitAsync(TimeSpan.FromSeconds(10));
		}
		finally
		{
			Cleanup(backend, interpreter, grandchild);
		}
	}

	[Fact]
	public async Task Kill_NeverThrows_Twice_AfterExit_OrAfterDispose()
	{
		var backend = WindowsMpvBackendLauncher.StartInJob(Path.Combine(Environment.SystemDirectory, "PING.EXE"), ["-n", "30", "127.0.0.1"], null);

		Assert.Null(Record.Exception(backend.Kill));
		await backend.Exited.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Null(Record.Exception(backend.Kill));
		backend.Dispose();
		Assert.Null(Record.Exception(backend.Kill));
		Assert.Null(Record.Exception(backend.Dispose));
	}

	[Fact]
	public void AFailureBeforeTheFirstInstruction_LeavesNothingRunning()
	{
		// the reviewer's R2: the process exists (suspended, in the job) when a step before its first instruction fails; it is
		// ended then and there, not left suspended until the job happens to close
		using var job = new JobObject();
		var pid = 0;

		Assert.Throws<InvalidOperationException>(() => JobProcess.Start(Path.Combine(Environment.SystemDirectory, "PING.EXE"), ["-n", "30", "127.0.0.1"], null, job,
			whileSuspended: handle =>
			{
				pid = GetProcessId(handle);
				throw new InvalidOperationException("a step before the first instruction failed");
			}));

		Assert.NotEqual(0, pid);
		try
		{
			using var process = Process.GetProcessById(pid);
			Assert.True(process.WaitForExit(5000), $"pid {pid} was left behind");
		}
		catch (ArgumentException)
		{
			// already gone
		}
	}

	[Fact]
	public async Task Kill_WhenTheJobCannotEndTheBackend_EndsItDirectly()
	{
		// the fallback for a backend outside its job (or a job Windows will not terminate): closing that job does not end it
		using var holder = new JobObject();   // the process's real job; closing it at the end cleans up whatever is left
		var process = JobProcess.Start(Path.Combine(Environment.SystemDirectory, "PING.EXE"), ["-n", "30", "127.0.0.1"], null, holder);
		var closed = new JobObject();
		closed.Dispose();
		using var backend = new WindowsMpvBackendLauncher.BackendProcess(process, closed, NullLogger.Instance);

		backend.Kill();

		Assert.Equal((int)WindowsMpvBackendLauncher.BackendProcess.KilledExitCode, await backend.Exited.WaitAsync(TimeSpan.FromSeconds(10)));
	}

	[DllImport("kernel32.dll")]
	private static extern int GetProcessId(Microsoft.Win32.SafeHandles.SafeProcessHandle process);

	[Fact]
	public void AProgramThatDoesNotExist_Throws()
	{
		var missing = Path.Combine(Path.GetTempPath(), $"no-such-backend-{Guid.NewGuid():N}.exe");

		var error = Assert.Throws<Win32Exception>(() => WindowsMpvBackendLauncher.StartInJob(missing, ["--dev"], null));

		Assert.Contains("no-such-backend", error.Message);
	}

	private static async Task<(Process Interpreter, Process Grandchild)> ReadTree(WindowsMpvBackendLauncher.BackendProcess backend)
	{
		var line = await backend.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
		var pids = line!.Split(' ').Select(int.Parse).ToArray();
		return (Process.GetProcessById(pids[0]), Process.GetProcessById(pids[1]));
	}

	private static void Cleanup(WindowsMpvBackendLauncher.BackendProcess backend, params Process[] processes)
	{
		backend.Kill();
		backend.Dispose();
		foreach (var process in processes)
		{
			try
			{
				if (!process.HasExited)
				{
					process.Kill();
				}
			}
			catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
			{
			}

			process.Dispose();
		}
	}

	private static string[] CommandLineToArgv(string commandLine)
	{
		var argv = CommandLineToArgvW(commandLine, out var count);
		Assert.NotEqual(0, argv);
		try
		{
			return [.. Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!)];
		}
		finally
		{
			LocalFree(argv);
		}
	}

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern nint CommandLineToArgvW(string commandLine, out int count);

	[DllImport("kernel32.dll")]
	private static extern nint LocalFree(nint memory);
}
