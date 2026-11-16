using FluentFin.Core;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Mpv;
using Microsoft.Extensions.Logging.Abstractions;

namespace FluentFin.Tests.Mpv;

/// <summary>fluentfin-mpv: with <c>--data-dir</c> the mpv backend's config folder and log are in that folder too.</summary>
public sealed class DataDirLauncherTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "FluentFinTests", Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, true);
		}
	}

	[Fact]
	public void TheBackendsConfigFolderAndLog_AreInTheDataFolder_AndNothingIsInTheDefault()
	{
		var dataDir = Path.Combine(_root, "data");
		var defaultRoot = Path.Combine(_root, "default", KnownFolders.FolderName);
		var folders = KnownFolders.ForStartup(["--data-dir", dataDir], _ => null, () => defaultRoot);
		var settings = new Settings(new LocalSettingsService(folders)) { MpvBackendCommand = @"C:\py\python.exe -I -m jellyfin_mpv_shim.bridge" };
		var launcher = new WindowsMpvBackendLauncher(settings, folders, NullLogger<WindowsMpvBackendLauncher>.Instance);

		var command = launcher.Resolve(dev: false, out _);

		Assert.NotNull(command);
		Assert.Equal(["-I", "-m", "jellyfin_mpv_shim.bridge", "--config-dir", Path.Combine(dataDir, "mpv-backend"), "--log-file", Path.Combine(dataDir, "Logs", "mpv-backend.log")], command.Arguments);
		Assert.True(Directory.Exists(Path.Combine(dataDir, "mpv-backend")));
		Assert.False(Directory.Exists(defaultRoot));
	}
}
