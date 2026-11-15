using System.Diagnostics;
using FluentFin.Mpv;

namespace FluentFin.Tests.Mpv;

public sealed class JobObjectTests
{
	[Fact]
	public async Task ChildrenOfAProcessInTheJob_AreKilledWithIt_EvenWhenTheirParentIsGone()
	{
		// like the venv's python.exe launcher and the interpreter (and mpv.exe) it starts: kill only the parent and the
		// child lives on, until the job closes
		var before = Process.GetProcessesByName("PING").Select(x => x.Id).ToHashSet();
		var parent = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c PING.EXE -n 60 127.0.0.1")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
		})!;
		var job = new JobObject();
		job.Assign(parent);

		var pings = new List<Process>();
		try
		{
			for (var attempt = 0; attempt < 50 && pings.Count == 0; attempt++)
			{
				await Task.Delay(100);
				pings = [.. Process.GetProcessesByName("PING").Where(x => !before.Contains(x.Id))];
			}

			Assert.NotEmpty(pings);
			parent.Kill();
			await parent.WaitForExitAsync();
			Assert.Contains(pings, x => !x.HasExited);

			job.Dispose();

			foreach (var ping in pings)
			{
				await ping.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
			}
		}
		finally
		{
			foreach (var ping in pings.Where(x => !x.HasExited))
			{
				ping.Kill();
			}

			job.Dispose();
			parent.Dispose();
		}
	}

	[Fact]
	public async Task ClosingTheJob_KillsTheProcessesInIt()
	{
		// a harmless long-running child: ping waits a second between echoes
		var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "PING.EXE"), "-n 60 127.0.0.1")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
		})!;

		try
		{
			var job = new JobObject();
			job.Assign(process);
			Assert.False(process.HasExited);

			job.Dispose();

			await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
			Assert.True(process.HasExited);
		}
		finally
		{
			if (!process.HasExited)
			{
				process.Kill();
			}
			process.Dispose();
		}
	}
}
