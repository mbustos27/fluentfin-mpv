using System.Text.Json;
using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.7 (upnav; the owner, 2026-11-21: an episode opened from Continue watching had no way back to its show): the item
/// page's way up. An episode shows its series and season, a season its series, from the item's own ids; one that does not
/// carry them is asked for once (TopNavigation.Trail).
/// </summary>
public sealed class ItemTrailTests
{
	private static readonly Guid SeriesId = Guid.NewGuid();
	private static readonly Guid SeasonId = Guid.NewGuid();

	private static readonly string Episode = typeof(EpisodeViewModel).FullName!;
	private static readonly string Season = typeof(SeasonViewModel).FullName!;
	private static readonly string Series = typeof(SeriesViewModel).FullName!;
	private static readonly string Movie = typeof(MovieViewModel).FullName!;

	/// <summary>An episode as Continue watching, search and Up next have it: its series' and season's ids, names and number.</summary>
	private static BaseItemDto FullEpisode(int? season = 3) => new()
	{
		Id = Guid.NewGuid(),
		Name = "The Siren",
		Type = BaseItemDto_Type.Episode,
		SeriesId = SeriesId,
		SeriesName = "What We Do in the Shadows",
		SeasonId = SeasonId,
		SeasonName = season is { } n ? $"Season {n}" : null,
		ParentIndexNumber = season,
		IndexNumber = 2,
	};

	// ---- the crumbs per item type

	[Fact]
	public void AnEpisode_ShowsItsSeries_ThenItsSeason_EachNamedForNarrator()
	{
		var trail = ItemTrail.From(FullEpisode());

		Assert.Equal(("What We Do in the Shadows", "Go to series What We Do in the Shadows"), (trail.Series?.Label, trail.Series?.Name));
		Assert.Equal((SeriesId, BaseItemDto_Type.Series), (trail.Series?.Target.Id, trail.Series?.Target.Type));
		Assert.Equal(("Season 3", "Go to Season 3"), (trail.Season?.Label, trail.Season?.Name));
		Assert.Equal((SeasonId, BaseItemDto_Type.Season), (trail.Season?.Target.Id, trail.Season?.Target.Type));
		// the season page gets its own trail from the target at once (its series), and shows nothing it was not given (no name)
		Assert.Equal((SeriesId, "What We Do in the Shadows", (string?)null), (trail.Season?.Target.SeriesId, trail.Season?.Target.SeriesName, trail.Season?.Target.Name));
		Assert.False(ItemTrail.NeedsLookup(FullEpisode()));
	}

	[Fact]
	public void EachCrumb_OpensItsOwnPage()
	{
		var trail = ItemTrail.From(FullEpisode());

		Assert.Equal(typeof(SeriesViewModel), GlobalCommands.PageFor(trail.Series!.Target.Type));
		Assert.Equal(typeof(SeasonViewModel), GlobalCommands.PageFor(trail.Season!.Target.Type));
	}

	[Theory]
	[InlineData(0, null, "Specials")]
	[InlineData(0, "Season 0", "Specials")]
	[InlineData(1, "Season 1", "Season 1")]
	[InlineData(12, "Staffel 12", "Season 12")]
	[InlineData(null, "The Lost Tapes", "The Lost Tapes")]
	[InlineData(null, "  ", "Season")]
	[InlineData(null, null, "Season")]
	public void TheSeasonCrumb_ReadsSeasonN_Specials_OrTheSeasonsOwnName(int? index, string? name, string expected) =>
		Assert.Equal(expected, ItemTrail.SeasonLabel(index, name));

	[Fact]
	public void ASpecial_ReadsSpecials()
	{
		var trail = ItemTrail.From(FullEpisode(season: 0));

		Assert.Equal(("Specials", "Go to Specials"), (trail.Season?.Label, trail.Season?.Name));
	}

	[Fact]
	public void ASeason_ShowsItsSeriesOnly()
	{
		var season = new BaseItemDto { Id = SeasonId, Type = BaseItemDto_Type.Season, IndexNumber = 3, SeriesId = SeriesId, SeriesName = "What We Do in the Shadows" };

		var trail = ItemTrail.From(season);

		Assert.Equal("Go to series What We Do in the Shadows", trail.Series?.Name);
		Assert.Null(trail.Season);
		Assert.False(ItemTrail.NeedsLookup(season));
	}

