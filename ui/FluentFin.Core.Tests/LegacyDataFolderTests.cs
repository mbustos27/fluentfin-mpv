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
		Assert.Equal("old log", File.ReadAllText(Path.Combine(New, "Logs", "log.txt")));
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

		Assert.Null(KnownFolders.CopyLegacyForStartup(["--data-dir", Path.Combine(_local, "d")], NoEnvironment, _local));
		Assert.Null(KnownFolders.CopyLegacyForStartup([], n => n == KnownFolders.DataDirVariable ? Path.Combine(_local, "d") : null, _local));
		Assert.Null(KnownFolders.CopyLegacyForStartup(["--data-dir"], NoEnvironment, _local));
		Assert.False(Directory.Exists(New));
	}

	[Fact]
	public void ADefaultRun_Copies()
	{
		MakeOld();

		Assert.NotNull(KnownFolders.CopyLegacyForStartup(["--mpv-selftest"], NoEnvironment, _local));
		Assert.True(File.Exists(Path.Combine(New, "settings.json")));
	}
}
