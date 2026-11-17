using System.Xml.Linq;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Seaglass 1.1.4 (UX review, prioritised fixes 5 and 15), read from the XAML as SettingsAndRailXamlTests does: the
/// profile flyout is the Account menu (the user and the server, Switch user, Sign out, Settings; no Dashboard, Play On, Sync
/// Play or Quick Connect); Sync Play, Quick Connect and Play On are in Settings > Other devices, wired to the same dialogs;
/// the server's administration is in Settings for administrators; the title bar's Back and Account follow the shell's frame.
/// </summary>
public sealed class AccountMenuXamlTests
{
	private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
	private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
	private static readonly XNamespace Toolkit = "using:CommunityToolkit.WinUI.Controls";
	private static readonly XNamespace Local = "using:FluentFin.Controls";

	private const string TitleBar = "FluentFin/Views/TitleBarControl.xaml";
	private const string Settings = "FluentFin/Views/SettingsPage.xaml";

	// ---- the Account menu

	private static XElement AccountButton() => Load(TitleBar).Descendants(Presentation + "Button").Single(b => (string?)b.Attribute(Xaml + "Name") == "AccountButton");

	[Fact]
	public void TheAccountMenu_HoldsSwitchUserSignOutAndSettings_InThatOrder()
	{
		var flyout = AccountButton().Descendants(Presentation + "Flyout").Single();
		var items = flyout.Descendants(Local + "MenuFlyoutButton").Select(b => ((string?)b.Attribute("Content"), (string?)b.Attribute("Command"))).ToList();

		Assert.Equal(
		[
			("Switch user", "{x:Bind ViewModel.SwitchUserCommand}"),
			("Sign out", "{x:Bind ViewModel.LogoutCommand}"),
			("Settings", "{x:Bind ViewModel.GoToSettingsCommand}"),
		], items);

		// each closes the menu as it runs
		Assert.All(flyout.Descendants(Local + "MenuFlyoutButton"), b => Assert.Equal("CloseAccountFlyout", (string?)b.Attribute("Click")));
	}

	[Fact]
	public void TheAccountMenu_HasNoDashboardPlayOnSyncPlayOrQuickConnect()
	{
		// every attribute of the title bar (its comments may say where they went)
		var values = Load(TitleBar).Descendants().SelectMany(e => e.Attributes()).Select(a => a.Value).ToList();

		foreach (var gone in new[] { "Dashboard", "Play On", "Sync Play", "Quick Connect", "Logout", "ChangeDefaultSession", "SelectSyncPlayGroup", "QuickConnectAuth" })
		{
			Assert.DoesNotContain(values, v => v.Contains(gone, StringComparison.Ordinal) && !v.Contains("LogoutCommand", StringComparison.Ordinal));
		}

		Assert.DoesNotContain("GoToDashboard", Code("FluentFin/ViewModels/TitleBarViewModel.cs"));
	}

	[Fact]
	public void TheAccountMenu_NamesTheUserAndTheServer_AndItsPaneIsNamedAccount()
	{
		var button = AccountButton();
		var texts = button.Descendants(Presentation + "TextBlock").Select(t => (string?)t.Attribute("Text")).ToList();

		Assert.Contains("{x:Bind ViewModel.User.Name, Mode=OneWay}", texts);
		Assert.Contains("{x:Bind ViewModel.ServerName, Mode=OneWay}", texts);
		Assert.Equal("{x:Bind core:ItemLabels.AccountButtonName(ViewModel.User.Name), Mode=OneWay}", (string?)button.Attribute("AutomationProperties.Name"));

		var presenter = button.Descendants(Presentation + "Style").Single(s => (string?)s.Attribute("TargetType") == "FlyoutPresenter");
		Assert.Equal("{StaticResource GlassFlyoutPresenterStyle}", (string?)presenter.Attribute("BasedOn"));
		Assert.Contains(presenter.Elements(Presentation + "Setter"), s => (string?)s.Attribute("Property") == "AutomationProperties.Name" && (string?)s.Attribute("Value") == "Account");
	}

	[Fact]
	public void TheAccountButton_IsA40PxTarget()
	{
		var button = AccountButton();

		Assert.Equal("40", (string?)button.Attribute("Width"));
		Assert.Equal("40", (string?)button.Attribute("Height"));
	}

	// ---- the title bar while the player page is shown

	[Fact]
	public void TheTitleBarsBackAndAccount_FollowTheViewModel_NotJustTheUser()
	{
		var bar = Load(TitleBar).Descendants(Presentation + "TitleBar").Single();

		Assert.Equal("{x:Bind ViewModel.IsBackButtonShown, Mode=OneWay}", (string?)bar.Attribute("IsBackButtonVisible"));
		Assert.Equal("{x:Bind c:Converters.BooleanToVisibility(ViewModel.IsAccountShown), Mode=OneWay}", (string?)AccountButton().Attribute("Visibility"));
	}

	[Fact]
	public void TheShellsFrame_TellsTheTitleBarWhichPageItShows()
	{
		var shell = Code("FluentFin/Views/ShellPage.xaml.cs");

		// by the page's type, as the frame shows it (not the navigation service's Navigated, which waits for the page's view model)
		Assert.Contains("NavigationFrame.Navigated += OnFrameNavigated;", shell);
		Assert.Contains("private void OnFrameNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e) => _titleBar.OnPageShown(e.SourcePageType);", shell);
		// the shell going (Log out, Switch user) shows them again; a shell back in the window looks again
		Assert.Contains("Unloaded += (_, _) => _titleBar.OnPageShown(null);", shell);
		Assert.Contains("_titleBar.OnPageShown(NavigationFrame.Content?.GetType());", shell);

		// nothing of the player's own files drives it
		Assert.DoesNotContain("OnPageShown", Code("FluentFin/Views/VideoPlayerPage.xaml.cs"));
	}

