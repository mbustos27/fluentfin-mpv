using System.Xml;
using System.Xml.Linq;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.1.4 (states): static checks of the sign-in, server, load-state, folder-picker and server-configuration views
/// (Narrator names, headings, live regions, no glass on glass, the neutral Try again) and of the theme dictionaries (every key
/// a theme defines has a high-contrast value, made of system colours). Reads the XAML as XML, like
/// <see cref="AccessibilityXamlTests"/>; the running views are scanned by tools/ui-harness/offscreen/before-states.ps1 and
/// then-states.ps1 (shots\a11y.md).
/// </summary>
public class StatesXamlTests
{
	private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
	private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

	[Fact]
	public void LoadStateView_TitleIsAHeadingAndALiveRegion_TheRingSaysWhatLoads_AndTryAgainIsNeutral()
	{
		var doc = Load("FluentFin/Controls/LoadStateView.xaml");
		var title = ByName(doc, "TitleText");
		Assert.Equal("Level2", (string?)title.Attribute("AutomationProperties.HeadingLevel"));
		Assert.Equal("Assertive", (string?)title.Attribute("AutomationProperties.LiveSetting"));
		Assert.Equal("Polite", (string?)ByName(doc, "LoadingTextBlock").Attribute("AutomationProperties.LiveSetting"));
		Assert.Equal("{x:Bind LoadingText, Mode=OneWay}", (string?)doc.Descendants(Presentation + "ProgressRing").Single().Attribute("AutomationProperties.Name"));
		// the accent is for Play: Try again is a neutral glass pill (ux review: "accent on the Retry button")
		Assert.Equal("{StaticResource GlassPillButtonStyle}", (string?)ByName(doc, "RetryButton").Attribute("Style"));
		// the words in the glass text colours (measured by OverlayContrastTests), not WinUI's default secondary
		Assert.DoesNotContain(doc.Descendants(), e => ((string?)e.Attribute("Foreground"))?.Contains("TextFillColorSecondaryBrush") == true);
		// its own glass panel, made flat in code when it is inside one already (one layer of glass)
		Assert.Equal("{StaticResource GlassSurfaceStyle}", (string?)ByName(doc, "MessageSurface").Attribute("Style"));
		var code = Code("FluentFin/Controls/LoadStateView.xaml.cs");
		Assert.Contains("IsInsideGlassSurface(this)", code);
		Assert.Contains("RaiseNotificationEvent", code);
	}

	[Fact]
	public void SelectServerPage_HasHeadings_ANamedList_AndALiveStatus_AndNoGlassOnGlass()
	{
		var doc = Load("FluentFin/Views/SelectServerPage.xaml");
		// the app's name (whatever it is called: the rename sweeps the text) is the page's one first-level heading
		Assert.Equal("Level1", (string?)Assert.Single(doc.Descendants(Presentation + "TextBlock"), t => (string?)t.Attribute("Style") == "{StaticResource GlassPageTitleTextBlockStyle}").Attribute("AutomationProperties.HeadingLevel"));
		Assert.Equal("Level2", (string?)Text(doc, "Saved servers").Attribute("AutomationProperties.HeadingLevel"));
		Assert.Equal("Saved servers", (string?)doc.Descendants(Presentation + "ItemsView").Single().Attribute("AutomationProperties.Name"));
		var status = doc.Descendants(Presentation + "TextBlock").Single(t => (string?)t.Attribute("Text") == "{x:Bind ViewModel.StatusText, Mode=OneWay}");
		Assert.Equal("Polite", (string?)status.Attribute("AutomationProperties.LiveSetting"));
		Assert.Equal("Server address", (string?)ByName(doc, "TxtUrl").Attribute("Header"));
		// the card is the one glass layer: a saved server's tile is the flat inset fill, not a second acrylic
		Assert.Equal("{ThemeResource GlassInsetFillBrush}", (string?)doc.Descendants(Presentation + "ItemContainer").Single().Attribute("Background"));
		Assert.Equal("{StaticResource GlassSurfaceStyle}", (string?)doc.Descendants(Presentation + "ContentControl").First().Attribute("Style"));
	}

	[Fact]
	public void ServerFolderPicker_NamesEachFolder_HasAHeading_AndSaysWhyAFolderIsEmpty()
	{
		var doc = Load("FluentFin/Controls/ServerFolderPicker.xaml");
		Assert.Equal("{x:Bind Name}", (string?)doc.Descendants(Presentation + "ItemContainer").Single().Attribute("AutomationProperties.Name"));
		Assert.Equal("Folders", (string?)ByName(doc, "FolderList").Attribute("AutomationProperties.Name"));
		Assert.Equal("Level1", (string?)Text(doc, "Choose a folder on the server").Attribute("AutomationProperties.HeadingLevel"));
		Assert.Equal("Polite", (string?)ByName(doc, "MessageTitle").Attribute("AutomationProperties.LiveSetting"));
		Assert.Equal("Try again", (string?)ByName(doc, "RetryButton").Attribute("Content"));
		Assert.Equal("Parent folder", (string?)ByName(doc, "UpButton").Attribute("AutomationProperties.Name"));
		// Cancel and the window's close pick nothing: only Select this folder sets the result
		var input = Code("FluentFin/Dialogs/UserInput/ServerFolderInput.cs");
		Assert.Contains("control.SelectedFolder ?? \"\"", input);
		Assert.DoesNotContain("return control.CurrentFolder", input);
	}

