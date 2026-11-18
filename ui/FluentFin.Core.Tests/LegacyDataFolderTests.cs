using FluentFin.Core;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie: the newest old data folder (%LOCALAPPDATA%\Seaglass, else %LOCALAPPDATA%\FluentFinMpv) is copied (never moved) to
/// %LOCALAPPDATA%\Nixie on the first start. Only temporary folders here, never the real LocalAppData.
/// </summary>
public sealed class LegacyDataFolderTests : IDisposable
{
	private readonly string _local = Path.Combine(Path.GetTempPath(), "FluentFinTests", Guid.NewGuid().ToString("N"));
	private string Seaglass => Path.Combine(_local, "Seaglass");
	private string FluentFinMpv => Path.Combine(_local, "FluentFinMpv");
	private string New => Path.Combine(_local, "Nixie");

	public LegacyDataFolderTests() => Directory.CreateDirectory(_local);

	public void Dispose() => Directory.Delete(_local, true);

	private static void MakeOld(string folder, string marker = "{\"a\":1}")
	{
		Directory.CreateDirectory(Path.Combine(folder, "Logs"));
		Directory.CreateDirectory(Path.Combine(folder, "Updates"));
		Directory.CreateDirectory(Path.Combine(folder, "mpv-backend", "shaders"));
		File.WriteAllText(Path.Combine(folder, "LocalSettings.json"), marker);
		File.WriteAllText(Path.Combine(folder, "Logs", "log.txt"), "old log");
		File.WriteAllText(Path.Combine(folder, "Updates", "x.msi"), "old update");
		File.WriteAllText(Path.Combine(folder, "mpv-backend", "shaders", "s.glsl"), "shader");
	}

	/// <summary>Every file under <paramref name="folder"/> with its bytes, to show an old folder was left exactly as it was.</summary>
	private static Dictionary<string, string> Snapshot(string folder) =>
		Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
			.ToDictionary(f => Path.GetRelativePath(folder, f), f => Convert.ToBase64String(File.ReadAllBytes(f)));

	private static Func<string, string?> NoEnvironment => _ => null;

	[Fact]
	public void NewNames()
	{
		Assert.Equal("Nixie", KnownFolders.FolderName);
		Assert.Equal("Seaglass", KnownFolders.SeaglassFolderName);
		Assert.Equal("FluentFinMpv", KnownFolders.FluentFinMpvFolderName);
		Assert.Equal(["Seaglass", "FluentFinMpv"], KnownFolders.LegacyFolderNames);
		Assert.Equal(@"Local\Nixie-data-folder-copy", KnownFolders.CopyMutexName);
		Assert.Equal("LocalSettings.json", KnownFolders.SettingsFileName);
		Assert.Equal("Seaglass", KnownFolders.LegacyAppName("Seaglass"));
		Assert.Equal("FluentFin MPV", KnownFolders.LegacyAppName("FluentFinMpv"));
	}

