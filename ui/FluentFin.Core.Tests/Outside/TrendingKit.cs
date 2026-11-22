using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using FluentFin.Core.Outside;
using FluentFin.Core.Settings;
using FluentFin.Core.SmartCollections;

namespace FluentFin.Core.Tests.Outside;

/// <summary>
/// 1.7 trending: Trakt and TMDb in one handler for the sources' tests, as strict as tools\ui-harness\fakeserver's fakes: Trakt
/// 403 without a User-Agent or the right Client ID, honest or placeholder page counts, limit clamped at 250, a bare 429 on
/// request; TMDb 401 without the right Bearer or with api_key in the address, 20 a page, poster paths in every result.
/// </summary>
internal sealed class TrendingFake : HttpMessageHandler
{
	public const string ClientId = "test-trakt-client-id-0123456789";
	public const string Token = "eyJhbGciOiJIUzI1NiJ9.eyJ0ZXN0IjoidHJlbmRpbmcifQ.dGVzdC1zaWduYXR1cmU";

	/// <summary>Nixie's own token as a release builds it in (TmdbAppToken); the fake takes it as it takes the user's.</summary>
	public const string AppToken = "eyJhbGciOiJIUzI1NiJ9.eyJuaXhpZSI6ImFwcCJ9.bml4aWUtYXBwLWtleQ";

	/// <summary>Refuse Nixie's own token with this status (a revoked app key), while the user's still works.</summary>
	public HttpStatusCode? RefuseAppToken { get; set; }

	private readonly ConcurrentQueue<HttpRequestMessage> _asked = new();

	/// <summary>The Trakt lists by name ("movies/trending"): their titles as (tmdb, imdb, tvdb, title).</summary>
	public Dictionary<string, List<(string? Tmdb, string? Imdb, string? Tvdb, string Title)>> Trakt { get; } = [];

	/// <summary>TMDb's trending week: "movie" and "tv", their TMDb ids.</summary>
	public Dictionary<string, List<(int Id, string Title)>> Tmdb { get; } = [];

	/// <summary>"honest" or "placeholder" (Page-Count 1 on every page).</summary>
	public string Counts { get; set; } = "honest";

	/// <summary>Answer every request with this status (a bare 429, a 502…) instead.</summary>
	public HttpStatusCode? Fail { get; set; }

	public IReadOnlyList<HttpRequestMessage> Asked => [.. _asked];

	/// <summary>Called with each request as it arrives, before it is answered (a test turns a switch off mid-list).</summary>
	public Action<HttpRequestMessage>? OnRequest { get; set; }

	public int Calls => _asked.Count;

	public static List<(string? Tmdb, string? Imdb, string? Tvdb, string Title)> Unowned(string prefix, int count, int from = 1) =>
		[.. Enumerable.Range(from, count).Select(i => ((string?)(9_000_000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), (string?)$"tt9{i:D6}", (string?)$"{8_000_000 + i}", $"{prefix} {i}"))];

	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		_asked.Enqueue(request);
		OnRequest?.Invoke(request);
		var uri = request.RequestUri!;
		if (Fail is { } fail)
		{
			return Task.FromResult(new HttpResponseMessage(fail) { Content = new ByteArrayContent([]) });
		}

