using System.Xml.Linq;
using FluentFin.Core.ViewModels;

namespace FluentFin.Core.Tests;

/// <summary>
/// Seaglass 1.1.3 (UX review, prioritised fixes 1, 2, 6 and 9), read from the XAML as AccessibilityXamlTests does: the rail
/// holds only destinations; Settings has a title on every group and About last, with the version, the licence, the credits,
/// Contribute and Report a problem as keyboard-operable buttons, and Check for updates and the shortcut sheet; Remove server
/// and Remove are neutral buttons; no stale "FluentFin" or "built-in players" copy.
/// </summary>
public sealed class SettingsAndRailXamlTests
{
	private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
	private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
	private static readonly XNamespace Toolkit = "using:CommunityToolkit.WinUI.Controls";

	private const string Settings = "FluentFin/Views/SettingsPage.xaml";
	private const string Shell = "FluentFin/Views/ShellPage.xaml";

	// ---- 1.2.0: no rail; the sections are tabs in the top bar (TopNavigationTests: which tabs, which one is marked)

	[Fact]
	public void TheShell_HasNoRail()
	{
		var shell = Load(Shell);

		Assert.Empty(shell.Descendants(Presentation + "NavigationView"));
		Assert.Empty(shell.Descendants(Presentation + "NavigationViewItem"));
		Assert.DoesNotContain(shell.Descendants(), e => e.Attribute("Tapped") is not null);
		var code = Code("FluentFin/Views/ShellPage.xaml.cs");
		Assert.DoesNotContain("OnContributeTapped", code);
		Assert.DoesNotContain("NavigationViewControl", code);
		Assert.DoesNotContain("RailSelection", code);
	}

	[Fact]
	public void TheTopBar_HostsTheTabs_WithNoSettingsGear()
	{
		var bar = Load("FluentFin/Views/TitleBarControl.xaml");
		XNamespace local = "using:FluentFin.Controls";

		var tabs = Assert.Single(bar.Descendants(local + "TopTabStrip"));
		Assert.Equal("{x:Bind c:Converters.BooleanToVisibility(ViewModel.IsNavigationShown), Mode=OneWay}", (string?)tabs.Attribute("Visibility"));
		// Settings is in the Account menu (and Ctrl+,): no gear in the bar outside that menu
		var flyout = bar.Descendants(Presentation + "Flyout").Single(f => (string?)f.Attribute(Xaml + "Name") == "AccountFlyout");
		Assert.DoesNotContain(bar.Descendants().Where(e => !e.Ancestors().Contains(flyout)), e => e.Attributes().Any(a => a.Value.Contains('\uE713')));
		Assert.Contains(flyout.Descendants(), e => (string?)e.Attribute("Command") == "{x:Bind ViewModel.GoToSettingsCommand}");
	}

	[Fact]
	public void Jellyseerr_AddsNothingToTheRail()
	{
		var plugin = Code("FluentFin.Plugins/Jellyseer/JellyseerPlugin.cs");
		var method = plugin[plugin.IndexOf("public void AddNavigationViewItem", StringComparison.Ordinal)..plugin.IndexOf("public void ConfigurePages", StringComparison.Ordinal)];

		Assert.DoesNotContain("AddNavigationItem(", method);
	}

	// ---- Settings: groups

	[Fact]
	public void EverySettingsGroup_HasATitle_AndAboutIsLast()
	{
		// Seaglass 1.1.4: a group is its heading and its cards, no panel around them (AccountMenuXamlTests.Groups)
		var groups = AccountMenuXamlTests.Groups().ToList();

		var titles = groups.Select(g =>
		{
			var heading = g.Descendants(Presentation + "TextBlock").First();
			Assert.Equal("Level2", (string?)heading.Attribute("AutomationProperties.HeadingLevel"));
			return (string?)heading.Attribute("Text");
		}).ToList();

		// Seaglass 1.1.4: Other devices (Quick Connect, SyncPlay, from the old profile flyout)
		Assert.Equal(["Appearance", "Playback", "Audio and subtitles", "Servers and accounts", "Other devices", "Integrations", "Advanced", "About"], titles);

		// nothing after About
		var stack = groups[^1].Parent!;
		Assert.Same(groups[^1], stack.Elements().Last());
	}

