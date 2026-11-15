using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FluentFin.Mpv;

/// <summary>
/// A Windows job object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: every process in it (and every process those start,
/// including ones in jobs nested under it such as the venv launcher's own job) is killed when the last handle to the job
/// closes, which is at the latest when FluentFin exits or crashes. The handle is not inheritable, so a child cannot keep
/// the job alive. The handle is a SafeHandle, so a <see cref="Terminate"/> racing <see cref="Dispose"/> never uses a
/// closed (or reused) handle value.
/// </summary>
internal sealed unsafe partial class JobObject : IDisposable
{
	private const int JobObjectExtendedLimitInformation = 9;
	private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

	private readonly SafeJobHandle _handle;

	public JobObject()
	{
		_handle = CreateJobObjectW(0, null);
		if (_handle.IsInvalid)
		{
			throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateJobObject failed");
		}

		var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
		info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
		if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, &info, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)))
		{
			var error = Marshal.GetLastPInvokeError();
			_handle.Dispose();
			throw new Win32Exception(error, "SetInformationJobObject failed");
		}
	}

	/// <summary>For <see cref="JobProcess"/>: the handle goes into PROC_THREAD_ATTRIBUTE_JOB_LIST.</summary>
	internal SafeHandle Handle => _handle;

	public bool IsClosed => _handle.IsClosed;

	public void Assign(Process process)
	{
		ObjectDisposedException.ThrowIf(_handle.IsClosed, this);

		if (!AssignProcessToJobObject(_handle, process.SafeHandle))
		{
			throw new Win32Exception(Marshal.GetLastPInvokeError(), "AssignProcessToJobObject failed");
		}
	}

	public void Assign(SafeProcessHandle process)
	{
		ObjectDisposedException.ThrowIf(_handle.IsClosed, this);

		if (!AssignProcessToJobObject(_handle, process))
		{
			throw new Win32Exception(Marshal.GetLastPInvokeError(), "AssignProcessToJobObject failed");
		}
	}

	/// <summary>Whether the process is in this job (or a job nested under it). False once the job is closed.</summary>
	public bool Contains(SafeProcessHandle process)
	{
		try
		{
			return IsProcessInJob(process, _handle, out var result) && result;
		}
		catch (ObjectDisposedException)
		{
			return false;
		}
	}

	/// <summary>
	/// Ends every process in the job (and in jobs nested under it) now. Never throws: false when the job is already closed
	/// or Windows refused, in which case closing the job (<see cref="Dispose"/>) still kills what is left.
	/// </summary>
	public bool Terminate(uint exitCode)
	{
		try
		{
			return TerminateJobObject(_handle, exitCode);
		}
		catch (ObjectDisposedException)
		{
			return false;
		}
	}

	/// <summary>Closing the handle kills every process in the job.</summary>
	public void Dispose() => _handle.Dispose();

	internal sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
	{
		protected override bool ReleaseHandle() => CloseHandle(handle);
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
	{
		public long PerProcessUserTimeLimit;
		public long PerJobUserTimeLimit;
		public uint LimitFlags;
		public nuint MinimumWorkingSetSize;
		public nuint MaximumWorkingSetSize;
		public uint ActiveProcessLimit;
		public nuint Affinity;
		public uint PriorityClass;
		public uint SchedulingClass;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct IO_COUNTERS
	{
		public ulong ReadOperationCount;
		public ulong WriteOperationCount;
		public ulong OtherOperationCount;
		public ulong ReadTransferCount;
		public ulong WriteTransferCount;
		public ulong OtherTransferCount;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
	{
		public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
		public IO_COUNTERS IoInfo;
		public nuint ProcessMemoryLimit;
		public nuint JobMemoryLimit;
		public nuint PeakProcessMemoryUsed;
		public nuint PeakJobMemoryUsed;
	}

	[LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
	private static partial SafeJobHandle CreateJobObjectW(nint lpJobAttributes, string? lpName);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetInformationJobObject(SafeJobHandle hJob, int jobObjectInfoClass, void* lpJobObjectInfo, uint cbJobObjectInfoLength);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool AssignProcessToJobObject(SafeJobHandle hJob, SafeProcessHandle hProcess);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool IsProcessInJob(SafeProcessHandle process, SafeJobHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool TerminateJobObject(SafeJobHandle hJob, uint exitCode);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool CloseHandle(nint hObject);
}
