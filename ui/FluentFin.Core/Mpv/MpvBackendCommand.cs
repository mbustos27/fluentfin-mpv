using System.Text;

namespace FluentFin.Core.Mpv;

/// <summary>How to start the backend. The arguments never carry a secret (docs/BRIDGE.md §1).</summary>
public sealed record MpvBackendCommand(string FileName, IReadOnlyList<string> Arguments, string Source)
{
	public const string FrozenExeName = "fluentfin-mpv-backend.exe";
	public const string BridgeModule = "jellyfin_mpv_shim.bridge";

	public override string ToString() => string.Join(' ', new[] { FileName }.Concat(Arguments).Select(Quote));

	/// <summary>
	/// Finds the backend: the user's override (everything before <c>--config-dir</c>), else a frozen
	/// <c>&lt;appdir&gt;\backend\fluentfin-mpv-backend.exe</c>, else a dev checkout found by walking up from the app folder to
	/// <c>backend\pyproject.toml</c> and using <c>backend\.venv\Scripts\python.exe -I -m jellyfin_mpv_shim.bridge</c>.
	/// Returns null with the places looked at when nothing is found.
	/// </summary>
	public static MpvBackendCommand? Resolve(string? overrideCommand,
											 string appDirectory,
											 string configDirectory,
											 string? logFile,
											 bool dev,
											 Func<string, bool> fileExists,
											 out string searched)
	{
		var tail = new List<string> { "--config-dir", configDirectory };
		if (!string.IsNullOrEmpty(logFile))
		{
			tail.AddRange(["--log-file", logFile]);
		}
		if (dev)
		{
			tail.Add("--dev");
		}

		if (!string.IsNullOrWhiteSpace(overrideCommand))
		{
			var parts = SplitCommandLine(overrideCommand);
			searched = $"the backend command set in Settings ({overrideCommand})";
			return parts.Count == 0 ? null : new MpvBackendCommand(parts[0], [.. parts.Skip(1), .. tail], "settings");
		}

		var looked = new List<string>();

		var frozen = Path.Combine(appDirectory, "backend", FrozenExeName);
		looked.Add(frozen);
		if (fileExists(frozen))
		{
			searched = frozen;
			return new MpvBackendCommand(frozen, tail, "bundled");
		}

		var checkout = false;
		for (var directory = new DirectoryInfo(appDirectory); directory is not null; directory = directory.Parent)
		{
			var backend = Path.Combine(directory.FullName, "backend");
			if (!fileExists(Path.Combine(backend, "pyproject.toml")))
			{
				continue;
			}

			checkout = true;
			var python = Path.Combine(backend, ".venv", "Scripts", "python.exe");
			looked.Add(python);
			if (fileExists(python))
			{
				searched = python;
				return new MpvBackendCommand(python, ["-I", "-m", BridgeModule, .. tail], "dev checkout");
			}

			break;
		}

		if (!checkout)
		{
			looked.Add($"a backend\\pyproject.toml in a folder above {appDirectory}");
		}
		searched = string.Join("; ", looked);
		return null;
	}

	/// <summary>Splits a command line the way Windows programs do for the common cases: spaces separate, double quotes group.</summary>
	public static List<string> SplitCommandLine(string commandLine)
	{
		var parts = new List<string>();
		var current = new StringBuilder();
		var inQuotes = false;
		var hasToken = false;

		foreach (var c in commandLine)
		{
			if (c == '"')
			{
				inQuotes = !inQuotes;
				hasToken = true;
			}
			else if (char.IsWhiteSpace(c) && !inQuotes)
			{
				if (hasToken)
				{
					parts.Add(current.ToString());
					current.Clear();
					hasToken = false;
				}
			}
			else
			{
				current.Append(c);
				hasToken = true;
			}
		}

		if (hasToken)
		{
			parts.Add(current.ToString());
		}

		return parts;
	}

	private static string Quote(string value) => value.Length == 0 || value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;
}