	[Fact]
	public void TheServersList_IsInsideItsGroup()
	{
		var servers = Group("Servers and accounts");

		Assert.Single(servers.Descendants(Presentation + "ItemsRepeater"), r => (string?)r.Attribute("ItemsSource") == "{x:Bind ViewModel.Servers}");
	}

	// ---- Settings: destructive buttons

	[Fact]
	public void NoSettingsButton_IsAccentStyled()
	{
		var accent = Load(Settings).Descendants().Where(e => ((string?)e.Attribute("Style"))?.Contains("Accent", StringComparison.Ordinal) == true).ToList();

		Assert.Empty(accent);
	}

	[Fact]
	public void TheRemoveButtons_AreNeutral_NamedForWhatTheyRemove_AndGoThroughTheAskingPaths()
	{
		var buttons = Group("Servers and accounts").Descendants(Presentation + "Button").ToList();

		var server = Assert.Single(buttons, b => (string?)b.Attribute("Content") == "Remove server");
		Assert.Null(server.Attribute("Style"));
		Assert.Equal("{x:Bind vm:SettingsViewModel.RemoveServerLabel(DisplayName)}", (string?)server.Attribute("AutomationProperties.Name"));
		Assert.Equal("{Binding ElementName=Page, Path=ViewModel.DeleteServerCommand}", (string?)server.Attribute("Command"));

		var user = Assert.Single(buttons, b => (string?)b.Attribute("Content") == "Remove sign-in");
		Assert.Null(user.Attribute("Style"));
		Assert.Equal("{x:Bind vm:SettingsViewModel.RemoveUserLabel(Username)}", (string?)user.Attribute("AutomationProperties.Name"));
		Assert.Equal("RemoveUser", (string?)user.Attribute("Click"));

		var code = Code("FluentFin/Views/SettingsPage.xaml.cs");
		Assert.Contains("ViewModel.Confirm = prompt => ConfirmDialog.AskAsync(XamlRoot, prompt);", code);
		Assert.Contains("await ViewModel.RemoveSavedUser(server, user);", code);
	}

	// ---- Settings: About

	[Fact]
	public void About_HasTheVersionLicenceAndCredits()
	{
		var about = Group("About");
		var cards = about.Descendants(Toolkit + "SettingsCard").ToList();

		var version = Assert.Single(cards, c => (string?)c.Attribute("Header") == "{x:Bind vm:SettingsViewModel.AboutTitle}");
		Assert.Contains("GPL-3.0", (string?)version.Attribute("Description"));
		Assert.Contains(version.Descendants(Presentation + "HyperlinkButton"), h => (string?)h.Attribute("NavigateUri") == "{x:Bind vm:SettingsViewModel.LicenseUri}");

		var credits = Assert.Single(cards, c => ((string?)c.Attribute("Header"))?.StartsWith("Built on", StringComparison.Ordinal) == true);
		Assert.Contains("FluentFin", (string?)credits.Attribute("Description"));
		Assert.Contains("jellyfin-mpv-shim", (string?)credits.Attribute("Description"));
		Assert.Equal(new string?[] { "{x:Bind vm:SettingsViewModel.FluentFinUri}", "{x:Bind vm:SettingsViewModel.ShimUri}" },
			credits.Descendants(Presentation + "HyperlinkButton").Select(h => (string?)h.Attribute("NavigateUri")));

		// the title is the app's name and a release version (AppVersion, as the title bar and the server get it)
		Assert.Equal("Nixie 1.2.0", SettingsViewModel.AboutTitleFor("1.2.0"));
		Assert.Matches(@"^Nixie \d+\.\d+\.\d+", SettingsViewModel.AboutTitle);
	}