	[Theory]
	[InlineData(BaseItemDto_Type.Series)]
	[InlineData(BaseItemDto_Type.Movie)]
	[InlineData(BaseItemDto_Type.BoxSet)]
	[InlineData(BaseItemDto_Type.Audio)]
	[InlineData(null)]
	public void ASeries_AFilm_OrAnythingElse_ShowsNone_AndAsksNothing(BaseItemDto_Type? type)
	{
		// even one that names a series (a film in a box set has none; a series names itself in some servers' answers)
		var item = new BaseItemDto { Id = Guid.NewGuid(), Type = type, SeriesId = SeriesId, SeriesName = "Somewhere", SeasonId = SeasonId };

		Assert.True(ItemTrail.From(item).IsEmpty);
		Assert.False(ItemTrail.NeedsLookup(item));
	}

	[Fact]
	public void ALongName_IsKeptWhole_TheLineTrimsIt()
	{
		var name = string.Concat(Enumerable.Repeat("A Very Long Series Name ", 12)).Trim();
		var episode = FullEpisode();
		episode.SeriesName = "  " + name + " ";

		Assert.Equal(name, ItemTrail.From(episode).Series?.Label);
		Assert.Equal($"Go to series {name}", ItemTrail.From(episode).Series?.Name);
	}

	// ---- with and without the season's id

	[Fact]
	public async Task AnEpisodeCarryingItsIds_AsksTheServerNothing()
	{
		var asked = 0;

		var trail = await ItemTrail.ResolveAsync(FullEpisode(), _ => { asked++; return Task.FromResult<BaseItemDto?>(null); }, _ => { asked++; return Task.FromResult<IReadOnlyList<BaseItemDto>?>(null); });

		Assert.Equal(0, asked);
		Assert.Equal("Season 3", trail?.Season?.Label);
	}

