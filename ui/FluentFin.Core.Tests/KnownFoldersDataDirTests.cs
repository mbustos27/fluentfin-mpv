using System.Text.RegularExpressions;
using FluentFin.Core;
using FluentFin.Core.Services;

namespace FluentFin.Core.Tests;

/// <summary>fluentfin-mpv: <c>--data-dir</c> / <c>FLUENTFIN_MPV_DATA_DIR</c> keep a dev or test run out of %LOCALAPPDATA%\FluentFinMpv.</summary>
public sealed class KnownFoldersDataDirTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "FluentFinTests", Guid.NewGuid().ToString("N"));

	// stands for %LOCALAPPDATA%\FluentFinMpv: a run with its own folder must never create or write it
	private string DefaultRoot => Path.Combine(_root, "default", KnownFolders.FolderName);
	private string DataDir => Path.Combine(_root, "data dir");

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, true);
		}
	}

	private static Func<string, string?> Environment(string? dataDir = null) =>
		name => name == KnownFolders.DataDirVariable ? dataDir : null;

	[Fact]
	public void NeitherSwitchNorVariable_IsTheDefaultFolder()
	{
		Assert.Null(KnownFolders.ResolveDataDir(["--mpv-selftest"], Environment()));

		var folders = KnownFolders.ForStartup([], Environment(), () => DefaultRoot);

		Assert.False(folders.IsCustom);
		Assert.Equal(DefaultRoot, folders.ApplicationData);
	}

	[Fact]
	public void DefaultRoot_IsLocalAppDataFluentFinMpv()
	{
		Assert.Equal(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "FluentFinMpv"), KnownFolders.DefaultRoot);
	}

	[Theory]
	[InlineData("--data-dir", "{0}")]
	[InlineData("--data-dir={0}", null)]
	[InlineData("--DATA-DIR", "{0}")]
	public void Switch_GivesTheFolder_AsAFullPath(string first, string? second)
	{
		string[] arguments = second is null ? [string.Format(first, DataDir)] : [first, string.Format(second, DataDir)];

		Assert.Equal(DataDir, KnownFolders.ResolveDataDir(["--mpv-selftest", .. arguments], Environment()));
	}

	[Fact]
	public void Switch_WinsOverTheVariable_AndTheLastSwitchWins()
	{
		var other = Path.Combine(_root, "other");

		Assert.Equal(DataDir, KnownFolders.ResolveDataDir(["--data-dir", other, "--data-dir", DataDir], Environment(Path.Combine(_root, "from env"))));
	}

	[Fact]
	public void Variable_IsUsedWithoutTheSwitch_ButNotWhenBlank()
	{
		Assert.Equal(DataDir, KnownFolders.ResolveDataDir([], Environment(DataDir)));
		Assert.Null(KnownFolders.ResolveDataDir([], Environment("   ")));
		Assert.Null(KnownFolders.ResolveDataDir([], Environment("")));
	}

	[Fact]
	public void RelativeFolder_IsTakenFromTheCurrentDirectory()
	{
		Assert.Equal(Path.Combine(System.Environment.CurrentDirectory, "rel", "data"), KnownFolders.ResolveDataDir(["--data-dir", Path.Combine("rel", "data")], Environment()));
	}

	[Theory]
	[InlineData("--data-dir", null)]
	[InlineData("--data-dir", "")]
	[InlineData("--data-dir", "  ")]
	[InlineData("--data-dir=", null)]
	[InlineData("--data-dir", "--mpv-selftest")]
	public void SwitchWithoutAFolder_Throws_InsteadOfFallingBackToTheDefault(string first, string? second)
	{
		string[] arguments = second is null ? [first] : [first, second];
		var defaultAskedFor = false;

		Assert.Throws<ArgumentException>(() => KnownFolders.ForStartup(arguments, Environment(DataDir), () => { defaultAskedFor = true; return DefaultRoot; }));
		Assert.False(defaultAskedFor);
		Assert.False(Directory.Exists(DefaultRoot));
	}

	[Fact]
	public void ARunWithItsOwnFolder_KeepsSettingsLogsAndBackendThere_AndNeverAsksForTheDefault()
	{
		var defaultAskedFor = false;

		var folders = KnownFolders.ForStartup(["--data-dir", DataDir], Environment(), () => { defaultAskedFor = true; return DefaultRoot; });
		var settings = new LocalSettingsService(folders);
		settings.SaveSetting("Volume", 42);
		_ = settings.GetEntropyBytes();
		File.AppendAllText(folders.LogFile, "a log line");

		Assert.True(folders.IsCustom);
		Assert.False(defaultAskedFor);
		Assert.False(Directory.Exists(DefaultRoot));
		Assert.Equal(DataDir, folders.ApplicationData);
		Assert.Equal(Path.Combine(DataDir, "Logs"), folders.Logs);
		Assert.Equal(Path.Combine(DataDir, "Updates"), folders.Updates);
		Assert.Equal(Path.Combine(DataDir, "Logs", "log.txt"), folders.LogFile);
		Assert.True(File.Exists(Path.Combine(DataDir, "LocalSettings.json")));
		Assert.Equal(42, new LocalSettingsService(KnownFolders.ForStartup([], Environment(DataDir), () => DefaultRoot)).ReadSetting("Volume", 0));
		Assert.False(Directory.Exists(DefaultRoot));
	}

	[Fact]
	public void WithoutDataDir_LeavesTheOtherArguments()
	{
		var library = Guid.NewGuid().ToString();

		Assert.Equal([library], KnownFolders.WithoutDataDir(["--data-dir", DataDir, library]));
		Assert.Equal([library, "--mpv-selftest"], KnownFolders.WithoutDataDir([library, $"--data-dir={DataDir}", "--mpv-selftest"]));
		Assert.Equal([library], KnownFolders.WithoutDataDir([library]));
	}

	/// <summary>
	/// Every path FluentFin writes to comes from <see cref="KnownFolders"/>, so <c>--data-dir</c> covers them all: no other
	/// source file of the app asks Windows for a user folder, reads APPDATA/LOCALAPPDATA, uses the packaged app's
	/// ApplicationData, the credential locker, or builds the default folders.
	/// </summary>
	[Fact]
	public void NoAppSourceButKnownFolders_FindsAUserFolderOfItsOwn()
	{
		var ui = FindUiFolder();
		var forbidden = new Regex(@"GetFolderPath\s*\(|SpecialFolder\.|ApplicationData\.Current|PasswordVault|""(LOCAL)?APPDATA""|new\s+(Core\.)?KnownFolders\s*\(\s*\)|IsolatedStorage", RegexOptions.Compiled);
		var offenders = new List<string>();

		foreach (var project in new[] { "FluentFin", "FluentFin.Core", "FluentFin.UI.Core", "FluentFin.Plugins" })
		{
			foreach (var file in Directory.EnumerateFiles(Path.Combine(ui, project), "*.cs", SearchOption.AllDirectories))
			{
				var relative = Path.GetRelativePath(ui, file);
				if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj") ||
					relative == Path.Combine("FluentFin.Core", "KnownFolders.cs"))
				{
					continue;
				}

				var lineNumber = 0;
				foreach (var line in File.ReadLines(file))
				{
					lineNumber++;
					var code = line.TrimStart();
					if (!code.StartsWith("//", StringComparison.Ordinal) && forbidden.IsMatch(code))
					{
						offenders.Add($"{relative}({lineNumber}): {code}");
					}
				}
			}
		}

		Assert.True(offenders.Count == 0, "Paths outside KnownFolders:\n" + string.Join("\n", offenders));
	}

	private static string FindUiFolder()
	{
		for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
		{
			if (File.Exists(Path.Combine(directory.FullName, "FluentFin.sln")))
			{
				return directory.FullName;
			}
		}

		throw new DirectoryNotFoundException($"No FluentFin.sln above {AppContext.BaseDirectory}");
	}
}