	[Theory]
	[InlineData("Contribute", "OnContributeClick", "ContributeUrl")]
	[InlineData("Report a problem", "OnReportProblemClick", "ReportProblemUrl")]
	public void AboutLinks_AreButtons_SoEnterAndSpaceWork(string header, string handler, string url)
	{
		var card = Assert.Single(Group("About").Descendants(Toolkit + "SettingsCard"), c => (string?)c.Attribute("Header") == header);

		// a clickable SettingsCard is a ButtonBase: Click fires for a click, Enter and Space (the old rail item was Tapped-only)
		Assert.Equal("True", (string?)card.Attribute("IsClickEnabled"));
		Assert.Equal(handler, (string?)card.Attribute("Click"));
		Assert.Null(card.Attribute("Tapped"));
		Assert.Contains($"private async void {handler}(object sender, RoutedEventArgs e) => await Launcher.LaunchUriAsync(new Uri(SettingsViewModel.{url}));", Code("FluentFin/Views/SettingsPage.xaml.cs"));
	}

	[Fact]
	public void AboutLinks_GoToThisProject()
	{
		Assert.Equal("https://github.com/mbustos27/fluentfin-mpv", SettingsViewModel.ContributeUrl);
		Assert.Equal("https://github.com/mbustos27/fluentfin-mpv/issues/new", SettingsViewModel.ReportProblemUrl);
		Assert.Equal(new Uri("https://github.com/mbustos27/fluentfin-mpv/blob/main/LICENSE"), SettingsViewModel.LicenseUri);
	}

	[Fact]
	public void CheckForUpdatesAndShortcuts_MovedFromPlaybackToAbout()
	{
		var about = Group("About").Descendants(Toolkit + "SettingsCard").Select(c => (string?)c.Attribute("Header")).ToList();
		var playback = Group("Playback").Descendants(Toolkit + "SettingsCard").Select(c => (string?)c.Attribute("Header")).ToList();

		Assert.Contains("Keyboard shortcuts", about);
		Assert.DoesNotContain("Check for updates", playback);
		Assert.DoesNotContain("Keyboard shortcuts", playback);
		// Nixie 1.5: Check for updates has its own group, Updates (Controls\UpdateSettingsPanel), just above About
		var settings = Load(Settings);
		var panel = settings.Descendants().Single(e => e.Name.LocalName == "UpdateSettingsPanel");
		var aboutHeading = settings.Descendants().Single(e => e.Name.LocalName == "TextBlock" && (string?)e.Attribute("Text") == "About");
		Assert.True(panel.IsBefore(aboutHeading));
		var updates = XDocument.Load(Path.Combine(RepoRoot, "FluentFin/Controls/UpdateSettingsPanel.xaml"));
		var headers = updates.Descendants(Toolkit + "SettingsCard").Select(c => (string?)c.Attribute("Header")).ToList();
		Assert.Equal(["Check for updates", "Install when I close Nixie"], headers);
		Assert.Contains(updates.Descendants(), e => e.Name.LocalName == "TextBlock" && (string?)e.Attribute("Text") == "Updates"
			&& (string?)e.Attribute(XName.Get("AutomationProperties.HeadingLevel")) == "Level2");
	}

	[Fact]
	public void NothingInSettings_IsTappedOnly()
	{
		Assert.DoesNotContain(Load(Settings).Descendants(), e => e.Attribute("Tapped") is not null);
	}

	// ---- Settings: tidy