	[Fact]
	public async Task AnEpisodeWithoutItsSeasonsId_ShowsItsSeries_ThenFindsTheSeasonByItsNumber()
	{
		var episode = FullEpisode();
		episode.SeasonId = null;
		var other = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Season, IndexNumber = 2, Name = "Season 2" };
		var third = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Season, IndexNumber = 3, Name = "Season 3" };
		var items = 0;
		var seasonsAsked = new List<Guid>();

		Assert.True(ItemTrail.NeedsLookup(episode));
		var meanwhile = ItemTrail.From(episode);
		Assert.Equal("What We Do in the Shadows", meanwhile.Series?.Label);
		Assert.Null(meanwhile.Season);

		var trail = await ItemTrail.ResolveAsync(
			episode,
			_ => { items++; return Task.FromResult<BaseItemDto?>(null); },
			id => { seasonsAsked.Add(id); return Task.FromResult<IReadOnlyList<BaseItemDto>?>([other, third]); });

		Assert.Equal(0, items); // it carries its series: only the seasons are asked
		Assert.Equal([SeriesId], seasonsAsked);
		Assert.Equal(("Season 3", third.Id), (trail?.Season?.Label, trail?.Season?.Target.Id));
	}

	[Fact]
	public async Task AnEpisodeWithoutItsSeasonsId_WhoseSeasonIsNotAmongTheSeasons_ShowsItsSeriesOnly()
	{
		var episode = FullEpisode();
		episode.SeasonId = null;

		var other = await ItemTrail.ResolveAsync(episode, _ => Task.FromResult<BaseItemDto?>(null),
			_ => Task.FromResult<IReadOnlyList<BaseItemDto>?>([new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Season, IndexNumber = 1 }]));

		Assert.Equal(("What We Do in the Shadows", null), (other?.Series?.Label, other?.Season));
	}

	[Fact]
	public async Task ARequestWithNoAnswer_GivesNoTrail_SoNothingIsKept()
	{
		// review item 1: the client answers null for a timeout or any error, never throws
		var episode = FullEpisode();
		episode.SeasonId = null;
		var bare = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Season, SeriesId = SeriesId };

		Assert.Null(await ItemTrail.ResolveAsync(episode, _ => Task.FromResult<BaseItemDto?>(null), _ => Task.FromResult<IReadOnlyList<BaseItemDto>?>(null)));
		Assert.Null(await ItemTrail.ResolveAsync(bare, _ => Task.FromResult<BaseItemDto?>(null), _ => Task.FromResult<IReadOnlyList<BaseItemDto>?>(null)));
	}

	[Fact]
	public async Task AnEpisodeOpenedWithItsIdOnly_IsAskedForOnce()
	{
		var bare = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Episode };
		var server = FullEpisode();
		server.Id = bare.Id;
		var asked = new List<Guid>();
		var seasons = 0;

		Assert.True(ItemTrail.NeedsLookup(bare));
		Assert.True(ItemTrail.From(bare).IsEmpty);
		var trail = await ItemTrail.ResolveAsync(bare, id => { asked.Add(id); return Task.FromResult<BaseItemDto?>(server); }, _ => { seasons++; return Task.FromResult<IReadOnlyList<BaseItemDto>?>(null); });

		Assert.Equal([bare.Id!.Value], asked);
		Assert.Equal(0, seasons);
		Assert.Equal(("What We Do in the Shadows", "Season 3"), (trail?.Series?.Label, trail?.Season?.Label));
	}

	[Fact]
	public void ASeasonWithoutItsSeriesName_ShowsTheSeriesCrumb_NamedPlainly()
	{
		var bare = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Season, SeriesId = SeriesId };

		var trail = ItemTrail.From(bare);

		// its series has no name to show: the crumb still leads there, named plainly
		Assert.Equal(("Series", "Go to the series", SeriesId), (trail.Series?.Label, trail.Series?.Name, trail.Series?.Target.Id));
	}

	// ---- the top bar's trail (what TrailEyebrow shows)

	[Fact]
	public void TheShownPage_HasItsTrail_ThePagesWithoutOne_None()
	{
		var top = Create(out var asked);
		var episode = FullEpisode();

		top.OnPageShown(Episode, episode);
		Assert.Equal(("What We Do in the Shadows", "Season 3"), (top.Trail.Series?.Label, top.Trail.Season?.Label));

		top.OnPageShown(Series, top.Trail.Series!.Target);
		Assert.True(top.Trail.IsEmpty);

		top.OnPageShown(Season, new BaseItemDto { Id = SeasonId, Type = BaseItemDto_Type.Season, SeriesId = SeriesId, SeriesName = "What We Do in the Shadows" });
		Assert.Equal(("What We Do in the Shadows", null), (top.Trail.Series?.Label, top.Trail.Season));

		top.OnPageShown(Movie, new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Movie });
		Assert.True(top.Trail.IsEmpty);
		top.OnPageShown(Episode, episode);
		top.OnPageShown(TopTabs.HomeKey, null);
		Assert.True(top.Trail.IsEmpty);
		// however it was reached: from Home, a library, the search results, a collection
		top.OnPageShown(TopTabs.LibraryKey, new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.CollectionFolder });
		top.OnPageShown(Episode, episode);
		Assert.Equal("Season 3", top.Trail.Season?.Label);
		Assert.Empty(asked);
	}

	[Fact]
	public void BackAfterACrumb_ShowsTheEpisodesTrailAgain()
	{
		var top = Create(out var asked);
		var episode = FullEpisode();

		top.OnPageShown(TopTabs.HomeKey, null, depth: 0);
		top.OnPageShown(Episode, episode, depth: 1);
		var crumbs = top.Trail;
		top.OnPageShown(Season, crumbs.Season!.Target, depth: 2);
		Assert.Equal(("What We Do in the Shadows", null), (top.Trail.Series?.Label, top.Trail.Season)); // the season's own trail, at once
		top.OnPageShown(Episode, episode, depth: 1, returning: true);

		Assert.Equal((crumbs.Series?.Name, crumbs.Season?.Name), (top.Trail.Series?.Name, top.Trail.Season?.Name));
		Assert.Equal(crumbs.Season?.Target.Id, top.Trail.Season?.Target.Id);
		Assert.Empty(asked);
	}

	[Fact]
	public async Task AnItemWithoutItsIds_IsAskedForOnce_ThenKept_UntilTheNextSignIn()
	{
		var top = Create(out var asked);
		var bare = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Episode };

		top.OnPageShown(Episode, bare);
		await top.TrailLookup;
		Assert.Equal("Season 3", top.Trail.Season?.Label);

		// Back to it, or opened again: no second request
		top.OnPageShown(Series, top.Trail.Series!.Target);
		top.OnPageShown(Episode, bare, returning: true);
		await top.TrailLookup;
		Assert.Equal("Season 3", top.Trail.Season?.Label);
		Assert.Equal([bare.Id!.Value], asked);

		// another user (or server) signs in: asked again
		top.Reset();
		Assert.True(top.Trail.IsEmpty);
		top.OnPageShown(Episode, bare);
		await top.TrailLookup;
		Assert.Equal(2, asked.Count);
	}

	[Fact]
	public async Task ALateTrail_ForAPageLeft_IsNotShown()
	{
		var top = Create(out _);
		var slow = new TaskCompletionSource<BaseItemDto?>();
		top.ItemOf = (_, _) => slow.Task;
		var bare = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Episode };

		top.OnPageShown(Episode, bare);
		var first = top.TrailLookup;
		top.OnPageShown(Movie, new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Movie });
		slow.SetResult(FullEpisode());
		await first;

		Assert.True(top.Trail.IsEmpty);
	}

	[Fact]
	public async Task ALookupWithNoAnswer_LeavesWhatTheItemCarries_AndIsAskedAgainNextTime()
	{
		// review item 1: the client answers null for a timeout or any error (JellyfinClient.GetItem), never throws
		var top = Create(out _);
		var asked = 0;
		top.ItemOf = (_, _) => { asked++; return Task.FromResult<BaseItemDto?>(null); };
		var season = new BaseItemDto { Id = SeasonId, Type = BaseItemDto_Type.Season, SeriesId = SeriesId };

		top.OnPageShown(Season, season);
		await top.TrailLookup;
		Assert.Equal(("Series", SeriesId), (top.Trail.Series?.Label, top.Trail.Series?.Target.Id));

		// the server answers now: shown the next time, not the first answer kept for the sign-in
		top.ItemOf = (id, _) => { asked++; return Task.FromResult<BaseItemDto?>(new BaseItemDto { Id = id, Type = BaseItemDto_Type.Season, SeriesId = SeriesId, SeriesName = "What We Do in the Shadows" }); };
		top.OnPageShown(Movie, new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Movie });
		top.OnPageShown(Season, season, returning: true);
		await top.TrailLookup;

		Assert.Equal(2, asked);
		Assert.Equal("What We Do in the Shadows", top.Trail.Series?.Label);
	}

	[Fact]
	public async Task ALookupThatEndsAfterSignOut_KeepsNothingForTheNextSignIn()
	{
		// review item 2: user A's answer, in after Reset (sign-out, another user), is neither kept nor shown
		var top = Create(out _);
		var slow = new TaskCompletionSource<BaseItemDto?>();
		var asked = 0;
		top.ItemOf = (_, _) => { asked++; return slow.Task; };
		var bare = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Episode };

		top.OnPageShown(Episode, bare);
		var first = top.TrailLookup;
		top.Reset();
		var answer = FullEpisode();
		answer.Id = bare.Id;
		slow.SetResult(answer);
		await first;
		Assert.True(top.Trail.IsEmpty);

		// the next sign-in opens the same episode: asked again (user B's own answer), not user A's kept one
		top.ItemOf = (_, _) => { asked++; return Task.FromResult<BaseItemDto?>(null); };
		top.OnPageShown(Episode, bare);
		await top.TrailLookup;

		Assert.Equal(2, asked);
		Assert.True(top.Trail.IsEmpty);
	}

	// ---- a crumb pressed (TrailEyebrow: OpenCrumb)

	[Fact]
	public void TwoPressesBeforeThePageIsShown_OpenItOnce_ThenBackOpensAgain()
	{
		// review item 4: a double click, or Enter held, sends the second press before the page it opened is shown
		var navigation = new Mock<INavigationServiceCore>();
		navigation.Setup(x => x.NavigateTo(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<bool>())).Returns(true);
		var top = Create(out _, navigation.Object);
		var episode = FullEpisode();
		top.OnPageShown(Episode, episode);
		var shown = top.Trail;

		Assert.True(top.OpenCrumb(shown, shown.Series!));
		Assert.False(top.OpenCrumb(shown, shown.Series!));
		Assert.False(top.OpenCrumb(shown, shown.Season!));
		navigation.Verify(x => x.NavigateTo(Series, shown.Series!.Target, false), Times.Once);
		navigation.Verify(x => x.NavigateTo(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<bool>()), Times.Once);

		// the series page shown, the episode's eyebrow still drawn a moment: nothing
		top.OnPageShown(Series, shown.Series!.Target);
		Assert.False(top.OpenCrumb(shown, shown.Season!));

		// Back: the episode's crumbs open again, once
		top.OnPageShown(Episode, episode, returning: true);
		var again = top.Trail;
		Assert.True(top.OpenCrumb(again, again.Season!));
		Assert.False(top.OpenCrumb(again, again.Season!));
		navigation.Verify(x => x.NavigateTo(Season, again.Season!.Target, false), Times.Once);
	}

	[Fact]
	public async Task BackToAnEpisodeWhoseTrailWasKept_OpensItsCrumbsAgain()
	{
		// a looked-up trail comes back as the same one (kept): pressed before, it still opens after Back
		var navigation = new Mock<INavigationServiceCore>();
		navigation.Setup(x => x.NavigateTo(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<bool>())).Returns(true);
		var top = Create(out _, navigation.Object);
		var bare = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Episode };
		top.OnPageShown(Episode, bare);
		await top.TrailLookup;
		var kept = top.Trail;

		Assert.True(top.OpenCrumb(kept, kept.Series!));
		top.OnPageShown(Series, kept.Series!.Target);
		top.OnPageShown(Episode, bare, returning: true);

		Assert.Same(kept, top.Trail);
		Assert.True(top.OpenCrumb(top.Trail, top.Trail.Series!));
	}

	[Fact]
	public void ACrumbThatIsNotTheShownTrails_OpensNothing()
	{
		var navigation = new Mock<INavigationServiceCore>();
		navigation.Setup(x => x.NavigateTo(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<bool>())).Returns(true);
		var top = Create(out _, navigation.Object);
		top.OnPageShown(Episode, FullEpisode());
		var stranger = ItemTrail.From(FullEpisode());

		Assert.False(top.OpenCrumb(stranger, stranger.Series!));
		Assert.False(top.OpenCrumb(top.Trail, stranger.Series!));
		navigation.Verify(x => x.NavigateTo(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<bool>()), Times.Never);
	}

	[Fact]
	public async Task ALookupThatThrows_LeavesWhatTheItemCarries()
	{
		var top = Create(out _);
		top.ItemOf = (_, _) => Task.FromException<BaseItemDto?>(new HttpRequestException("offline"));
		var season = new BaseItemDto { Id = SeasonId, Type = BaseItemDto_Type.Season, SeriesId = SeriesId };

		top.OnPageShown(Season, season);
		await top.TrailLookup;

		Assert.Equal(("Series", SeriesId), (top.Trail.Series?.Label, top.Trail.Series?.Target.Id));
	}

	[Fact]
	public async Task TheLibraryCrumb_KeepsItsOwnRule()
	{
		// the trail is beside the eyebrow, not instead of it: a page with a trail and no eyebrow (reached from its own library)
		var top = Create(out _);
		var shows = new BaseItemDto { Id = Guid.NewGuid(), Name = "Shows", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Tvshows };
		top.SetLibraries([shows]);
		top.LibraryOf = _ => Task.FromResult<Guid?>(shows.Id);

		top.OnPageShown(TopTabs.LibraryKey, shows);
		top.OnPageShown(Episode, FullEpisode());
		await top.EyebrowLookup;
		Assert.Null(top.Eyebrow);
		Assert.Equal("Season 3", top.Trail.Season?.Label);

		// from Home (Continue watching): "Shows › What We Do in the Shadows › Season 3"
		top.OnPageShown(TopTabs.HomeKey, null);
		top.OnPageShown(Episode, FullEpisode());
		await top.EyebrowLookup;
		Assert.Equal("Shows", top.Eyebrow?.Title);
		Assert.Equal(("What We Do in the Shadows", "Season 3"), (top.Trail.Series?.Label, top.Trail.Season?.Label));
	}

	private static TopNavigation Create(out List<Guid> asked, INavigationServiceCore? navigation = null)
	{
		var requests = new List<Guid>();
		asked = requests;
		var top = new TopNavigation(navigation ?? new Mock<INavigationServiceCore>().Object, new NoSettings(), NullLogger<TopNavigation>.Instance)
		{
			Profile = () => "server-a|user-1",
			ItemOf = (id, _) =>
			{
				requests.Add(id);
				var episode = FullEpisode();
				episode.Id = id;
				return Task.FromResult<BaseItemDto?>(episode);
			},
			SeasonsOf = (_, _) => Task.FromResult<IReadOnlyList<BaseItemDto>?>([]),
		};
		top.Reset();
		return top;
	}

	private sealed class NoSettings : ILocalSettingsService
	{
		private readonly Dictionary<string, JsonNode?> _values = [];

		public JsonNode? ReadSettingRaw(string key) => _values.GetValueOrDefault(key);

		public T ReadSetting<T>(string key, T defaultValue) => _values.TryGetValue(key, out var v) && v is not null ? v.Deserialize<T>()! : defaultValue;

		public void SaveSetting<T>(string key, T value) => _values[key] = JsonNode.Parse(JsonSerializer.Serialize(value));

		public void RemoveSetting(string key) => _values.Remove(key);

		public byte[] GetEntropyBytes() => [];
	}
}
