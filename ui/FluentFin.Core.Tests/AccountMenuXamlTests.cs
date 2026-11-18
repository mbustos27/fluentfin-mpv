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
	public void TheAccountMenu_HoldsSwitchUserAndSettings_ThenSignOutLast()
	{
		var flyout = AccountButton().Descendants(Presentation + "Flyout").Single();
		var items = flyout.Descendants(Local + "MenuFlyoutButton").Select(b => ((string?)b.Attribute("Content"), (string?)b.Attribute("Command"))).ToList();

		// layout audit item 18: account flyouts end with Sign out, after a separator
		Assert.Equal(
		[
			("Switch user", "{x:Bind ViewModel.SwitchUserCommand}"),
			("Settings", "{x:Bind ViewModel.GoToSettingsCommand}"),
			("Sign out", "{x:Bind ViewModel.LogoutCommand}"),
		], items);
		var signOut = flyout.Descendants(Local + "MenuFlyoutButton").Last();
		Assert.Equal(Presentation + "MenuFlyoutSeparator", ((XElement)signOut.PreviousNode!).Name);

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
	public void TheAccountMenu_DimsNothing()
	{
		// layout audit item 18: LightDismissOverlayMode="On" washed the whole window white (light) or dimmed it (dark)
		Assert.Equal("Off", (string?)AccountButton().Descendants(Presentation + "Flyout").Single().Attribute("LightDismissOverlayMode"));
	}

	[Fact]
	public void TheTitleBar_HidesSearchWhenSignedOut_AndHasNoVersionSubtitle()
	{
		var doc = Load(TitleBar);
		var box = doc.Descendants(Presentation + "AutoSuggestBox").Single();

		// a property, so signing out hides it: x:Bind does not call SearchVisibility(User, ...) again once User is null; 1.2.0: the
		// box or, in a narrow window, its button (TitleBarControl.ShowSearch), both from IsSearchShown
		Assert.Equal("Collapsed", (string?)box.Attribute("Visibility"));
		var show = Code("FluentFin/Views/TitleBarControl.xaml.cs");
		show = show[show.IndexOf("private void ShowSearch()", StringComparison.Ordinal)..];
		show = show[..show.IndexOf("\n\t}", StringComparison.Ordinal)];
		Assert.Contains("var shown = ViewModel.IsSearchShown;", show);
		Assert.Contains("SearchBox.Visibility = shown && (!_compact || _searchOpen)", show);
		Assert.Contains("SearchIconButton.Visibility = shown && _compact && !_searchOpen", show);
		Assert.Equal("Hidden", (string?)box.Attribute("KeyboardAcceleratorPlacementMode"));
		// layout audit item 20: the version is in Settings > About; the subtitle says "Preview" on a test build only
		// 1.2.0: the wordmark's own subtitle line, in the bar's left header with the tabs (the TitleBar's own Title and Subtitle unused)
		Assert.Contains(doc.Descendants(Presentation + "TextBlock"), t => (string?)t.Attribute(Xaml + "Name") == "SubtitleText" && (string?)t.Attribute("Text") == "{x:Bind ViewModel.Subtitle}");
		// the 1.2.0 merge: the glass stream's wordmark at title-bar size in the placeholder's place
		Assert.Contains(doc.Descendants(), t => t.Name.LocalName == "AppWordmark" && (string?)t.Attribute(Xaml + "Name") == "WordmarkText" && (string?)t.Attribute("Text") == "{x:Bind ViewModel.AppName}" && (string?)t.Attribute("Size") == "15");
		Assert.Null(doc.Descendants(Presentation + "TitleBar").Single().Attribute("Subtitle"));
		// layout audit item 10: a friendly kind under a suggestion, not the raw "Series"
		Assert.Contains(doc.Descendants(Presentation + "TextBlock"), t => (string?)t.Attribute("Text") == "{x:Bind cvm:SearchLabels.KindTitle(Item.Type)}");
	}

	[Fact]
	public void TheCaptionButtons_FollowTheAppsTheme()
	{
		var code = Code("FluentFin/Views/TitleBarControl.xaml.cs");

		Assert.Contains("ActualThemeChanged += (_, _) => ApplyCaptionTheme();", code);
		// review: high contrast switched while running (Windows' colours then, or the theme's again)
		Assert.Contains("_themeSettings.Changed += (_, _) => DispatcherQueue.TryEnqueue(ApplyCaptionTheme);", code);
		Assert.Contains("var highContrast = _themeSettings?.HighContrast", code);
		// set up once the window exists, inside a try: subscribing to AccessibilitySettings in the constructor threw, and the
		// window never opened
		Assert.Contains("Loaded += (_, _) => WatchHighContrast();", code);
		Assert.DoesNotContain("HighContrastChanged +=", code);
		Assert.Contains("bar.PreferredTheme = CaptionThemeFor(ActualTheme, highContrast);", code);
		Assert.Contains("bar.ButtonForegroundColor = colors?.Foreground;", code);
		Assert.Contains("bar.ButtonHoverBackgroundColor = colors?.HoverBackground;", code);
	}

	[Fact]
	public void TheAccountButton_IsA32PxCircle_AndTheSearchIconA32PxSquare()
	{
		// button review T8 and T5: they rendered as 40 x 44 ellipses (stretched by the bar's height); 32 px is above WCAG 2.5.8's 24
		var button = AccountButton();
		Assert.Equal(("32", "32", "16", "Center"), ((string?)button.Attribute("Width"), (string?)button.Attribute("Height"), (string?)button.Attribute("CornerRadius"), (string?)button.Attribute("VerticalAlignment")));
		Assert.Equal("{x:Bind core:ItemLabels.AccountTooltip(ViewModel.User.Name), Mode=OneWay}", (string?)button.Attribute("ToolTipService.ToolTip"));
		Assert.Equal("Account (alice)", ItemLabels.AccountTooltip(" alice "));

		var search = Load(TitleBar).Descendants(Presentation + "Button").Single(b => (string?)b.Attribute(Xaml + "Name") == "SearchIconButton");
		Assert.Equal(("32", "32", "8", "Center"), ((string?)search.Attribute("Width"), (string?)search.Attribute("Height"), (string?)search.Attribute("CornerRadius"), (string?)search.Attribute("VerticalAlignment")));
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
		var handler = shell[shell.IndexOf("private void OnFrameNavigated", StringComparison.Ordinal)..];
		handler = handler[..handler.IndexOf("\n\t}", StringComparison.Ordinal)];
		// review: with the page's parameter, so a results page puts its own query back in the box
		Assert.Contains("_titleBar.OnPageShown(e.SourcePageType, e.Parameter);", handler);
		// 1.2.0: the tab the page marks, and where it starts (under the bar for a hero)
		Assert.Contains("_top.OnPageShown(KeyOf(e.SourcePageType), e.Parameter, NavigationFrame.BackStackDepth,", handler);
		Assert.Contains("UpdateInsets();", handler);
		// the shell going (Log out, Switch user) shows them again; a shell back in the window looks again
		Assert.Contains("Unloaded += (_, _) => _titleBar.OnPageShown(null);", shell);
		Assert.Contains("_titleBar.OnPageShown(NavigationFrame.Content?.GetType());", shell);

		// nothing of the player's own files drives it
		Assert.DoesNotContain("OnPageShown", Code("FluentFin/Views/VideoPlayerPage.xaml.cs"));
	}

	// ---- Settings > Other devices

	[Fact]
	public void Settings_HasNoPanelAroundAGroup_AndItsColumnIsLeftAligned()
	{
		var doc = Load(Settings);

		// layout audit items 3 and 17: no boxes in boxes; headers outside the cards
		Assert.DoesNotContain(doc.Descendants(), e => ((string?)e.Attribute("Style"))?.Contains("GlassSurfaceStyle", StringComparison.Ordinal) == true);
		Assert.All(Groups(), g => Assert.Equal("{StaticResource BodyStrongTextBlockStyle}", (string?)g.Elements().First().Attribute("Style")));
		// one star column up to 1000 px, the rest of a wide window empty on the right (a centred column sat 435 px from the rail)
		var column = doc.Descendants(Presentation + "ColumnDefinition").First();
		Assert.Equal(("*", "1000"), ((string?)column.Attribute("Width"), (string?)column.Attribute("MaxWidth")));
	}

	[Fact]
	public void RemoveServer_IsInsideTheExpandedCard()
	{
		var expander = Group("Servers and accounts").Descendants(Toolkit + "SettingsExpander").Single();
		var footer = expander.Element(Toolkit + "SettingsExpander.ItemsFooter");

		Assert.NotNull(footer);
		Assert.Contains(footer!.Descendants(Presentation + "Button"), b => (string?)b.Attribute("Content") == "Remove server");
		Assert.DoesNotContain(expander.Elements(Presentation + "Button"), _ => true);
	}

	[Fact]
	public void OtherDevices_ComesAfterServersAndAccounts()
	{
		var titles = Groups().Select(Title).ToList();

		Assert.Equal(titles.IndexOf("Servers and accounts") + 1, titles.IndexOf("Other devices"));
	}

	[Theory]
	[InlineData("Quick Connect", "QuickConnectButton", "Enter a code", "Enter a code for Quick Connect", "{x:Bind root:App.Dialogs.QuickConnectAuthCommand}")]
	[InlineData("SyncPlay", "SyncPlayButton", "Choose a group", "Choose a group for SyncPlay", "{x:Bind root:App.Dialogs.SelectSyncPlayGroupCommand}")]
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
	public void PlayOn_LeftSettings()
	{
		// layout audit item 18: its dialog showed nothing with no other device; a cast target belongs by what you play
		Assert.DoesNotContain(Load(Settings).Descendants(), e => (string?)e.Attribute(Xaml + "Name") is "PlayOnCard" or "PlayOnButton");
		Assert.DoesNotContain("ChangeDefaultSession", Code(Settings), StringComparison.Ordinal);
	}

	[Fact]
	public void SyncPlay_IsDisabledWithTheMpvPlayer_AndSaysWhy()
	{
		var card = Assert.Single(Group("Other devices").Descendants(Toolkit + "SettingsCard"), c => (string?)c.Attribute("Header") == "SyncPlay");

		Assert.Equal("{x:Bind ViewModel.SyncPlayDescription, Mode=OneWay}", (string?)card.Attribute("Description"));
		Assert.Equal("{x:Bind ViewModel.IsSyncPlayAvailable, Mode=OneWay}", (string?)card.Descendants(Presentation + "Button").Single().Attribute("IsEnabled"));
	}

	[Theory]
	[InlineData(UserPolicy_SyncPlayAccess.None, false, "Not available on this server")]
	[InlineData(UserPolicy_SyncPlayAccess.JoinGroups, true, "Your account can't start one here.")]
	[InlineData(UserPolicy_SyncPlayAccess.CreateAndJoinGroups, true, "join a group")]
	public void SyncPlay_FollowsWhatTheServerAllowsThisAccount(UserPolicy_SyncPlayAccess access, bool available, string says)
	{
		var vm = new SettingsViewModel(Mock.Of<ISettings>(), Mock.Of<FluentFin.Core.Mpv.IMpvBackend>(), Mock.Of<IJellyfinClient>(), null!)
		{
			CurrentUser = () => new UserDto { Policy = new UserPolicy { SyncPlayAccess = access } },
		};
		vm.MediaPlayerType = MediaPlayerType.WindowsMediaPlayer;

		Assert.Equal(available, vm.IsSyncPlayAvailable);
		Assert.Contains(says, vm.SyncPlayDescription, StringComparison.Ordinal);
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

	/// <summary>Settings' groups: a StackPanel whose first element is its level-2 heading (no panel around them since 1.1.4).</summary>
	internal static IEnumerable<XElement> Groups() => Load(Settings).Descendants(Presentation + "StackPanel")
		.Where(x => x.Elements().FirstOrDefault() is { } first && first.Name == Presentation + "TextBlock" && (string?)first.Attribute("AutomationProperties.HeadingLevel") == "Level2");

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
