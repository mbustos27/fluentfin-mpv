using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.ViewModels;
using FluentFin.Services;
using FluentFin.Themes;
using FluentFin.Themes.Classic;
using FluentFin.UI.Core;
using FluentFin.Views;
using Microsoft.UI.Xaml.Controls;
using Moq;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.3.0, the theme foundation: finding the themes, the per-PC choice (upgrades on Classic, new installs on the
/// default), NIXIE_LAYOUT, the fallback chain, the theme-aware page lookup, and the contract every theme keeps (with fake
/// themes, and the app's own themes read from their source).
/// </summary>
public sealed class ThemeServiceTests
{
	// ---- the choice ----

	[Fact]
	public void NewInstalls_StartOnDriveIn()
	{
		// the user, 2026-11-17: new installs start on Drive-in (one constant)
		Assert.Equal(LayoutThemeId.DriveIn, ThemeChoice.DefaultForNewInstalls);

		var settings = new MemorySettings();
		var service = new ThemeService(settings, AllThemes(), layoutOverride: null);

		Assert.Equal(ThemeChoice.DefaultForNewInstalls, service.Chosen);
		Assert.Equal(ThemeChoice.DefaultForNewInstalls, service.ActiveId);
		Assert.False(service.IsFirstStartAfterUpgrade);
		Assert.Equal("DriveIn", settings.Saved(ThemeChoice.SettingKey));
		Assert.True(service.ShouldOfferChooseLook);
		Assert.Equal(LayoutThemeId.DriveIn, service.ChooseLookPreselect);
	}

	[Fact]
	public void AnInstallUpgradedFrom12_StartsOnClassic_AndIsOfferedChooseALookOnce_WithClassicPreselected()
	{
		var settings = new MemorySettings();
		settings.Seed(ThemeChoice.ServersKey, new JsonArray(new JsonObject { ["Name"] = "Home" }));

		var service = new ThemeService(settings, AllThemes(), layoutOverride: null);

		Assert.Equal(LayoutThemeId.Classic, service.Chosen);
		Assert.Equal(LayoutThemeId.Classic, service.ActiveId);
		Assert.True(service.IsFirstStartAfterUpgrade);
		Assert.Equal("Classic", settings.Saved(ThemeChoice.SettingKey));
		Assert.True(service.ShouldOfferChooseLook);
		Assert.Equal(LayoutThemeId.Classic, service.ChooseLookPreselect);

		service.ChooseLookOffered();
		Assert.False(new ThemeService(settings, AllThemes(), null).ShouldOfferChooseLook);
	}

	[Fact]
	public void TheFirstChoice_IsKept_ANewInstallSigningInLaterStaysOnItsDefault()
	{
		var settings = new MemorySettings();
		_ = new ThemeService(settings, AllThemes(), null);              // first start: no server yet
		settings.Seed(ThemeChoice.ServersKey, new JsonArray(new JsonObject { ["Name"] = "Home" })); // then a sign-in

		var next = new ThemeService(settings, AllThemes(), null);

		Assert.Equal(ThemeChoice.DefaultForNewInstalls, next.Chosen);
		Assert.False(next.IsFirstStartAfterUpgrade);
	}

	[Fact]
	public void AnEmptyServerList_IsNotAnUpgrade()
	{
		Assert.False(ThemeChoice.IsUpgrade(null));
		Assert.False(ThemeChoice.IsUpgrade(new JsonArray()));
		Assert.False(ThemeChoice.IsUpgrade(JsonValue.Create("x")));
		Assert.True(ThemeChoice.IsUpgrade(new JsonArray(new JsonObject())));
	}

	[Fact]
	public void TheSavedChoice_IsUsed_AndAnUnreadableOneIsDecidedAgain()
	{
		var settings = new MemorySettings();
		settings.Seed(ThemeChoice.SettingKey, JsonValue.Create("Editorial"));
		settings.Seed(ThemeChoice.ServersKey, new JsonArray(new JsonObject()));
		Assert.Equal(LayoutThemeId.Editorial, new ThemeService(settings, AllThemes(), null).ActiveId);

		var broken = new MemorySettings();
		broken.Seed(ThemeChoice.SettingKey, JsonValue.Create("Vaporwave"));
		Assert.Equal(ThemeChoice.DefaultForNewInstalls, new ThemeService(broken, AllThemes(), null).Chosen);
	}

	[Fact]
	public void NixieLayout_OverridesTheChoiceForTheRun_WithoutSavingIt()
	{
		var settings = new MemorySettings();
		settings.Seed(ThemeChoice.SettingKey, JsonValue.Create("Classic"));

		var service = new ThemeService(settings, AllThemes(), layoutOverride: "drive-in");

		Assert.Equal(LayoutThemeId.DriveIn, service.Override);
		Assert.Equal(LayoutThemeId.DriveIn, service.ActiveId);
		Assert.Equal(LayoutThemeId.Classic, service.Chosen);
		Assert.Equal("Classic", settings.Saved(ThemeChoice.SettingKey));

		// a name that is no theme is ignored
		Assert.Equal(LayoutThemeId.Classic, new ThemeService(settings, AllThemes(), "Vaporwave").ActiveId);
	}

	[Fact]
	public void NixieLayout_IsReadThroughTheAppsEnvironmentPrefix()
	{
		var env = new Dictionary<string, string> { ["NIXIE_LAYOUT"] = "Cinematic" };
		Assert.Equal(LayoutThemeId.Cinematic, ThemeChoice.Parse(Core.AppIdentity.ReadEnvironment("LAYOUT", n => env.GetValueOrDefault(n))));
		Assert.Contains("Core.AppIdentity.ReadEnvironment(\"LAYOUT\")", File.ReadAllText(Path.Combine(FlyleafEngineTests.RepoRoot, "FluentFin", "App.xaml.cs")));
	}

	[Theory]
	[InlineData("DriveIn", LayoutThemeId.DriveIn)]
	[InlineData("drive-in", LayoutThemeId.DriveIn)]
	[InlineData(" Drive in ", LayoutThemeId.DriveIn)]
	[InlineData("CINEMATIC", LayoutThemeId.Cinematic)]
	[InlineData("editorial", LayoutThemeId.Editorial)]
	[InlineData("Classic", LayoutThemeId.Classic)]
	public void Parse_TakesAThemesName(string text, LayoutThemeId expected) => Assert.Equal(expected, ThemeChoice.Parse(text));

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("0")]
	[InlineData("3")]
	[InlineData("Noir")]
	public void Parse_RefusesAnythingElse(string? text) => Assert.Null(ThemeChoice.Parse(text));

	[Fact]
	public void Choose_SavesAndSwitches_OnceOnly()
	{
		var settings = new MemorySettings();
		var service = new ThemeService(settings, AllThemes(), null);
		var switches = 0;
		service.ActiveChanged += (_, _) => switches++;

		Assert.True(service.Choose(LayoutThemeId.Editorial));
		Assert.False(service.Choose(LayoutThemeId.Editorial));

		Assert.Equal(1, switches);
		Assert.Equal(LayoutThemeId.Editorial, service.ActiveId);
		Assert.Equal("Editorial", settings.Saved(ThemeChoice.SettingKey));
	}

	// ---- the fallback ----

	[Fact]
	public void AMissingTheme_FallsBackToTheNextOne_EndingAtClassic()
	{
		var classic = new ClassicTheme();
		var cinematic = new FakeTheme(LayoutThemeId.Cinematic);
		var editorial = new FakeTheme(LayoutThemeId.Editorial);

		Assert.Same(cinematic, ThemeChoice.Resolve(LayoutThemeId.DriveIn, [cinematic, editorial, classic]));
		Assert.Same(editorial, ThemeChoice.Resolve(LayoutThemeId.DriveIn, [editorial, classic]));
		Assert.Same(classic, ThemeChoice.Resolve(LayoutThemeId.DriveIn, [classic]));
		Assert.Same(classic, ThemeChoice.Resolve(LayoutThemeId.Editorial, [cinematic, classic]));
		Assert.Null(ThemeChoice.Resolve(LayoutThemeId.DriveIn, []));
		Assert.Equal(LayoutThemeId.Classic, Enum.GetValues<LayoutThemeId>().Max());
	}

	[Fact]
	public void AMissingTheme_FallsBack_InTheService_AndThePagesAreTheShared()
	{
		// only Classic is in (the theme streams not merged yet): Drive-in's new install shows Classic, the shared pages
		var service = new ThemeService(new MemorySettings(), [new ClassicTheme()], null);
		Assert.Equal(LayoutThemeId.DriveIn, service.Chosen);
		Assert.Equal(LayoutThemeId.Classic, service.ActiveId);

		var pages = new PageService(Mock.Of<IPluginManager>(), service);
		Assert.Equal(typeof(HomePage), pages.GetPageType(typeof(HomeViewModel).FullName!));
	}

	// ---- the pages ----

	[Fact]
	public void PageService_AsksTheActiveThemeFirst_ThenTheSharedPage()
	{
		var settings = new MemorySettings();
		settings.Seed(ThemeChoice.SettingKey, JsonValue.Create("DriveIn"));
		var driveIn = new FakeTheme(LayoutThemeId.DriveIn, (typeof(HomeViewModel), typeof(FakeHomeView)), (typeof(SeasonViewModel), typeof(FakeSeasonView)));
		var service = new ThemeService(settings, [driveIn, new ClassicTheme()], null);
		var pages = new PageService(Mock.Of<IPluginManager>(), service);

		Assert.Equal(typeof(FakeHomeView), pages.GetPageType(typeof(HomeViewModel).FullName!));
		Assert.Equal(typeof(MoviePage), pages.GetPageType(typeof(MovieViewModel).FullName!)); // no view of its own: shared
		Assert.Equal(typeof(HomeViewModel), pages.GetViewModelType(typeof(FakeHomeView)));
		Assert.Equal(typeof(HomePage), pages.SharedPageType(typeof(FakeHomeView)));
		Assert.Equal(typeof(SeasonPage), pages.SharedPageType(typeof(FakeSeasonView)));
		Assert.Equal(typeof(MoviePage), pages.SharedPageType(typeof(MoviePage)));

		service.Choose(LayoutThemeId.Classic);
		Assert.Equal(typeof(HomePage), pages.GetPageType(typeof(HomeViewModel).FullName!));
		// a theme's page shown before the switch (in the history) still maps back to its view model
		Assert.Equal(typeof(HomeViewModel), pages.GetViewModelType(typeof(FakeHomeView)));
	}

	[Fact]
	public void OnlyTheBrowsingPages_AreThemed_WhateverATheme_Answers()
	{
		var greedy = new FakeTheme(LayoutThemeId.DriveIn, (typeof(SettingsViewModel), typeof(FakeHomeView)), (typeof(MovieViewModel), typeof(string)));

		Assert.Null(ThemeService.ViewFor(greedy, typeof(SettingsViewModel)));
		Assert.Null(ThemeService.ViewFor(greedy, typeof(MovieViewModel)));   // not a Page
		Assert.Null(ThemeService.ViewFor(new ThrowingViewsTheme(), typeof(HomeViewModel)));
		Assert.Equal(
			[typeof(HomeViewModel), typeof(SeriesViewModel), typeof(SeasonViewModel), typeof(MovieViewModel), typeof(EpisodeViewModel), typeof(LibraryViewModel)],
			ThemeContract.ThemableViewModels);
	}

	[Fact]
	public void TheHistoryRules_SeeAThemesPageAsTheSharedOne()
	{
		var code = File.ReadAllText(Path.Combine(FlyleafEngineTests.RepoRoot, "FluentFin", "Services", "NavigationService.cs"));
		Assert.Contains("PruneForwardStack(frame.ForwardStack, x => Shared(x.SourcePageType))", code);
		Assert.Contains("DropReplacedPlayerPage(frame.BackStack, x => Shared(x.SourcePageType), Shared(e.SourcePageType)", code);
		Assert.Contains("DropSwitchedSeasonPage(frame.BackStack, x => Shared(x.SourcePageType), x => x.Parameter, Shared(e.SourcePageType)", code);
	}

	// ---- finding the themes ----

	[Fact]
	public void Discover_FindsEveryThemeInTheApp_ClassicIncluded()
	{
		var themes = LayoutThemes.Discover(typeof(App).Assembly);
		var classes = typeof(App).Assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ILayoutTheme).IsAssignableFrom(t)).ToList();

		Assert.Contains(themes, t => t is ClassicTheme);
		Assert.Equal(classes.Count, themes.Count);
		Assert.Equal(themes.OrderBy(t => t.Id).Select(t => t.Id), themes.Select(t => t.Id));
	}

	[Fact]
	public void Discover_SkipsAbstractAndBrokenThemes_AndKeepsOnePerId()
	{
		var themes = LayoutThemes.Discover(typeof(ThemeServiceTests).Assembly);

		Assert.Contains(themes, t => t is DiscoverableDriveIn);
		Assert.DoesNotContain(themes, t => t is BrokenEditorialTheme);
		Assert.Single(themes, t => t.Id == LayoutThemeId.DriveIn);
		Assert.DoesNotContain(themes, t => t.Id == LayoutThemeId.Editorial);
	}

	// ---- the contract ----

	[Fact]
	public void Contract_EveryViewForType_IsAPage()
	{
		Assert.Empty(ThemeContract.ViewProblems(new FakeTheme(LayoutThemeId.DriveIn, (typeof(HomeViewModel), typeof(FakeHomeView)))));

		var notAPage = ThemeContract.ViewProblems(new FakeTheme(LayoutThemeId.DriveIn, (typeof(HomeViewModel), typeof(string))));
		Assert.Contains(notAPage, p => p.Contains("is not a Page", StringComparison.Ordinal));

		var noConstructor = ThemeContract.ViewProblems(new FakeTheme(LayoutThemeId.DriveIn, (typeof(HomeViewModel), typeof(FakeViewWithArgument))));
		Assert.Contains(noConstructor, p => p.Contains("parameterless", StringComparison.Ordinal));

		var twice = ThemeContract.ViewProblems(new FakeTheme(LayoutThemeId.DriveIn, (typeof(HomeViewModel), typeof(FakeHomeView)), (typeof(MovieViewModel), typeof(FakeHomeView))));
		Assert.Contains(twice, p => p.Contains("both", StringComparison.Ordinal));

		Assert.Contains(ThemeContract.ViewProblems(new ThrowingViewsTheme()), p => p.Contains("threw", StringComparison.Ordinal));

		var codeOnly = new FakeTheme(LayoutThemeId.DriveIn, (typeof(HomeViewModel), typeof(FakeCodeOnlyView)));
		Assert.Contains(ThemeContract.ViewProblems(codeOnly), p => p.Contains("not a XAML page", StringComparison.Ordinal));
		Assert.Null(ThemeService.ViewFor(codeOnly, typeof(HomeViewModel))); // never navigated to: it would end the process
	}

	[Fact]
	public void Contract_EveryRequiredKey_Resolves_FollowingMergedDictionaries()
	{
		var files = new Dictionary<string, string>
		{
			["Themes/DriveIn/DriveIn.xaml"] = XamlDictionary(
				"""<ResourceDictionary.MergedDictionaries><ResourceDictionary Source="Cards.xaml" /></ResourceDictionary.MergedDictionaries>""",
				"""<Style x:Key="TopBarStyle" TargetType="lc:TopBarChrome" />""",
				"""<Style x:Key="PageHeaderStyle" TargetType="TextBlock" />""",
				"""<DataTemplate x:Key="RowHeaderTemplate" />"""),
			["Themes/DriveIn/Cards.xaml"] = XamlDictionary("""<DataTemplate x:Key="PosterTemplate" />""", """<DataTemplate x:Key="StillTemplate" />"""),
		};

		Assert.Empty(ThemeContract.KeyProblems(new FakeTheme(LayoutThemeId.DriveIn), Loader(files)));
	}

	[Fact]
	public void Contract_AMissingOrWrongKey_IsReported()
	{
		var files = new Dictionary<string, string>
		{
			["Themes/DriveIn/DriveIn.xaml"] = XamlDictionary(
				"""<Style x:Key="TopBarStyle" TargetType="Grid" />""",
				"""<DataTemplate x:Key="PageHeaderStyle" />""",
				"""<DataTemplate x:Key="RowHeaderTemplate" />""",
				"""<DataTemplate x:Key="PosterTemplate" />""",
				"""<Style x:Key="SignInBackdropTemplate" TargetType="Grid" />"""),
		};

		var problems = ThemeContract.KeyProblems(new FakeTheme(LayoutThemeId.DriveIn), Loader(files));

		Assert.Contains("DriveIn: StillTemplate is missing", problems);
		Assert.Contains("DriveIn: PageHeaderStyle is a DataTemplate, not a Style", problems);
		Assert.Contains("DriveIn: TopBarStyle must target TopBarChrome", problems);
		Assert.Contains("DriveIn: SignInBackdropTemplate is a Style, not a DataTemplate", problems);
		Assert.Contains(ThemeContract.KeyProblems(new FakeTheme(LayoutThemeId.DriveIn), _ => null), p => p.Contains("no dictionary", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData("Themes/DriveIn/DriveIn.xaml", "Cards.xaml", "Themes/DriveIn/Cards.xaml")]
	[InlineData("Themes/DriveIn/DriveIn.xaml", "../Shared/Rows.xaml", "Themes/Shared/Rows.xaml")]
	[InlineData("Themes/DriveIn/DriveIn.xaml", "/Styles/Glass.xaml", "Styles/Glass.xaml")]
	[InlineData("Themes/DriveIn/DriveIn.xaml", "ms-appx:///Themes/DriveIn/Cards.xaml", "Themes/DriveIn/Cards.xaml")]
	public void Contract_ResolvesAMergedDictionarysSource(string from, string source, string expected) =>
		Assert.Equal(expected, ThemeContract.ResolveSource(from, source));

	[Fact]
	public void EveryThemeInTheApp_KeepsTheContract()
	{
		foreach (var theme in LayoutThemes.Discover(typeof(App).Assembly))
		{
			Assert.Equal($"ms-appx:///Themes/{theme.Id}/{theme.Id}.xaml", theme.Dictionary.ToString());
			Assert.Empty(ThemeContract.ViewProblems(theme));
			Assert.Empty(ThemeContract.KeyProblems(theme, SourceLoader));
			Assert.False(string.IsNullOrWhiteSpace(theme.DisplayName));
		}
	}

	[Fact]
	public void Classic_IsTheSharedPages_WithEveryRequiredKey_AndThe12TopBar()
	{
		var classic = new ClassicTheme();

		Assert.Equal("Classic", classic.DisplayName);
		Assert.All(ThemeContract.ThemableViewModels, vm => Assert.Null(classic.ViewFor(vm)));
		Assert.Null(((ILayoutTheme)classic).CreateOptions());
		Assert.Empty(ThemeContract.KeyProblems(classic, SourceLoader));

		// its top bar sets nothing over TopBarChrome's default style, which is 1.2.0's bar
		var bar = ThemeContract.DefinedKeys(classic.Dictionary, SourceLoader)[ThemeContract.TopBarStyleKey];
		Assert.Empty(bar.Elements());
	}

	// ---- the shared parts that follow the theme ----

	[Fact]
	public void TheSignInBackdrop_ShowsOnlyWithATemplate_AndNeverInHighContrast()
	{
		Assert.True(LoginPage.ShowsThemeBackdrop(themeHasOne: true, highContrast: false));
		Assert.False(LoginPage.ShowsThemeBackdrop(themeHasOne: false, highContrast: false));
		Assert.False(LoginPage.ShowsThemeBackdrop(themeHasOne: true, highContrast: true));
	}

	[Fact]
	public void TheTopBarsChrome_NamesItsTwoStates()
	{
		Assert.Equal("OverHero", Controls.TopBarChrome.StateFor(true));
		Assert.Equal("OverContent", Controls.TopBarChrome.StateFor(false));
	}

	[Fact]
	public void AppXaml_HasTheThemeSlot_LastOfItsMergedDictionaries()
	{
		var app = XDocument.Load(Path.Combine(FlyleafEngineTests.RepoRoot, "FluentFin", "App.xaml"));
		var merged = app.Descendants().Single(e => e.Name.LocalName == "ResourceDictionary.MergedDictionaries").Elements().ToList();
		Assert.Equal("LayoutThemeSlot", merged[^1].Name.LocalName);
	}

	// ---- helpers ----

	private static IReadOnlyList<ILayoutTheme> AllThemes() =>
		[new FakeTheme(LayoutThemeId.DriveIn), new FakeTheme(LayoutThemeId.Cinematic), new FakeTheme(LayoutThemeId.Editorial), new ClassicTheme()];

	private static XDocument? SourceLoader(string appPath)
	{
		var file = Path.Combine(FlyleafEngineTests.RepoRoot, "FluentFin", appPath.Replace('/', Path.DirectorySeparatorChar));
		return File.Exists(file) ? XDocument.Load(file) : null;
	}

	private static Func<string, XDocument?> Loader(Dictionary<string, string> files) =>
		path => files.TryGetValue(path, out var text) ? XDocument.Parse(text) : null;

	private static string XamlDictionary(params string[] body) =>
		"""<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:lc="using:FluentFin.Controls">"""
		+ string.Concat(body) + "</ResourceDictionary>";

	/// <summary>The local settings file in memory, with ReadSetting storing the default it returns (as LocalSettingsService does).</summary>
	private sealed class MemorySettings : ILocalSettingsService
	{
		private readonly JsonObject _values = [];

		public void Seed(string key, JsonNode node) => _values[key] = node;

		public string? Saved(string key) => _values[key]?.GetValue<string>();

		public JsonNode? ReadSettingRaw(string key) => _values[key];

		public T ReadSetting<T>(string key, T defaultValue)
		{
			if (!_values.ContainsKey(key))
			{
				SaveSetting(key, defaultValue);
				return defaultValue;
			}

			return _values[key].Deserialize<T>() ?? defaultValue;
		}

		public void SaveSetting<T>(string key, T value) => _values[key] = JsonSerializer.SerializeToNode(value);

		public void RemoveSetting(string key) => _values.Remove(key);

		public byte[] GetEntropyBytes() => [1, 2, 3];
	}
}

/// <summary>A theme for the tests: the views it is given, and a dictionary at the usual place.</summary>
internal sealed class FakeTheme(LayoutThemeId id, params (Type ViewModel, Type View)[] views) : ILayoutTheme
{
	public LayoutThemeId Id => id;

	public string DisplayName => $"Fake {id}";

	public Uri Dictionary => new($"ms-appx:///Themes/{id}/{id}.xaml");

	public Type? ViewFor(Type viewModelType) => views.FirstOrDefault(v => v.ViewModel == viewModelType).View;
}

internal sealed class ThrowingViewsTheme : ILayoutTheme
{
	public LayoutThemeId Id => LayoutThemeId.Cinematic;

	public string DisplayName => "Throws";

	public Uri Dictionary => new("ms-appx:///Themes/Cinematic/Cinematic.xaml");

	public Type? ViewFor(Type viewModelType) => throw new InvalidOperationException("no views");
}

/// <summary>Found by <see cref="LayoutThemes.Discover"/> in this assembly.</summary>
public sealed class DiscoverableDriveIn : ILayoutTheme
{
	public LayoutThemeId Id => LayoutThemeId.DriveIn;

	public string DisplayName => "Drive-in (test)";

	public Uri Dictionary => new("ms-appx:///Themes/DriveIn/DriveIn.xaml");

	public Type? ViewFor(Type viewModelType) => null;
}

/// <summary>A second class for the same id: left out (the first by name wins).</summary>
public sealed class DiscoverableDriveInToo : ILayoutTheme
{
	public LayoutThemeId Id => LayoutThemeId.DriveIn;

	public string DisplayName => "Drive-in again";

	public Uri Dictionary => new("ms-appx:///Themes/DriveIn/DriveIn.xaml");

	public Type? ViewFor(Type viewModelType) => null;
}

/// <summary>Not a theme anyone can show: left out.</summary>
public abstract class AbstractCinematicTheme : ILayoutTheme
{
	public LayoutThemeId Id => LayoutThemeId.Cinematic;

	public string DisplayName => "Abstract";

	public Uri Dictionary => new("ms-appx:///Themes/Cinematic/Cinematic.xaml");

	public Type? ViewFor(Type viewModelType) => null;
}

/// <summary>A theme whose constructor throws: left out, its id falls back.</summary>
public sealed class BrokenEditorialTheme : ILayoutTheme
{
	public BrokenEditorialTheme() => throw new InvalidOperationException("broken");

	public LayoutThemeId Id => LayoutThemeId.Editorial;

	public string DisplayName => "Broken";

	public Uri Dictionary => new("ms-appx:///Themes/Editorial/Editorial.xaml");

	public Type? ViewFor(Type viewModelType) => null;
}

/// <summary>A theme's view for the tests: a XAML page has InitializeComponent (ThemeContract.IsXamlPage).</summary>
public sealed partial class FakeHomeView : Page
{
	public void InitializeComponent()
	{
	}
}

public sealed partial class FakeSeasonView : Page
{
	public void InitializeComponent()
	{
	}
}

public sealed partial class FakeViewWithArgument : Page
{
	public FakeViewWithArgument(int value) => Value = value;

	public int Value { get; }
}

/// <summary>A page written in code only: the frame cannot create it.</summary>
public sealed partial class FakeCodeOnlyView : Page
{
}
