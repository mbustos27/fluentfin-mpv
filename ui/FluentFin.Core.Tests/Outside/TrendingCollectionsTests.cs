using FluentFin.Core.Outside;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Tests.Outside;

/// <summary>
/// 1.7 trending: the lists matched against the library on this PC (TMDb ids of the same kind, IMDb, TheTVDB), the Collections
/// page's rows (Trending, Popular, Most anticipated, Box office this week; empty ones left out), Home's "Trending in your
/// library" (both sources merged, only when there are titles), each in its list's order on the Collection page, and what
/// matched forgotten on sign-out. Trending's why line says each source's window (Trakt's API Use Policy: discovery lists
/// labelled accurately; Trakt's trending is right now, TMDb's the week).
/// </summary>
public sealed class TrendingCollectionsTests
{
	private static ListedTitle T(int rank, ListedKind kind, string? tmdb = null, string? imdb = null, string? tvdb = null) =>
		new(rank, kind, tmdb, imdb, tvdb, "t" + rank, 2026);

	private static TrendingListRead Read(OutsideSourceId source, TrendingList list, ListedKind kind, params ListedTitle[] titles) =>
		new(source, list, kind, OutsideKit.Noon, titles);

	private static readonly IndexEntry Matrix = TrendingRig.Entry("The Matrix", IndexKind.Movie, tmdb: "603", imdb: "tt0133093");
	private static readonly IndexEntry Dune = TrendingRig.Entry("Dune", IndexKind.Movie, tmdb: "438631");
	private static readonly IndexEntry Severance = TrendingRig.Entry("Severance", IndexKind.Series, tmdb: "95396", tvdb: "371980");
	private static readonly IndexEntry Andor = TrendingRig.Entry("Andor", IndexKind.Series, imdb: "tt9253284");
	// a show with the same TMDb number as The Matrix: a TV id, not a film's
	private static readonly IndexEntry SameNumber = TrendingRig.Entry("A show numbered 603", IndexKind.Series, tmdb: "603");

	private static IndexSnapshot Library => TrendingRig.Index(Matrix, Dune, Severance, Andor, SameNumber);

	[Fact]
	public void Matching_UsesTmdbIdsOfTheSameKind_ImdbAndTvdb()
	{
		var lists = new TrendingSnapshot(
		[
			Read(OutsideSourceId.Trakt, TrendingList.Trending, ListedKind.Movie, T(1, ListedKind.Movie, tmdb: "1"), T(2, ListedKind.Movie, tmdb: "603"), T(3, ListedKind.Movie, imdb: "tt0000001")),
			Read(OutsideSourceId.Trakt, TrendingList.Trending, ListedKind.Series, T(1, ListedKind.Series, tvdb: "371980"), T(2, ListedKind.Series, imdb: "tt9253284"), T(3, ListedKind.Series, tmdb: "1")),
		], 1);

		var made = TrendingBuilder.Build(Library, lists);

		Assert.Equal(["Trending films", "Trending shows"], made.Select(x => x.Title));
		Assert.Equal([Matrix.Id], made[0].ItemIds);                 // not the show numbered 603
		Assert.Equal([Severance.Id, Andor.Id], made[1].ItemIds);    // by TheTVDB, then IMDb
	}

	[Fact]
	public void TheRows_AreTrendingPopularAnticipatedAndBoxOffice_InTheListsOrder_WithTheirSourcesCredit()
	{
		var lists = new TrendingSnapshot(
		[
			Read(OutsideSourceId.Trakt, TrendingList.Trending, ListedKind.Movie, T(1, ListedKind.Movie, tmdb: "438631"), T(2, ListedKind.Movie, tmdb: "603")),
			Read(OutsideSourceId.TmdbTrending, TrendingList.Trending, ListedKind.Movie, T(1, ListedKind.Movie, tmdb: "603")),
			Read(OutsideSourceId.Trakt, TrendingList.Popular, ListedKind.Movie, T(1, ListedKind.Movie, tmdb: "603")),
			Read(OutsideSourceId.Trakt, TrendingList.Anticipated, ListedKind.Series, T(5, ListedKind.Series, tmdb: "95396")),
			Read(OutsideSourceId.Trakt, TrendingList.BoxOffice, ListedKind.Movie, T(1, ListedKind.Movie, tmdb: "438631")),
			Read(OutsideSourceId.Trakt, TrendingList.Popular, ListedKind.Series, T(1, ListedKind.Series, tmdb: "1")), // none owned: left out
		], 1);

		var made = TrendingBuilder.Build(Library, lists);

		Assert.Equal(["Trending films", "Popular films", "Most anticipated shows", "Box office this week"], made.Select(x => x.Title));
		Assert.Equal([SmartCollectionKind.Trending, SmartCollectionKind.Popular, SmartCollectionKind.Anticipated, SmartCollectionKind.BoxOffice], made.Select(x => x.Kind));
		// merged by best rank: The Matrix is 1st on TMDb, Dune 1st on Trakt (Trakt first on a tie)
		Assert.Equal([Dune.Id, Matrix.Id], made[0].ItemIds);
		Assert.Equal("Trakt: watched right now · TMDb: this week", made[0].Why);
		Assert.Equal("Popular on Trakt", made[1].Why);
		Assert.Equal("Most anticipated on Trakt", made[2].Why);
		Assert.Equal("The US box office this weekend, from Trakt", made[3].Why);
		Assert.All(made, x =>
		{
			Assert.True(x.HasOwnOrder);
			Assert.True(x.InListOrder);
			Assert.Equal("Made by Nixie · " + SmartCollectionKinds.Singular(x.Kind), x.Eyebrow);
		});
		Assert.Equal("Trending films, made by Nixie, trending, 2 films", made[0].AutomationName);
		Assert.Equal(["Trending", "Popular", "Most anticipated", "Box office this week"], TrendingBuilder.PageOrder.Select(SmartCollectionKinds.Plural));
	}

