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

	private static string Source(params string[] path) => File.ReadAllText(Path.Combine([FlyleafEngineTests.RepoRoot, .. path]));

	[Fact]
	public void TheNoticeIsStartedByTheApp_AndShownByTheShell_AndNotWhileItIsOff()
	{
		var app = Source("FluentFin", "App.xaml.cs");
		Assert.Contains("AddSingleton<UpdateNoticeService>", app);
		Assert.DoesNotContain("AddHostedService<WindowsUpdateService>", app);

		// Nixie 1.5: the shell hosts the notice control and gives it room when it comes and goes
		var shell = Source("FluentFin", "Views", "ShellPage.xaml");
		Assert.Contains("<controls:UpdateNotice x:Name=\"UpdateBar\" OpenChanged=\"OnUpdateBarOpenChanged\" />", shell);
		Assert.Contains("private void OnUpdateBarOpenChanged(object? sender, EventArgs e) => UpdateInsets();", Source("FluentFin", "Views", "ShellPage.xaml.cs"));
		// on Settings, whose Updates group says the same with the same action, the notice steps aside (one Primary per view)
		Assert.Contains("UpdateBar.HiddenHere = e.SourcePageType == typeof(JellyfinSettings.SettingsPage);", Source("FluentFin", "Views", "ShellPage.xaml.cs"));
		Assert.Contains("var open = !_hiddenHere && Shows(status, _updates.Coordinator.Notice);", Source("FluentFin", "Controls", "UpdateNotice.xaml.cs"));

		// the service asks the coordinator, which checks the setting before any request
		var coordinator = Source("FluentFin.Core", "Updates", "UpdateFlow.cs");
		var tick = coordinator[coordinator.IndexOf("public async Task TickAsync", StringComparison.Ordinal)..];
		Assert.True(tick.IndexOf("settings.CheckForUpdates", StringComparison.Ordinal) < tick.IndexOf("SingleAsync(", StringComparison.Ordinal));
		var returned = coordinator[coordinator.IndexOf("public async Task<UpdateCheckResult?> ReturnedAsync", StringComparison.Ordinal)..];
		Assert.True(returned.IndexOf("settings.CheckForUpdates", StringComparison.Ordinal) < returned.IndexOf("SingleAsync(", StringComparison.Ordinal));
	}

	[Fact]
	public void SettingsOffersTheToggle_CheckNow_AndInstallWhenIClose_OffByDefault()
	{
		var page = Source("FluentFin", "Controls", "UpdateSettingsPanel.xaml");
		Assert.Contains("Header=\"Check for updates\"", page);
		Assert.Contains("IsOn=\"{x:Bind CheckForUpdates, Mode=TwoWay}\"", page);
		// Nixie 1.3.1: Check now, and the text says the check is on by default and what it sends; 1.5: how often
		Assert.Contains("Content=\"Check now\"", page);
		Assert.Contains("Nothing is sent but the request for the latest release", page);
		Assert.Contains("every 4 hours, and when you come back to it", page);
		Assert.Contains("On by default.", page);
		Assert.Contains("Header=\"Install when I close Nixie\"", page);
		Assert.False(Core.Settings.SettingKeys.InstallUpdatesOnClose.Default.Value);
		// the status line is a live region and Narrator hears each step
		Assert.Contains("x:Name=\"UpdateStatusTitle\"", page);
		Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", page);
		var code = Source("FluentFin", "Controls", "UpdateSettingsPanel.xaml.cs");
		Assert.Contains("await Updates.Center.CheckNowAsync();", code);
		Assert.Contains("CheckNowButton.IsEnabled = false;", code);
		Assert.Contains("RaiseNotificationEvent", code);
		Assert.Contains("<controls:UpdateSettingsPanel x:Name=\"UpdatesGroup\" />", Source("FluentFin", "Views", "SettingsPage.xaml"));
	}

	// Nixie 1.5: the notice names each step, one row, one Primary; it never installs by itself
	[Fact]
	public void TheNotice_IsOneRow_NamesEachStep_AndNeverInstallsByItself()
	{
		var notice = Source("FluentFin", "Controls", "UpdateNotice.xaml");
		Assert.Contains("Content=\"Dismiss\"", notice);
		Assert.Contains("Content=\"Cancel\"", notice);
		Assert.Contains("AutomationProperties.Name=\"Download progress\"", notice);
		Assert.DoesNotContain("<InfoBar", notice);
		// title, detail (trims), progress, then the buttons: Primary, link, Cancel, Dismiss
		Assert.True(notice.IndexOf("x:Name=\"UpdateTitle\"", StringComparison.Ordinal) < notice.IndexOf("x:Name=\"UpdateDetail\"", StringComparison.Ordinal)
			&& notice.IndexOf("x:Name=\"UpdateDetail\"", StringComparison.Ordinal) < notice.IndexOf("x:Name=\"UpdateProgress\"", StringComparison.Ordinal)
			&& notice.IndexOf("x:Name=\"UpdateProgress\"", StringComparison.Ordinal) < notice.IndexOf("x:Name=\"UpdatePrimaryButton\"", StringComparison.Ordinal)
			&& notice.IndexOf("x:Name=\"UpdatePrimaryButton\"", StringComparison.Ordinal) < notice.IndexOf("x:Name=\"UpdateLinkButton\"", StringComparison.Ordinal)
			&& notice.IndexOf("x:Name=\"UpdateLinkButton\"", StringComparison.Ordinal) < notice.IndexOf("x:Name=\"UpdateDismissButton\"", StringComparison.Ordinal));
		Assert.Contains("TextTrimming=\"CharacterEllipsis\"", notice);
		var code = Source("FluentFin", "Controls", "UpdateNotice.xaml.cs");
		Assert.Contains("UpdateView.ForNotice(status)", code);
		Assert.Contains("RaiseNotificationEvent", code);
		// installs only from a click: RestartToUpdate from UpdateActions (the buttons), or as Nixie closes with the setting on
		var service = Source("FluentFin", "Services", "UpdateNoticeService.cs");
		var loop = service[service.IndexOf("private async Task RunAsync", StringComparison.Ordinal)..service.IndexOf("public async Task StopAsync", StringComparison.Ordinal)];
		Assert.DoesNotContain("Hand(", loop);
		Assert.DoesNotContain("RestartToUpdate(", loop);
		var stop = service[service.IndexOf("public async Task StopAsync", StringComparison.Ordinal)..];
		Assert.Contains("Settings.InstallOnClose && Center.Status.Phase == UpdatePhase.Ready && !Coordinator.IsDismissed(Center.Status.Release?.Version)", stop);
		Assert.Contains("Hand(reopen: false, quiet: true", stop);
		var actions = Source("FluentFin", "Services", "UpdateActions.cs");
		Assert.Contains("updates.RestartToUpdate(CurrentPlace());", actions);
		// a test run asks the fake server, never GitHub, and turns the check off unless it asks for it
		Assert.Contains("UpdateSource.ApiFor(", service);
		var prepare = File.ReadAllText(Path.Combine(FlyleafEngineTests.RepoRoot, "..", "tools", "ui-harness", "offscreen", "prepare.ps1"));
		Assert.Contains("-NotePropertyName CheckForUpdates -NotePropertyValue $false", prepare);
	}

	/// <summary>Nixie 1.5: the release notes' links open in the browser only on a click, and only http(s) ones.</summary>
	[Fact]
	public void WhatsNew_OpensLinksOnlyOnAClick_AndOnlyWebOnes()
	{
		var actions = Source("FluentFin", "Services", "UpdateActions.cs");
		Assert.Contains("link.Click += (_, _) => OpenLink(url);", actions);
		Assert.Contains("if (ReleaseNotes.SafeLink(url) is { } safe)", actions);
		Assert.DoesNotContain("NavigateUri", actions);
	}

	/// <summary>
	/// Nixie 1.6.1 (bug hunt BH-160-m3): the release notes start with the keyboard on Close in every theme, the session's first
	/// dialog too. The turn to Close was timed (the dialog's first 800 ms), and a session's first dialog focused its first link
	/// later than that in Classic and Drive-in. Now every move no pointer made goes to Close until the user presses a key.
	/// </summary>
	[Fact]
	public void TheNotes_StartOnClose_ByWhatTheUserDid_NotByTheTime()
	{
		var none = Microsoft.UI.Xaml.Input.FocusInputDeviceKind.None;
		var keyboard = Microsoft.UI.Xaml.Input.FocusInputDeviceKind.Keyboard;
		// the dialog's own first focus, however late (it says None, or Keyboard after Enter opened it)
		Assert.True(FluentFin.Services.UpdateActions.TurnsToClose(userPressedAKey: false, none));
		Assert.True(FluentFin.Services.UpdateActions.TurnsToClose(userPressedAKey: false, keyboard));
		// a click goes where it was made, and after a key (Tab) the keyboard goes where it is sent
		foreach (var pointer in new[] { Microsoft.UI.Xaml.Input.FocusInputDeviceKind.Mouse, Microsoft.UI.Xaml.Input.FocusInputDeviceKind.Touch, Microsoft.UI.Xaml.Input.FocusInputDeviceKind.Pen })
		{
			Assert.False(FluentFin.Services.UpdateActions.TurnsToClose(userPressedAKey: false, pointer));
		}

		Assert.False(FluentFin.Services.UpdateActions.TurnsToClose(userPressedAKey: true, keyboard));
		Assert.False(FluentFin.Services.UpdateActions.TurnsToClose(userPressedAKey: true, none));

		var actions = Source("FluentFin", "Services", "UpdateActions.cs").Replace("\r\n", "\n");
		var notes = actions[actions.IndexOf("public static async Task ShowNotesAsync(", StringComparison.Ordinal)..];
		notes = notes[..notes.IndexOf("await dialog.ShowAsync();", StringComparison.Ordinal)];
		Assert.DoesNotContain("Stopwatch", notes);
		Assert.DoesNotContain("ElapsedMilliseconds", notes);
		// the links are no tab stops until a key is pressed (the dialog's own first focus has nothing in the notes to land on), and
		// the first key frees them before its own move
		Assert.Contains("HoldLinks(links, page, held: true);", notes);
		var key = notes[notes.IndexOf("dialog.PreviewKeyDown += (_, args) =>", StringComparison.Ordinal)..];
		key = key[..key.IndexOf("dialog.GettingFocus", StringComparison.Ordinal)];
		Assert.Contains("if (!keyed && !args.KeyStatus.WasKeyDown)", key);
		Assert.Contains("HoldLinks(links, page, held: false);", key);
		Assert.True(notes.IndexOf("HoldLinks(links, page, held: true);", StringComparison.Ordinal) < notes.IndexOf("dialog.PreviewKeyDown", StringComparison.Ordinal));
		var hold = actions[actions.IndexOf("private static void HoldLinks(", StringComparison.Ordinal)..];
		Assert.Contains("link.IsTabStop = !held;", hold);
		Assert.Contains("page.IsTabStop = !held;", hold);
		Assert.Contains("if (TurnsToClose(keyed, args.InputDevice) && FindNamed<Button>(dialog, \"CloseButton\") is { } close", notes);
		Assert.Contains("if (!keyed && FindNamed<Button>(dialog, \"CloseButton\") is { } close && close.FocusState == FocusState.Unfocused)", notes);
	}
}
