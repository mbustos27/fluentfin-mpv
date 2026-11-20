using System.Xml.Linq;
using FluentFin.Controls;
using FluentFin.Services;
using FluentFin.Themes;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.3.1: the theatre window's frame. A theme may draw it (TrailerFrameTemplate, an optional key of the contract); a
/// theme without one (Classic) gets the shared glass frame. Every frame has the Close button, the video's place and the title.
/// </summary>
public sealed class TrailerFrameTests
{
	private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

	private static string App(params string[] parts) => Path.Combine([FlyleafEngineTests.RepoRoot, "FluentFin", .. parts]);

	private static XDocument? SourceLoader(string appPath)
	{
		var file = App(appPath.Split('/'));
		return File.Exists(file) ? XDocument.Load(file) : null;
	}

	[Fact]
	public void TheKey_IsAnOptionalControlTemplate_OfTheContract()
	{
		Assert.Contains(("TrailerFrameTemplate", "ControlTemplate"), ThemeContract.OptionalKeys);
		Assert.DoesNotContain(ThemeContract.RequiredKeys, k => k.Key == "TrailerFrameTemplate");
	}

	[Fact]
	public void NoKey_OrAKeyOfAnotherKind_FallsBackToTheSharedFrame()
	{
		Assert.Null(TrailerFrame.ThemeTemplate(null));
		Assert.Null(TrailerFrame.ThemeTemplate("not a template"));
		Assert.Null(TrailerFrame.ThemeTemplate(42));
	}

	[Fact]
	public void AKeyOfTheWrongKind_IsReportedByTheContract()
	{
		const string dictionary = """
			<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:lc="using:FluentFin.Controls">
			<Style x:Key="TopBarStyle" TargetType="lc:TopBarChrome" /><DataTemplate x:Key="RowHeaderTemplate" /><DataTemplate x:Key="PosterTemplate" />
			<DataTemplate x:Key="StillTemplate" /><Style x:Key="PageHeaderStyle" TargetType="TextBlock" /><DataTemplate x:Key="TrailerFrameTemplate" />
			</ResourceDictionary>
			""";

		var problems = ThemeContract.KeyProblems(new FakeTheme(Themes.LayoutThemeId.DriveIn), _ => XDocument.Parse(dictionary));

		Assert.Contains("DriveIn: TrailerFrameTemplate is a DataTemplate, not a ControlTemplate", problems);
	}

	[Fact]
	public void Classic_HasNoFrameOfItsOwn_AndTheOtherThemesDrawTheirs()
	{
		var themes = LayoutThemes.Discover(typeof(FluentFin.App).Assembly).ToDictionary(t => t.Id);

		Assert.DoesNotContain(TrailerFrame.TemplateKey, ThemeContract.DefinedKeys(themes[LayoutThemeId.Classic].Dictionary, SourceLoader).Keys);
		foreach (var id in new[] { LayoutThemeId.DriveIn, LayoutThemeId.Cinematic, LayoutThemeId.Editorial })
		{
			var keys = ThemeContract.DefinedKeys(themes[id].Dictionary, SourceLoader);
			Assert.True(keys.TryGetValue(TrailerFrame.TemplateKey, out var template), $"{id} has no {TrailerFrame.TemplateKey}");
			Assert.Equal("ControlTemplate", template!.Name.LocalName);
			Assert.EndsWith("TrailerFrame", (string?)template.Attribute("TargetType"));
			FrameHasItsParts(template, id.ToString());
		}
	}

	[Fact]
	public void TheSharedFrame_HasItsParts()
	{
		var template = XDocument.Load(App("Styles", "TrailerFrame.xaml")).Descendants().Single(e => e.Name.LocalName == "ControlTemplate");

		FrameHasItsParts(template, "shared");
		Assert.Contains("/Styles/TrailerFrame.xaml", File.ReadAllText(App("Themes", "Generic.xaml")));
	}

	/// <summary>Close (named for Narrator), the video in a place sized by VideoWidth and VideoHeight, and the title.</summary>
	private static void FrameHasItsParts(XElement template, string which)
	{
		var close = template.Descendants().SingleOrDefault(e => (string?)e.Attribute(X + "Name") == TrailerFrame.CloseButtonPart);
		Assert.True(close is not null, $"{which}: no {TrailerFrame.CloseButtonPart}");
		Assert.Equal("Button", close!.Name.LocalName);
		Assert.Equal(TrailerFrame.CloseName, (string?)close.Attribute("AutomationProperties.Name"));

		var text = template.ToString();
		Assert.Contains("{TemplateBinding Content}", text);
		Assert.Contains("{TemplateBinding VideoWidth}", text);
		Assert.Contains("{TemplateBinding VideoHeight}", text);
		Assert.True(text.Contains("{TemplateBinding Title}") || text.Contains("{TemplateBinding MarqueeTitle}"), $"{which}: no title");
	}

