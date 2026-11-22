using System.Text.Json;
using FluentFin.Core.Outside;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.7 trending: Trakt's and TMDb's list parsers, paging and refusals (pure functions). Trakt's
/// parsers read the answers the live check recorded on 2026-11-21 (tools\live\live_trending_sources.ps1, the first 12
/// titles of each list, with the paging headers Trakt sent), never typed by hand.
/// </summary>
public sealed class TrendingListsTests
{
	private static readonly string Fixtures = Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "FluentFin.Core.Tests", "Fixtures", "Outside", "trakt");
	private static string Recorded(string name) => File.ReadAllText(Path.Combine(Fixtures, name + ".json"));

	private static TraktLists.Paging RecordedPaging(string name)
	{
		using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, name + ".headers.json")));
		var headers = meta.RootElement.GetProperty("headers").EnumerateObject()
			.Select(p => new KeyValuePair<string, IEnumerable<string>>(p.Name, [p.Value.GetString() ?? ""]));
		return TraktLists.ParsePaging(headers);
	}

	private static TraktLists.Endpoint Endpoint(TrendingList list, ListedKind kind) => TraktLists.All.Single(e => e.List == list && e.Kind == kind);

	// ---- Trakt: the recorded answers

	[Theory]
	[InlineData("trakt-movies-trending", TrendingList.Trending, ListedKind.Movie)]
	[InlineData("trakt-shows-trending", TrendingList.Trending, ListedKind.Series)]
	[InlineData("trakt-movies-popular", TrendingList.Popular, ListedKind.Movie)]
	[InlineData("trakt-shows-popular", TrendingList.Popular, ListedKind.Series)]
	[InlineData("trakt-movies-anticipated", TrendingList.Anticipated, ListedKind.Movie)]
	[InlineData("trakt-shows-anticipated", TrendingList.Anticipated, ListedKind.Series)]
	[InlineData("trakt-movies-boxoffice", TrendingList.BoxOffice, ListedKind.Movie)]
	public void TraktsRecordedAnswers_GiveRankedTitles_WithLibraryIds(string fixture, TrendingList list, ListedKind kind)
	{
		var page = TraktLists.ParsePage(Recorded(fixture), Endpoint(list, kind));

		Assert.Equal(list == TrendingList.BoxOffice ? 10 : 12, page.Entries);
		Assert.NotEmpty(page.Titles);
		Assert.Equal(Enumerable.Range(1, page.Titles.Count), page.Titles.Select(t => t.Rank));
		Assert.All(page.Titles, t =>
		{
			Assert.Equal(kind, t.Kind);
			Assert.True(t.HasIds);
			Assert.False(string.IsNullOrWhiteSpace(t.Title));
			Assert.True(t.Tmdb is null || long.Parse(t.Tmdb) > 0);
			Assert.True(t.Imdb is null || (t.Imdb.StartsWith("tt", StringComparison.Ordinal) && t.Imdb[2..].All(char.IsAsciiDigit)));
			Assert.True(kind == ListedKind.Series || t.Tvdb is null);
		});
		// most titles carry a TMDb id; shows carry TVDB ids too (Jellyfin's ProviderIds match them directly)
		Assert.True(page.Titles.Count(t => t.Tmdb is not null) >= page.Titles.Count - 1);
		if (kind == ListedKind.Series)
		{
			Assert.Contains(page.Titles, t => t.Tvdb is not null);
		}
	}

	[Fact]
	public void TraktsTitles_KeepTheListsOrder_AndRankFromTheGivenPlace()
	{
		var json = Recorded("trakt-movies-trending");
		var first = TraktLists.ParsePage(json, Endpoint(TrendingList.Trending, ListedKind.Movie));
		var later = TraktLists.ParsePage(json, Endpoint(TrendingList.Trending, ListedKind.Movie), firstRank: 251);

		using var doc = JsonDocument.Parse(json);
		var tmdbInOrder = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("movie").GetProperty("ids").GetProperty("tmdb").GetRawText()).ToList();
		Assert.Equal(tmdbInOrder, first.Titles.Select(t => t.Tmdb));
		Assert.Equal(251, later.Titles[0].Rank);
	}

	[Fact]
	public void TraktsEmptyPage_IsNoTitles()
	{
		var page = TraktLists.ParsePage(Recorded("trakt-movies-trending-page2"), Endpoint(TrendingList.Trending, ListedKind.Movie));
		Assert.Empty(page.Titles);
		Assert.Equal(0, page.Entries);
	}

	[Theory]
	[InlineData("<html>Bad gateway</html>")]
	[InlineData("{\"error\": \"nope\"}")]
	[InlineData("")]
	public void ANonList_IsNixiesOwnMessage(string text)
	{
		var refused = Assert.Throws<ListUnreadable>(() => TraktLists.ParsePage(text, TraktLists.All[0]));
		Assert.Equal("Trakt answered something that isn't a list.", refused.Message);
	}

	[Fact]
	public void ATitleWithoutAnId_IsLeftOut_ButCountsAsAnEntry()
	{
		var page = TraktLists.ParsePage("""[{"watchers":1,"movie":{"title":"No ids","year":2020,"ids":{"trakt":5,"slug":"x","tmdb":null,"imdb":null}}},{"watchers":1,"movie":{"title":"Ok","ids":{"tmdb":7}}}]""",
			Endpoint(TrendingList.Trending, ListedKind.Movie));
		Assert.Equal(2, page.Entries);
		Assert.Equal(("7", 1), (page.Titles.Single().Tmdb, page.Titles.Single().Rank));
	}

	// ---- Trakt: requests and paging

	[Fact]
	public void TraktsRequests_AreTheFixedListAddresses_AtLimit250_AndTheBoxOfficeUnpaged()
	{
		Assert.Equal(
			["/movies/trending?page=1&limit=250", "/shows/trending?page=1&limit=250", "/movies/popular?page=1&limit=250", "/shows/popular?page=1&limit=250",
			 "/movies/anticipated?page=1&limit=250", "/shows/anticipated?page=1&limit=250", "/movies/boxoffice"],
			TraktLists.All.Select(e => TraktLists.PathAndQuery(e, 1)));
		Assert.Equal("/shows/popular?page=3&limit=250", TraktLists.PathAndQuery(Endpoint(TrendingList.Popular, ListedKind.Series), 3));
		Assert.Throws<ArgumentOutOfRangeException>(() => TraktLists.PathAndQuery(TraktLists.All[0], 0));
	}

	[Fact]
	public void TraktsHeaders_AreTheVersionTheClientIdAndJson()
	{
		var headers = TraktLists.Headers("abc123");
		Assert.Equal([("Content-Type", "application/json"), ("trakt-api-version", "2"), ("trakt-api-key", "abc123")], headers.Select(h => (h.Key, h.Value)));
		Assert.ThrowsAny<ArgumentException>(() => TraktLists.Headers(" "));
	}

	[Fact]
	public void Paging_FollowsAnHonestCount_AsTraktSentIt()
	{
		// movies/trending: 240 titles, one page; shows/trending: 679 titles, three pages (live, 2026-11-21)
		var movies = RecordedPaging("trakt-movies-trending");
		Assert.Equal(new TraktLists.Paging(1, 250, 1, 240), movies);
		Assert.False(TraktLists.ReadNext(Endpoint(TrendingList.Trending, ListedKind.Movie), 1, movies, onPage: 240, newTitles: 240));

		var shows = RecordedPaging("trakt-shows-trending");
		Assert.Equal(3, shows.PageCount);
		Assert.True(TraktLists.ReadNext(Endpoint(TrendingList.Trending, ListedKind.Series), 1, shows, 250, 250));
		Assert.True(TraktLists.ReadNext(Endpoint(TrendingList.Trending, ListedKind.Series), 2, shows with { Page = 2 }, 250, 250));
		Assert.False(TraktLists.ReadNext(Endpoint(TrendingList.Trending, ListedKind.Series), 3, shows with { Page = 3 }, 179, 179));
	}

	[Fact]
	public void Paging_ReadsOnPastAPlaceholderCount_UntilAnEmptyPage()
	{
		var popular = Endpoint(TrendingList.Popular, ListedKind.Movie);
		// a count of 1 on a full page can't be right: read on
		Assert.True(TraktLists.ReadNext(popular, 1, new TraktLists.Paging(1, 250, 1, 250), 250, 250));
		// no count at all, a full page: read on; a short one: the end
		Assert.True(TraktLists.ReadNext(popular, 2, new TraktLists.Paging(null, null, null, null), 250, 250));
		Assert.False(TraktLists.ReadNext(popular, 2, new TraktLists.Paging(null, null, null, null), 249, 249));
		// a smaller limit than asked (250 is a common cap, not a promise): full is judged by the answer's limit
		Assert.True(TraktLists.ReadNext(popular, 1, new TraktLists.Paging(1, 100, 1, 100), 100, 100));
		// the empty page past the end, as Trakt answered it (live: page 2 of trending, counts 0): stop
		var past = RecordedPaging("trakt-movies-trending-page2");
		Assert.Equal(new TraktLists.Paging(2, 250, 0, 0), past);
		Assert.False(TraktLists.ReadNext(popular, 2, past, 0, 0));
	}

	[Fact]
	public void Paging_StopsAtAnEmptyPage_AtTheCap_OnRepeats_AndNeverForTheBoxOffice()
	{
		var popular = Endpoint(TrendingList.Popular, ListedKind.Series);
		var inflated = new TraktLists.Paging(1, 250, 99, 24750);
		Assert.True(TraktLists.ReadNext(popular, 1, inflated, 250, 250));
		Assert.False(TraktLists.ReadNext(popular, 2, inflated, 0, 0));
		Assert.False(TraktLists.ReadNext(popular, TraktLists.MaxPages, inflated, 250, 250));
		Assert.False(TraktLists.ReadNext(popular, 1, inflated, 250, newTitles: 0));
		Assert.False(TraktLists.ReadNext(Endpoint(TrendingList.BoxOffice, ListedKind.Movie), 1, inflated, 10, 10));
	}

	[Fact]
	public void TheLimitClamp_IsWhatTraktSent()
	{
		// asked 300, Trakt used 250 (live, 2026-11-21): the fake clamps the same way
		Assert.Equal(250, RecordedPaging("trakt-movies-popular-limit300").Limit);
	}

	[Fact]
	public void Join_LeavesOutTitlesSeenOnAnEarlierPage_AndRanksFromOne()
	{
		var endpoint = Endpoint(TrendingList.Trending, ListedKind.Movie);
		var page = TraktLists.ParsePage(Recorded("trakt-movies-trending"), endpoint).Titles;
		var list = new List<ListedTitle>();

		Assert.Equal(page.Count, TraktLists.Join(list, page));
		Assert.Equal(0, TraktLists.Join(list, page));   // the same page again: nothing new
		Assert.Equal(page.Count, list.Count);
		Assert.Equal(Enumerable.Range(1, list.Count), list.Select(t => t.Rank));
	}

	[Fact]
	public void TheBoxOffice_IsAskedAgainAtItsMondayUpdate_OrADayLater_TheListsAfter6Hours()
	{
		var box = Endpoint(TrendingList.BoxOffice, ListedKind.Movie);
		var trending = TraktLists.All[0];
		var saturday = new DateTimeOffset(2026, 11, 21, 20, 0, 0, TimeSpan.Zero);

		Assert.Equal(saturday.AddHours(6), TraktLists.FreshUntil(trending, saturday));
		// Saturday evening: Monday's update is two days off, and no list is kept past a day (Trakt's maximum age)
		Assert.Equal(saturday.AddDays(1), TraktLists.FreshUntil(box, saturday));
		// Sunday evening: the Monday update comes first
		Assert.Equal(new DateTimeOffset(2026, 11, 23, 18, 0, 0, TimeSpan.Zero), TraktLists.FreshUntil(box, new DateTimeOffset(2026, 11, 22, 20, 0, 0, TimeSpan.Zero)));
		// Monday morning before it: that afternoon's
		Assert.Equal(new DateTimeOffset(2026, 11, 23, 18, 0, 0, TimeSpan.Zero), TraktLists.FreshUntil(box, new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero)));
		// Monday after it: a day later (the next update is a week off)
		Assert.Equal(new DateTimeOffset(2026, 11, 24, 19, 0, 0, TimeSpan.Zero), TraktLists.FreshUntil(box, new DateTimeOffset(2026, 11, 23, 19, 0, 0, TimeSpan.Zero)));
		Assert.Equal(new DateTimeOffset(2026, 11, 22, 20, 0, 0, TimeSpan.Zero), TmdbLists.FreshUntil(saturday));
	}

	[Theory]
	[InlineData(401, "Trakt turned this copy of Nixie away. Its registration may have lapsed; a Nixie update fixes that.")]
	[InlineData(403, "Trakt turned this copy of Nixie away. Its registration may have lapsed; a Nixie update fixes that.")]
	[InlineData(429, "Trakt asked Nixie to slow down. Nixie asks again later.")]
	[InlineData(502, "Trakt is having trouble right now. Nixie asks again later.")]
	[InlineData(522, "Trakt is having trouble right now. Nixie asks again later.")]
	[InlineData(412, "Trakt couldn't give its lists (error 412). Nixie asks again later.")]
	public void TraktsRefusals_AreNixiesWords(int status, string words) => Assert.Equal(words, TraktLists.Refusal(status));

	// ---- TMDb

	// TMDb's trending answer as its reference documents it (/3/trending/{movie,tv}/week). Not recorded: the live check's
	// TMDb leg needs NIXIE_LIVE_TMDB_TOKEN, which this PC doesn't have yet (an open item: record it when a token exists).
	private const string TmdbMovies = """
		{"page":1,"results":[
		 {"adult":false,"backdrop_path":"/b.jpg","id":1084242,"title":"Zootopia 2","original_title":"Zootopia 2","overview":"","poster_path":"/p.jpg","media_type":"movie","original_language":"en","genre_ids":[16],"popularity":512.3,"release_date":"2025-11-26","video":false,"vote_average":7.6,"vote_count":900},
		 {"adult":false,"id":0,"title":"Bad id","media_type":"movie"},
		 {"adult":false,"id":1311031,"title":"Without a date","media_type":"movie","release_date":""},
		 {"id":12,"name":"A person","media_type":"person"}],
		 "total_pages":500,"total_results":10000}
		""";

	[Fact]
	public void TmdbsAnswer_GivesRankedTitlesByTmdbId_AndIgnoresImagesAndOtherTypes()
	{
		var page = TmdbLists.ParsePage(TmdbMovies, ListedKind.Movie);

		Assert.Equal(500, page.TotalPages);
		Assert.Equal([("1084242", "Zootopia 2", (int?)2025, 1), ("1311031", "Without a date", null, 2)], page.Titles.Select(t => (t.Tmdb!, t.Title, t.Year, t.Rank)));
		Assert.All(page.Titles, t => Assert.Null(t.Imdb));
		var shows = TmdbLists.ParsePage("""{"page":2,"results":[{"id":1399,"name":"Show","first_air_date":"2011-04-17","media_type":"tv"}],"total_pages":3}""", ListedKind.Series, firstRank: 21);
		Assert.Equal(("1399", "Show", (int?)2011, 21, ListedKind.Series), (shows.Titles[0].Tmdb!, shows.Titles[0].Title, shows.Titles[0].Year, shows.Titles[0].Rank, shows.Titles[0].Kind));
		Assert.Throws<ListUnreadable>(() => TmdbLists.ParsePage("[]", ListedKind.Movie));
		Assert.Throws<ListUnreadable>(() => TmdbLists.ParsePage("{\"status_code\":7,\"status_message\":\"Invalid API key\"}", ListedKind.Movie));
	}

	[Fact]
	public void TmdbsRequests_AreTheTwoFixedAddresses_AndTheTokenOnlyABearer()
	{
		Assert.Equal("/3/trending/movie/week?page=1", TmdbLists.PathAndQuery(ListedKind.Movie, 1));
		Assert.Equal("/3/trending/tv/week?page=5", TmdbLists.PathAndQuery(ListedKind.Series, 5));
		Assert.DoesNotContain("api_key", TmdbLists.PathAndQuery(ListedKind.Movie, 1));
		Assert.Equal([("Authorization", "Bearer eyJ.a.b"), ("Accept", "application/json")], TmdbLists.Headers(" eyJ.a.b ").Select(h => (h.Key, h.Value)));
	}

	[Fact]
	public void TmdbPaging_ReadsFivePages_OrFewerWhenTheListEnds()
	{
		var full = new TmdbLists.Page([new ListedTitle(1, ListedKind.Movie, "1", null, null, "x", null)], 500);
		Assert.True(TmdbLists.ReadNext(1, full));
		Assert.False(TmdbLists.ReadNext(TmdbLists.Pages, full));
		Assert.False(TmdbLists.ReadNext(2, full with { TotalPages = 2 }));
		Assert.False(TmdbLists.ReadNext(1, full with { Titles = [] }));
		Assert.True(TmdbLists.ReadNext(1, full with { TotalPages = null }));
	}

	[Theory]
	[InlineData(401, "TMDb didn't accept your token. Check the API Read Access Token in Settings.")]
	[InlineData(429, "TMDb asked Nixie to slow down. Nixie asks again later.")]
	[InlineData(503, "TMDb is having trouble right now. Nixie asks again later.")]
	public void TmdbsRefusals_AreNixiesWords(int status, string words) => Assert.Equal(words, TmdbLists.Refusal(status));

	[Theory]
	[InlineData("eyJhbGciOiJIUzI1NiJ9.eyJuaXhpZSI6Imhhcm5lc3MifQ.bml4aWUtaGFybmVzcy1zaWduYXR1cmU", null)]
	[InlineData("", null)]
	[InlineData("0123456789abcdef0123456789ABCDEF", "That's TMDb's short API key. Paste the API Read Access Token (the long one) from the same page.")]
	[InlineData("hello world", "That doesn't look like a TMDb API Read Access Token.")]
	[InlineData("eyJ.a.b\r\nX-Injected: 1", "That doesn't look like a TMDb API Read Access Token.")]
	public void APastedToken_IsCheckedForItsShape(string token, string? problem) => Assert.Equal(problem, TmdbLists.TokenProblem(token));
}