		return Task.FromResult(uri.Host switch
		{
			"api.trakt.tv" => TraktAnswer(request, uri),
			"api.themoviedb.org" => TmdbAnswer(request, uri),
			_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not a host of this fake") },
		});
	}

	private HttpResponseMessage TraktAnswer(HttpRequestMessage request, Uri uri)
	{
		if (!request.Headers.UserAgent.Any() || !request.Headers.TryGetValues("trakt-api-key", out var keys) || keys.Single() != ClientId)
		{
			return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new ByteArrayContent([]) };
		}

		var name = uri.AbsolutePath.Trim('/');
		if (!Trakt.TryGetValue(name, out var titles))
		{
			return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) };
		}

		var query = HttpUtility.ParseQueryString(uri.Query);
		var wrapper = name.StartsWith("movies/", StringComparison.Ordinal) ? "movie" : "show";
		object Entry((string? Tmdb, string? Imdb, string? Tvdb, string Title) t)
		{
			var ids = new Dictionary<string, object?> { ["trakt"] = 1, ["slug"] = "x", ["imdb"] = t.Imdb, ["tmdb"] = t.Tmdb is null ? null : long.Parse(t.Tmdb, System.Globalization.CultureInfo.InvariantCulture) };
			if (wrapper == "show")
			{
				ids["tvdb"] = t.Tvdb is null ? null : long.Parse(t.Tvdb, System.Globalization.CultureInfo.InvariantCulture);
			}

			var body = new Dictionary<string, object?> { ["title"] = t.Title, ["year"] = 2026, ["ids"] = ids };
			return name.EndsWith("/popular", StringComparison.Ordinal) ? body : new Dictionary<string, object?> { ["watchers"] = 5, [wrapper] = body };
		}

		if (name == "movies/boxoffice")
		{
			return Json(JsonSerializer.Serialize(titles.Take(10).Select(Entry)));
		}

		var page = int.TryParse(query["page"], out var p) ? Math.Max(1, p) : 1;
		var limit = int.TryParse(query["limit"], out var l) ? Math.Clamp(l, 1, 250) : 10;
		var chunk = titles.Skip((page - 1) * limit).Take(limit).ToList();
		var response = Json(JsonSerializer.Serialize(chunk.Select(Entry)));
		var pageCount = chunk.Count == 0 ? 0 : Counts == "placeholder" ? 1 : (titles.Count + limit - 1) / limit;
		response.Headers.Add("X-Pagination-Page", page.ToString(System.Globalization.CultureInfo.InvariantCulture));
		response.Headers.Add("X-Pagination-Limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
		response.Headers.Add("X-Pagination-Page-Count", pageCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
		response.Headers.Add("X-Pagination-Item-Count", (chunk.Count == 0 ? 0 : Counts == "placeholder" ? chunk.Count : titles.Count).ToString(System.Globalization.CultureInfo.InvariantCulture));
		return response;
	}

	private HttpResponseMessage TmdbAnswer(HttpRequestMessage request, Uri uri)
	{
		var query = HttpUtility.ParseQueryString(uri.Query);
		var bearer = request.Headers.Authorization?.ToString();
		if (bearer == "Bearer " + AppToken && RefuseAppToken is { } refused)
		{
			return new HttpResponseMessage(refused) { Content = new StringContent("""{"status_code":7,"status_message":"Invalid API key: You must be granted a valid key.","success":false}""") };
		}

		if (query["api_key"] is not null || (bearer != "Bearer " + Token && bearer != "Bearer " + AppToken))
		{
			return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"status_code":7,"status_message":"Invalid API key: You must be granted a valid key.","success":false}""") };
		}

		var media = uri.AbsolutePath switch { "/3/trending/movie/week" => "movie", "/3/trending/tv/week" => "tv", _ => null };
		if (media is null || !Tmdb.TryGetValue(media, out var all))
		{
			return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"status_code":34}""") };
		}

		var page = int.TryParse(query["page"], out var p) ? p : 1;
		var results = all.Skip((page - 1) * 20).Take(20).Select(t => media == "movie"
			? (object)new { id = t.Id, title = t.Title, release_date = "2026-01-01", media_type = media, poster_path = $"/p{t.Id}.jpg", backdrop_path = $"/b{t.Id}.jpg" }
			: new { id = t.Id, name = t.Title, first_air_date = "2026-01-01", media_type = media, poster_path = $"/p{t.Id}.jpg", backdrop_path = $"/b{t.Id}.jpg" });
		return Json(JsonSerializer.Serialize(new { page, results, total_pages = 500, total_results = 10000 }));
	}

	private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
}

/// <summary>1.7 trending: a hub, its sources and the fake, for one test.</summary>
internal sealed class TrendingRig : IDisposable
{
	public TrendingRig(string? clientId = TrendingFake.ClientId, bool trakt = false, bool tmdb = false, string? token = null, OutsideRouting? routing = null, string? appToken = null)
	{
		Folder = OutsideKit.TempFolder();
		Store = new MemoryStore();
		Recorder = new OutsideRequestRecorder(Fake);
		Hub = new OutsideHub(new HttpClient(Recorder), routing ?? OutsideRouting.Real, "1.7.0", new OutsideCache(Path.Combine(Folder, "Outside"), Clock.Func), Store, Clock.Func);
		Hub.Noted += note => Notes.Add(note);
		Token = new TmdbToken(Store);
		if (token is not null)
		{
			Assert.Null(Token.Save(token));
		}

		Sources = new TrendingSources(Hub, Token, clientId, Clock.Func, appToken);
		if (trakt)
		{
			Store.SaveSetting(SettingKeys.LookUpTrakt.Name, true);
		}

		if (tmdb)
		{
			Store.SaveSetting(SettingKeys.LookUpTmdbTrending.Name, true);
		}
	}

	public string Folder { get; }

	public TestClock Clock { get; } = new(OutsideKit.Noon);

	public TrendingFake Fake { get; } = new();

	public MemoryStore Store { get; }

	public OutsideRequestRecorder Recorder { get; }

	public OutsideHub Hub { get; }

	public TmdbToken Token { get; }

	public TrendingSources Sources { get; }

	public ConcurrentBag<string> Notes { get; } = [];

	public IEnumerable<OutsideRequestRecord> To(string host) => Recorder.Requests.Where(r => r.Url.Host == host || r.Url.AbsolutePath.Contains("/" + host + "/", StringComparison.Ordinal));

	public void Dispose()
	{
		if (Directory.Exists(Folder))
		{
			Directory.Delete(Folder, recursive: true);
		}
	}

	/// <summary>An index entry with these ids (a library's film or show).</summary>
	public static IndexEntry Entry(string name, IndexKind kind, string? tmdb = null, string? imdb = null, string? tvdb = null) =>
		new() { Id = Guid.NewGuid(), Name = name, Kind = kind, Tmdb = tmdb, Imdb = imdb, Tvdb = tvdb };

	public static IndexSnapshot Index(params IndexEntry[] entries) => new([], entries, new Dictionary<Guid, SeriesPlays>(), false, OutsideKit.Noon);
}
