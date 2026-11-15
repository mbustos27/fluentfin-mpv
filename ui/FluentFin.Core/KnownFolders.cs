namespace FluentFin.Core;

public class KnownFolders
{
	public string ApplicationData { get; }
	public string Updates { get; }
	public string Logs { get; }

	// fluentfin-mpv: its own folder, so this build never shares settings, saved logins or logs with an upstream FluentFin install
	public const string FolderName = "FluentFinMpv";

	/// <summary>
	/// fluentfin-mpv: <c>FluentFin.exe --data-dir &lt;folder&gt;</c> (or <c>--data-dir=&lt;folder&gt;</c>) keeps everything this
	/// run writes -- settings and saved logins, logs, the mpv backend's config folder and log -- under that folder instead of
	/// <c>%LOCALAPPDATA%\FluentFinMpv</c>: a dev or test run that never touches the user's own data (tools/README.md).
	/// </summary>
	public const string DataDirSwitch = "--data-dir";

	/// <summary>fluentfin-mpv: the same as <see cref="DataDirSwitch"/> from the environment; the switch wins when both are set.</summary>
	public const string DataDirVariable = "FLUENTFIN_MPV_DATA_DIR";

	/// <summary><c>%LOCALAPPDATA%\FluentFinMpv</c>.</summary>
	public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);

	/// <summary>True when the folder came from <see cref="DataDirSwitch"/> or <see cref="DataDirVariable"/>.</summary>
	public bool IsCustom { get; }

	/// <summary>The FluentFin log file (Serilog adds the date to its name).</summary>
	public string LogFile => Path.Combine(Logs, "log.txt");

	public KnownFolders() : this(DefaultRoot) { }

	public KnownFolders(string root) : this(root, isCustom: false) { }

	private KnownFolders(string root, bool isCustom)
	{
		ApplicationData = root;
		Updates = Path.Combine(root, "Updates");
		Logs = Path.Combine(root, "Logs");
		IsCustom = isCustom;

		Directory.CreateDirectory(ApplicationData);
		Directory.CreateDirectory(Logs);
		Directory.CreateDirectory(Updates);
	}

	/// <summary>
	/// The folders for this run: the one <see cref="ResolveDataDir"/> finds in <paramref name="arguments"/> (the command line
	/// without the program) or the environment, else <paramref name="defaultRoot"/> (<see cref="DefaultRoot"/> when null),
	/// which is not even asked for when a folder was given. Throws <see cref="ArgumentException"/> for a switch with no folder.
	/// </summary>
	public static KnownFolders ForStartup(IReadOnlyList<string> arguments, Func<string, string?> environment, Func<string>? defaultRoot = null) =>
		ResolveDataDir(arguments, environment) is { } custom
			? new KnownFolders(custom, isCustom: true)
			: new KnownFolders((defaultRoot ?? (() => DefaultRoot))());

	/// <summary>
	/// The data folder asked for as a full path, or null for the default: <c>--data-dir &lt;folder&gt;</c> or
	/// <c>--data-dir=&lt;folder&gt;</c> in <paramref name="arguments"/> (the last one wins), else a non-blank
	/// <see cref="DataDirVariable"/>. A relative folder is taken from the current directory. A switch with no folder (the
	/// last argument, an empty value, or followed by another switch) throws <see cref="ArgumentException"/>: falling back to
	/// the default folder would put a test run's data into the user's.
	/// </summary>
	public static string? ResolveDataDir(IReadOnlyList<string> arguments, Func<string, string?> environment)
	{
		string? asked = null;
		for (var i = 0; i < arguments.Count; i++)
		{
			var argument = arguments[i];
			if (string.Equals(argument, DataDirSwitch, StringComparison.OrdinalIgnoreCase))
			{
				asked = i + 1 < arguments.Count ? arguments[++i] : "";
				if (asked.StartsWith("--", StringComparison.Ordinal))
				{
					asked = "";
				}
			}
			else if (argument.StartsWith(DataDirSwitch + "=", StringComparison.OrdinalIgnoreCase))
			{
				asked = argument[(DataDirSwitch.Length + 1)..];
			}
			else
			{
				continue;
			}

			if (string.IsNullOrWhiteSpace(asked))
			{
				throw new ArgumentException($"{DataDirSwitch} needs a folder: {DataDirSwitch} <folder>");
			}
		}

		if (asked is null && environment(DataDirVariable) is { } fromEnvironment && !string.IsNullOrWhiteSpace(fromEnvironment))
		{
			asked = fromEnvironment;
		}

		return asked is null ? null : Path.GetFullPath(asked.Trim());
	}

	/// <summary>The command line without the program and without <see cref="DataDirSwitch"/> and its folder.</summary>
	public static string[] WithoutDataDir(IEnumerable<string> arguments)
	{
		var rest = new List<string>();
		using var e = arguments.GetEnumerator();
		while (e.MoveNext())
		{
			if (string.Equals(e.Current, DataDirSwitch, StringComparison.OrdinalIgnoreCase))
			{
				e.MoveNext();
				continue;
			}
			if (!e.Current.StartsWith(DataDirSwitch + "=", StringComparison.OrdinalIgnoreCase))
			{
				rest.Add(e.Current);
			}
		}
		return [.. rest];
	}
}
