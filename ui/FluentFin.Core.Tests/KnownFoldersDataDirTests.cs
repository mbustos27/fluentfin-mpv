using System.Text.RegularExpressions;
using FluentFin.Core;
using FluentFin.Core.Services;

namespace FluentFin.Core.Tests;

/// <summary>fluentfin-mpv: <c>--data-dir</c> / <c>FLUENTFIN_MPV_DATA_DIR</c> keep a dev or test run out of %LOCALAPPDATA%\Seaglass.</summary>
public sealed class KnownFoldersDataDirTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "FluentFinTests", Guid.NewGuid().ToString("N"));

	// stands for %LOCALAPPDATA%\Seaglass: a run with its own folder must never create or write it
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
	public void DefaultRoot_IsLocalAppDataSeaglass()
	{
		Assert.Equal(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Seaglass"), KnownFolders.DefaultRoot);
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

	// review: a mistyped switch was ignored, so the run went to the user's folder
	[Theory]
	[InlineData("--datadir")]
	[InlineData("--data-dir:{0}")]
	[InlineData("-data-dir")]
	[InlineData("/data-dir")]
	[InlineData("--data_dir")]
	[InlineData("--DataDir={0}")]
	[InlineData("---data-dir")]
	public void MistypedSwitch_Throws_InsteadOfUsingTheDefault(string mistyped)
	{
		var defaultAskedFor = false;
		string[] arguments = [string.Format(mistyped, DataDir), DataDir];

		var ex = Assert.Throws<ArgumentException>(() => KnownFolders.ForStartup(arguments, Environment(), () => { defaultAskedFor = true; return DefaultRoot; }));

		Assert.Contains(KnownFolders.DataDirSwitch, ex.Message);
		Assert.False(defaultAskedFor);
		Assert.False(Directory.Exists(DefaultRoot));
	}

	[Fact]
	public void OtherArguments_AreNotMistakenForTheSwitch()
	{
		Assert.Null(KnownFolders.ResolveDataDir(["--mpv-selftest", Guid.NewGuid().ToString(), "--data", "dir", "data-dir"], Environment()));
	}

	// review: "C:\data\" quoted by a script reaches FluentFin as C:\data" and CreateDirectory threw before any window
	[Fact]
	public void AFolderWithAQuote_Throws_AndSaysWhy()
	{
		var ex = Assert.Throws<ArgumentException>(() => KnownFolders.ResolveDataDir(["--data-dir", DataDir + "\""], Environment()));

		Assert.Contains("backslash", ex.Message);
	}

	[Fact]
	public void TryForStartup_AFolderThatCannotBeMade_GivesAMessageNamingIt()
	{
		Directory.CreateDirectory(_root);
		var file = Path.Combine(_root, "a file");
		File.WriteAllText(file, "not a folder");

		var folders = KnownFolders.TryForStartup(["--data-dir", file], Environment(), out var error, () => DefaultRoot);

		Assert.Null(folders);
		Assert.NotNull(error);
		Assert.Contains(file, error);
		Assert.False(Directory.Exists(DefaultRoot));
	}

	[Fact]
	public void TryForStartup_ASwitchWithoutAFolder_GivesTheMessage_AndAGoodOneTheFolders()
	{
		Assert.Null(KnownFolders.TryForStartup(["--data-dir"], Environment(), out var error, () => DefaultRoot));
		Assert.Contains("needs a folder", error);

		var folders = KnownFolders.TryForStartup(["--data-dir", DataDir], Environment(), out error, () => DefaultRoot);
		Assert.Null(error);
		Assert.Equal(DataDir, folders!.ApplicationData);
		Assert.True(folders.IsCustom);
	}

	// review: a parameterless constructor meant %LOCALAPPDATA%\Seaglass; a container that built KnownFolders itself would
	// have used it and put a --data-dir run's files there
	[Fact]
	public void NoConstructorPicksTheFolderByItself()
	{
		var constructors = typeof(KnownFolders).GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

		Assert.NotEmpty(constructors);
		Assert.All(constructors, c => Assert.NotEmpty(c.GetParameters()));
	}

	[Fact]
	public void App_StartsThroughTryForStartup_SoABadFolderIsAMessage()
	{
		var app = File.ReadAllText(Path.Combine(FindUiFolder(), "FluentFin", "App.xaml.cs"));

		Assert.Contains("KnownFolders.TryForStartup(", app);
		Assert.Contains("services.AddSingleton(knownFolders)", app);
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
	/// ApplicationData, the credential locker, or builds the default folders -- itself or by having the container build
	/// KnownFolders (a registration by type, not the instance ForStartup made).
	/// </summary>
	[Fact]
	public void NoAppSourceButKnownFolders_FindsAUserFolderOfItsOwn()
	{
		var ui = FindUiFolder();
		var forbidden = new Regex(@"GetFolderPath\s*\(|SpecialFolder\.|ApplicationData\.Current|PasswordVault|""(LOCAL)?APPDATA""|new\s+(Core\.)?KnownFolders\s*\(\s*\)|IsolatedStorage|" +
			@"Add(Singleton|Scoped|Transient)\s*<\s*(FluentFin\.)?(Core\.)?KnownFolders\s*(,[^>]*)?>\s*\(\s*\)|typeof\s*\(\s*(FluentFin\.)?(Core\.)?KnownFolders\s*\)", RegexOptions.Compiled);
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