	[Fact]
	public void CopiesTheSeaglassFolder_AndLeavesItAsItWas()
	{
		MakeOld(Seaglass);
		var before = Snapshot(Seaglass);

		var note = KnownFolders.CopyLegacyFolder(_local, out var failed, out var source);

		Assert.False(failed, note);
		Assert.Equal(Seaglass, source);
		Assert.Contains("Copied", note);
		Assert.Contains(Seaglass, note);
		Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(New, "LocalSettings.json")));
		Assert.False(Directory.Exists(Path.Combine(New, "Logs"))); // old logs are skipped
		Assert.False(Directory.Exists(Path.Combine(New, "Updates"))); // and downloaded updates
		Assert.Equal("shader", File.ReadAllText(Path.Combine(New, "mpv-backend", "shaders", "s.glsl")));
		Assert.Equal(before, Snapshot(Seaglass));
		Assert.False(Directory.Exists(New + ".copying"));
	}

	[Fact]
	public void WithNoSeaglassFolder_CopiesTheFluentFinMpvFolder_AndLeavesItAsItWas()
	{
		MakeOld(FluentFinMpv);
		var before = Snapshot(FluentFinMpv);

		var note = KnownFolders.CopyLegacyFolder(_local, out var failed, out var source);

		Assert.False(failed, note);
		Assert.Equal(FluentFinMpv, source);
		Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(New, "LocalSettings.json")));
		Assert.False(Directory.Exists(Path.Combine(New, "Logs")));
		Assert.Equal(before, Snapshot(FluentFinMpv));
		Assert.False(Directory.Exists(Seaglass)); // nothing is made under the in-between name
	}

	[Fact]
	public void WithBoth_TheSeaglassFolderIsTheOneCopied()
	{
		MakeOld(FluentFinMpv, "{\"from\":\"fluentfin mpv\"}");
		MakeOld(Seaglass, "{\"from\":\"seaglass\"}");
		var olderBefore = Snapshot(FluentFinMpv);

		KnownFolders.CopyLegacyFolder(_local, out var failed, out var source);

		Assert.False(failed);
		Assert.Equal(Seaglass, source);
		Assert.Equal("{\"from\":\"seaglass\"}", File.ReadAllText(Path.Combine(New, "LocalSettings.json")));
		Assert.Equal(olderBefore, Snapshot(FluentFinMpv));
	}

	[Fact]
	public void AnExistingNewFolder_IsNotTouched()
	{
		MakeOld(Seaglass);
		Directory.CreateDirectory(New);
		File.WriteAllText(Path.Combine(New, "mine.txt"), "x");

		Assert.Null(KnownFolders.CopyLegacyFolder(_local));

		Assert.Equal(["mine.txt"], Directory.GetFileSystemEntries(New).Select(Path.GetFileName));
	}

	[Fact]
	public void AStubSeaglassFolder_WithoutSettings_DoesNotWin_OverARealFluentFinMpv()
	{
		// what a standalone backend run leaves (bridge.py's default config folder), or any folder that never held the app
		Directory.CreateDirectory(Path.Combine(Seaglass, "mpv-backend"));
		File.WriteAllText(Path.Combine(Seaglass, "mpv-backend", "conf.json"), "{}");
		MakeOld(FluentFinMpv, "{\"real\":true}");

		var note = KnownFolders.CopyLegacyFolder(_local, out var failed, out var source);

		Assert.False(failed, note);
		Assert.Equal(FluentFinMpv, source);
		Assert.Equal(FluentFinMpv, KnownFolders.FindLegacyFolder(_local));
		Assert.Equal("{\"real\":true}", File.ReadAllText(Path.Combine(New, "LocalSettings.json")));
		Assert.False(File.Exists(Path.Combine(New, "mpv-backend", "conf.json")));
	}

	[Fact]
	public void OldFoldersWithoutSettings_AreNotCopied()
	{
		Directory.CreateDirectory(Path.Combine(Seaglass, "mpv-backend"));
		Directory.CreateDirectory(Path.Combine(FluentFinMpv, "Logs"));

		Assert.Null(KnownFolders.CopyLegacyFolder(_local, out var failed, out var source));
		Assert.False(failed);
		Assert.Null(source);
		Assert.Null(KnownFolders.FindLegacyFolder(_local));
		Assert.False(Directory.Exists(New));
	}

	[Fact]
	public void WhenAnotherStartHoldsTheCopyTooLong_NothingIsCopiedOrDeleted_AndTheOldFolderIsUsedThisTime()
	{
		MakeOld(Seaglass);
		// the other start's half-done copy, and the lock it holds
		Directory.CreateDirectory(New + ".copying");
		File.WriteAllText(Path.Combine(New + ".copying", "theirs.txt"), "half done");
		using var held = new ManualResetEventSlim();
		using var release = new ManualResetEventSlim();
		var other = new Thread(() =>
		{
			using var mutex = new Mutex(false, KnownFolders.CopyMutexName);
			try
			{
				mutex.WaitOne();
				held.Set();
				release.Wait();
			}
			finally
			{
				mutex.ReleaseMutex();
			}
		});
		other.Start();
		Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
		try
		{
			var note = KnownFolders.CopyLegacyFolder(_local, out var failed, out var source, lockTimeout: TimeSpan.FromMilliseconds(300));

			Assert.True(failed, note);
			Assert.Equal(Seaglass, source); // the caller runs on it in place, and the next start tries again
			Assert.Contains("still copying", note);
			Assert.Equal("half done", File.ReadAllText(Path.Combine(New + ".copying", "theirs.txt")));
			Assert.False(Directory.Exists(New));
		}
		finally
		{
			release.Set();
			other.Join();
		}

		// the next start, with the lock free: the copy (and the stale partial folder is replaced)
		Assert.Contains("Copied", KnownFolders.CopyLegacyFolder(_local));
		Assert.True(File.Exists(Path.Combine(New, "LocalSettings.json")));
		Assert.False(File.Exists(Path.Combine(New, "theirs.txt")));
	}

	[Fact]
	public void NoOldFolder_DoesNothing()
	{
		Assert.Null(KnownFolders.CopyLegacyFolder(_local, out var failed, out var source));
		Assert.False(failed);
		Assert.Null(source);
		Assert.False(Directory.Exists(New));
	}

	[Fact]
	public void ASecondStart_CopiesNothingAgain()
	{
		MakeOld(Seaglass);
		KnownFolders.CopyLegacyFolder(_local);
		File.WriteAllText(Path.Combine(New, "LocalSettings.json"), "changed");
		File.WriteAllText(Path.Combine(Seaglass, "LocalSettings.json"), "newer old");

		Assert.Null(KnownFolders.CopyLegacyFolder(_local));
		Assert.Equal("changed", File.ReadAllText(Path.Combine(New, "LocalSettings.json")));
	}

	[Fact]
	public void ALeftoverPartialCopy_IsReplaced()
	{
		MakeOld(Seaglass);
		Directory.CreateDirectory(New + ".copying");
		File.WriteAllText(Path.Combine(New + ".copying", "stale.txt"), "x");

		Assert.NotNull(KnownFolders.CopyLegacyFolder(_local));

		Assert.False(File.Exists(Path.Combine(New, "stale.txt")));
		Assert.True(File.Exists(Path.Combine(New, "LocalSettings.json")));
	}

	[Fact]
	public void ADataDirRun_DoesNotCopy()
	{
		MakeOld(Seaglass);
		MakeOld(FluentFinMpv);

		Assert.Null(KnownFolders.CopyLegacyForStartup(["--data-dir", Path.Combine(_local, "d")], NoEnvironment, _local).Note);
		Assert.Null(KnownFolders.CopyLegacyForStartup([], n => n == KnownFolders.DataDirVariable ? Path.Combine(_local, "d") : null, _local).Note);
		Assert.Null(KnownFolders.CopyLegacyForStartup(["--data-dir"], NoEnvironment, _local).Note);
		Assert.False(Directory.Exists(New));
	}

	[Fact]
	public void ADefaultRun_Copies()
	{
		MakeOld(Seaglass);

		var result = KnownFolders.CopyLegacyForStartup(["--mpv-selftest"], NoEnvironment, _local);

		Assert.NotNull(result.Note);
		Assert.False(result.Failed);
		Assert.Null(result.LegacyRoot); // nothing to fall back to: the run uses Nixie
		Assert.True(File.Exists(Path.Combine(New, "LocalSettings.json")));
	}

	[Theory]
	[InlineData("Seaglass", "Seaglass")]
	[InlineData("FluentFinMpv", "FluentFin MPV")]
	public void AFailedCopy_LeavesNoNixieFolder_UsesTheOldOne_AndTheNextStartRetries(string oldFolder, string oldApp)
	{
		var old = Path.Combine(_local, oldFolder);
		MakeOld(old);
		var before = Snapshot(old);
		var calls = 0;

		var first = KnownFolders.CopyLegacyForStartup([], NoEnvironment, _local, _ => { if (++calls == 2) throw new InvalidOperationException("disk full"); });

		Assert.True(first.Failed);
		Assert.Equal(old, first.LegacyRoot);
		Assert.Equal(oldApp, first.LegacyApp);
		Assert.Contains("disk full", first.Note);
		Assert.False(Directory.Exists(New));
		Assert.False(Directory.Exists(New + ".copying"));
		Assert.Equal(before, Snapshot(old));
		// the run goes on in the old folder, which is unchanged
		var folders = KnownFolders.TryForStartup([], NoEnvironment, out var error, () => first.LegacyRoot!);
		Assert.NotNull(folders);
		Assert.Null(error);
		Assert.Equal(old, folders.ApplicationData);
		Assert.False(Directory.Exists(New));

		// second start: retried, and it works
		var second = KnownFolders.CopyLegacyForStartup([], NoEnvironment, _local);

		Assert.False(second.Failed);
		Assert.Contains("Copied", second.Note);
		Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(New, "LocalSettings.json")));
	}

	[Fact]
	public void ASecondStartDuringTheCopy_WaitsForIt_AndThenFindsTheFinishedFolder()
	{
		MakeOld(Seaglass);
		using var held = new ManualResetEventSlim();
		using var release = new ManualResetEventSlim();
		// the first start's copy, holding the named mutex (a mutex belongs to the thread that took it)
		var first = new Thread(() =>
		{
			using var mutex = new Mutex(false, KnownFolders.CopyMutexName);
			try
			{
				mutex.WaitOne();
				held.Set();
				release.Wait();
				KnownFolders.CopyLegacyFolder(_local);
			}
			finally
			{
				mutex.ReleaseMutex();
			}
		});
		first.Start();
		Assert.True(held.Wait(TimeSpan.FromSeconds(10)));

		var second = Task.Run(() =>
		{
			var note = KnownFolders.CopyLegacyFolder(_local, out var failed, out var source);
			return (note, failed, source);
		});
		Assert.False(second.Wait(TimeSpan.FromMilliseconds(400))); // waits for the first
		Assert.False(Directory.Exists(New));
		release.Set();
		Assert.True(second.Wait(TimeSpan.FromSeconds(30)));
		first.Join();

		Assert.Null(second.Result.note); // the first copied; the second found the finished folder
		Assert.False(second.Result.failed);
		Assert.True(File.Exists(Path.Combine(New, "LocalSettings.json")));
	}

	[Fact]
	public void TheJournal_IsCopiedWithItsUncheckpointedWalRows_AndTheOldOneIsNotCheckpointed()
	{
		MakeOld(Seaglass);
		var db = Path.Combine(Seaglass, "journal.db");
		var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Pooling=False");
		connection.Open();
		Exec(connection, "PRAGMA journal_mode=WAL");
		Exec(connection, "PRAGMA wal_autocheckpoint=0");
		Exec(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT)");
		for (var i = 0; i < 50; i++)
		{
			Exec(connection, $"INSERT INTO t(v) VALUES('row{i}')");
		}

		// the writer (Seaglass 1.1.x) is still open: the rows are in the -wal file, not in journal.db
		Assert.True(File.Exists(db + "-wal") && new FileInfo(db + "-wal").Length > 0);
		var dbBefore = ReadShared(db);
		try
		{
			var note = KnownFolders.CopyLegacyFolder(_local, out var failed);

			Assert.False(failed, note);
			Assert.False(File.Exists(Path.Combine(New, "journal.db-wal")));
			Assert.Equal(dbBefore, ReadShared(db)); // read-only: no checkpoint into the old journal
			using var copy = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(New, "journal.db")};Mode=ReadOnly;Pooling=False");
			copy.Open();
			using var count = copy.CreateCommand();
			count.CommandText = "SELECT COUNT(*) FROM t";
			Assert.Equal(50L, count.ExecuteScalar());
		}
		finally
		{
			connection.Dispose();
		}
	}

	/// <summary>A file the open writer still holds (SQLite shares it for reading and writing).</summary>
	private static byte[] ReadShared(string path)
	{
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using var copy = new MemoryStream();
		stream.CopyTo(copy);
		return copy.ToArray();
	}

	private static void Exec(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
	{
		using var command = connection.CreateCommand();
		command.CommandText = sql;
		command.ExecuteNonQuery();
	}
}
