using FluentFin.Core.Mpv;

namespace FluentFin.Tests.Mpv;

public sealed class MpvBackendCommandTests
{
	private const string Config = @"C:\Users\u\AppData\Local\FluentFin\mpv-backend";
	private const string Log = @"C:\Users\u\AppData\Local\FluentFin\Logs\mpv-backend.log";

	[Fact]
	public void DevCheckout_IsFoundAboveTheAppFolder()
	{
		var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			@"C:\Dev\fluentfin-mpv\backend\pyproject.toml",
			@"C:\Dev\fluentfin-mpv\backend\.venv\Scripts\python.exe",
		};

		var command = MpvBackendCommand.Resolve(null, @"C:\Dev\fluentfin-mpv\ui\FluentFin\bin\x64\Debug\net10.0-windows10.0.19041.0\", Config, Log, dev: false, files.Contains, out _);

		Assert.NotNull(command);
		Assert.Equal(@"C:\Dev\fluentfin-mpv\backend\.venv\Scripts\python.exe", command.FileName);
		Assert.Equal(["-I", "-m", "jellyfin_mpv_shim.bridge", "--config-dir", Config, "--log-file", Log], command.Arguments);
		Assert.Equal("dev checkout", command.Source);
	}

	[Fact]
	public void BundledExe_WinsOverADevCheckout_AndDevAddsTheSwitch()
	{
		var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			@"C:\Program Files\FluentFin\backend\fluentfin-mpv-backend.exe",
			@"C:\Program Files\backend\pyproject.toml",
			@"C:\Program Files\backend\.venv\Scripts\python.exe",
		};

		var command = MpvBackendCommand.Resolve("  ", @"C:\Program Files\FluentFin", Config, Log, dev: true, files.Contains, out _);

		Assert.NotNull(command);
		Assert.Equal(@"C:\Program Files\FluentFin\backend\fluentfin-mpv-backend.exe", command.FileName);
		Assert.Equal(["--config-dir", Config, "--log-file", Log, "--dev"], command.Arguments);
	}

	[Fact]
	public void Override_IsSplitLikeACommandLine_AndGetsTheBridgeArguments()
	{
		var command = MpvBackendCommand.Resolve("\"C:\\My Tools\\python.exe\" -I -m jellyfin_mpv_shim.bridge", @"C:\app", Config, null, dev: false, _ => false, out _);

		Assert.NotNull(command);
		Assert.Equal(@"C:\My Tools\python.exe", command.FileName);
		Assert.Equal(["-I", "-m", "jellyfin_mpv_shim.bridge", "--config-dir", Config], command.Arguments);
		Assert.Equal("settings", command.Source);
	}

	[Fact]
	public void NothingFound_SaysWhereItLooked()
	{
		var command = MpvBackendCommand.Resolve(null, @"C:\app\bin", Config, Log, dev: false, _ => false, out var searched);

		Assert.Null(command);
		Assert.Contains("fluentfin-mpv-backend.exe", searched);
		Assert.Contains("pyproject.toml", searched);
	}

	[Fact]
	public void ARelease_NeverUsesABackendFolderAboveIt()
	{
		// fluentfin-mpv (release 1.0, review): the bundled exe missing, a C:\backend\ with a venv would have been run
		var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			@"C:\backend\pyproject.toml",
			@"C:\backend\.venv\Scripts\python.exe",
		};

		var release = MpvBackendCommand.Resolve(null, @"C:\FluentFinMPV-1.0.0-win-x64", Config, Log, dev: false, files.Contains, out var searched, searchCheckout: false);
		var dev = MpvBackendCommand.Resolve(null, @"C:\FluentFinMPV-1.0.0-win-x64", Config, Log, dev: false, files.Contains, out _);

		Assert.Null(release);
		Assert.Equal(@"C:\FluentFinMPV-1.0.0-win-x64\backend\fluentfin-mpv-backend.exe", searched);
		Assert.Equal(@"C:\backend\.venv\Scripts\python.exe", dev?.FileName);
	}

	[Fact]
	public void ARelease_StillUsesItsBundledExe()
	{
		var command = MpvBackendCommand.Resolve(null, @"C:\FluentFinMPV", Config, Log, dev: false,
			path => path == @"C:\FluentFinMPV\backend\fluentfin-mpv-backend.exe", out _, searchCheckout: false);

		Assert.Equal("bundled", command?.Source);
	}

	[Fact]
	public void CheckoutWithoutVenv_NamesThePythonItExpected()
	{
		var command = MpvBackendCommand.Resolve(null, @"C:\Dev\x\ui\bin", Config, Log, dev: false, path => path == @"C:\Dev\x\backend\pyproject.toml", out var searched);

		Assert.Null(command);
		Assert.Contains(@"C:\Dev\x\backend\.venv\Scripts\python.exe", searched);
	}

	[Theory]
	[InlineData("a b  c", new[] { "a", "b", "c" })]
	[InlineData("\"a b\" c", new[] { "a b", "c" })]
	[InlineData("a \"\" c", new[] { "a", "", "c" })]
	[InlineData("", new string[0])]
	public void SplitCommandLine(string commandLine, string[] expected)
	{
		Assert.Equal(expected, MpvBackendCommand.SplitCommandLine(commandLine));
	}
}