	// ---- Settings > Other devices

	[Fact]
	public void OtherDevices_ComesAfterServersAndAccounts()
	{
		var titles = Groups().Select(Title).ToList();

		Assert.Equal(titles.IndexOf("Servers and accounts") + 1, titles.IndexOf("Other devices"));
	}

	[Theory]
	[InlineData("Quick Connect", "QuickConnectButton", "Enter a code", "Enter a code for Quick Connect", "{x:Bind root:App.Dialogs.QuickConnectAuthCommand}")]
	[InlineData("Sync Play", "SyncPlayButton", "Choose a group", "Choose a group for Sync Play", "{x:Bind root:App.Dialogs.SelectSyncPlayGroupCommand}")]
	[InlineData("Play On", "PlayOnButton", "Choose a device", "Choose a device for Play On", "{x:Bind root:App.Dialogs.ChangeDefaultSessionCommand}")]
	public void OtherDevices_OpensTheSameDialogsTheProfileFlyoutDid(string header, string name, string content, string automationName, string command)
	{
		var card = Assert.Single(Group("Other devices").Descendants(Toolkit + "SettingsCard"), c => (string?)c.Attribute("Header") == header);
		var button = Assert.Single(card.Descendants(Presentation + "Button"));

		Assert.Equal(name, (string?)button.Attribute(Xaml + "Name"));
		Assert.Equal(content, (string?)button.Attribute("Content"));
		// the visible text first, then what it is for (WCAG 2.5.3, label in name)
		Assert.StartsWith(content, automationName, StringComparison.Ordinal);
		Assert.Equal(automationName, (string?)button.Attribute("AutomationProperties.Name"));
		Assert.Equal(command, (string?)button.Attribute("Command"));
		Assert.Null(button.Attribute("Style"));
	}

	[Fact]
	public void SyncPlay_IsDisabledWithTheMpvPlayer_AndSaysWhy()
	{
		var card = Assert.Single(Group("Other devices").Descendants(Toolkit + "SettingsCard"), c => (string?)c.Attribute("Header") == "Sync Play");

		Assert.Equal("{x:Bind ViewModel.SyncPlayDescription, Mode=OneWay}", (string?)card.Attribute("Description"));
		Assert.Equal("{x:Bind ViewModel.IsSyncPlayAvailable, Mode=OneWay}", (string?)card.Descendants(Presentation + "Button").Single().Attribute("IsEnabled"));
	}

	[Theory]
	[InlineData(MediaPlayerType.Mpv, false)]
	[InlineData(MediaPlayerType.WindowsMediaPlayer, true)]
	public void SyncPlay_FollowsThePlayer(MediaPlayerType player, bool available)
	{
		var vm = new SettingsViewModel(Mock.Of<ISettings>(), Mock.Of<FluentFin.Core.Mpv.IMpvBackend>(), Mock.Of<IJellyfinClient>(), null!);
		var changed = new List<string?>();
		vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

		vm.MediaPlayerType = player == MediaPlayerType.Mpv ? MediaPlayerType.WindowsMediaPlayer : MediaPlayerType.Mpv;
		changed.Clear();
		vm.MediaPlayerType = player;

		Assert.Equal(available, vm.IsSyncPlayAvailable);
		Assert.Equal(!available, vm.SyncPlayDescription.Contains("Not available with the mpv player", StringComparison.Ordinal));
		Assert.Contains(nameof(SettingsViewModel.IsSyncPlayAvailable), changed);
		Assert.Contains(nameof(SettingsViewModel.SyncPlayDescription), changed);
	}

	// ---- Settings > Servers and accounts > Server administration (the old Dashboard), administrators only

	[Fact]
	public void ServerAdministration_IsInServersAndAccounts_ForAdministratorsOnly()
	{
		var card = Assert.Single(Group("Servers and accounts").Descendants(Toolkit + "SettingsCard"), c => (string?)c.Attribute("Header") == "Server administration");
		var button = Assert.Single(card.Descendants(Presentation + "Button"));

		Assert.Equal("{x:Bind c:Converters.BooleanToVisibility(ShowsServerAdministration)}", (string?)card.Attribute("Visibility"));
		Assert.Equal("OnServerAdministrationClick", (string?)button.Attribute("Click"));
		Assert.Equal("Open server administration", (string?)button.Attribute("AutomationProperties.Name"));

		var code = Code("FluentFin/Views/SettingsPage.xaml.cs");
		Assert.Contains("public bool ShowsServerAdministration => SettingsViewModel.ShowsServerAdministration(SessionInfo.CurrentUser);", code);
		Assert.Contains("NavigateTo(typeof(JellyfinSettingsViewModel).FullName!, new object())", code);
	}

	[Fact]
	public void ServerAdministration_ShowsForAnAdministrator()
	{
		Assert.True(SettingsViewModel.ShowsServerAdministration(new UserDto { Policy = new UserPolicy { IsAdministrator = true } }));
		Assert.False(SettingsViewModel.ShowsServerAdministration(new UserDto { Policy = new UserPolicy { IsAdministrator = false } }));
		Assert.False(SettingsViewModel.ShowsServerAdministration(new UserDto()));
		Assert.False(SettingsViewModel.ShowsServerAdministration(null));
	}

	private static IEnumerable<XElement> Groups() => Load(Settings).Descendants(Presentation + "ContentControl")
		.Where(x => (string?)x.Attribute("Style") == "{StaticResource GlassSurfaceStyle}");

	private static string? Title(XElement group) => (string?)group.Descendants(Presentation + "TextBlock").First().Attribute("Text");

	private static XElement Group(string title) => Groups().Single(g => Title(g) == title);

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
