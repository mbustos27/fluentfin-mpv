using System.Text.Json;
using System.Text.Json.Nodes;
using FluentFin.Controls;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.ViewModels;
using FluentFin.Helpers;
using FluentFin.Services;
using FluentFin.Themes;
using FluentFin.Themes.Classic;
using FluentFin.ViewModels;
using FluentFin.Views;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.3.0, switching the layout theme at run time: the history keeps view models (its entries become the new theme's
/// pages), the page shown is shown again with the same view model (the player stays), the place is kept by item, the
/// crossfade and Narrator's line, the picker and the one-time "Choose a look" step.
/// </summary>
public sealed class ThemeSwitchTests
{
	// ---- the history and the page shown ----

	[Fact]
	public void TheHistory_KeepsViewModels_ItsEntriesBecomeTheNewThemesPages_WithTheirParameters()
	{
		var show = new BaseItemDto { Id = Guid.NewGuid() };
		var stack = new List<(Type Page, object? Parameter)>
		{
			(typeof(HomePage), null),
			(typeof(SeriesPage), show),
			(typeof(SettingsPageStandIn), null),
			(typeof(FakeSeasonView), show),
		};
		var newTheme = new Dictionary<Type, Type?>
		{
			[typeof(HomePage)] = typeof(FakeHomeView),     // Home has a view in the new theme
			[typeof(SeriesPage)] = typeof(SeriesPage),     // Series has none: shared
			[typeof(FakeSeasonView)] = typeof(SeasonPage), // the old theme's season view: the shared one now
		};

		var replaced = NavigationService.RemapStack(stack, x => x.Page, page => newTheme.GetValueOrDefault(page), (x, page) => (page, x.Parameter));

		Assert.Equal(2, replaced);
		Assert.Equal([typeof(FakeHomeView), typeof(SeriesPage), typeof(SettingsPageStandIn), typeof(SeasonPage)], stack.Select(x => x.Page));
		Assert.Same(show, stack[1].Parameter);
		Assert.Same(show, stack[3].Parameter);
	}

	[Fact]
	public void TheSwitch_ShowsThePageAgain_ButNeverThePlayer_NorAPageThatLooksTheSame()
	{
		Assert.Equal(ThemeReload.Reloaded, NavigationService.PlanReload(typeof(HomePage), typeof(HomePage), typeof(FakeHomeView)));
		Assert.Equal(ThemeReload.Reloaded, NavigationService.PlanReload(typeof(HomePage), typeof(FakeHomeView), typeof(HomePage)));
		Assert.Equal(ThemeReload.SamePage, NavigationService.PlanReload(typeof(SeriesPage), typeof(SeriesPage), typeof(SeriesPage)));
		Assert.Equal(ThemeReload.Deferred, NavigationService.PlanReload(typeof(VideoPlayerPage), typeof(VideoPlayerPage), typeof(VideoPlayerPage)));
		Assert.Equal(ThemeReload.None, NavigationService.PlanReload(typeof(HomePage), typeof(HomePage), null));
	}

	[Fact]
	public void TheSwitch_KeepsTheViewModel_SkipsItsReload_AndTheBarKeepsItsText()
	{
		var nav = Source("Services", "NavigationService.cs");
		Assert.Contains("using (ViewModelHandoff.Offer(frame.GetPageViewModel()))", nav);
		Assert.Contains("frame.Navigate(next!, _currentParameter, new SuppressNavigationTransitionInfo())", nav);
		// the reload's Navigated returns before OnNavigatedTo, the page transition and the history rules
		var reload = nav.IndexOf("if (_pendingThemeReload is { } reload", StringComparison.Ordinal);
		Assert.True(reload > 0 && reload < nav.IndexOf("PageTransition.Play(e.Content, e.NavigationMode);", StringComparison.Ordinal));
		Assert.True(reload < nav.IndexOf("await navigationAware.OnNavigatedTo(e.Parameter);", StringComparison.Ordinal));

		Assert.Contains("ViewModelHandoff.TryTake(typeof(T), out var handed)", Source("App.xaml.cs"));
		var shell = Source("Views", "ShellPage.xaml.cs");
		Assert.True(shell.IndexOf("LastNavigationWasThemeReload: true", StringComparison.Ordinal) < shell.IndexOf("_titleBar.OnPageShown(e.SourcePageType, e.Parameter);", StringComparison.Ordinal));
	}

	[Fact]
	public void TheDictionarySwap_ShowsThePageAgain_InTheSameTurn_WhoeverChoseTheTheme()
	{
		// 1.7.5 (g4, the basics gate's theme switch): a theme chosen outside ThemeSwitcher (the dev hook; a later shortcut or
		// restore) swapped the dictionary and left the old theme's page in the window: Drive-in's film page measured against the
		// new keys at the next layout pass and ended Nixie (DriveInBoardTextBrush not found). The swap reloads the page itself.
		var resources = Source("Services", "ThemeResources.cs");
		var changed = resources.IndexOf("private static void OnActiveChanged", StringComparison.Ordinal);
		Assert.True(changed > 0);
		var body = resources[changed..resources.IndexOf('}', changed)];
		var show = body.IndexOf("Show(_service?.Active);", StringComparison.Ordinal);
		var reload = body.IndexOf("LastReload = ReloadShownPage();", StringComparison.Ordinal);
		Assert.True(show >= 0 && reload > show, "the new dictionary first, then the page shown again in its view");
		Assert.Contains("navigation.ReloadForTheme()", resources);

		// and the outgoing theme's dictionary stays, below every other dictionary, for whatever of that theme is still measured
		// (fxmid-d: the reload alone still left Drive-in's series page ending Nixie at the switch to Cinematic)
		var showAt = resources.IndexOf("public static void Show(", StringComparison.Ordinal);
		var clear = resources.IndexOf("slot.MergedDictionaries.Clear();", showAt, StringComparison.Ordinal);
		Assert.True(resources.IndexOf("var outgoing = Current;", showAt, StringComparison.Ordinal) is > 0 and var kept && kept < clear);
		Assert.True(resources.IndexOf("KeepBehind(outgoing);", showAt, StringComparison.Ordinal) > clear);
		Assert.Contains("resources.MergedDictionaries.Insert(0, s_behind);", resources);

		// and the switcher reloads nothing a second time: it reads what the swap did
		var switcher = Source("Services", "ThemeSwitcher.cs");
		Assert.DoesNotContain("ReloadForTheme()", switcher);
		Assert.Contains("themes.Choose(id) ? ThemeResources.LastReload : ThemeReload.None", switcher);
	}

	[Fact]
	public void TheHandoff_GivesTheViewModel_ToOneRequestOfItsExactType()
	{
		var vm = new FakeViewModel();
		using (ViewModelHandoff.Offer(vm))
		{
			Assert.False(ViewModelHandoff.TryTake(typeof(object), out _));
			Assert.False(ViewModelHandoff.TryTake(typeof(string), out _));
			Assert.True(ViewModelHandoff.TryTake(typeof(FakeViewModel), out var taken));
			Assert.Same(vm, taken);
			Assert.False(ViewModelHandoff.TryTake(typeof(FakeViewModel), out _));
		}

		using (ViewModelHandoff.Offer(vm))
		{
			Assert.True(ViewModelHandoff.IsOffered);
		}

		Assert.False(ViewModelHandoff.IsOffered); // withdrawn: no later page gets it
	}

	// ---- the place ----

	[Fact]
	public void TheFirstVisibleItem_IsTheFirstWhoseMiddleShows()
	{
		var a = new object();
		var b = new object();
		var c = new object();

		// a scrolled half out to the left: b leads
		Assert.Same(b, ThemeAnchors.FirstVisible([(c, 400, 600), (a, -150, 50), (b, 60, 260)], 0, 1000));
		// a's middle still shows
		Assert.Same(a, ThemeAnchors.FirstVisible([(a, -40, 160), (b, 170, 370)], 0, 1000));
		// only a sliver of one item shows
		Assert.Same(c, ThemeAnchors.FirstVisible([(c, 980, 1180)], 0, 1000));
		Assert.Null(ThemeAnchors.FirstVisible<object>([(a, 1200, 1400)], 0, 1000));
	}

	[Fact]
	public void AnItem_IsKnownByItsId_AcrossViews()
	{
		var id = Guid.NewGuid();
		var dto = new BaseItemDto { Id = id };
		var model = new BaseItemViewModel { Dto = dto };
		var other = new object();

		Assert.Equal(id, ThemeAnchors.ItemKey(model));
		Assert.Equal(id, ThemeAnchors.ItemKey(dto));
		Assert.Equal(id, ThemeAnchors.ItemKey(new BaseItemPerson { Id = id }));
		Assert.Same(other, ThemeAnchors.ItemKey(other));
		Assert.Null(ThemeAnchors.ItemKey(null));

		var row = new List<BaseItemViewModel> { new() { Dto = new BaseItemDto { Id = Guid.NewGuid() } }, model };
		Assert.Equal(1, ThemeAnchors.IndexOf(row, id));
		Assert.Equal(-1, ThemeAnchors.IndexOf(row, Guid.NewGuid()));
	}

	// ---- the crossfade and Narrator ----

	[Fact]
	public void TheCrossfade_Is200Ms_OrNoneWhenMotionIsReduced()
	{
		Assert.Equal(TimeSpan.FromMilliseconds(200), ThemeSwitcher.CrossfadeFor(motionAllowed: true));
		Assert.Equal(TimeSpan.Zero, ThemeSwitcher.CrossfadeFor(motionAllowed: false));
		Assert.Contains("CrossfadeFor(GlassMode.MotionAllowed)", Source("Services", "ThemeSwitcher.cs"));
	}

	[Fact]
	public void Narrator_SaysTheNewThemesName() => Assert.Equal("Theme changed to Drive-in", ThemeSwitcher.Announcement("Drive-in"));

	// ---- the player ----

	[Fact]
	public void ThePlayersBars_TakeTheThemesStyles_InPlace()
	{
		var player = Source("Controls", "MpvPlayerControls.xaml");
		Assert.Contains("themes:ThemeStyle.Key=\"PlayerTopBarStyle\"", player);
		Assert.Contains("themes:ThemeStyle.Key=\"PlayerBottomBarStyle\"", player);
		Assert.Contains("Style=\"{StaticResource PlayerTopBarBaseStyle}\"", player);
		Assert.Contains("Style=\"{StaticResource PlayerBottomBarBaseStyle}\"", player);
		Assert.Equal(["PlayerTopBarStyle", "PlayerBottomBarStyle"], ThemeContract.PlayerOverlayKeys);
	}

	// ---- the picker ----

	[Fact]
	public void ThePicker_ListsTheThemes_MarksTheOneInUse_AndAppliesAChoice()
	{
		var service = Service(LayoutThemeId.DriveIn);
		var applied = new List<LayoutThemeId>();
		var picker = new ThemePickerViewModel(service, id =>
		{
			applied.Add(id);
			service.Choose(id);
			return Task.CompletedTask;
		});

		Assert.True(picker.HasChoice);
		Assert.Equal([LayoutThemeId.DriveIn, LayoutThemeId.Cinematic, LayoutThemeId.Editorial, LayoutThemeId.Classic], picker.Options.Select(o => o.Id));
		Assert.Equal(LayoutThemeId.DriveIn, picker.Selected!.Id);
		Assert.True(picker.Options[0].IsActive);
		Assert.Equal("A drive-in at dusk: marquee rows, nixie numbers.", picker.Options[0].Description);

		picker.Selected = picker.Options[3];

		Assert.Equal([LayoutThemeId.Classic], applied);
		Assert.True(picker.Options[3].IsActive);
		Assert.False(picker.Options[0].IsActive);
		Assert.Equal("Classic options", picker.OptionsHeader);
		Assert.Null(picker.OptionsBlock); // Classic has none
	}

	[Fact]
	public void ThePicker_ShowsTheActiveThemesOptions()
	{
		var service = Service(LayoutThemeId.Cinematic);
		var picker = new ThemePickerViewModel(service, _ => Task.CompletedTask);

		Assert.Equal("Fake Cinematic options", picker.OptionsHeader);
		Assert.True(picker.HasOptions);
		Assert.Equal("options of Cinematic", picker.OptionsBlock);
	}

	[Fact]
	public void ChooseALook_OnlySelects_UntilItsButton_AndStartsOnThePreselected()
	{
		var service = Service(LayoutThemeId.Classic);
		var picker = new ThemePickerViewModel(service, apply: null, preselect: LayoutThemeId.Classic);

		Assert.Equal(LayoutThemeId.Classic, picker.Selected!.Id);
		picker.Selected = picker.Options[0];
		Assert.Equal(LayoutThemeId.Classic, service.ActiveId);
		Assert.Null(picker.OptionsBlock);

		// a switch elsewhere keeps the step's selection
		service.Choose(LayoutThemeId.Editorial);
		Assert.Equal(LayoutThemeId.DriveIn, picker.Selected!.Id);
		picker.Detach();
	}

	[Fact]
	public void ThePicker_IsHidden_WithOneTheme()
	{
		var service = new ThemeService(new MemorySettings(), [new ClassicTheme()], null);
		Assert.False(new ThemePickerViewModel(service, _ => Task.CompletedTask).HasChoice);
	}

	[Fact]
	public void TheTiles_AreNamedForNarrator()
	{
		var option = new ThemeOption(LayoutThemeId.Editorial, "Editorial", ThemeCatalog.Description(LayoutThemeId.Editorial));
		Assert.Equal("Editorial. Dense and fast: big type, details open in place.", ThemePicker.TileName(option));
		option.IsActive = true;
		Assert.Equal("Editorial. Dense and fast: big type, details open in place. In use.", ThemePicker.TileName(option));
		Assert.Equal("Editorial", option.ToString());
		Assert.All(Enum.GetValues<LayoutThemeId>(), id => Assert.False(string.IsNullOrEmpty(ThemeCatalog.Description(id))));
		Assert.All(Enum.GetValues<LayoutThemeId>(), id => Assert.Contains($"x:Name=\"{ThemePreview.SketchFor(id)}\"", Source("Controls", "ThemePreview.xaml")));
	}

	// ---- "Choose a look" ----

	[Theory]
	[InlineData(true, 4, false, true)]
	[InlineData(false, 4, false, false)] // offered before
	[InlineData(true, 1, false, false)]  // one theme in this build: later
	[InlineData(true, 4, true, false)]   // NIXIE_LAYOUT forces a theme (the harness)
	public void ChooseALook_IsOfferedOnce_WhenThereIsAChoice(bool notOffered, int themes, bool forced, bool expected) =>
		Assert.Equal(expected, ChooseLookStep.ShouldOffer(notOffered, themes, forced));

	[Fact]
	public void ChooseALook_FollowsTheFirstPageAfterTheSignIn_AndTheHarnessSkipsIt()
	{
		Assert.Contains("await ChooseLookStep.OfferAsync(XamlRoot)", Source("Views", "ShellPage.xaml.cs"));
		var prepare = File.ReadAllText(Path.Combine(FlyleafEngineTests.RepoRoot, "..", "tools", "ui-harness", "offscreen", "prepare.ps1"));
		Assert.Contains("ChooseLookOffered -NotePropertyValue $true", prepare);
	}

	// ---- helpers ----

	private static ThemeService Service(LayoutThemeId chosen)
	{
		var settings = new MemorySettings();
		settings.Seed(ThemeChoice.SettingKey, JsonValue.Create(chosen.ToString()));
		return new ThemeService(settings, [new OptionsTheme(LayoutThemeId.DriveIn), new OptionsTheme(LayoutThemeId.Cinematic), new OptionsTheme(LayoutThemeId.Editorial), new ClassicTheme()], null);
	}

	/// <summary>
	/// Nixie 1.4.0 (bug hunt m6): "Choose a look" hands Home's view model to the new theme's Home, so the view model outlives
	/// the page. Every theme's Home listens to it (and to its rows, Up next and genres) only while shown: subscribed on Loaded,
	/// let go on Unloaded, never in the constructor, where the old page kept listening and its notice timer closed the new
	/// page's Undo.
	/// </summary>
	[Theory]
	[InlineData("HomePage", "Views", "HomePage.xaml.cs")]
	[InlineData("DriveInHomePage", "Themes", "DriveIn", "Views", "DriveInHomePage.xaml.cs")]
	[InlineData("CinematicHomePage", "Themes", "Cinematic", "Views", "CinematicHomePage.xaml.cs")]
	[InlineData("EditorialHomePage", "Themes", "Editorial", "Views", "EditorialHomePage.xaml.cs")]
	public void EveryHome_ListensToItsViewModel_OnlyWhileShown(string page, params string[] path)
	{
		var code = Source(path).Replace("\r\n", "\n");
		var start = code.IndexOf($"public {page}()", StringComparison.Ordinal);
		Assert.True(start > 0, page);
		var ctor = code[start..code.IndexOf("\n\t}\n", start, StringComparison.Ordinal)];
		var loaded = ctor.IndexOf("Loaded += (_, _) =>", StringComparison.Ordinal);
		var unloaded = ctor.IndexOf("Unloaded += (_, _) =>", StringComparison.Ordinal);
		Assert.True(loaded > 0 && unloaded > 0, page);

		// outside the Loaded and Unloaded handlers, the constructor subscribes to nothing of the view model's
		var outside = System.Text.RegularExpressions.Regex.Replace(ctor, @"(Un)?[Ll]oaded \+= \(_, _\) =>\n\t\t\{.*?\n\t\t\};", "", System.Text.RegularExpressions.RegexOptions.Singleline);
		Assert.DoesNotMatch(@"ViewModel(\.\w+)*\.\w+ \+=", outside);
		// and each subscription is let go on Unloaded
		Assert.Matches(@"Unloaded \+= \(_, _\) =>\n\t\t\{[^}]*(-=|Listen\(false\)|Unwatch\(\))", ctor);
	}

	private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([FlyleafEngineTests.RepoRoot, "FluentFin", .. parts]));

	private sealed class FakeViewModel;

	private sealed class SettingsPageStandIn;

	private sealed class OptionsTheme(LayoutThemeId id) : ILayoutTheme
	{
		public LayoutThemeId Id => id;

		public string DisplayName => $"Fake {id}";

		public Uri Dictionary => new($"ms-appx:///Themes/{id}/{id}.xaml");

		public Type? ViewFor(Type viewModelType) => null;

		public object? CreateOptions() => id == LayoutThemeId.Cinematic ? "options of Cinematic" : null;
	}

	private sealed class MemorySettings : ILocalSettingsService
	{
		private readonly JsonObject _values = [];

		public void Seed(string key, JsonNode node) => _values[key] = node;

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