	[Fact]
	public void TrendingsWhyLine_SaysEachSourcesWindow_OnlyForTheSourcesIn()
	{
		// Trakt's /trending is what is watched right now, TMDb's trending the week: no "this week" over both (Trakt's API Use
		// Policy, 2026-10-20: "Label discovery lists accurately so users understand what they represent")
		Assert.Equal("Trakt: watched right now · TMDb: this week", TrendingBuilder.Windows([OutsideSourceId.TmdbTrending, OutsideSourceId.Trakt, OutsideSourceId.Trakt]));
		Assert.Equal("Trakt: watched right now", TrendingBuilder.Windows([OutsideSourceId.Trakt]));
		Assert.Equal("TMDb: this week", TrendingBuilder.Windows([OutsideSourceId.TmdbTrending]));

		static TrendingSnapshot Only(OutsideSourceId source) => new([Read(source, TrendingList.Trending, ListedKind.Movie, T(1, ListedKind.Movie, tmdb: "603"))], 1);
		Assert.Equal("Trakt: watched right now", TrendingBuilder.Build(Library, Only(OutsideSourceId.Trakt)).Single().Why);
		Assert.Equal("TMDb: this week", TrendingBuilder.Build(Library, Only(OutsideSourceId.TmdbTrending)).Single().Why);
		Assert.Equal("Trakt: watched right now", TrendingBuilder.HomeRow(Library, Only(OutsideSourceId.Trakt))!.Why);
		Assert.Equal("TMDb: this week", TrendingBuilder.HomeRow(Library, Only(OutsideSourceId.TmdbTrending))!.Why);
		// no title of the trending rows says "week"; the box office's does (Trakt's box office is the weekend's)
		var both = new TrendingSnapshot(
		[
			Read(OutsideSourceId.Trakt, TrendingList.Trending, ListedKind.Movie, T(1, ListedKind.Movie, tmdb: "603")),
			Read(OutsideSourceId.TmdbTrending, TrendingList.Trending, ListedKind.Series, T(1, ListedKind.Series, tmdb: "95396")),
			Read(OutsideSourceId.Trakt, TrendingList.BoxOffice, ListedKind.Movie, T(1, ListedKind.Movie, tmdb: "438631")),
		], 1);
		var titles = TrendingBuilder.Build(Library, both).Where(x => x.Kind == SmartCollectionKind.Trending).Select(x => x.Title)
			.Append(TrendingBuilder.HomeRow(Library, both)!.Title).Append(SmartCollectionKinds.Plural(SmartCollectionKind.Trending));
		Assert.All(titles, t => Assert.DoesNotContain("week", t, StringComparison.OrdinalIgnoreCase));
		Assert.Equal("Box office this week", SmartCollectionKinds.Plural(SmartCollectionKind.BoxOffice));
	}

