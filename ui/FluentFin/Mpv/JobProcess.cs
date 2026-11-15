using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FluentFin.Mpv;

/// <summary>
/// A child process that is inside a job object from its first instruction (docs/BRIDGE.md §6, "Embedded input and hangs"):
/// created suspended with PROC_THREAD_ATTRIBUTE_JOB_LIST, checked to be in the job (assigned while still suspended if the
/// attribute did not take), then resumed. Anything it starts afterwards (the venv launcher's real interpreter, mpv) is in
/// the job too, so closing or terminating the job ends all of it. Its stdin, stdout and stderr are anonymous pipes; only
/// the child's ends are inheritable, and only those three handles are inherited (PROC_THREAD_ATTRIBUTE_HANDLE_LIST).
/// No console window, no shell, the caller's environment.
/// </summary>
internal sealed unsafe partial class JobProcess : IDisposable
{
	private const uint STARTF_USESTDHANDLES = 0x00000100;
	private const uint CREATE_SUSPENDED = 0x00000004;
	private const uint CREATE_NO_WINDOW = 0x08000000;
	private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
	private const nint PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x00020002;
	private const nint PROC_THREAD_ATTRIBUTE_JOB_LIST = 0x0002000D;
	private const uint HANDLE_FLAG_INHERIT = 0x00000001;
	private const uint DUPLICATE_SAME_ACCESS = 0x00000002;
	private const uint INFINITE = 0xFFFFFFFF;
	private const uint WAIT_OBJECT_0 = 0x00000000;
	private const uint WAIT_TIMEOUT = 0x00000102;

	private readonly SafeProcessHandle _process;
	private long _exitedAt;

	private JobProcess(SafeProcessHandle process, int id, Stream standardInput, Stream standardOutput, Stream standardError, bool startedWithJobList)
	{
		_process = process;
		Id = id;
		StandardInput = standardInput;
		StandardOutput = standardOutput;
		StandardError = standardError;
		StartedWithJobList = startedWithJobList;
		Exited = WaitForExit(process, at => Volatile.Write(ref _exitedAt, at));
	}

	public int Id { get; }

	/// <summary>The child's stdin (write end).</summary>
	public Stream StandardInput { get; }

	/// <summary>The child's stdout (read end).</summary>
	public Stream StandardOutput { get; }

	/// <summary>The child's stderr (read end).</summary>
	public Stream StandardError { get; }

	/// <summary>Completes with the exit code when the process has exited.</summary>
	public Task<int> Exited { get; }

	/// <summary>
	/// When the process ended (<see cref="Environment.TickCount64"/>), from the exit time Windows recorded for it: set before
	/// <see cref="Exited"/> completes, and right even when the wait's callback ran late. Null before the exit.
	/// </summary>
	public long? ExitedAt => Volatile.Read(ref _exitedAt) is var at and not 0 ? at : null;

	/// <summary>
	/// The process has ended, asked of Windows now (its handle is signalled from the moment it ended): true before the wait
	/// behind <see cref="Exited"/> has run its callback, which a busy thread pool can delay by seconds. After
	/// <see cref="Dispose"/>, what <see cref="Exited"/> says. Never throws.
	/// </summary>
	public bool HasExited
	{
		get
		{
			try
			{
				return IsSignalled(_process) ?? Exited.IsCompleted;
			}
			catch (ObjectDisposedException)
			{
				return Exited.IsCompleted;
			}
		}
	}

	/// <summary>
	/// WaitForSingleObject with no wait: whether the process behind <paramref name="process"/> has ended; null when Windows
	/// cannot say (the wait failed).
	/// </summary>
	internal static bool? IsSignalled(SafeProcessHandle process)
	{
		var added = false;
		try
		{
			process.DangerousAddRef(ref added);
			return WaitForSingleObject(process.DangerousGetHandle(), 0) switch
			{
				WAIT_OBJECT_0 => true,
				WAIT_TIMEOUT => false,
				_ => null,
			};
		}
		finally
		{
			if (added)
			{
				process.DangerousRelease();
			}
		}
	}

	public SafeProcessHandle Handle => _process;

	/// <summary>True when PROC_THREAD_ATTRIBUTE_JOB_LIST put it in the job; false when it was assigned while suspended.</summary>
	public bool StartedWithJobList { get; }

