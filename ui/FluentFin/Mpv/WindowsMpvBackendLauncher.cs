using System.Text;
using FluentFin.Core;
using FluentFin.Core.Mpv;
using FluentFin.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FluentFin.Mpv;

/// <summary>
/// Starts the backend with its stdio on pipes (UTF-8, no BOM), no console window, its config dir as working directory,
/// inside its own kill-on-close job object from its first instruction (<see cref="JobProcess"/>): everything it starts
/// (the venv's real interpreter behind the launcher python.exe, an external mpv.exe) dies when that backend is stopped,
/// killed or crashes, and at the latest when FluentFin exits, because then Windows closes the job handles.
/// </summary>
public sealed class WindowsMpvBackendLauncher(ISettings settings, KnownFolders knownFolders, ILogger<WindowsMpvBackendLauncher> logger) : IMpvBackendLauncher
{
	private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);

	/// <summary>%LOCALAPPDATA%\FluentFinMpv\mpv-backend: the shim's conf.json, mpv.conf, input.conf and shaders, apart from a standalone shim install.</summary>
	public string ConfigDirectory => Path.Combine(knownFolders.ApplicationData, "mpv-backend");

	public string LogFile => Path.Combine(knownFolders.Logs, "mpv-backend.log");

	public MpvBackendCommand? Resolve(bool dev, out string searched)
	{
		Directory.CreateDirectory(ConfigDirectory);
		return MpvBackendCommand.Resolve(settings.MpvBackendCommand, AppContext.BaseDirectory, ConfigDirectory, LogFile, dev, File.Exists, out searched);
	}

	public IMpvBackendProcess Start(MpvBackendCommand command)
	{
		Directory.CreateDirectory(ConfigDirectory);
		return StartInJob(command.FileName, command.Arguments, ConfigDirectory, logger);
	}

	/// <summary>The backend process in a new job; throws (leaving nothing running) when it cannot be started inside one.</summary>
	internal static IMpvBackendProcess StartInJob(string fileName, IReadOnlyList<string> arguments, string? workingDirectory, ILogger? logger = null,
												  Action<Microsoft.Win32.SafeHandles.SafeProcessHandle>? whileSuspended = null)
	{
		var job = new JobObject();
		try
		{
			var process = JobProcess.Start(fileName, arguments, workingDirectory, job, whileSuspended);
			return new BackendProcess(process, job, logger ?? NullLogger.Instance);
		}
		catch
		{
			job.Dispose();
			throw;
		}
	}

	internal sealed class BackendProcess : IMpvBackendProcess
	{
		/// <summary>The exit code of a backend ended by <see cref="Kill"/>.</summary>
		public const uint KilledExitCode = 0xDEAD;

		private readonly JobProcess _process;
		private readonly JobObject _job;
		private readonly ILogger _logger;

		public BackendProcess(JobProcess process, JobObject job, ILogger logger)
		{
			_process = process;
			_job = job;
			_logger = logger;
			Id = process.Id;
			StandardInput = new StreamWriter(process.StandardInput, s_utf8, 4096) { AutoFlush = true };
			StandardOutput = new StreamReader(process.StandardOutput, s_utf8, detectEncodingFromByteOrderMarks: true, 4096);
			StandardError = new StreamReader(process.StandardError, s_utf8, detectEncodingFromByteOrderMarks: true, 4096);
		}

		public int Id { get; }
		public TextReader StandardOutput { get; }
		public TextWriter StandardInput { get; }
		public TextReader StandardError { get; }
		public Task<int> Exited => _process.Exited;
		public long? ExitedAt => _process.ExitedAt;

		/// <summary>Asked of Windows, not whether the exit was seen yet (docs/BRIDGE.md §6: what happened first).</summary>
		public bool HasExited => _process.HasExited;

		internal JobProcess Process => _process;
		internal JobObject Job => _job;

		/// <summary>
		/// Ends everything in the job now: the backend, the interpreter behind the venv launcher, mpv. Never throws: it runs
		/// on the watchdog's timer thread and on shutdown paths, where an exception would take FluentFin down.
		/// </summary>
		public void Kill()
		{
			try
			{
				if (_job.Terminate(KilledExitCode))
				{
					return;
				}

				// the job is already closed (Dispose) or Windows refused: closing it kills what is left, and the backend
				// itself is ended directly in case it never got into the job
				_job.Dispose();
				_process.TerminateOnly(KilledExitCode);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Killing the mpv backend (pid {Pid}) failed", Id);
			}
		}

		/// <summary>After the exit (or a kill): closing the job ends whatever the backend left behind.</summary>
		public void Dispose()
		{
			_job.Dispose();
			try
			{
				_process.Dispose();
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Releasing the mpv backend's handles failed");
			}
		}
	}
}