	[Fact]
	public void HomesRow_MergesFilmsAndShowsOfBothSources_AndIsNoneWithoutTitles()
	{
		var lists = new TrendingSnapshot(
		[
			Read(OutsideSourceId.Trakt, TrendingList.Trending, ListedKind.Movie, T(3, ListedKind.Movie, tmdb: "603")),
			Read(OutsideSourceId.Trakt, TrendingList.Trending, ListedKind.Series, T(1, ListedKind.Series, tmdb: "95396")),
			Read(OutsideSourceId.TmdbTrending, TrendingList.Trending, ListedKind.Movie, T(2, ListedKind.Movie, tmdb: "438631")),
			Read(OutsideSourceId.Trakt, TrendingList.Popular, ListedKind.Movie, T(1, ListedKind.Movie, imdb: "tt9253284")), // not trending
		], 1);

		var row = TrendingBuilder.HomeRow(Library, lists)!;

		Assert.Equal("Trending on this server", row.Title);
		Assert.Equal([Severance.Id, Dune.Id, Matrix.Id], row.ItemIds);
		Assert.Equal("Trakt: watched right now · TMDb: this week", row.Why);
		Assert.Null(row.Only);
		Assert.Equal(3, row.Cover.Count);
		Assert.Equal("Trending on this server", new NixieHomeRowViewModel(row).Title);
		Assert.Equal("TRENDING ON THIS SERVER", new NixieHomeRowViewModel(row).SignTitle);

		Assert.Null(TrendingBuilder.HomeRow(Library, TrendingSnapshot.Empty));
		Assert.Null(TrendingBuilder.HomeRow(Library, new TrendingSnapshot([Read(OutsideSourceId.Trakt, TrendingList.Trending, ListedKind.Movie, T(1, ListedKind.Movie, tmdb: "1"))], 1)));
		Assert.Empty(TrendingBuilder.Build(Library, TrendingSnapshot.Empty));
		Assert.Empty(TrendingBuilder.Build(TrendingRig.Index(), lists));
	}

	[Fact]
	public void OnTheCollectionPage_ATrendingCollection_KeepsItsListsOrder_Numbered()
	{
		var lists = new TrendingSnapshot([Read(OutsideSourceId.Trakt, TrendingList.Trending, ListedKind.Movie, T(1, ListedKind.Movie, tmdb: "603"), T(2, ListedKind.Movie, tmdb: "438631"))], 1);
		var made = TrendingBuilder.Build(Library, lists).Single();
		var item = new NixieCollection(made).AsItem(favorite: false);
		Assert.Equal(CollectionBrowse.ListOrder, item.DisplayOrder);

		// the server answers in the order asked (GetItemsByIds); The Matrix (1999) stays before Dune (2021)... and after it when the list says so
		var dune = new BaseItemDto { Id = Dune.Id, Name = "Dune", ProductionYear = 2021 };
		var matrix = new BaseItemDto { Id = Matrix.Id, Name = "The Matrix", ProductionYear = 1999 };
		Assert.Equal(["Dune", "The Matrix"], CollectionBrowse.Ordered([dune, matrix], CollectionOrder.Collection, playlist: false, item.DisplayOrder).Select(x => x.Name));
		Assert.True(CollectionBrowse.PositionsMeanSomething(CollectionOrder.Collection, playlist: false, item.DisplayOrder));
		// other orders still sort as asked
		Assert.Equal(["The Matrix", "Dune"], CollectionBrowse.Ordered([dune, matrix], CollectionOrder.ReleaseDate, playlist: false, item.DisplayOrder).Select(x => x.Name));
		// a franchise keeps release order
		Assert.Equal("PremiereDate", new NixieCollection(made with { InListOrder = false }).AsItem(false).DisplayOrder);
	}

	[Fact]
	public void AListsOwnTitles_NeverNameACollection_OnlyTheirIdsMatch()
	{
		// the coordinator's rule (2026-11-21): free-text names from an outside list never become collection names unless a
		// curated list has them. Trakt's and TMDb's titles are matched by id only; the names are Nixie's own fixed words.
		const string Planted = "Heist Night [free text from a list]";
		var lists = new TrendingSnapshot(
		[
			Read(OutsideSourceId.Trakt, TrendingList.Trending, ListedKind.Movie, new ListedTitle(1, ListedKind.Movie, "603", null, null, Planted, 2026)),
			Read(OutsideSourceId.TmdbTrending, TrendingList.Trending, ListedKind.Series, new ListedTitle(1, ListedKind.Series, "95396", null, null, Planted, 2026)),
			Read(OutsideSourceId.Trakt, TrendingList.BoxOffice, ListedKind.Movie, new ListedTitle(1, ListedKind.Movie, "438631", null, null, Planted, 2026)),
		], 1);

		var made = TrendingBuilder.Build(Library, lists).Append(TrendingBuilder.HomeRow(Library, lists)!).ToList();

		Assert.Equal(4, made.Count);
		Assert.All(made, x =>
		{
			Assert.DoesNotContain("Heist", x.Title, StringComparison.Ordinal);
			Assert.DoesNotContain("Heist", x.Why, StringComparison.Ordinal);
			Assert.DoesNotContain("Heist", x.Id, StringComparison.Ordinal);
			Assert.Contains(x.Title, new[] { "Trending films", "Trending shows", "Box office this week", TrendingBuilder.HomeRowTitle });
			// the cards are the library's own titles (the server's names), never the list's
			Assert.All(x.Cover, c => Assert.DoesNotContain("Heist", c.Name, StringComparison.Ordinal));
		});
	}

