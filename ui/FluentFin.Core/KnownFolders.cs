namespace FluentFin.Core;

public class KnownFolders
{
	public string ApplicationData { get; }
	public string Updates { get; }
	public string Logs { get; }

	// fluentfin-mpv: its own folder, so this build never shares settings, saved logins or logs with an upstream FluentFin install.
	// Seaglass (1.1.0): renamed from FluentFinMpv; CopyLegacyFolder brings the old folder's contents over once.
	public const string FolderName = "Seaglass";

	/// <summary>The data folder's name before the rename to Seaglass (FluentFin MPV 1.0.x).</summary>
	public const string LegacyFolderName = "FluentFinMpv";

	/// <summary>
	/// fluentfin-mpv: <c>Seaglass.exe --data-dir &lt;folder&gt;</c> (or <c>--data-dir=&lt;folder&gt;</c>) keeps everything this
	/// run writes -- settings and saved logins, logs, the mpv backend's config folder and log -- under that folder instead of
	/// <c>%LOCALAPPDATA%\Seaglass</c>: a dev or test run that never touches the user's own data (tools/README.md).
	/// </summary>
	public const string DataDirSwitch = "--data-dir";

	/// <summary>fluentfin-mpv: the same as <see cref="DataDirSwitch"/> from the environment; the switch wins when both are set.</summary>
	public const string DataDirVariable = "FLUENTFIN_MPV_DATA_DIR";

