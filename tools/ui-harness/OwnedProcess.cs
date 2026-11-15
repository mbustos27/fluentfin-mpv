// Process safety for the harnesses that start a backend stand-in (killharness, jobharness; linked from their .csproj):
// a process the harness did not start itself (the interpreter behind the venv launcher, the grandchild it starts) is
// opened once by pid and acted on only through that handle, and only after the handle proved it is this run's: created
// at or after the run began (GetProcessTimes) and with the run's unique marker in its command line (the marker is an
// argument of the script, and the grandchild's argv[0]). The pid is never opened again, and while the handle is open
// Windows cannot give the pid to another process.
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

sealed class OwnedProcess : IDisposable
{
	const uint PROCESS_TERMINATE = 0x0001, PROCESS_SUSPEND_RESUME = 0x0800, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, SYNCHRONIZE = 0x00100000;
	const uint WAIT_OBJECT_0 = 0;

	OwnedProcess(int id, SafeProcessHandle handle, long created)
	{
		Id = id;
		Handle = handle;
		CreatedUtc = created;
	}

	public int Id { get; }

	/// <summary>The handle every check and every action goes through.</summary>
	public SafeProcessHandle Handle { get; }

	/// <summary>Creation time, FILETIME UTC.</summary>
	public long CreatedUtc { get; }

	/// <summary>Now on the clock Windows stamps a process's creation with (FILETIME UTC): read it before starting anything.</summary>
	public static long Now()
	{
		GetSystemTimeAsFileTime(out var now);
		return now;
	}

	/// <summary>A marker no other process has in its command line.</summary>
	public static string NewMarker(string tool) => $"{tool}-{Environment.ProcessId}-{Guid.NewGuid():N}";

	/// <summary>
	/// Opens <paramref name="pid"/> once and returns it only if, through that handle, it was created at or after
	/// <paramref name="notBefore"/> and its command line contains <paramref name="marker"/>; else null and why not.
	/// </summary>
	public static OwnedProcess? Open(int pid, long notBefore, string marker, out string why)
	{
		var handle = OpenProcess(PROCESS_TERMINATE | PROCESS_SUSPEND_RESUME | PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, false, pid);
		if (handle.IsInvalid)
		{
			why = $"pid {pid} cannot be opened (error {Marshal.GetLastPInvokeError()})";
			handle.Dispose();
			return null;
		}

		if (!GetProcessTimes(handle, out var created, out _, out _, out _))
		{
			why = $"pid {pid}: no creation time";
		}
		else if (created < notBefore)
		{
			why = $"pid {pid} was started before this run began";
		}
		else if (CommandLineOf(handle) is not { } commandLine || !commandLine.Contains(marker, StringComparison.Ordinal))
		{
			why = $"pid {pid} does not carry this run's marker in its command line";
		}
		else
		{
			why = "";
			return new OwnedProcess(pid, handle, created);
		}

		handle.Dispose();
		return null;
	}

	public bool HasExited => WaitForSingleObject(Handle, 0) == WAIT_OBJECT_0;

	/// <summary>True when it exited within <paramref name="milliseconds"/>.</summary>
	public bool WaitForExit(int milliseconds) => WaitForSingleObject(Handle, (uint)milliseconds) == WAIT_OBJECT_0;

	public bool Terminate(uint exitCode) => TerminateProcess(Handle, exitCode);

	public bool Suspend() => NtSuspendProcess(Handle) == 0;

	public void Dispose() => Handle.Dispose();

	/// <summary>ProcessCommandLineInformation (60): a UNICODE_STRING followed by the text, in the buffer given.</summary>
	static unsafe string? CommandLineOf(SafeProcessHandle handle)
	{
		NtQueryInformationProcess(handle, 60, null, 0, out var needed);
		if (needed <= 0 || needed > 1 << 20)
		{
			return null;
		}

		var buffer = new byte[needed];
		fixed (byte* p = buffer)
		{
			if (NtQueryInformationProcess(handle, 60, p, needed, out _) != 0)
			{
				return null;
			}

			var length = *(ushort*)p;
			var text = *(char**)(p + IntPtr.Size);
			return text is null ? null : new string(text, 0, length / 2);
		}
	}

	[DllImport("kernel32.dll", SetLastError = true)] static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
	[DllImport("kernel32.dll")] static extern bool GetProcessTimes(SafeProcessHandle h, out long creation, out long exit, out long kernel, out long user);
	[DllImport("kernel32.dll")] static extern void GetSystemTimeAsFileTime(out long time);
	[DllImport("kernel32.dll")] static extern uint WaitForSingleObject(SafeProcessHandle h, uint milliseconds);
	[DllImport("kernel32.dll")] static extern bool TerminateProcess(SafeProcessHandle h, uint exitCode);
	[DllImport("ntdll.dll")] static extern int NtSuspendProcess(SafeProcessHandle h);
	[DllImport("ntdll.dll")] static extern unsafe int NtQueryInformationProcess(SafeProcessHandle h, int cls, void* info, int length, out int returned);
}