	[Fact]
	public void EveryTrailerButton_PlaysInNixie_NotInTheBrowser()
	{
		foreach (var file in new[]
		{
			App("Controls", "ItemHero.xaml.cs"),
			App("Themes", "DriveIn", "Controls", "ItemBoard.xaml.cs"),
			App("Themes", "Cinematic", "Controls", "CinematicItemStage.xaml.cs"),
			App("Themes", "Editorial", "Controls", "EditorialItemHeader.xaml.cs"),
			App("Themes", "Editorial", "Controls", "InlineDetailsRow.xaml.cs"),
		})
		{
			var code = File.ReadAllText(file);
			Assert.Contains("TrailerPlayback.Play(", code);
			Assert.Contains("TrailerPlayback.ButtonVisibility(dto)", code);
			Assert.DoesNotContain("TrailerUrl(", code);
			Assert.DoesNotContain("opens in your browser", File.ReadAllText(file[..^3]));
		}
	}

	[Fact]
	public void TheTheatre_IsInTheMainWindow_OverThePagesAndTheTopBar()
	{
		var window = XDocument.Load(App("MainWindow.xaml"));
		var children = window.Root!.Elements().Single(e => e.Name.LocalName == "Grid").Elements().Select(e => e.Name.LocalName).ToList();

		Assert.True(children.IndexOf("TrailerTheater") > children.IndexOf("TitleBarControl"));
		Assert.True(children.IndexOf("TrailerTheater") > children.IndexOf("Frame"));
	}

	[Fact]
	public void TheWebView_IsLockedDown()
	{
		var code = File.ReadAllText(App("Controls", "TrailerTheater.cs"));

		Assert.Contains("settings.AreDevToolsEnabled = !AppVersion.IsReleasePackage;", code);
		Assert.Contains("settings.AreHostObjectsAllowed = false;", code);
		Assert.Contains("settings.IsWebMessageEnabled = false;", code);
		// Nixie 1.4.0: the page's navigations and its frames' both go through the allow-list, with who started them
		Assert.Contains("core.NavigationStarting += (_, e) => OnNavigationStarting(e, frame: false);", code);
		Assert.Contains("core.FrameNavigationStarting += (_, e) => OnNavigationStarting(e, frame: true);", code);
		Assert.Contains("core.NewWindowRequested += OnNewWindowRequested;", code);
		Assert.Contains("Trailers.Navigation(target, _embedBase, frame, e.IsUserInitiated)", code);
		Assert.Contains("Trailers.OpensInBrowser(target, e.IsUserInitiated)", code);
		// its own user-data folder under Nixie's data folder, and closed with the window (the sound stops at once)
		Assert.Contains("KnownFolders>().ApplicationData, \"WebView2\")", code);
		Assert.Contains("web.Close();", code);
		Assert.DoesNotContain("AddHostObjectToScript", code);
		Assert.DoesNotContain("WebMessageReceived", code);
	}

	/// <summary>
	/// Nixie 1.4.0 (bug hunt m8): the top bar is the window's title bar, and its empty parts were the window's caption under
	/// the theatre's dim: a press there dragged the window, a double press maximized it. While the theatre is open the caption
	/// is set aside (the window is click-through but its own buttons: a press on the band closes the theatre), again after a
	/// resize, and the bar's own click-through areas are put back on close.
	/// (The harness asks Windows itself: then-trailer.ps1, WM_NCHITTEST at 1920 and 1100 wide.)
	/// </summary>
	[Fact]
	public void WhileOpen_TheTopBarIsNotTheWindowsCaption_AndIsAgainAfter()
	{
		var code = File.ReadAllText(App("Controls", "TrailerTheater.cs")).Replace("\r\n", "\n");
		var open = code[code.IndexOf("public bool Open(", StringComparison.Ordinal)..code.IndexOf("public void Close()", StringComparison.Ordinal)];
		Assert.Contains("CaptionOff();", open);
		var close = code[code.IndexOf("public void Close()", StringComparison.Ordinal)..];
		Assert.True(close.IndexOf("CaptionBack();", StringComparison.Ordinal) < close.IndexOf("Visibility = Visibility.Collapsed;", StringComparison.Ordinal));
		// the whole window but Windows' own buttons is click-through while open; the bar's own areas come back on close
		Assert.Contains("source.SetRegionRects(Passthrough, [WholeWindowButCaptionButtons()]);", code);
		Assert.Contains("size.Width - window.TitleBar.RightInset", code);
		Assert.Contains(".SetRegionRects(Passthrough, saved);", code);
		Assert.Contains("KeepCaptionOff();", code[code.IndexOf("SizeChanged += (_, _) =>", StringComparison.Ordinal)..]);
	}
}
