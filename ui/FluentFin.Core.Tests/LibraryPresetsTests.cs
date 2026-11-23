using FluentFin.Core.Contracts.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.6 colpage (the top bar, library and UI review's LB2, LB3, LB5, L25): one presets strip in every theme, fed by one model:
/// All · Unwatched · Favorites · Recently added, then the library's Nixie collections; Unwatched lights whenever the filter is
/// on, whatever else is on; every preset has a way out on a second click; Filter's count leaves out what has its own switch.
/// </summary>
public sealed class LibraryPresetsTests
{
	static LibraryPresetsTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	private static readonly BaseItemDto Movies = new() { Id = Guid.NewGuid(), Name = "Movies", CollectionType = BaseItemDto_CollectionType.Movies };

	private readonly Mock<IJellyfinClient> _client = new();

	public LibraryPresetsTests() => _client.SetupGet(x => x.UserId).Returns(Guid.NewGuid());

	private static BaseItemDto Movie(string name, string? genre = null, bool played = false, bool favorite = false, int day = 1) => new()
	{
		Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.Movie, Genres = genre is null ? [] : [genre],
		DateCreated = new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero),
		UserData = new UserItemDataDto { Played = played, IsFavorite = favorite },
	};

	private async Task<LibraryViewModel> Library(IReadOnlyList<BaseItemDto> items, ILibraryPresetSource? source = null, ISavedFilters? saved = null)
	{
		_client.SetupLibrary(items);
		var vm = new LibraryViewModel(_client.Object, presetSource: source, savedFilters: saved);
		await vm.OnNavigatedTo(Movies);
		await vm.RestLoaded;
		return vm;
	}

	private static string Lit(LibraryViewModel vm) => string.Join(",", vm.Presets.Where(p => p.IsOn).Select(p => p.Id));

	private static LibraryPreset Preset(LibraryViewModel vm, string id) => vm.Presets.Single(p => p.Id == id).Preset;

	[Fact]
	public async Task TheStrip_IsTheSameFourInEveryLibrary_AllLitAtRest()
	{
		var vm = await Library([Movie("A"), Movie("B", played: true)]);
		Assert.Equal(["all", "unwatched", "favorites", "recent"], vm.Presets.Select(p => p.Id));
		Assert.Equal(["All", "Unwatched", "Favorites", "Recently added"], vm.Presets.Select(p => p.Label));
		Assert.Equal("all", Lit(vm));
		Assert.Equal("All titles, 2", vm.Presets[0].Name);
		Assert.Equal("Unwatched, 1 title", vm.Presets[1].Name);
	}

	/// <summary>LB2: the Unwatched preset is the filter's twin: lit whenever it is on, whatever else is on (it was lit only alone).</summary>
	[Fact]
	public async Task Unwatched_LightsWheneverItIsOn_AndASecondClickTurnsItOff()
	{
		var vm = await Library([Movie("A", "Drama"), Movie("B", "Comedy", played: true), Movie("C", "Drama")]);

		vm.TogglePreset(Preset(vm, "unwatched"));
		Assert.True(vm.Filter.Unwatched);
		Assert.Equal("unwatched", Lit(vm));
		Assert.Equal(2, vm.TotalCount);

		vm.Filter.Genres.Add("Drama"); // from the Filter panel: Unwatched stays lit
		Assert.Equal("unwatched", Lit(vm));

		vm.TogglePreset(Preset(vm, "unwatched")); // the way out: a second click
		Assert.False(vm.Filter.Unwatched);
		Assert.Equal(["Drama"], vm.Filter.Genres); // the rest kept
		Assert.Equal("", Lit(vm)); // a genre on: not All

		vm.TogglePreset(Preset(vm, "all")); // All: every filter off
		Assert.Empty(vm.Filter.Genres);
		Assert.Equal("all", Lit(vm));
	}

	[Fact]
	public async Task UnwatchedAndFavorites_CanBeOnTogether_AndTheFilterSwitchLightsThePreset()
	{
		var vm = await Library([Movie("A", favorite: true), Movie("B", played: true, favorite: true), Movie("C")]);
		vm.TogglePreset(Preset(vm, "unwatched"));
		vm.TogglePreset(Preset(vm, "favorites"));
		Assert.Equal("unwatched,favorites", Lit(vm));
		Assert.Equal(1, vm.TotalCount);

		// the Filter panel's "Favorites only" (or the applied-filter row's ✕) and the preset are one filter
		vm.RemoveFilter(vm.AppliedFilters.Single(f => f.Group == FilterGroup.Favorites));
		Assert.Equal("unwatched", Lit(vm));
		vm.Filter.Favorites = true;
		Assert.Equal("unwatched,favorites", Lit(vm));
	}

	/// <summary>LB3: Filter's count leaves out what has its own switch (Unwatched was lit, counted in "Filter (1)" and chipped).</summary>
	[Fact]
	public async Task FiltersCount_LeavesOutTheStripsSwitches()
	{
		var vm = await Library([Movie("A", "Drama"), Movie("B", "Comedy")]);
		vm.TogglePreset(Preset(vm, "unwatched"));
		vm.TogglePreset(Preset(vm, "favorites"));
		Assert.Equal(("Filter", "Filter (none on)"), (vm.FilterText, vm.FilterButtonName));
		Assert.Equal(2, vm.ActiveFilters); // still two filters on (Clear filters, the applied-filter row)
		Assert.Equal(["Unwatched", "Favorites"], vm.AppliedFilters.Select(f => f.Label));

		vm.Filter.Genres.Add("Drama");
		Assert.Equal(("Filter (1)", "Filter (1 on)"), (vm.FilterText, vm.FilterButtonName)); // L25 kept: its words first
	}

	[Fact]
	public async Task RecentlyAdded_IsTheSort_AndASecondClickPutsTheSortBefore_Back()
	{
		var vm = await Library([Movie("B", day: 1), Movie("A", day: 3), Movie("C", day: 2)]);
		vm.UpdateSortBy(ItemSortBy.CommunityRating);
		var before = (vm.SortBy, vm.Order);

		vm.TogglePreset(Preset(vm, "recent"));
		Assert.Equal((ItemSortBy.DateCreated, SortOrder.Descending), (vm.SortBy, vm.Order));
		Assert.Equal("recent", Lit(vm));
		Assert.Equal(["A", "C", "B"], vm.Items.Select(i => i.Name));

		vm.TogglePreset(Preset(vm, "recent"));
		Assert.Equal(before, (vm.SortBy, vm.Order));
		Assert.Equal("all", Lit(vm));

		// All while Recently added is lit puts the sort back too; picked from the Sort menu first, All goes to A to Z
		vm.UpdateSortBy(ItemSortBy.DateCreated);
		vm.UpdateSortOrder(SortOrder.Descending);
		Assert.Equal("recent", Lit(vm));
		vm.TogglePreset(Preset(vm, "all"));
		Assert.Equal((ItemSortBy.Name, SortOrder.Ascending), (vm.SortBy, vm.Order));
	}

	private sealed class Source(Func<IReadOnlyList<BaseItemDto>, IReadOnlyList<LibraryPreset>> make) : ILibraryPresetSource
	{
		public int Calls;

		/// <summary>Set: the next ask waits for it (the collections' index read in the background).</summary>
		public TaskCompletionSource? Gate;

		public event EventHandler? Changed;

		public int Listeners => Changed?.GetInvocationList().Length ?? 0;

		public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

		public async Task<IReadOnlyList<LibraryPreset>> PresetsForAsync(BaseItemDto library, IReadOnlyList<BaseItemDto> titles, CancellationToken cancellationToken)
		{
			Calls++;
			if (Gate is { } gate)
			{
				await gate.Task.WaitAsync(cancellationToken);
			}

			return make(titles);
		}
	}

	[Fact]
	public async Task ANixieCollection_JoinsTheStrip_NarrowsToItsTitles_AndHasAWayOut()
	{
		var heist = Movie("Heist One");
		var other = Movie("Other");
		var source = new Source(titles => [LibraryPreset.ForCollection("nixie:keyword:heist", "Heist", new HashSet<Guid> { titles[0].Id!.Value })]);
		var vm = await Library([heist, other], source);
		await vm.PresetsLoaded;
		Assert.Equal(1, source.Calls);
		Assert.Equal("nixie:keyword:heist", vm.Presets[^1].Id);
		Assert.Equal("Heist, a Nixie collection", vm.Presets[^1].Name);

		vm.TogglePreset(vm.Presets[^1].Preset);
		Assert.Equal(["Heist One"], vm.Items.Select(i => i.Name));
		Assert.Equal("nixie:keyword:heist", Lit(vm));
		Assert.Equal("Heist", vm.AppliedFilters.Single().Label);
		Assert.Equal(("Filter", 1), (vm.FilterText, vm.ActiveFilters));

		// the way out: its chip's ✕, a second click, All
		vm.RemoveFilter(vm.AppliedFilters.Single());
		Assert.Equal(2, vm.TotalCount);
		vm.TogglePreset(vm.Presets[^1].Preset);
		vm.TogglePreset(vm.Presets[^1].Preset);
		Assert.Equal(2, vm.TotalCount);
		vm.TogglePreset(vm.Presets[^1].Preset);
		vm.TogglePreset(Preset(vm, "all"));
		Assert.Equal(2, vm.TotalCount);
		Assert.Null(vm.Filter.Preset);
	}

	/// <summary>
	/// Review Major 4: the collections' index is read in the background, so the source answers late, or says later that it has
	/// more (Changed): a library opened early gets its Nixie presets in the same visit, and a library that is left lets go.
	/// </summary>
	[Fact]
	public async Task ASourceThatAnswersLate_OrHasMoreLater_FillsTheStripInTheSameVisit()
	{
		var index = new List<LibraryPreset>();
		var source = new Source(_ => [.. index]) { Gate = new TaskCompletionSource() };
		var vm = await Library([Movie("Heist One"), Movie("Other")], source);
		Assert.Equal(4, vm.Presets.Count); // the four, while the source waits for its index

		// the answer comes late
		index.Add(LibraryPreset.ForCollection("nixie:keyword:heist", "Heist", new HashSet<Guid>()));
		source.Gate.SetResult();
		await vm.PresetsLoaded;
		Assert.Equal("nixie:keyword:heist", vm.Presets[^1].Id);

		// the index has more later: Changed asks again, while the library is shown
		source.Gate = null;
		index.Add(LibraryPreset.ForCollection("nixie:decade:1990s", "The 1990s", new HashSet<Guid>()));
		Assert.Equal(1, source.Listeners);
		source.RaiseChanged();
		await vm.PresetsLoaded;
		Assert.Equal(["nixie:keyword:heist", "nixie:decade:1990s"], vm.Presets.Skip(4).Select(p => p.Id));

		// left: the source is let go, and a later Changed asks nothing
		await vm.OnNavigatedFrom();
		Assert.Equal(0, source.Listeners);
		var calls = source.Calls;
		source.RaiseChanged();
		Assert.Equal(calls, source.Calls);

		// a late answer for a library already left is dropped (no presets set behind the user's back)
		var late = new Source(_ => [LibraryPreset.ForCollection("nixie:x", "X", new HashSet<Guid>())]) { Gate = new TaskCompletionSource() };
		var left = await Library([Movie("A")], late);
		await left.OnNavigatedFrom();
		late.Gate.SetResult();
		await left.PresetsLoaded;
		Assert.Equal(4, left.Presets.Count);
	}

	[Fact]
	public async Task ASourceThatFails_LeavesTheFourBuiltIn()
	{
		var vm = await Library([Movie("A")], new Source(_ => throw new InvalidOperationException("no index")));
		Assert.Equal(["all", "unwatched", "favorites", "recent"], vm.Presets.Select(p => p.Id));
	}

	[Fact]
	public async Task AnEmptyLibrary_StillHasItsStrip()
	{
		var vm = await Library([]);
		Assert.Equal(4, vm.Presets.Count);
		Assert.Equal("All titles, 0", vm.Presets[0].Name);
	}

	private sealed class Saved(bool answer) : ISavedFilters
	{
		public List<(string Name, LibrarySavedFilter Filter)> Calls { get; } = [];

		public Task<bool> SaveFilter(string name, LibrarySavedFilter filter)
		{
			Calls.Add((name, filter));
			return Task.FromResult(answer);
		}
	}

	[Fact]
	public async Task SaveAsCollection_SavesTheFiltersOn_OnlyWithAName()
	{
		var saved = new Saved(true);
		var vm = await Library([Movie("A", "Comedy"), Movie("B", "Drama")], saved: saved);
		Assert.False(vm.CanSaveFilter); // nothing on yet
		vm.TogglePreset(Preset(vm, "unwatched"));
		vm.Filter.Genres.Add("Comedy");
		Assert.True(vm.CanSaveFilter);
		Assert.Equal("Unwatched Comedy", LibraryPresets.SuggestedName(vm.Filter));

		Assert.False(await vm.SaveFilterAs("   "));
		Assert.False(await vm.SaveFilterAs(new string('x', 81)));
		Assert.Empty(saved.Calls);

		Assert.True(await vm.SaveFilterAs("  Funny ones "));
		var (name, filter) = saved.Calls.Single();
		Assert.Equal("Funny ones", name);
		Assert.Equal((Movies.Id!.Value, true, false, "Comedy"), (filter.LibraryId, filter.Unwatched, filter.Favorites, filter.Genres.Single()));

		// with nowhere to save (the collections stream not merged), there is no Save as collection
		var none = await Library([Movie("A")]);
		none.TogglePreset(Preset(none, "unwatched"));
		Assert.False(none.CanSaveFilter);
		Assert.False(await none.SaveFilterAs("Name"));
	}

	// --- 1.7.3: the strip keeps decades, genre mixes and countries, and drops people, studios and networks (the owner's answer 3)

	private static readonly DateTimeOffset November = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

	// a library of 24 films that makes every kind the strip ever had: three franchises, two decades, a genre mix, a keyword, a
	// country besides the commonest, a studio, a director and an actor, and the acclaimed ones
	private static List<SmartCollections.IndexEntry> EveryKind(Guid library) =>
	[
		.. Enumerable.Range(0, 24).Select(i => new SmartCollections.IndexEntry
		{
			Id = Guid.NewGuid(),
			Name = $"Film {i:D2}",
			Kind = SmartCollections.IndexKind.Movie,
			LibraryId = library,
			Year = 1990 + (i % 20),
			TmdbCollection = $"{i / 8}",
			Genres = (i % 3) switch { 0 => ["Comedy", "Horror"], 1 => ["Comedy"], _ => ["Horror"] },
			Tags = i < 6 ? ["heist"] : [],
			Countries = [i % 2 == 0 ? "France" : "Japan"],
			Studios = ["North Line"],
			Rating = 5 + (i % 5),
			TagCount = 10, // 1.7.3 (M1's review): the acclaimed ones are backed by their tags
			RunTimeTicks = TimeSpan.FromMinutes(120).Ticks,
			People = [new("Ana Reyes", null, "Director"), new("Clara Quill", null, "Actor")],
		}),
	];

	private static SmartCollections.IndexSnapshot Index(IReadOnlyList<SmartCollections.IndexEntry> entries, params Guid[] libraries) =>
		new([.. libraries.Select(id => new SmartCollections.IndexLibrary(id, "Movies", SmartCollections.IndexKind.Movie))], entries,
			new Dictionary<Guid, SmartCollections.SeriesPlays>(), true, November);

	[Fact]
	public void TheStrip_KeepsDecadesGenreMixesAndCountries_InItsOrder_AndHasNoPeopleStudiosOrNetworks()
	{
		var movies = Guid.NewGuid();

		var presets = SmartCollections.NixiePresets.For(Index(EveryKind(movies), movies), movies, November, most: 50);

		// one of each kind first, in the strip's order (1.7.2's, less the people, studios and networks), then the rest
		Assert.Equal(
			[SmartCollections.SmartCollectionKind.Franchise, SmartCollections.SmartCollectionKind.Decade, SmartCollections.SmartCollectionKind.GenreMix,
			SmartCollections.SmartCollectionKind.Keyword, SmartCollections.SmartCollectionKind.Country, SmartCollections.SmartCollectionKind.HighlyRated],
			presets.Select(p => p.Kind).Distinct());
		Assert.Equal(["2000s", "Comedy · Horror", "Heists", "Japan", "Acclaimed"], presets.Skip(1).Take(5).Select(p => p.Label));
		Assert.DoesNotContain(presets, p => SmartCollections.RetiredCollections.Gone.Contains(p.Kind));
		Assert.All(presets, p => Assert.False(SmartCollections.RetiredCollections.IsGone(p.Id)));
		// the strip's own kinds are found by their ids (a pin of one is made that way)
		Assert.All(presets.Where(p => p.Kind is SmartCollections.SmartCollectionKind.Decade or SmartCollections.SmartCollectionKind.GenreMix or SmartCollections.SmartCollectionKind.Country),
			p => Assert.True(SmartCollections.NixiePresets.IsStripOnly(p.Id)));
		Assert.All(presets.Where(p => p.Kind is SmartCollections.SmartCollectionKind.Franchise or SmartCollections.SmartCollectionKind.Keyword or SmartCollections.SmartCollectionKind.HighlyRated),
			p => Assert.False(SmartCollections.NixiePresets.IsStripOnly(p.Id)));
	}

	[Fact]
	public void TheStripsOwnKinds_TurnedOffInSettings_StayOutOfTheStrip_BeforeAndAfterApply()
	{
		// 1.7.3 fix round (BH-173-m2): 1.7.2 left decades and countries off; the first 1.7.3 build took them out of
		// "CollectionsKindsOff" at the start and ignored the setting in the strip, so both came back in every library
		var movies = Guid.NewGuid();
		var settings = new Mock<ILocalSettingsService>();
		var stored = "Country,Decade,Director";
		settings.Setup(x => x.ReadSetting(SmartCollections.CollectionSettingKeys.KindsOff.Name, It.IsAny<string>())).Returns(() => stored);
		settings.Setup(x => x.SaveSetting(SmartCollections.CollectionSettingKeys.KindsOff.Name, It.IsAny<string>())).Callback((string _, string v) => stored = v);
		SmartCollections.SmartCollectionKind[] stripOnly = [SmartCollections.SmartCollectionKind.Decade, SmartCollections.SmartCollectionKind.Country];
		IReadOnlyList<SmartCollections.NixiePreset> Strip() =>
			SmartCollections.NixiePresets.For(Index(EveryKind(movies), movies), movies, November, kindsOff: SmartCollections.CollectionSettings.KindsOff(settings.Object), most: 50);

		var before = Strip();
		SmartCollections.CollectionSettings.Apply(settings.Object); // the next start
		var after = Strip();

		Assert.Equal("Country,Decade,Director", stored);
		foreach (var strip in new[] { before, after })
		{
			Assert.DoesNotContain(strip, p => stripOnly.Contains(p.Kind));
			Assert.Contains(strip, p => p.Kind == SmartCollections.SmartCollectionKind.GenreMix); // still on
		}

		// its switch turns it on again
		new SmartCollections.CollectionsSettingsViewModel(settings.Object).LibraryFilters.Single(x => x.Name == "Decades").IsOn = true;
		Assert.Contains(Strip(), p => p.Kind == SmartCollections.SmartCollectionKind.Decade);
	}

	[Fact]
	public void TheRetiredKindsIds_AreKnownByTheirBuildersOwnIds()
	{
		// RetiredCollections.IsGone reads an id's first part: each builder of the four kinds that went makes ids it knows
		var movies = Guid.NewGuid();
		var context = SmartCollections.Builders.BuildContext.For(Index(EveryKind(movies), movies), November);
		SmartCollections.Builders.ISmartCollectionBuilder[] gone =
			[new SmartCollections.Builders.DirectorBuilder(), new SmartCollections.Builders.ActorBuilder(), new SmartCollections.Builders.StudioBuilder(SmartCollections.IndexKind.Movie)];
		var made = gone.SelectMany(b => b.Build(context)).ToList();

		Assert.Equal(3, made.Count);
		Assert.All(made, x => Assert.True(SmartCollections.RetiredCollections.IsGone(x.Id) && SmartCollections.RetiredCollections.IsGone("nixie:" + x.Id), x.Id));
		Assert.True(SmartCollections.RetiredCollections.IsGone("network:hbo"));
		Assert.False(SmartCollections.RetiredCollections.IsGone("decade:movie:1990"));
		Assert.False(SmartCollections.RetiredCollections.IsGone("nixie:franchise:2344"));
		Assert.False(SmartCollections.RetiredCollections.IsGone(null));
	}

	[Fact]
	public void ATitleTheServerListsInTwoLibraries_IsInBothLibrariesPresets()
	{
		// 1.7.3 (P0-2a): each entry has every library it is in (stream I fills LibraryIds); the strip read LibraryId alone
		var movies = Guid.NewGuid();
		var classics = Guid.NewGuid();
		var films = EveryKind(movies).Select((x, i) => i < 12 ? x with { LibraryIds = [movies, classics] } : x).ToList();

		var index = Index(films, movies, classics);
		var inClassics = SmartCollections.NixiePresets.For(index, classics, November, most: 50);
		var inMovies = SmartCollections.NixiePresets.For(index, movies, November, most: 50);

		var decade = Assert.Single(inClassics, p => p.Kind == SmartCollections.SmartCollectionKind.Decade);
		Assert.Equal(10, decade.Items.Count); // films 00-09: the 1990s of the twelve it shares
		// both libraries' strips have the shared titles: its first library's 1990s are all fourteen
		var shared = films.Take(12).Select(x => x.Id).ToHashSet();
		Assert.True(shared.IsSubsetOf(inMovies.SelectMany(p => p.Items)));
		Assert.True(shared.IsSubsetOf(inClassics.SelectMany(p => p.Items)));
		Assert.Equal(14, inMovies.Single(p => p.Id == "nixie:decade:movie:1990").Items.Count);
	}

	[Fact]
	public void TheSuggestedName_ReadsWhatIsOn()
	{
		var filter = new LibraryFilter();
		Assert.Equal("My collection", LibraryPresets.SuggestedName(filter));
		filter.Favorites = true;
		Assert.Equal("Favorites", LibraryPresets.SuggestedName(filter));
		filter.Unwatched = true;
		Assert.Equal("Unwatched favorites", LibraryPresets.SuggestedName(filter));
		filter.Years.Add("1999");
		Assert.Equal("Unwatched favorite 1999", LibraryPresets.SuggestedName(filter));
		filter.Unwatched = false;
		filter.Favorites = false;
		filter.OfficialRatings.Add("PG");
		Assert.Equal("1999, rated PG", LibraryPresets.SuggestedName(filter));
	}
}