	[Fact]
	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	public async Task WhatMatched_GoesWithItsList_WhenTheListAgesOut_WhileTheSourceRefuses()
	{
		// the review's #3: the saved matches outlived their list past Trakt's 24 hours while Trakt kept refusing
		using var rig = new TrendingRig(trakt: true);
		rig.Fake.Trakt["movies/trending"] = [("603", null, null, "The Matrix")];
		await rig.Sources.RefreshAsync();
		var collections = new TrendingCollections(rig.Sources);
		var library = Library;
		Assert.Single(collections.For(library));
		Assert.NotNull(collections.HomeRowFor(library));

		rig.Fake.Fail = System.Net.HttpStatusCode.Forbidden;
		rig.Clock.Now += OutsideSources.Trakt.MaxAge - TimeSpan.FromMinutes(1);
		await rig.Sources.RefreshAsync();
		Assert.Single(collections.For(library));            // refused, but the list is still within its day: it stays

		rig.Clock.Now += TimeSpan.FromMinutes(2);           // just past 24 hours
		Assert.Empty(collections.For(library));
		Assert.Null(collections.HomeRowFor(library));
		Assert.False(collections.HoldsMatches);
	}

	[Fact]
	public void TheKindsAreNumberedApart_SoASavedHomePageKeepsThem()
	{
		Assert.Equal(100, (int)SmartCollectionKind.Trending);
		Assert.Equal(103, (int)SmartCollectionKind.BoxOffice);
		Assert.DoesNotContain(SmartCollectionKind.Trending, SmartCollectionKinds.PageOrder);   // not a Settings → Collections switch, nor a library preset
		Assert.DoesNotContain(SmartCollectionKind.Trending, SmartCollectionKinds.Picks);
	}

	[Fact]
	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	public async Task WhatMatched_IsKeptForTheIndex_AndForgottenWhenItsUserSignsOut()
	{
		using var rig = new TrendingRig(trakt: true);
		rig.Fake.Trakt["movies/trending"] = [("603", null, null, "The Matrix")];
		await rig.Sources.RefreshAsync();
		var folder = OutsideKit.TempFolder();
		var messages = new System.Reactive.Subjects.Subject<Jellyfin.Sdk.WebSockets.IOutboundWebSocketMessage>();
		using var homeCache = new Services.HomeCache(Path.Combine(folder, "HomeCache"), messages, new CoreListLogger<Services.HomeCache>(), TimeProvider.System);
		var client = new Moq.Mock<Contracts.Services.IJellyfinClient>();
		client.SetupGet(x => x.BaseUrl).Returns("http://server");
		var user = Guid.NewGuid();
		client.SetupGet(x => x.UserId).Returns(user);
		using var index = new LibraryIndex(client.Object, homeCache, Path.Combine(folder, "LibraryIndex"), messages, new CoreListLogger<LibraryIndex>(), TimeProvider.System);
		var collections = new TrendingCollections(rig.Sources, index);

		var library = Library;
		var made = collections.For(library);
		Assert.Equal("Trending films", Assert.Single(made).Title);
		Assert.True(collections.HoldsMatches);
		Assert.Same(made, collections.For(library));   // kept, not matched again

		await homeCache.Forget("http://server", user);
		for (var i = 0; i < 50 && collections.HoldsMatches; i++)
		{
			await Task.Delay(20);
		}

		Assert.False(collections.HoldsMatches);
		Directory.Delete(folder, recursive: true);
	}

	[Fact]
	public async Task AMatchUnderWayAsTheUserSignsOut_IsNotKeptAfterIt()
	{
		// BH-170-q4 (the 1.7.0 bug hunt): the match ran outside the lock and was stored after sign-out's Forget, so "forgotten
		// when you sign out" wasn't strictly true. A session counter (as the episode crumbs' since ecf4ff6) drops it.
		using var rig = new TrendingRig(trakt: true);
		rig.Fake.Trakt["movies/trending"] = [("603", null, null, "The Matrix")];
		await rig.Sources.RefreshAsync();
		var collections = new TrendingCollections(rig.Sources);
		collections.Matching += collections.Forget;   // the sign-out lands while the lists are being matched

		var made = collections.For(Library);

		Assert.Equal("Trending films", Assert.Single(made).Title);   // the page that asked still gets its answer
		Assert.False(collections.HoldsMatches);                      // but nothing of it is kept

		collections.Matching -= collections.Forget;
		Assert.Single(collections.For(Library));                     // the next match, in the new session, is kept
		Assert.True(collections.HoldsMatches);
	}
}
