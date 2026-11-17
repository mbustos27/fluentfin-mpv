namespace FluentFin.Tests;

/// <summary>
/// Seaglass: the update notice is opt-in and can only name this repository. Upstream FluentFin's updater (WindowsUpdateService)
/// downloaded upstream's installer and offered to run it; it is gone, and nothing may bring it back. Read from the sources, since
/// the app cannot run outside WinUI.
/// </summary>
public sealed class UpdateNoticeSourceTests
{
	private static IEnumerable<string> AppSources(string folder) =>
		Directory.EnumerateFiles(Path.Combine(FlyleafEngineTests.RepoRoot, folder), "*.cs", SearchOption.AllDirectories)
			.Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

	[Fact]
	public void UpstreamsUpdater_IsGone_AndNothingNamesUpstreamsReleases()
	{
		Assert.False(File.Exists(Path.Combine(FlyleafEngineTests.RepoRoot, "FluentFin", "UpdateService.cs")));
		foreach (var file in AppSources("FluentFin").Concat(AppSources("FluentFin.Core")))
		{
			var text = File.ReadAllText(file);
			Assert.DoesNotContain("insomniachi/fluentfin/releases", text, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("class WindowsUpdateService", text, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void TheNoticeIsStartedByTheApp_AndShownByTheShell_AndNotWhileItIsOff()
	{
		var app = File.ReadAllText(Path.Combine(FlyleafEngineTests.RepoRoot, "FluentFin", "App.xaml.cs"));
		Assert.Contains("AddSingleton<UpdateNoticeService>", app);
		Assert.DoesNotContain("AddHostedService<WindowsUpdateService>", app);

		var shell = File.ReadAllText(Path.Combine(FlyleafEngineTests.RepoRoot, "FluentFin", "Views", "ShellPage.xaml"));
		Assert.Contains("Download and install", shell);
		Assert.Contains("Release notes", shell);

		// the service asks the coordinator, which checks the setting before any request
		var coordinator = File.ReadAllText(Path.Combine(FlyleafEngineTests.RepoRoot, "FluentFin.Core", "Updates", "UpdateFlow.cs"));
		var tick = coordinator[coordinator.IndexOf("public async Task TickAsync", StringComparison.Ordinal)..];
		Assert.True(tick.IndexOf("settings.CheckForUpdates", StringComparison.Ordinal) < tick.IndexOf("checker.CheckAsync", StringComparison.Ordinal));
	}

	[Fact]
	public void SettingsOffersTheToggle()
	{
		var page = File.ReadAllText(Path.Combine(FlyleafEngineTests.RepoRoot, "FluentFin", "Views", "SettingsPage.xaml"));
		Assert.Contains("Header=\"Check for updates\"", page);
		Assert.Contains("ViewModel.CheckForUpdates", page);
	}
}
