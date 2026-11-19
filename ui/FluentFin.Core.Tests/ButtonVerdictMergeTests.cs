using System.Xml.Linq;

namespace FluentFin.Core.Tests;

/// <summary>
/// The 1.2.0 release merge: the button review's section 4 verdicts for the files no stream owned (main's), applied with
/// the button system's Nixie* styles. Each fails if its verdict is undone.
/// </summary>
public sealed class ButtonVerdictMergeTests
{
	private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

	[Fact]
	public void TheLegacyBar_SaysWhatEachButtonDoes_AndEveryButtonIsATabStop()
	{
		var bar = Load("FluentFin/Controls/TransportControls.xaml");
		var buttons = bar.Descendants().Where(e => e.Name.LocalName == "AppBarButton").ToList();
		Assert.DoesNotContain(buttons, b => (string?)b.Attribute("IsTabStop") == "False"); // P28-P44
		var labels = buttons.Select(b => (string?)b.Attribute("Label")).ToList();
		Assert.Contains("Forward 30 seconds", labels); // P30
		Assert.Contains("Back 10 seconds", labels);    // P34
		Assert.Contains("Next episode", labels);       // P31
		Assert.Contains("Previous episode", labels);   // P33
		Assert.Contains("Full screen", labels);        // P37
		Assert.Contains("Play on another device…", labels); // P39
		Assert.Contains(bar.Descendants(), e => (string?)e.Attribute("Text") == "{x:Bind SkipText, Mode=OneWay}"); // P29
		var code = Code("FluentFin/Controls/TransportControls.xaml.cs");
		Assert.Contains("PlayPauseButton.Label = \"Pause\"", code); // P32
		Assert.Contains("if (await App.Dialogs.PlayOnAnotherDeviceAsync(dto))", code); // P39: stops only after a device is picked
		Assert.Contains("\"Exit full screen\" : \"Full screen\"", Code("FluentFin/Controls/MediaPlayerHost.xaml.cs"));
		Assert.Contains("Text = \"Off\"", Code("FluentFin/Converters/Converters.cs")); // P40
	}

	[Fact]
	public void ShowMore_IsALinkThatSaysWhetherTheTextIsExpanded()
	{
		var toggle = Assert.Single(Load("FluentFin/Controls/ExpandableText.xaml").Descendants(), e => (string?)e.Attribute(Xaml + "Name") == "MoreButton"); // I29
		Assert.Equal("ExpandToggleButton", toggle.Name.LocalName);
		Assert.Equal("{StaticResource NixieLinkButtonStyle}", (string?)toggle.Attribute("Style"));
		Assert.Contains("PatternInterface.ExpandCollapse", Code("FluentFin/Controls/ExpandToggleButton.cs"));
	}

	[Fact]
	public void AConfirmation_HasTheDestructiveDialogStyle()
	{
		// L47, L48: the confirm is Destructive and Cancel, the default, Secondary
		var code = Code("FluentFin/Dialogs/ConfirmDialog.cs");
		Assert.Contains("ButtonRoles.Find(ButtonRoles.DestructiveDialog)", code);
		Assert.Contains("DefaultButton = ContentDialogButton.Close", code);
	}

	[Fact]
	public void TheSmallerVerdicts()
	{
		// L33: the tile menu added a top-bar tab; Nixie 1.4.0: every library is a tab, and the dead menu is gone
		Assert.DoesNotContain(Load("FluentFin/Views/LibrariesLandingPage.xaml").Descendants(), e => (string?)e.Attribute("Text") == "Add to the top bar");
		// A98: the user editor's number boxes have 32 x 32 spin buttons, as Settings' (A42)
		var boxes = Load("FluentFin/Views/JellyfinSettings/UserProfileEditorPage.xaml").Descendants().Where(e => e.Name.LocalName == "NumberBox").ToList();
		Assert.Equal(3, boxes.Count);
		Assert.All(boxes, b => Assert.Equal("32", (string?)b.Attributes().Single(a => a.Name.LocalName == "NumberBoxTargets.MinTargetSize").Value));
		// D92: Connect has the link glyph, and the address can be changed once connected
		var jellyseerr = Code("FluentFin.Plugins/Jellyseer/Views/JellyseerDashboardPage.xaml");
		Assert.Contains("&#xE71B;", jellyseerr);
		Assert.Contains("ChangeAddressCommand", jellyseerr);
		// D93: the plugin's tab says what it holds
		Assert.Contains("Name = \"Playback reports\"", Code("FluentFin.Plugins/Playback Reporting/PlaybackReportingPlugin.cs"));
	}