	/// <summary>
	/// Starts <paramref name="fileName"/> with <paramref name="arguments"/> in <paramref name="job"/>.
	/// <paramref name="whileSuspended"/> runs after the process exists and before its first instruction (tests).
	/// Throws when the process cannot be started or cannot be put in the job; then nothing of it is left running.
	/// </summary>
	public static JobProcess Start(string fileName, IReadOnlyList<string> arguments, string? workingDirectory, JobObject job,
								   Action<SafeProcessHandle>? whileSuspended = null, bool useJobList = true)
	{
		ArgumentException.ThrowIfNullOrEmpty(fileName);

		var commandLine = BuildCommandLine(fileName, arguments);

		SafeFileHandle? stdinChild = null, stdinParent = null, stdoutParent = null, stdoutChild = null, stderrParent = null, stderrChild = null;
		try
		{
			CreatePipeOrThrow(out stdinChild, out stdinParent);     // the child reads, we write
			CreatePipeOrThrow(out stdoutParent, out stdoutChild);   // the child writes, we read
			CreatePipeOrThrow(out stderrParent, out stderrChild);

			var info = CreateSuspendedInJob(commandLine, workingDirectory, job, stdinChild, stdoutChild, stderrChild, useJobList, out var startedWithJobList);

			var process = new SafeProcessHandle(info.hProcess, ownsHandle: true);
			var thread = info.hThread;
			try
			{
				if (!job.Contains(process))
				{
					// the attribute did not take (or was not used): the process has not run a single instruction yet
					job.Assign(process);
				}

				whileSuspended?.Invoke(process);

				if (ResumeThread(thread) == uint.MaxValue)
				{
					throw new Win32Exception(Marshal.GetLastPInvokeError(), "ResumeThread failed");
				}
			}
			catch
			{
				// never leave a suspended process (or one outside the job) behind
				TerminateProcess(process, 1);
				process.Dispose();
				throw;
			}
			finally
			{
				CloseHandle(thread);
			}

			// our copies of the child's ends: the pipes report EOF when the child's copies close
			stdinChild.Dispose();
			stdoutChild.Dispose();
			stderrChild.Dispose();

			return new JobProcess(process, (int)info.dwProcessId,
				new FileStream(stdinParent, FileAccess.Write, 4096, isAsync: false),
				new FileStream(stdoutParent, FileAccess.Read, 4096, isAsync: false),
				new FileStream(stderrParent, FileAccess.Read, 4096, isAsync: false),
				startedWithJobList);
		}
		catch
		{
			foreach (var handle in new[] { stdinChild, stdinParent, stdoutParent, stdoutChild, stderrParent, stderrChild })
			{
				handle?.Dispose();
			}
			throw;
		}
	}

