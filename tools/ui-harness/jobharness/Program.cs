// Is the backend in its job from its first instruction (docs/BRIDGE.md section 6)? Starts the venv's python.exe (a
// launcher that starts the real interpreter at once) running a script that starts a grandchild (PING.EXE, standing in for
// mpv) and prints both pids; checks that the interpreter and the grandchild are in the job, closes the job (what Windows
// does when FluentFin dies) and checks that nothing survives.
//
//   jobharness <python.exe> <runs> <gapMs> [new|new-nojoblist|old] [gc|cpu]
//     new            the app's launcher: JobProcess.Start (suspended, PROC_THREAD_ATTRIBUTE_JOB_LIST); the gap is spent
//                    while the process is still suspended, before its first instruction
//     new-nojoblist  the same without the attribute: assigned to the job while suspended
//     old            the launcher before the fix: Process.Start, the gap while it already runs, then job.Assign
//     gc|cpu         background garbage or CPU load on this process
//
// Uses the repo's ui\FluentFin\Mpv\JobObject.cs and JobProcess.cs (see jobharness.csproj). Kills only processes it started:
// the launcher through the handle it started it with, the interpreter and the grandchild only through a handle opened
// once and proved this run's (OwnedProcess.cs: created after the run began, the run's marker in the command line), so
// also when they escaped the job (old mode).
using System.Diagnostics;
using System.Text;
using FluentFin.Mpv;

static class P
{
	// argv[1] is the run's marker: in the interpreter's command line, and the grandchild's argv[0]
	const string Script = "import os, subprocess, sys, time\n" +
		"g = subprocess.Popen(['ping-' + sys.argv[1], '-n', '120', '127.0.0.1'], executable=os.path.join(os.environ['SystemRoot'], 'System32', 'PING.EXE'), stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)\n" +
		"print(os.getpid(), g.pid, flush=True)\n" +
		"time.sleep(120)\n";

	static int Main(string[] a)
	{
		if (a.Length < 3)
		{
			Console.Error.WriteLine("usage: jobharness <python.exe> <runs> <gapMs> [new|new-nojoblist|old] [gc|cpu]");
			return 2;
		}

		var python = a[0];
		var runs = int.Parse(a[1]);
		var gap = int.Parse(a[2]);
		var mode = a.Length > 3 ? a[3] : "new";
		var load = a.Length > 4 ? a[4] : "";
		if (mode is not ("new" or "new-nojoblist" or "old"))
		{
			Console.Error.WriteLine($"unknown mode {mode}");
			return 2;
		}

		StartLoad(load);
		int interpreterOutside = 0, grandchildOutside = 0, survivors = 0, viaJobList = 0, unproved = 0;
		for (var i = 0; i < runs; i++)
		{
			var runStart = OwnedProcess.Now();
			var marker = OwnedProcess.NewMarker("jobharness");
			var job = new JobObject();
			Process? oldLauncher = null;
			JobProcess? launcher = null;
			StreamReader stdout;
			if (mode == "old")
			{
				var utf8 = new UTF8Encoding(false);
				var psi = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = utf8, StandardErrorEncoding = utf8 };
				psi.ArgumentList.Add("-I"); psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(Script); psi.ArgumentList.Add(marker);
				oldLauncher = Process.Start(psi)!;   // holds the handle CreateProcess returned
				if (gap > 0) Thread.Sleep(gap);
				job.Assign(oldLauncher);
				stdout = oldLauncher.StandardOutput;
			}
			else
			{
				launcher = JobProcess.Start(python, ["-I", "-c", Script, marker], null, job,
					whileSuspended: gap > 0 ? _ => Thread.Sleep(gap) : null,
					useJobList: mode == "new");
				if (launcher.StartedWithJobList) viaJobList++;
				stdout = new StreamReader(launcher.StandardOutput);
			}

			var line = stdout.ReadLine() ?? throw new InvalidOperationException("the script printed nothing");
			var pids = line.Split(' ').Select(int.Parse).ToArray();
			using var interpreter = OwnedProcess.Open(pids[0], runStart, marker, out var whyInterpreter);
			using var grandchild = OwnedProcess.Open(pids[1], runStart, marker, out var whyGrandchild);
			if (interpreter is null || grandchild is null)
			{
				// never acted on: if it escaped the job, it ends by itself within 120 s
				unproved++;
				Console.WriteLine($"#{i} NOT PROVED this run's, left alone: {string.Join("; ", new[] { whyInterpreter, whyGrandchild }.Where(x => x.Length > 0))}");
			}

			var interpreterIn = interpreter is not null && job.Contains(interpreter.Handle);
			var grandchildIn = grandchild is not null && job.Contains(grandchild.Handle);
			if (!interpreterIn) interpreterOutside++;
			if (!grandchildIn) grandchildOutside++;

			job.Dispose();   // what Windows does when FluentFin dies
			Thread.Sleep(1500);
			var alive = new List<string>();
			if (launcher is not null ? !launcher.Exited.IsCompleted : !oldLauncher!.HasExited) alive.Add("launcher");
			if (interpreter is not null && !interpreter.HasExited) alive.Add("interpreter");
			if (grandchild is not null && !grandchild.HasExited) alive.Add("grandchild");
			Console.WriteLine($"#{i} interp={pids[0]} inJob={interpreterIn} grandchild={pids[1]} inJob={grandchildIn} alive after job close: [{string.Join(",", alive)}]");
			if (alive.Count > 0) survivors++;

			// only what this run started, each through the handle that proved it
			foreach (var p in new[] { grandchild, interpreter }) { if (p is not null && !p.HasExited) p.Terminate(1); }
			if (launcher is not null) { launcher.TerminateOnly(1); launcher.Dispose(); }
			if (oldLauncher is not null) { try { if (!oldLauncher.HasExited) oldLauncher.Kill(); } catch { } oldLauncher.Dispose(); }
		}

		Console.WriteLine($"SUMMARY mode={mode} runs={runs} gapMs={gap} startedWithJobList={viaJobList} interpreterOutsideJob={interpreterOutside} grandchildOutsideJob={grandchildOutside} runsWithSurvivors={survivors} notProvedThisRuns={unproved}");
		return interpreterOutside + grandchildOutside + survivors + unproved == 0 ? 0 : 1;
	}

	static void StartLoad(string load)
	{
		if (load == "gc")
		{
			for (var t = 0; t < 2; t++) new Thread(() => { var keep = new List<byte[]>(); var r = new Random(); while (true) { keep.Add(new byte[r.Next(1000, 200000)]); if (keep.Count > 3000) keep.Clear(); } }) { IsBackground = true }.Start();
		}
		else if (load == "cpu")
		{
			for (var t = 0; t < Environment.ProcessorCount * 2; t++) new Thread(() => { long x = 0; while (true) x++; }) { IsBackground = true }.Start();
		}
	}
}