	/// <summary><c>%LOCALAPPDATA%\Seaglass</c>.</summary>
	public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);

	/// <summary>What the copy step did. <see cref="Failed"/>: nothing was made under Seaglass and this run must use <see cref="LegacyRoot"/> in place.</summary>
	public sealed record LegacyCopyResult(string? Note, bool Failed, string? LegacyRoot)
	{
		public static LegacyCopyResult Nothing { get; } = new(null, false, null);
	}

	/// <summary>
	/// Seaglass: the start-up step before the folders are made. With no <see cref="DataDirSwitch"/> and no
	/// <see cref="DataDirVariable"/> (those runs never touch the user's own folders), copies the old data folder to the new one
	/// (<see cref="CopyLegacyFolder"/>). <see cref="LegacyCopyResult.Note"/> is what to log, null when there was nothing to do.
	/// </summary>
	public static LegacyCopyResult CopyLegacyForStartup(IReadOnlyList<string> arguments, Func<string, string?> environment, string? localAppData = null, Action<string>? afterEachFile = null)
	{
		try
		{
			localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			if (ResolveDataDir(arguments, environment) is not null)
			{
				return LegacyCopyResult.Nothing;
			}
		}
		catch (ArgumentException)
		{
			return LegacyCopyResult.Nothing; // TryForStartup reports it
		}
		catch (Exception)
		{
			return LegacyCopyResult.Nothing;
		}

		var note = CopyLegacyFolder(localAppData, out var failed, afterEachFile);
		return new LegacyCopyResult(note, failed, failed ? Path.Combine(localAppData, LegacyFolderName) : null);
	}

	/// <summary>
	/// Seaglass: when <c>&lt;localAppData&gt;\Seaglass</c> does not exist and <c>&lt;localAppData&gt;\FluentFinMpv</c> does, COPIES the
	/// old folder (recursively, except Logs and Updates) to the new one; the old folder is never moved, changed or deleted. The
	/// journal database is copied with SQLite's online backup (a consistent snapshot even while FluentFin MPV 1.0.x runs), never as
	/// its db, -wal and -shm files. The copy is made beside the target and renamed into place, so an interrupted copy never leaves
	/// a half-filled Seaglass folder. On ANY failure the partial copy is deleted and no Seaglass folder exists: the caller runs on
	/// the old folder in place and the next start tries again (<paramref name="failed"/> is true, the text says why).
	/// </summary>
	public static string? CopyLegacyFolder(string localAppData, out bool failed, Action<string>? afterEachFile = null)
	{
		failed = false;
		var target = Path.Combine(localAppData, FolderName);
		var legacy = Path.Combine(localAppData, LegacyFolderName);
		var partial = target + ".copying";
		// two Seaglass windows started together on the first run must not share the partial folder (one would delete or move
		// the other's half-done copy): one copies, the other waits and then finds the finished folder
		using var copyLock = new Mutex(false, @"Local\Seaglass-data-folder-copy");
		try
		{
			try
			{
				copyLock.WaitOne(TimeSpan.FromMinutes(5));
			}
			catch (AbandonedMutexException)
			{
				// the other copier ended without releasing it: ours now, and its partial folder is deleted below
			}

			if (Directory.Exists(target) || !Directory.Exists(legacy))
			{
				return null;
			}

			if (Directory.Exists(partial))
			{
				Directory.Delete(partial, recursive: true);
			}

			CopyDirectory(legacy, partial, top: true, afterEachFile);
			Directory.Move(partial, target);
			return $"Copied the data folder {legacy} to {target} (the old folder is left as it was)";
		}
		catch (Exception ex)
		{
			failed = true;
			try
			{
				if (Directory.Exists(partial))
				{
					Directory.Delete(partial, recursive: true);
				}
			}
			catch (Exception)
			{
				// the next start deletes it before copying
			}

			return $"Could not copy the data folder {legacy} to {target}: {ex.Message}. Using the old folder in place; the copy is tried again at the next start";
		}
		finally
		{
			try { copyLock.ReleaseMutex(); }
			catch (ApplicationException) { } // not held (the wait timed out)
		}
	}

	/// <summary><see cref="CopyLegacyFolder(string, out bool, Action{string}?)"/> for callers that only want the note.</summary>
	public static string? CopyLegacyFolder(string localAppData) => CopyLegacyFolder(localAppData, out _);

	private static void CopyDirectory(string from, string to, bool top, Action<string>? afterEachFile)
	{
		Directory.CreateDirectory(to);
		foreach (var file in Directory.EnumerateFiles(from))
		{
			var name = Path.GetFileName(file);
			if (top && name.StartsWith(Journal.JournalStore.FileName, StringComparison.OrdinalIgnoreCase))
			{
				if (string.Equals(name, Journal.JournalStore.FileName, StringComparison.OrdinalIgnoreCase))
				{
					BackupJournal(file, Path.Combine(to, name));
					afterEachFile?.Invoke(file);
				}

				continue; // -wal, -shm, -journal: part of the snapshot the backup makes
			}

			File.Copy(file, Path.Combine(to, name));
			afterEachFile?.Invoke(file);
		}

		foreach (var directory in Directory.EnumerateDirectories(from))
		{
			var name = Path.GetFileName(directory);
			if (top && (name.Equals("Logs", StringComparison.OrdinalIgnoreCase) || name.Equals("Updates", StringComparison.OrdinalIgnoreCase)))
			{
				continue; // old logs and downloaded updates are not needed; skipping keeps the first start fast
			}

			CopyDirectory(directory, Path.Combine(to, name), top: false, afterEachFile);
		}
	}

	private static void BackupJournal(string from, string to)
	{
		using var source = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
		{
			DataSource = from,
			Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, // the old folder is never changed (no checkpoint on close)
			Pooling = false,
		}.ToString());
		using var destination = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
		{
			DataSource = to,
			Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
			Pooling = false,
		}.ToString());
		source.Open();
		destination.Open();
		source.BackupDatabase(destination);
	}

	// what a mistyped data folder switch looks like: dashes or a slash, then data, dir with or without - or _ between
	private static readonly System.Text.RegularExpressions.Regex LooksLikeDataDir =
		new(@"^[-/]+data[-_]?dir", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

	/// <summary>True when the folder came from <see cref="DataDirSwitch"/> or <see cref="DataDirVariable"/>.</summary>
	public bool IsCustom { get; }

	/// <summary>The FluentFin log file (Serilog adds the date to its name).</summary>
	public string LogFile => Path.Combine(Logs, "log.txt");

	// fluentfin-mpv: no parameterless constructor. It meant %LOCALAPPDATA%\Seaglass, so a container that ever built this
	// class itself would have put a --data-dir run's files into the user's folder; the app registers the instance
	// ForStartup made (KnownFoldersDataDirTests guards both).
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
	/// fluentfin-mpv: <see cref="ForStartup"/> for the app's start. A switch with no folder or a mistyped one, or a folder
	/// that cannot be made (a file of that name, no access, a name Windows refuses), gives null and a message to show instead
	/// of a crash before any window.
	/// </summary>
	public static KnownFolders? TryForStartup(IReadOnlyList<string> arguments, Func<string, string?> environment, out string? error, Func<string>? defaultRoot = null)
	{
		string? folder = null;
		try
		{
			var custom = ResolveDataDir(arguments, environment);
			folder = custom ?? (defaultRoot ?? (() => DefaultRoot))();
			error = null;
			return custom is not null ? new KnownFolders(custom, isCustom: true) : new KnownFolders(folder);
		}
		catch (ArgumentException ex)
		{
			error = ex.Message;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			error = $"Seaglass could not create its data folder {folder}: {ex.Message}";
		}

		return null;
	}

	/// <summary>
	/// The data folder asked for as a full path, or null for the default: <c>--data-dir &lt;folder&gt;</c> or
	/// <c>--data-dir=&lt;folder&gt;</c> in <paramref name="arguments"/> (the last one wins), else a non-blank
	/// <see cref="DataDirVariable"/>. A relative folder is taken from the current directory. A switch with no folder (the
	/// last argument, an empty value, or followed by another switch) throws <see cref="ArgumentException"/>: falling back to
	/// the default folder would put a test run's data into the user's. So does a mistyped switch (<c>--datadir</c>,
	/// <c>-data-dir</c>, <c>--data-dir:X</c>, <c>/data_dir</c>: anything that starts like it but is not one of the two forms)
	/// and a folder with a quote in it (<c>"C:\data\"</c> reaches the program as <c>C:\data"</c>).
	/// </summary>
	public static string? ResolveDataDir(IReadOnlyList<string> arguments, Func<string, string?> environment)
	{
		string? asked = null;
		for (var i = 0; i < arguments.Count; i++)
		{
			var argument = arguments[i];
			if (LooksLikeDataDir.IsMatch(argument) && !string.Equals(argument, DataDirSwitch, StringComparison.OrdinalIgnoreCase) &&
				!argument.StartsWith(DataDirSwitch + "=", StringComparison.OrdinalIgnoreCase))
			{
				throw new ArgumentException($"Unknown switch {argument}: the data folder is given as {DataDirSwitch} <folder> or {DataDirSwitch}=<folder>");
			}

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

			if (asked.Contains('"'))
			{
				throw new ArgumentException($"{DataDirSwitch} {asked}: a folder name has no quote in it. A quoted folder that ends in a backslash (\"C:\\data\\\") arrives as C:\\data\"; leave out the last backslash.");
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
