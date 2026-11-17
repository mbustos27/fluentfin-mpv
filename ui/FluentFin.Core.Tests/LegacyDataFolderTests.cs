using FluentFin.Core;

namespace FluentFin.Core.Tests;

/// <summary>Seaglass: %LOCALAPPDATA%\FluentFinMpv is copied (never moved) to %LOCALAPPDATA%\Seaglass on the first start. Only temporary folders here, never the real LocalAppData.</summary>
public sealed class LegacyDataFolderTests : IDisposable
{
	private readonly string _local = Path.Combine(Path.GetTempPath(), "FluentFinTests", Guid.NewGuid().ToString("N"));
	private string Old => Path.Combine(_local, "FluentFinMpv");
	private string New => Path.Combine(_local, "Seaglass");

	public LegacyDataFolderTests() => Directory.CreateDirectory(_local);

	public void Dispose() => Directory.Delete(_local, true);

	private void MakeOld()
	{
		Directory.CreateDirectory(Path.Combine(Old, "Logs"));
		Directory.CreateDirectory(Path.Combine(Old, "mpv-backend", "shaders"));
		File.WriteAllText(Path.Combine(Old, "settings.json"), "{\"a\":1}");
		File.WriteAllText(Path.Combine(Old, "Logs", "log.txt"), "old log");
		File.WriteAllText(Path.Combine(Old, "mpv-backend", "shaders", "s.glsl"), "shader");
	}

	private static Func<string, string?> NoEnvironment => _ => null;

	[Fact]
	public void NewNames()
	{
		Assert.Equal("Seaglass", KnownFolders.FolderName);
		Assert.Equal("FluentFinMpv", KnownFolders.LegacyFolderName);
	}

	[Fact]
	public void CopiesTheOldFolder_AndLeavesItAsItWas()
	{
		MakeOld();

		var note = KnownFolders.CopyLegacyFolder(_local);

		Assert.NotNull(note);
		Assert.Contains("Copied", note);
		Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(New, "settings.json")));
		Assert.False(Directory.Exists(Path.Combine(New, "Logs"))); // old logs are skipped
		Assert.Equal("shader", File.ReadAllText(Path.Combine(New, "mpv-backend", "shaders", "s.glsl")));
		Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(Old, "settings.json")));
		Assert.True(File.Exists(Path.Combine(Old, "mpv-backend", "shaders", "s.glsl")));
		Assert.False(Directory.Exists(New + ".copying"));
	}

	[Fact]
	public void AnExistingNewFolder_IsNotTouched()
	{
		MakeOld();
		Directory.CreateDirectory(New);
		File.WriteAllText(Path.Combine(New, "mine.txt"), "x");

		Assert.Null(KnownFolders.CopyLegacyFolder(_local));

		Assert.Equal(["mine.txt"], Directory.GetFileSystemEntries(New).Select(Path.GetFileName));
	}

	[Fact]
	public void NoOldFolder_DoesNothing()
	{
		Assert.Null(KnownFolders.CopyLegacyFolder(_local));
		Assert.False(Directory.Exists(New));
	}

	[Fact]
	public void ASecondStart_CopiesNothingAgain()
	{
		MakeOld();
		KnownFolders.CopyLegacyFolder(_local);
		File.WriteAllText(Path.Combine(New, "settings.json"), "changed");
		File.WriteAllText(Path.Combine(Old, "settings.json"), "newer old");

		Assert.Null(KnownFolders.CopyLegacyFolder(_local));
		Assert.Equal("changed", File.ReadAllText(Path.Combine(New, "settings.json")));
	}

	[Fact]
	public void ALeftoverPartialCopy_IsReplaced()
	{
		MakeOld();
		Directory.CreateDirectory(New + ".copying");
		File.WriteAllText(Path.Combine(New + ".copying", "stale.txt"), "x");

		Assert.NotNull(KnownFolders.CopyLegacyFolder(_local));

		Assert.False(File.Exists(Path.Combine(New, "stale.txt")));
		Assert.True(File.Exists(Path.Combine(New, "settings.json")));
	}

	[Fact]
	public void ADataDirRun_DoesNotCopy()
	{
		MakeOld();

		Assert.Null(KnownFolders.CopyLegacyForStartup(["--data-dir", Path.Combine(_local, "d")], NoEnvironment, _local).Note);
		Assert.Null(KnownFolders.CopyLegacyForStartup([], n => n == KnownFolders.DataDirVariable ? Path.Combine(_local, "d") : null, _local).Note);
		Assert.Null(KnownFolders.CopyLegacyForStartup(["--data-dir"], NoEnvironment, _local).Note);
		Assert.False(Directory.Exists(New));
	}

	[Fact]
	public void ADefaultRun_Copies()
	{
		MakeOld();

		Assert.NotNull(KnownFolders.CopyLegacyForStartup(["--mpv-selftest"], NoEnvironment, _local).Note);
		Assert.True(File.Exists(Path.Combine(New, "settings.json")));
	}

	[Fact]
	public void AFailedCopy_LeavesNoSeaglassFolder_UsesTheOldOne_AndTheNextStartRetries()
	{
		MakeOld();
		var calls = 0;

		var first = KnownFolders.CopyLegacyForStartup([], NoEnvironment, _local, _ => { if (++calls == 2) throw new InvalidOperationException("disk full"); });

		Assert.True(first.Failed);
		Assert.Equal(Old, first.LegacyRoot);
		Assert.Contains("disk full", first.Note);
		Assert.False(Directory.Exists(New));
		Assert.False(Directory.Exists(New + ".copying"));
		// the run goes on in the old folder, which is unchanged
		var folders = KnownFolders.TryForStartup([], NoEnvironment, out var error, () => first.LegacyRoot!);
		Assert.NotNull(folders);
		Assert.Null(error);
		Assert.Equal(Old, folders.ApplicationData);
		Assert.False(Directory.Exists(New));

		// second start: retried, and it works
		var second = KnownFolders.CopyLegacyForStartup([], NoEnvironment, _local);

		Assert.False(second.Failed);
		Assert.Contains("Copied", second.Note);
		Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(New, "settings.json")));
	}

	[Fact]
	public void TheJournal_IsCopiedWithItsUncheckpointedWalRows()
	{
		MakeOld();
		var db = Path.Combine(Old, "journal.db");
		var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Pooling=False");
		connection.Open();
		Exec(connection, "PRAGMA journal_mode=WAL");
		Exec(connection, "PRAGMA wal_autocheckpoint=0");
		Exec(connection, "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT)");
		for (var i = 0; i < 50; i++)
		{
			Exec(connection, $"INSERT INTO t(v) VALUES('row{i}')");
		}

		// the writer (FluentFin MPV 1.0.x) is still open: the rows are in the -wal file, not in journal.db
		Assert.True(File.Exists(db + "-wal") && new FileInfo(db + "-wal").Length > 0);
		try
		{
			var note = KnownFolders.CopyLegacyFolder(_local, out var failed);

			Assert.False(failed, note);
			Assert.False(File.Exists(Path.Combine(New, "journal.db-wal")));
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

	private static void Exec(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
	{
		using var command = connection.CreateCommand();
		command.CommandText = sql;
		command.ExecuteNonQuery();
	}
}
