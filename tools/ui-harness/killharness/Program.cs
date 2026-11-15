// The kill path the watchdog takes (docs/BRIDGE.md section 6: it must never throw into FluentFin, and must end the
// whole tree). Starts the venv's python.exe running a script that starts a grandchild (PING.EXE, standing in for mpv),
// kills it twice (a second kill races the first, as a watchdog kill and a shutdown kill can), optionally with the
// grandchild suspended (a hung mpv) and with the process disposed on another thread as soon as it exits (what
// MpvBackendHost.OnExited does), and counts escaped exceptions, kill times and survivors.
//
//   killharness <python.exe> <runs> [new|old] [suspend] [dispose <delayMs>]
//     new   the app's launcher: WindowsMpvBackendLauncher.StartInJob, then BackendProcess.Kill (TerminateJobObject)
//     old   the launcher before the fix: Process.Start + job.Assign, then job.Dispose + Process.Kill of the launcher and
//           of the interpreter and grandchild through the handles below (the old launcher's
//           Process.Kill(entireProcessTree: true) walked parent pids, which this harness does not do)
//
// Uses the repo's ui\FluentFin\Mpv sources (see killharness.csproj). Kills only processes it started: the launcher
// through the handle it started it with, the interpreter and the grandchild only through a handle opened once and
// proved this run's (OwnedProcess.cs: created after the run began, the run's marker in the command line).
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
		if (a.Length < 2)
		{
			Console.Error.WriteLine("usage: killharness <python.exe> <runs> [new|old] [suspend] [dispose <delayMs>]");
			return 2;
		}

		var python = a[0];
		var runs = int.Parse(a[1]);
		var rest = a.Skip(2).ToList();
		var mode = rest.FirstOrDefault(x => x is "new" or "old") ?? "new";
		var suspend = rest.Contains("suspend");
		var disposeAt = rest.IndexOf("dispose");
		var dispose = disposeAt >= 0;
		var delay = dispose && disposeAt + 1 < rest.Count && int.TryParse(rest[disposeAt + 1], out var d) ? d : 0;

		var outcomes = new Dictionary<string, int>();
		var killMs = new List<long>();
		var survivors = 0;
		var unproved = 0;
		for (var i = 0; i < runs; i++)
		{
			var runStart = OwnedProcess.Now();
			var marker = OwnedProcess.NewMarker("killharness");
			IDisposable started;
			Action kill;
			Task exited;
			string line;
			// the interpreter and the grandchild, once proved this run's (the old mode's kill ends them through these)
			var owned = new List<OwnedProcess>();
			if (mode == "new")
			{
				var backend = WindowsMpvBackendLauncher.StartInJob(python, ["-I", "-c", Script, marker], null);
				started = backend;
				kill = backend.Kill;               // exactly what the watchdog path calls
				exited = backend.Exited;
				line = backend.StandardOutput.ReadLine()!;
			}
			else
			{
				var utf8 = new UTF8Encoding(false);
				var psi = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = utf8, StandardErrorEncoding = utf8 };
				psi.ArgumentList.Add("-I"); psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(Script); psi.ArgumentList.Add(marker);
				var job = new JobObject();
				var process = Process.Start(psi)!;   // holds the handle CreateProcess returned
				job.Assign(process);
				started = process;
				kill = () =>
				{
					job.Dispose();
					try
					{
						process.Kill();
					}
					catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
					{
					}

					foreach (var p in owned)
					{
						p.Terminate(1);
					}
				};
				exited = process.WaitForExitAsync();
				line = process.StandardOutput.ReadLine()!;
			}

			var pids = line.Split(' ').Select(int.Parse).ToArray();
			var interpreter = OwnedProcess.Open(pids[0], runStart, marker, out var whyInterpreter);
			var grandchild = OwnedProcess.Open(pids[1], runStart, marker, out var whyGrandchild);
			if (interpreter is not null) owned.Add(interpreter);
			if (grandchild is not null) owned.Add(grandchild);
			if (interpreter is null || grandchild is null)
			{
				unproved++;
				Console.WriteLine($"#{i} NOT PROVED this run's, left alone: {string.Join("; ", new[] { whyInterpreter, whyGrandchild }.Where(x => x.Length > 0))}");
			}

			if (suspend && grandchild is not null)
			{
				grandchild.Suspend();
				Thread.Sleep(300);
			}

			if (dispose)
			{
				_ = exited.ContinueWith(_ => { if (delay > 0) Thread.Sleep(delay); started.Dispose(); }, TaskScheduler.Default);
			}

			string outcome;
			try
			{
				var sw = Stopwatch.StartNew();
				kill();
				killMs.Add(sw.ElapsedMilliseconds);
				kill();                                // and again, racing the dispose above
				outcome = "ok";
			}
			catch (Exception ex)
			{
				outcome = "ESCAPED " + ex.GetType().Name + ": " + ex.Message;
			}

			outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
			foreach (var p in owned)
			{
				p.WaitForExit(5000);
			}

			if (owned.Any(p => !p.HasExited))
			{
				survivors++;
				foreach (var p in owned)
				{
					p.Terminate(1);
				}
			}

			foreach (var p in owned)
			{
				p.Dispose();
			}

			if (!dispose)
			{
				started.Dispose();
			}
		}

		foreach (var (k, v) in outcomes) Console.WriteLine($"{v}x {k}");
		Console.WriteLine($"SUMMARY mode={mode} runs={runs} suspend={suspend} dispose={dispose}/{delay}ms kill ms: min {killMs.DefaultIfEmpty().Min()} max {killMs.DefaultIfEmpty().Max()} avg {(killMs.Count > 0 ? killMs.Average() : 0):0}; runs with a survivor: {survivors}; runs with a process not proved this run's: {unproved}");
		return outcomes.Keys.All(x => x == "ok") && survivors == 0 && unproved == 0 ? 0 : 1;
	}
}
