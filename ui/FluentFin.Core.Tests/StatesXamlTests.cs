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
		// (the 1.2.0 merge: the glass stream's lit wordmark, AppWordmark, which gives its heading level to its text)
		Assert.Equal("Level1", (string?)Assert.Single(doc.Descendants(), t => t.Name.LocalName == "AppWordmark").Attribute("AutomationProperties.HeadingLevel"));
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
	public void ASavedServersMoreOptions_IsNamed_AndReachableWithTheKeyboard()
	{
		// review of wip/114-states: it was IsTabStop="False"; the row's context menu (Shift+F10) has Remove too
		var row = Assert.Single(Load("FluentFin/Views/SelectServerPage.xaml").Descendants(Presentation + "ItemContainer"));
		var more = Assert.Single(row.Descendants(Presentation + "Button"));
		Assert.Equal("{x:Bind local:SelectServerPage.MoreOptionsName(DisplayName)}", (string?)more.Attribute("AutomationProperties.Name"));
		Assert.NotEqual("False", (string?)more.Attribute("IsTabStop"));
		Assert.NotNull(row.Element(Presentation + "ItemContainer.ContextFlyout"));
		Assert.Contains("SavedServersView.ScrollView", Code("FluentFin/Views/SelectServerPage.xaml.cs"));
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
		Assert.Equal(new string?[] { "Save", "Undo changes" }, buttons); // 1.2.0 (the button review, S23)
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

	/// <summary>
	/// High contrast uses the user's colours: every colour a HighContrast dictionary under Styles gives (a Color, a brush of
	/// any kind with its colours, gradient stops, an acrylic's tint and fallback, a StaticResource alias) is transparent, a
	/// system colour, or a key that has a HighContrast value of its own (checked in turn).
	/// </summary>
	[Fact]
	public void HighContrastDictionaries_UseOnlySystemColours()
	{
		var files = Directory.GetFiles(Path.Combine(RepoRoot, "FluentFin", "Styles"), "*.xaml").Select(f => (Path.GetFileName(f), XDocument.Load(f))).ToList();

		var problems = HighContrastProblems(files);

		Assert.True(problems.Count == 0, "Not the user's colours in high contrast:\n" + string.Join("\n", problems));
	}

	/// <summary>The check above finds each kind of fixed colour (review of wip/114-states: a fixed Color and an acrylic passed).</summary>
	[Theory]
	[InlineData("""<Color x:Key="AccentColor">#FFFFFF00</Color>""", "AccentColor")]
	[InlineData("""<Color x:Key="AccentColor">Yellow</Color>""", "AccentColor")]
	[InlineData("""<SolidColorBrush x:Key="B" Color="#FF000000" />""", "B")]
	[InlineData("""<SolidColorBrush x:Key="B" Color="Black" />""", "B")]
	[InlineData("""<SolidColorBrush x:Key="B">#FF000000</SolidColorBrush>""", "B")]
	[InlineData("""<AcrylicBrush x:Key="B" TintColor="#FF202020" FallbackColor="{ThemeResource SystemColorWindowColor}" />""", "B")]
	[InlineData("""<AcrylicBrush x:Key="B" TintColor="{ThemeResource SystemColorWindowColor}" FallbackColor="#FF202020" />""", "B")]
	[InlineData("""<LinearGradientBrush x:Key="B"><GradientStop Offset="0" Color="#FF5CCFC1" /></LinearGradientBrush>""", "B")]
	[InlineData("""<SolidColorBrush x:Key="B" Color="{ThemeResource OnlyDarkColor}" />""", "OnlyDarkColor")]
	[InlineData("""<StaticResource x:Key="B" ResourceKey="OnlyDarkBrush" />""", "OnlyDarkBrush")]
	[InlineData("""<Color x:Key="KeyedColor">#FFFFFF00</Color><SolidColorBrush x:Key="B" Color="{ThemeResource KeyedColor}" />""", "KeyedColor")]
	public void TheHighContrastCheck_FindsAFixedColour(string entries, string reported)
	{
		var problems = HighContrastProblems([("Sample.xaml", Sample(entries))]);

		Assert.Contains(problems, p => p.Contains(reported, StringComparison.Ordinal));
	}

	[Theory]
	[InlineData("""<SolidColorBrush x:Key="B" Color="{ThemeResource SystemColorWindowColor}" />""")]
	[InlineData("""<SolidColorBrush x:Key="B" Color="Transparent" />""")]
	[InlineData("""<StaticResource x:Key="B" ResourceKey="SystemControlForegroundBaseHighBrush" />""")]
	[InlineData("""<StaticResource x:Key="B" ResourceKey="SubtleFillColorTransparentBrush" />""")]
	[InlineData("""<StaticResource x:Key="AccentColor" ResourceKey="SystemColorHighlightColor" /><SolidColorBrush x:Key="B" Color="{ThemeResource AccentColor}" />""")]
	[InlineData("""<AcrylicBrush x:Key="B" TintColor="{ThemeResource SystemColorWindowColor}" FallbackColor="{ThemeResource SystemColorWindowColor}" TintOpacity="0.5" />""")]
	[InlineData("""<x:Double x:Key="ShadowOpacity">0</x:Double>""")]
	public void TheHighContrastCheck_AcceptsTheUsersColours(string entries) =>
		Assert.Empty(HighContrastProblems([("Sample.xaml", Sample(entries))]));

	private static XDocument Sample(string entries) => XDocument.Parse($"""
		<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
		    <ResourceDictionary.ThemeDictionaries>
		        <ResourceDictionary x:Key="Dark"><Color x:Key="OnlyDarkColor">#FF101010</Color><SolidColorBrush x:Key="OnlyDarkBrush" Color="#FF101010" /></ResourceDictionary>
		        <ResourceDictionary x:Key="HighContrast">{entries}</ResourceDictionary>
		    </ResourceDictionary.ThemeDictionaries>
		</ResourceDictionary>
		""");

	/// <summary>Attributes that hold a colour or a brush (others, such as Opacity, are numbers).</summary>
	private static bool IsColourAttribute(string name) =>
		name.Contains("Color", StringComparison.Ordinal) || name.EndsWith("Brush", StringComparison.Ordinal)
		|| name is "Foreground" or "Background" or "Fill" or "Stroke" or "ResourceKey";

	/// <summary>A key whose value is the user's colour in high contrast without a HighContrast entry of ours.</summary>
	private static bool IsSystemKey(string key) =>
		key.StartsWith("SystemColor", StringComparison.Ordinal)        // the contrast theme's own colours
		|| key.StartsWith("SystemControl", StringComparison.Ordinal)   // WinUI's brushes made of them in high contrast
		|| key.EndsWith("TransparentBrush", StringComparison.Ordinal); // WinUI's transparent fills

	/// <summary>
	/// Every colour the HighContrast dictionaries of <paramref name="files"/> give that is not the user's: "file: key: value".
	/// A reference (ThemeResource, StaticResource, an alias's ResourceKey) must name a system key or a key with a HighContrast
	/// value in one of the files (whose own colours are checked as entries).
	/// </summary>
	private static List<string> HighContrastProblems(IEnumerable<(string Name, XDocument Doc)> files)
	{
		var contrast = files.SelectMany(f => Themes(f.Doc).GetValueOrDefault("HighContrast", []).Select(e => (File: f.Name, e.Key, Entry: e.Value))).ToList();
		var contrastKeys = contrast.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
		var problems = new List<string>();

		foreach (var (file, key, entry) in contrast)
		{
			foreach (var element in entry.DescendantsAndSelf())
			{
				var values = element.Attributes().Where(a => a.Name.Namespace == XNamespace.None && IsColourAttribute(a.Name.LocalName)).Select(a => a.Value).ToList();
				if (element.Name.LocalName is "Color" || element.Name.LocalName.EndsWith("Brush", StringComparison.Ordinal))
				{
					values.AddRange(element.Nodes().OfType<XText>().Select(t => t.Value.Trim()).Where(t => t.Length > 0));
				}

				foreach (var value in values)
				{
					var reference = System.Text.RegularExpressions.Regex.Match(value, @"^\{(?:ThemeResource|StaticResource)\s+(\w+)\}$");
					var named = reference.Success ? reference.Groups[1].Value : element.Name.LocalName == "StaticResource" ? value : null;
					var fine = named is not null
						? IsSystemKey(named) || contrastKeys.Contains(named)
						: value == "Transparent";
					if (!fine)
					{
						problems.Add(named is not null
							? $"{file}: {key}: {value} ({named} has no HighContrast value and is not a system colour)"
							: $"{file}: {key}: {value}");
					}
				}
			}
		}

		return problems;
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