	private static PROCESS_INFORMATION CreateSuspendedInJob(string commandLine, string? workingDirectory, JobObject job,
															 SafeFileHandle stdin, SafeFileHandle stdout, SafeFileHandle stderr,
															 bool useJobList, out bool startedWithJobList)
	{
		var refs = new List<SafeHandle>(4);
		try
		{
			foreach (var safe in new SafeHandle[] { stdin, stdout, stderr, job.Handle })
			{
				var added = false;
				safe.DangerousAddRef(ref added);
				refs.Add(safe);
			}

			var handles = stackalloc nint[3] { stdin.DangerousGetHandle(), stdout.DangerousGetHandle(), stderr.DangerousGetHandle() };
			var jobHandle = job.Handle.DangerousGetHandle();

			// the child's ends are inheritable only around CreateProcess, so no other process started meanwhile picks them up
			for (var i = 0; i < 3; i++)
			{
				if (!SetHandleInformation(handles[i], HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT))
				{
					throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetHandleInformation failed");
				}
			}

			try
			{
				var attributeCount = useJobList ? 2u : 1u;
				nuint size = 0;
				InitializeProcThreadAttributeList(null, attributeCount, 0, &size);
				var attributes = (void*)Marshal.AllocHGlobal((nint)size);
				try
				{
					if (!InitializeProcThreadAttributeList(attributes, attributeCount, 0, &size))
					{
						throw new Win32Exception(Marshal.GetLastPInvokeError(), "InitializeProcThreadAttributeList failed");
					}

					try
					{
						if (!UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles, (nuint)(3 * sizeof(nint)), null, null))
						{
							throw new Win32Exception(Marshal.GetLastPInvokeError(), "UpdateProcThreadAttribute(HANDLE_LIST) failed");
						}

						// Windows 10+: in the job at creation. Older systems refuse the attribute; the caller assigns while suspended.
						startedWithJobList = useJobList && UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_JOB_LIST, &jobHandle, (nuint)sizeof(nint), null, null);

						var startup = new STARTUPINFOEXW();
						startup.StartupInfo.cb = (uint)sizeof(STARTUPINFOEXW);
						startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
						startup.StartupInfo.hStdInput = handles[0];
						startup.StartupInfo.hStdOutput = handles[1];
						startup.StartupInfo.hStdError = handles[2];
						startup.lpAttributeList = attributes;

						// CreateProcessW may write into the command line buffer
						var buffer = new char[commandLine.Length + 1];
						commandLine.CopyTo(buffer);
						PROCESS_INFORMATION info;
						fixed (char* command = buffer)
						fixed (char* directory = workingDirectory)
						{
							if (!CreateProcessW(null, command, null, null, true,
												CREATE_SUSPENDED | CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT,
												null, string.IsNullOrEmpty(workingDirectory) ? null : directory,
												&startup, &info))
							{
								var error = Marshal.GetLastPInvokeError();
								throw new Win32Exception(error, $"Could not start {commandLine}: {new Win32Exception(error).Message}");
							}
						}

						return info;
					}
					finally
					{
						DeleteProcThreadAttributeList(attributes);
					}
				}
				finally
				{
					Marshal.FreeHGlobal((nint)attributes);
				}
			}
			finally
			{
				for (var i = 0; i < 3; i++)
				{
					SetHandleInformation(handles[i], HANDLE_FLAG_INHERIT, 0);
				}
			}
		}
		finally
		{
			foreach (var safe in refs)
			{
				safe.DangerousRelease();
			}
		}
	}

	/// <summary>
	/// The command line the way <see cref="System.Diagnostics.Process"/> builds it: the program in quotes, then each argument
	/// quoted and escaped so CommandLineToArgvW and the C runtime (Python's argv) read it back unchanged.
	/// </summary>
	internal static string BuildCommandLine(string fileName, IReadOnlyList<string> arguments)
	{
		var builder = new StringBuilder();
		var program = fileName.Trim();
		if (program.Length >= 2 && program[0] == '"' && program[^1] == '"')
		{
			builder.Append(program);
		}
		else
		{
			builder.Append('"').Append(program).Append('"');
		}

		foreach (var argument in arguments)
		{
			builder.Append(' ');
			AppendArgument(builder, argument);
		}

		return builder.ToString();
	}

	private static void AppendArgument(StringBuilder builder, string argument)
	{
		if (argument.Length != 0 && argument.AsSpan().IndexOfAny(" \t\n\v\"") < 0)
		{
			builder.Append(argument);
			return;
		}

		builder.Append('"');
		var index = 0;
		while (index < argument.Length)
		{
			var c = argument[index++];
			if (c == '\\')
			{
				var backslashes = 1;
				while (index < argument.Length && argument[index] == '\\')
				{
					index++;
					backslashes++;
				}

				if (index == argument.Length)
				{
					// before the closing quote: every backslash doubled
					builder.Append('\\', backslashes * 2);
				}
				else if (argument[index] == '"')
				{
					// before a literal quote: doubled, plus one for the quote
					builder.Append('\\', backslashes * 2 + 1).Append('"');
					index++;
				}
				else
				{
					builder.Append('\\', backslashes);
				}

				continue;
			}

			if (c == '"')
			{
				builder.Append('\\').Append('"');
				continue;
			}

			builder.Append(c);
		}

		builder.Append('"');
	}

	private static void CreatePipeOrThrow(out SafeFileHandle read, out SafeFileHandle write)
	{
		// not inheritable here: only the child's end becomes inheritable, and only while CreateProcess runs
		if (!CreatePipe(out read, out write, null, 0))
		{
			throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreatePipe failed");
		}
	}

	/// <summary>
	/// Waits on a duplicate of the handle, so disposing this object while the process runs cannot pull the handle away from the
	/// wait. <paramref name="exitedAt"/> gets the exit time before the task completes.
	/// </summary>
	private static Task<int> WaitForExit(SafeProcessHandle process, Action<long> exitedAt)
	{
		var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
		var self = GetCurrentProcess();
		if (!DuplicateHandle(self, process, self, out var duplicate, 0, false, DUPLICATE_SAME_ACCESS))
		{
			throw new Win32Exception(Marshal.GetLastPInvokeError(), "DuplicateHandle failed");
		}

		var waitHandle = new ExitWaitHandle(duplicate);
		RegisteredWaitHandle? registration = null;
		var registrationLock = new Lock();
		var completed = false;

		void OnExit(object? state, bool timedOut)
		{
			var code = GetExitCodeProcess(duplicate, out var exitCode) ? unchecked((int)exitCode) : -1;
			exitedAt(ExitTickCount(duplicate));
			lock (registrationLock)
			{
				completed = true;
				registration?.Unregister(null);
			}
			waitHandle.Dispose();
			exited.TrySetResult(code);
		}

		lock (registrationLock)
		{
			registration = ThreadPool.RegisterWaitForSingleObject(waitHandle, OnExit, null, Timeout.Infinite, executeOnlyOnce: true);
			if (completed)
			{
				registration.Unregister(null);
			}
		}

		return exited.Task;
	}

	/// <summary>The process's exit time from GetProcessTimes as an <see cref="Environment.TickCount64"/> value; now when Windows does not say.</summary>
	private static long ExitTickCount(SafeWaitHandle process) =>
		GetProcessTimes(process, out _, out var exit, out _, out _)
			? TickCountOf(exit, Environment.TickCount64, DateTime.UtcNow)
			: Environment.TickCount64;

	/// <summary>
	/// A UTC FILETIME as an <see cref="Environment.TickCount64"/> value, given both clocks read now: as long ago as the
	/// FILETIME is. Never later than now (the clocks tick at different moments); now for no time.
	/// </summary>
	internal static long TickCountOf(long fileTimeUtc, long nowTicks, DateTime nowUtc)
	{
		if (fileTimeUtc <= 0)
		{
			return nowTicks;
		}

		var ago = nowUtc - DateTime.FromFileTimeUtc(fileTimeUtc);
		return ago <= TimeSpan.Zero ? nowTicks : nowTicks - (long)ago.TotalMilliseconds;
	}

	/// <summary>Closes our ends of the pipes and the process handle. Does not end the process: the job does that.</summary>
	public void Dispose()
	{
		foreach (var stream in new[] { StandardInput, StandardOutput, StandardError })
		{
			try
			{
				stream.Dispose();
			}
			catch (Exception ex) when (ex is IOException or ObjectDisposedException)
			{
			}
		}

		_process.Dispose();
	}

	/// <summary>Ends just this process (when the job could not be terminated). Never throws.</summary>
	public bool TerminateOnly(uint exitCode)
	{
		try
		{
			return TerminateProcess(_process, exitCode);
		}
		catch (ObjectDisposedException)
		{
			return false;
		}
	}

	private sealed class ExitWaitHandle : WaitHandle
	{
		public ExitWaitHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct STARTUPINFOW
	{
		public uint cb;
		public char* lpReserved;
		public char* lpDesktop;
		public char* lpTitle;
		public uint dwX;
		public uint dwY;
		public uint dwXSize;
		public uint dwYSize;
		public uint dwXCountChars;
		public uint dwYCountChars;
		public uint dwFillAttribute;
		public uint dwFlags;
		public ushort wShowWindow;
		public ushort cbReserved2;
		public nint lpReserved2;
		public nint hStdInput;
		public nint hStdOutput;
		public nint hStdError;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct STARTUPINFOEXW
	{
		public STARTUPINFOW StartupInfo;
		public void* lpAttributeList;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct PROCESS_INFORMATION
	{
		public nint hProcess;
		public nint hThread;
		public uint dwProcessId;
		public uint dwThreadId;
	}

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, void* pipeAttributes, uint size);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetHandleInformation(nint handle, uint mask, uint flags);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool InitializeProcThreadAttributeList(void* attributeList, uint attributeCount, uint flags, nuint* size);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool UpdateProcThreadAttribute(void* attributeList, uint flags, nint attribute, void* value, nuint size, void* previousValue, nuint* returnSize);

	[LibraryImport("kernel32.dll")]
	private static partial void DeleteProcThreadAttributeList(void* attributeList);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool CreateProcessW(char* applicationName, char* commandLine, void* processAttributes, void* threadAttributes,
											   [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, void* environment,
											   char* currentDirectory, STARTUPINFOEXW* startupInfo, PROCESS_INFORMATION* processInformation);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	private static partial uint ResumeThread(nint thread);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetExitCodeProcess(SafeWaitHandle process, out uint exitCode);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

	[LibraryImport("kernel32.dll")]
	private static partial nint GetCurrentProcess();

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetProcessTimes(SafeWaitHandle process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool DuplicateHandle(nint sourceProcess, SafeProcessHandle sourceHandle, nint targetProcess, out SafeWaitHandle targetHandle,
												uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint options);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool CloseHandle(nint handle);
}
