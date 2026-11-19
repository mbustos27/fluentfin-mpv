using System.Globalization;
using System.Xml.Linq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.4.0 (the UX review's plan items 1, 10 and 13): every layout theme gives the shared pages its ground and its page
/// grid (a gutter at 1100 and 1920 px and a title y, on the 4 epx grid), and the shared pages take them instead of a margin
/// of their own.
/// </summary>
public sealed class PageGridXamlTests
{
	private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

	public static TheoryData<string> Themes => new() { "Classic/Classic.xaml", "DriveIn/DriveIn.xaml", "Cinematic/Cinematic.xaml", "Editorial/Editorial.xaml" };

	[Theory]
	[MemberData(nameof(Themes))]
	public void EveryTheme_GivesThePageGrid_OnTheFourPixelGrid(string theme)
	{
		var doc = XDocument.Load(Path.Combine(RepoRoot, "FluentFin/Themes", theme));
		foreach (var key in new[] { "PageGutterNarrow", "PageGutterWide", "PageTitleY" })
		{
			var element = doc.Root!.Elements().SingleOrDefault(e => (string?)e.Attribute(Xaml + "Key") == key);
			Assert.True(element is not null, $"{theme}: {key} is missing");
			Assert.Equal("Double", element!.Name.LocalName);
			var value = double.Parse(element.Value, CultureInfo.InvariantCulture);
			Assert.True(value % 4 == 0, $"{theme}: {key} = {value} is off the 4 epx grid");
		}
	}

	[Theory]
	[MemberData(nameof(Themes))]
	public void EveryTheme_GivesTheSharedPagesAGround_InEachThemeDictionary(string theme)
	{
		var doc = XDocument.Load(Path.Combine(RepoRoot, "FluentFin/Themes", theme));
		var themeDictionaries = doc.Root!.Elements().Where(e => e.Name.LocalName == "ResourceDictionary.ThemeDictionaries").Elements().ToList();
		if (themeDictionaries.Count == 0)
		{
			// Classic: the shared default (Styles/PageGrid.xaml, transparent: the window's glass ground)
			return;
		}

		foreach (var names in new[] { new[] { "Default", "Dark" }, ["Light"], ["HighContrast"] })
		{
			var name = names[0];
			var dictionary = themeDictionaries.SingleOrDefault(d => names.Contains((string?)d.Attribute(Xaml + "Key")));
			Assert.True(dictionary is not null, $"{theme}: no {name} theme dictionary");
			Assert.Contains(dictionary!.Elements(), e => (string?)e.Attribute(Xaml + "Key") == "PageGroundBrush");
		}
	}

	[Fact]
	public void TheDefaults_AreClassics_AndMergedBeforeTheThemeSlot()
	{
		var app = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/App.xaml"));
		Assert.True(app.IndexOf("/Styles/PageGrid.xaml", StringComparison.Ordinal) < app.IndexOf("<themes:LayoutThemeSlot", StringComparison.Ordinal));
		var doc = XDocument.Load(Path.Combine(RepoRoot, "FluentFin/Styles/PageGrid.xaml"));
		string Value(string key) => doc.Root!.Elements().Single(e => (string?)e.Attribute(Xaml + "Key") == key).Value;
		Assert.Equal("36", Value("PageGutterNarrow"));
		Assert.Equal("36", Value("PageGutterWide"));
		Assert.Equal("72", Value("PageTitleY"));
	}

	[Theory]
	[InlineData("FluentFin/Views/CollectionPage.xaml")]
	[InlineData("FluentFin/Views/LibrariesLandingPage.xaml")]
	[InlineData("FluentFin/Views/SearchResultsPage.xaml")]
	[InlineData("FluentFin/Views/SettingsPage.xaml")]
	public void TheSharedPages_TakeTheThemesGrid(string page)
	{
		var text = File.ReadAllText(Path.Combine(RepoRoot, page));
		Assert.Matches(":PageGrid.Apply=\"True\"", text);
		Assert.Contains("{StaticResource PageHeaderStyle}", text, StringComparison.Ordinal);
		Assert.Contains("Background=\"{ThemeResource PageGroundBrush}\"", text, StringComparison.Ordinal);
		Assert.DoesNotContain("GlassPageTitleTextBlockStyle", text, StringComparison.Ordinal);
	}

	[Fact]
	public void TheCollectionPage_IsOneSharedPage_SkinnedByTheThemesKeys()
	{
		var text = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Views/CollectionPage.xaml"));
		Assert.Contains("ContentTemplate=\"{StaticResource PosterTemplate}\"", text, StringComparison.Ordinal);
		Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "NixiePrimaryButtonStyle"));
		// no theme gives it a view of its own
		foreach (var theme in Directory.GetFiles(Path.Combine(RepoRoot, "FluentFin/Themes"), "*Theme.cs", SearchOption.AllDirectories))
		{
			Assert.DoesNotContain("CollectionViewModel", File.ReadAllText(theme), StringComparison.Ordinal);
		}
	}

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