	[Fact]
	public void TheLibraryStreamsVerdicts()
	{
		var library = Load("FluentFin/Views/LibraryPage.xaml");
		Assert.Contains(library.Descendants(), e => (string?)e.Attribute("Text") == "{x:Bind local:LibraryPage.SortLabel(ViewModel.SortName), Mode=OneWay}"); // L15
		Assert.Contains(library.Descendants(), e => (string?)e.Attribute("Text") == "Descending"); // L24
		Assert.Equal("{StaticResource NixieCompactButtonStyle}", (string?)library.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == "ClearFiltersButton").Attribute("Style")); // L26
		Assert.DoesNotContain(library.Descendants(), e => (string?)e.Attribute("Style") == "{StaticResource LetterButtonStyle}" && e.Attribute("ToolTipService.ToolTip") is not null); // L30
		var menu = Code("FluentFin/Converters/JellyfinFlyoutConverter.cs");
		Assert.Contains("\"\uEB51\"", menu); // L36: add to favourites (the file writes glyphs as the characters themselves)
		Assert.Contains("\"\uE777\"", menu); // L38
		Assert.Contains("\"\uE946\"", menu); // L39
		Assert.Contains("NixieDangerForegroundBrush", menu); // L46
		Assert.Contains("\"Edit segments\")", Code("FluentFin.Core/ItemMenu.cs")); // L42: no ellipsis, it opens a page
		var home = Load("FluentFin/Views/HomePage.xaml");
		Assert.Equal("{StaticResource NixieCompactButtonStyle}", (string?)home.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == "UndoButton").Attribute("Style")); // L4
		Assert.Equal("{StaticResource NixieCompactIconButtonStyle}", (string?)home.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == "NoticeCloseButton").Attribute("Style")); // L5
		var arrow = Load("FluentFin/Controls/ShelfRow.xaml").Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == "ScrollLeftBtn"); // L7
		Assert.Equal(("44", "44"), ((string?)arrow.Attribute("Width"), (string?)arrow.Attribute("Height")));
	}

	[Fact]
	public void TheStatesStreamsVerdicts()
	{
		var login = Load("FluentFin/Views/LoginPage.xaml");
		Assert.Equal("{StaticResource NixieLinkButtonStyle}", (string?)login.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == "ChangeServerLink").Attribute("Style")); // S1
		Assert.Matches(@"\[RelayCommand\(CanExecute = nameof\(IsIdle\)\)\]\s+private void SwitchServer\(\)", Code("FluentFin/ViewModels/LoginViewModel.cs")); // S1
		Assert.Contains(login.Descendants(), e => (string?)e.Attribute("AutomationProperties.HelpText") == "Type a username to sign in"); // S2
		var server = Load("FluentFin/Views/SelectServerPage.xaml");
		Assert.Contains(server.Descendants(), e => (string?)e.Attribute("Text") == "Connect"); // S13
		Assert.Equal(2, server.Descendants().Count(e => e.Name.LocalName == "MenuFlyoutItem" && (string?)e.Attribute("Foreground") == "{ThemeResource NixieDangerForegroundBrush}")); // S9, S11
		Assert.Contains("NixieCompactButtonStyle", Code("FluentFin/Controls/LoadStateView.xaml")); // S15
		var picker = Code("FluentFin/Controls/ServerFolderPicker.xaml");
		Assert.Contains("NixieCompactIconButtonStyle", picker); // S17
		Assert.Contains("NixiePrimaryCompactButtonStyle", picker); // S20
		Assert.Contains("Undo changes", Code("FluentFin/Views/JellyfinSettings/ServerConfigurationPage.xaml")); // S23
	}

	private static XDocument Load(string file) => XDocument.Load(Path.Combine(RepoRoot, file));

	private static string Code(string file) => File.ReadAllText(Path.Combine(RepoRoot, file));

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