	[Fact]
	public void NoStaleCopy_FluentFinOrBuiltInPlayers()
	{
		// the credits card names FluentFin on purpose; everything else on the page is Seaglass
		var credits = Group("About").Descendants(Toolkit + "SettingsCard").Single(c => ((string?)c.Attribute("Header"))?.StartsWith("Built on", StringComparison.Ordinal) == true);
		var texts = Load(Settings).Descendants()
			.Where(e => !e.AncestorsAndSelf().Any(a => a.Name == credits.Name && (string?)a.Attribute("Header") == (string?)credits.Attribute("Header")))
			.SelectMany(e => e.Attributes().Where(a => a.Name.LocalName is "Description" or "Text" or "Header" or "Content" or "Title" or "PlaceholderText"))
			.Select(a => a.Value)
			.ToList();

		Assert.NotEmpty(texts);
		Assert.DoesNotContain(texts, t => t.Contains("FluentFin", StringComparison.Ordinal));
		Assert.DoesNotContain(texts, t => t.Contains("built-in player", StringComparison.OrdinalIgnoreCase));
		Assert.DoesNotContain(SettingsViewModel.AllMediaPlayers, p => p.Name.Contains("jellyfin-mpv-shim", StringComparison.Ordinal));
	}

	[Fact]
	public void ThePlayerList_IsShownOnlyWithAChoice_AndAutoplayOnlyForAPlayerThatFollowsIt()
	{
		var playback = Group("Playback");
		var player = Assert.Single(playback.Descendants(Toolkit + "SettingsCard"), c => (string?)c.Attribute("Header") == "Player");
		Assert.Equal("{x:Bind c:Converters.BooleanToVisibility(HasPlayerChoice)}", (string?)player.Attribute("Visibility"));
		Assert.Contains("public bool HasPlayerChoice => SettingsViewModel.HasPlayerChoice;", Code("FluentFin/Views/SettingsPage.xaml.cs"));

		var autoplay = Assert.Single(Load(Settings).Descendants(Toolkit + "SettingsCard"), c => (string?)c.Attribute("Header") == "Play the next episode automatically");
		// Seaglass 1.1.4 (interaction audit, item 7): for every player, the mpv player too (it was hidden with mpv: no way to turn
		// auto-play off with the only shipped player); bound to the user's EnableNextEpisodeAutoPlay, which the player sends on
		Assert.Null(autoplay.Attribute("Visibility"));
		Assert.Equal("{x:Bind ViewModel.AutoPlayNextEpisode, Mode=TwoWay}", (string?)autoplay.Descendants(Presentation + "ToggleSwitch").Single().Attribute("IsOn"));
	}

	// ---- Settings: Integrations

	[Fact]
	public void Integrations_OpensJellyseerr_WithAButton()
	{
		var card = Assert.Single(Group("Integrations").Descendants(Toolkit + "SettingsCard"), c => (string?)c.Attribute("Header") == "Jellyseerr");
		var button = Assert.Single(card.Descendants(Presentation + "Button"));

		Assert.Equal("OnJellyseerrClick", (string?)button.Attribute("Click"));
		Assert.Equal("{x:Bind JellyseerrButtonName}", (string?)button.Attribute("AutomationProperties.Name"));
		Assert.Contains("NavigateTo(JellyseerrEntry.PageKey)", Code("FluentFin/Views/SettingsPage.xaml.cs"));
	}

	private static XElement Group(string title) => AccountMenuXamlTests.Groups()
		.Single(x => (string?)x.Elements().First().Attribute("Text") == title);

	private static XDocument Load(string repoRelativePath) => XDocument.Load(Path.Combine(RepoRoot, repoRelativePath), LoadOptions.SetLineInfo);

	private static string Code(string repoRelativePath) => File.ReadAllText(Path.Combine(RepoRoot, repoRelativePath));

	private static string RepoRoot { get; } = FindRepoRoot();

	private static string FindRepoRoot()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FluentFin.sln")))
		{
			dir = dir.Parent;
		}

		return dir?.FullName ?? throw new DirectoryNotFoundException("FluentFin.sln not found above " + AppContext.BaseDirectory);
	}
}