	[Fact]
	public void ServerConfigurationPage_NamesSaveAndReset_HasAHeading_AndShrinksWithTheWindow()
	{
		var doc = Load("FluentFin/Views/JellyfinSettings/ServerConfigurationPage.xaml");
		var buttons = doc.Descendants(Presentation + "Button").Select(b => (string?)b.Attribute("AutomationProperties.Name")).ToList();
		Assert.Equal(new string?[] { "Save", "Reset" }, buttons);
		Assert.Equal("Level1", (string?)doc.Descendants(Presentation + "TextBlock").First().Attribute("AutomationProperties.HeadingLevel"));
		Assert.DoesNotContain(doc.Descendants(), e => (string?)e.Attribute("Width") == "1000");
	}

	public static TheoryData<string> ThemedFiles() =>
	[
		.. Directory.GetFiles(Path.Combine(RepoRoot, "FluentFin", "Styles"), "*.xaml").Select(f => "FluentFin/Styles/" + Path.GetFileName(f))
			.Where(f => Load(f).Descendants(Presentation + "ResourceDictionary.ThemeDictionaries").Any()),
	];

	/// <summary>
	/// Every key a theme dictionary under Styles defines (Dark, Light or Default) has a HighContrast value too: a key missing
	/// there falls back to the dark or light value in high contrast (glass, hard-coded colours).
	/// </summary>
	[Theory]
	[MemberData(nameof(ThemedFiles))]
	public void EveryThemedKey_HasAHighContrastValue(string file)
	{
		var themes = Themes(Load(file));
		Assert.True(themes.ContainsKey("HighContrast"), $"{file} has theme dictionaries but no HighContrast one");
		var contrast = themes["HighContrast"];
		var missing = themes.Where(t => t.Key != "HighContrast").SelectMany(t => t.Value.Keys.Select(k => (Theme: t.Key, Key: k)))
			.Where(x => !contrast.ContainsKey(x.Key)).Select(x => $"{x.Theme}:{x.Key}").Distinct().ToList();

		Assert.True(missing.Count == 0, $"{file}: no HighContrast value for {string.Join(", ", missing)}");
	}

	/// <summary>High contrast uses the user's colours: a brush there is a system colour (or transparent), never a fixed one.</summary>
	[Theory]
	[MemberData(nameof(ThemedFiles))]
	public void HighContrastBrushes_UseSystemColours(string file)
	{
		var fixedColours = Themes(Load(file)).GetValueOrDefault("HighContrast", [])
			.Where(e => e.Value.Name.LocalName is "SolidColorBrush" or "Color")
			.Where(e => !IsSystemColour(e.Value))
			.Select(e => $"{e.Key} = {(string?)e.Value.Attribute("Color") ?? e.Value.Value}")
			.ToList();

		Assert.True(fixedColours.Count == 0, $"{file}: fixed colours in high contrast: {string.Join("; ", fixedColours)}");
	}

	private static bool IsSystemColour(XElement e)
	{
		var colour = (string?)e.Attribute("Color") ?? e.Value.Trim();
		return colour == "Transparent" || colour.StartsWith("{ThemeResource SystemColor", StringComparison.Ordinal) || colour.StartsWith("{StaticResource SystemColor", StringComparison.Ordinal);
	}

	private static Dictionary<string, Dictionary<string, XElement>> Themes(XDocument doc) =>
		doc.Root!.Elements(Presentation + "ResourceDictionary.ThemeDictionaries").Elements(Presentation + "ResourceDictionary")
			.ToDictionary(d => (string)d.Attribute(Xaml + "Key")!,
				d => d.Elements().Where(e => e.Attribute(Xaml + "Key") is not null).GroupBy(e => (string)e.Attribute(Xaml + "Key")!).ToDictionary(g => g.Key, g => g.First()));

	private static XElement ByName(XDocument doc, string name) =>
		doc.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == name);

	private static XElement Text(XDocument doc, string text) =>
		doc.Descendants(Presentation + "TextBlock").Single(t => (string?)t.Attribute("Text") == text);

	private static string Code(string file) => File.ReadAllText(Path.Combine(RepoRoot, file));

	private static XDocument Load(string file) => XDocument.Load(Path.Combine(RepoRoot, file), LoadOptions.SetLineInfo);

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
