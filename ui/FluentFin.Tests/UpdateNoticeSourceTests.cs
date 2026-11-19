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
		// Nixie 1.3.1: Check now, and the text says the check is on by default and what it sends
		Assert.Contains("Content=\"Check now\"", page);
		Assert.Contains("Nothing is sent but the request for the latest release", page);
		Assert.Contains("On by default.", page);
		var code = File.ReadAllText(Path.Combine(FlyleafEngineTests.RepoRoot, "FluentFin", "Views", "SettingsPage.xaml.cs"));
		Assert.Contains("Coordinator.CheckNowAsync()", code);
		Assert.Contains("CheckNowButton.IsEnabled = false;", code);
		Assert.Contains("RaiseNotificationEvent", code);
	}

	// Nixie 1.3.1: the notice names the version and offers Download and install, Release notes and Dismiss; it only tells
	[Fact]
	public void TheNotice_OffersInstallNotesAndDismiss_AndNeverInstallsByItself()
	{
		var shell = File.ReadAllText(Path.Combine(FlyleafEngineTests.RepoRoot, "FluentFin", "Views", "ShellPage.xaml"));
		Assert.Contains("Content=\"Dismiss\"", shell);
		var code = File.ReadAllText(Path.Combine(FlyleafEngineTests.RepoRoot, "FluentFin", "Views", "ShellPage.xaml.cs"));
		Assert.Contains("UpdateTitle.Text = $\"{Core.AppIdentity.Name} {notice.Version} is available\";", code);
		// one row (Nixie 1.3.1): the title, Download and install, Release notes and Dismiss side by side, centred
		var bar = shell[shell.IndexOf("x:Name=\"UpdateBar\"", StringComparison.Ordinal)..];
		bar = bar[..bar.IndexOf("</Border>", StringComparison.Ordinal)];
		Assert.Contains("Orientation=\"Horizontal\"", bar);
		Assert.True(bar.IndexOf("UpdateTitle", StringComparison.Ordinal) < bar.IndexOf("UpdateInstallButton", StringComparison.Ordinal)
			&& bar.IndexOf("UpdateInstallButton", StringComparison.Ordinal) < bar.IndexOf("Release notes", StringComparison.Ordinal)
			&& bar.IndexOf("Release notes", StringComparison.Ordinal) < bar.IndexOf("Content=\"Dismiss\"", StringComparison.Ordinal));
		Assert.DoesNotContain("<InfoBar", bar);
		Assert.Contains("_updates.Coordinator.Dismiss()", code);
		// InstallAsync only from the buttons' click handlers
		var service = File.ReadAllText(Path.Combine(FlyleafEngineTests.RepoRoot, "FluentFin", "Services", "UpdateNoticeService.cs"));
		Assert.DoesNotContain("InstallAsync(", service[service.IndexOf("private async Task RunAsync", StringComparison.Ordinal)..]);
		// a test run asks the fake server, never GitHub, and turns the check off unless it asks for it
		Assert.Contains("UpdateSource.ApiFor(", service);
		var prepare = File.ReadAllText(Path.Combine(FlyleafEngineTests.RepoRoot, "..", "tools", "ui-harness", "offscreen", "prepare.ps1"));
		Assert.Contains("-NotePropertyName CheckForUpdates -NotePropertyValue $false", prepare);
	}
}
