using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.SmartCollections.Builders;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;
using Xunit.Abstractions;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (stream text): the summaries' pass measured on a library-shaped fake server over HTTP, through the app's own
/// client: the survey's 25,877 films and 4,975 shows (the owner's index, counts only), each show's summary of TMDb's length
/// (about 250 to 450 characters; 8 % with none) and answers of Jellyfin's lean shape. What the pass asks (requests, bytes,
/// time, the memory at its peak), and that it asks nothing before the index's read is in and Home's first row is shown, so it
/// can't slow either. A fake answers at once (rule 2): its times are the app's own cost, not a real server's.
/// </summary>
[Collection(SessionInfoCollection.Name)]
public sealed class TextPassMeasureTests(ITestOutputHelper output) : IDisposable
{
	private const int Films = MicroGenreFixture.SurveyFilms;
	private const int Shows = MicroGenreFixture.SurveyShows;
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "nixie-text190m-" + Guid.NewGuid().ToString("N"));

	static TextPassMeasureTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public void Dispose() => TestFolder.Delete(_folder);

	[Fact]
	public async Task OnALibraryShapedServer_ThePassReadsTheShowsSummariesOnce_AfterTheIndexAndHomesFirstRow()
	{
		using var server = new LibraryServer(Films, Shows);
		server.Start();
		using var homeCache = new HomeCache(Path.Combine(_folder, "HomeCache"), new Subject<IOutboundWebSocketMessage>(), new CoreListLogger<HomeCache>(), TimeProvider.System);
		var client = new JellyfinClient(new CoreListLogger<JellyfinClient>(), Mock.Of<IObserver<IOutboundWebSocketMessage>>(), Mock.Of<IJumpListService>(), Mock.Of<IDeviceProfileFactory>(), Mock.Of<Settings.ISettings>(), homeCache);
		await client.Initialize(server.BaseUrl, new AuthenticationResult
		{
			AccessToken = "fake-token-text-measure-0123456789",
			ServerId = "fake-server",
			User = new UserDto { Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000001901"), Name = "alice" },
		});
		using var index = new LibraryIndex(client, homeCache, Path.Combine(_folder, "LibraryIndex"), new Subject<IOutboundWebSocketMessage>(), new CoreListLogger<LibraryIndex>(), TimeProvider.System)
		{
			PeopleWanted = () => false,
		};
		var firstRow = new TaskCompletionSource();
		var log = new CoreListLogger<TextPass>();
		using var pass = new TextPass(client, index, homeCache, Path.Combine(_folder, "LibraryIndex", "Summaries"), log, TimeProvider.System, new PauseGate(), shared: false)
		{
			Wanted = () => true,
			Catalogue = () => MicroGenres.Shipped,
			AfterFirstScreen = token => firstRow.Task.WaitAsync(token),
		};

		// the titles' read, with the pass waiting behind it
		var titlesWatch = Stopwatch.StartNew();
		Assert.NotNull(await index.GetAsync().WaitAsync(TimeSpan.FromMinutes(2)));
		await index.Settled.WaitAsync(TimeSpan.FromMinutes(2));
		var titlesTook = titlesWatch.Elapsed;
		var titles = server.Taken();
		await Task.Delay(300);
		Assert.Equal(0, server.TextRequests); // the index is in, Home's first row not yet: nothing asked
		Assert.Equal(Films + Shows, index.Current!.Entries.Count);

		// Home's first row: the pass, its memory sampled
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
		var baseline = GC.GetTotalMemory(false);
		var allocated = GC.GetTotalAllocatedBytes(precise: true);
		var peak = baseline;
		using var sampling = new CancellationTokenSource();
		var sampler = Task.Run(async () =>
		{
			while (!sampling.IsCancellationRequested)
			{
				peak = Math.Max(peak, GC.GetTotalMemory(false));
				await Task.Delay(5);
			}
		});
		var passWatch = Stopwatch.StartNew();
		firstRow.SetResult();
		await Task.Delay(50);
		await pass.Running.WaitAsync(TimeSpan.FromMinutes(2));
		var passTook = passWatch.Elapsed;
		await sampling.CancelAsync();
		await sampler;
		var passAllocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
		var retained = GC.GetTotalMemory(false) - baseline;
		var text = server.Taken();

		var found = pass.Current;
		Assert.Equal(Shows, found.Count); // every show once, no film
		Assert.Equal(0, server.TextFilmRequests);
		Assert.InRange(found.Unknown, Shows * 6 / 100, Shows * 10 / 100); // about 8 % with no summary
		Assert.True(found.Of("tv-workplace-comedies").Count > 0);
		Assert.Equal(1 + (Shows - IndexPages.FirstPageSize + IndexPages.PageSize - 1) / IndexPages.PageSize, text.Requests); // as the index's pages: 200, then a thousand at a time

		// Home: the categories made with the matches, against without
		var context = BuildContext.For(index.Current, DateTimeOffset.UtcNow);
		new MicroGenreBuilder(MicroGenres.Shipped) { Text = found }.Run(context); // warm: the titles collapsed, the posting lists made
		var without = Stopwatch.StartNew();
		var plain = new MicroGenreBuilder(MicroGenres.Shipped) { Text = TextMatches.Empty }.Run(context);
		without.Stop();
		var with = Stopwatch.StartNew();
		var matched = new MicroGenreBuilder(MicroGenres.Shipped) { Text = found }.Run(context);
		with.Stop();
		Assert.True(matched.Census.WaitForText < plain.Census.WaitForText, matched.Census.Note);

		// then a day's top-up: the new titles only, by id
		var mb = 1024.0 * 1024;
		output.WriteLine(string.Create(CultureInfo.InvariantCulture,
			$"titles' read: {Films + Shows} titles in {titles.Requests} requests, {titles.Bytes / mb:0.0} MB, {titlesTook.TotalSeconds:0.00} s"));
		output.WriteLine(string.Create(CultureInfo.InvariantCulture,
			$"summaries' pass: {found.Count} shows ({found.Known} with a summary, {found.Unknown} without) in {text.Requests} requests, {text.Bytes / mb:0.00} MB, {passTook.TotalSeconds:0.00} s"));
		output.WriteLine(string.Create(CultureInfo.InvariantCulture,
			$"memory: peak {(peak - baseline) / mb:0.0} MB above the index's, {passAllocated / mb:0.0} MB allocated, {retained / mb:0.0} MB kept after"));
		output.WriteLine(string.Create(CultureInfo.InvariantCulture,
			$"categories: {matched.Census.Made} made with the matches ({matched.Census.WaitForText} wait), {plain.Census.Made} without ({plain.Census.WaitForText} wait); the build {with.Elapsed.TotalMilliseconds:0} ms with, {without.Elapsed.TotalMilliseconds:0} ms without"));
		var saved = Directory.GetFiles(Path.Combine(_folder, "LibraryIndex", "Summaries"), "*.json").Single();
		output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"saved matches: {new FileInfo(saved).Length / 1024.0:0} KB"));
		foreach (var line in log.Entries)
		{
			output.WriteLine($"log: {line.Message}");
		}

		Assert.InRange(text.Bytes, 1, titles.Bytes / 4); // the lean pass of the shows: a fraction of the titles' read
		Assert.InRange(peak - baseline, 0, 300L * 1024 * 1024);
		Assert.InRange(new FileInfo(saved).Length, 1, 512 * 1024);
	}

	/// <summary>A Jellyfin server of a Movies and a Shows library, answering /Items as Jellyfin 12.4 shapes it (no score, no people).</summary>
	private sealed class LibraryServer : IDisposable
	{
		private static readonly Guid MoviesId = Guid.Parse("bbbbbbbb-1111-0000-0000-000000001901");
		private static readonly Guid ShowsId = Guid.Parse("bbbbbbbb-2222-0000-0000-000000001901");
		private static readonly string[] s_filmGenres = ["Drama", "Comedy", "Thriller", "Romance", "Action", "Horror", "Crime", "Adventure"];
		private static readonly string[] s_showGenres = ["Drama", "Comedy", "Animation", "Anime", "Action", "Crime", "Science Fiction", "Reality", "Mystery"];

		// TMDb-like sentences: a show's summary is three to five of them (about 250 to 450 characters)
		private static readonly string[] s_sentences =
		[
			"When a small-town sheriff finds a body in the lake, old secrets begin to surface.",
			"A dysfunctional family runs a struggling hotel on the coast of Maine.",
			"The employees of a paper company navigate office politics under a clueless boss.",
			"A starship crew explores the far reaches of space after a war that changed everything.",
			"Four best friends in their thirties navigate love, careers and life in New York City.",
			"A brilliant surgeon at a Seattle hospital struggles to balance her career and her family.",
			"After the apocalypse, a group of survivors searches for a safe place to call home.",
			"Detectives in a Baltimore precinct chase a drug lord across the city.",
			"A high school teacher turned manufacturer builds an empire, one bad decision at a time.",
			"Based on the true story of a disaster that shocked the world.",
			"An animated adventure through a magical kingdom full of talking animals.",
			"A reality competition where contestants race around the world for a cash prize.",
		];

		private readonly HttpListener _listener = new();
		private readonly int _films;
		private readonly int _shows;
		private readonly Dictionary<Guid, (bool Show, int I)> _byId = [];
		private readonly ConcurrentQueue<(bool Text, long Bytes)> _answers = new();
		private int _taken;
		private Task? _loop;

		public LibraryServer(int films, int shows)
		{
			_films = films;
			_shows = shows;
			for (var i = 0; i < films; i++)
			{
				_byId[Id(false, i)] = (false, i);
			}

			for (var i = 0; i < shows; i++)
			{
				_byId[Id(true, i)] = (true, i);
			}
		}

		public string BaseUrl { get; private set; } = "";

		public int TextRequests => _answers.Count(x => x.Text);

		public int TextFilmRequests;

		/// <summary>The requests and bytes answered since the last call.</summary>
		public (int Requests, long Bytes) Taken()
		{
			var all = _answers.ToArray();
			var mine = all.Skip(_taken).ToArray();
			_taken = all.Length;
			return (mine.Length, mine.Sum(x => x.Bytes));
		}

		private static Guid Id(bool show, int i) => Guid.Parse($"{i:D8}-{(show ? "2222" : "1111")}-0190-0000-000000000001");

		public void Start()
		{
			var probe = new TcpListener(IPAddress.Loopback, 0);
			probe.Start();
			var port = ((IPEndPoint)probe.LocalEndpoint).Port;
			probe.Stop();
			BaseUrl = $"http://localhost:{port}";
			_listener.Prefixes.Add(BaseUrl + "/");
			_listener.Start();
			_loop = Task.Run(Serve);
		}

		private async Task Serve()
		{
			while (_listener.IsListening)
			{
				HttpListenerContext context;
				try
				{
					context = await _listener.GetContextAsync();
				}
				catch (Exception) when (!_listener.IsListening)
				{
					return;
				}
				catch (HttpListenerException)
				{
					return;
				}

				_ = Task.Run(() => Answer(context));
			}
		}

		private async Task Answer(HttpListenerContext context)
		{
			var path = context.Request.Url!.AbsolutePath;
			var query = System.Web.HttpUtility.ParseQueryString(context.Request.Url.Query);
			var (status, body, text) = path.ToLowerInvariant() switch
			{
				"/items" => Items(query),
				"/userviews" => (200, Views(), false),
				"/plugins" => (200, "[]", false),
				"/sessions/capabilities/full" => (204, "", false),
				_ => (404, "", false),
			};
			try
			{
				context.Response.StatusCode = status;
				var bytes = Encoding.UTF8.GetBytes(body);
				if (path.Equals("/items", StringComparison.OrdinalIgnoreCase))
				{
					_answers.Enqueue((text, bytes.Length));
				}

				if (bytes.Length > 0)
				{
					context.Response.ContentType = "application/json";
					context.Response.ContentLength64 = bytes.Length;
					await context.Response.OutputStream.WriteAsync(bytes);
				}

				context.Response.Close();
			}
			catch (Exception)
			{
				// the client went away
			}
		}

		private static string[] Values(NameValueCollection q, string name) => [.. (q.GetValues(name) ?? []).SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries))];

		private static string Views() => $$"""
			{"Items":[{"Name":"Movies","ServerId":"fake","Id":"{{MoviesId:N}}","Type":"CollectionFolder","CollectionType":"movies","IsFolder":true},
			{"Name":"Shows","ServerId":"fake","Id":"{{ShowsId:N}}","Type":"CollectionFolder","CollectionType":"tvshows","IsFolder":true}],"TotalRecordCount":2,"StartIndex":0}
			""";

		private (int, string, bool) Items(NameValueCollection q)
		{
			var types = Values(q, "includeItemTypes");
			if (types.Contains("Episode", StringComparer.OrdinalIgnoreCase))
			{
				return (200, """{"Items":[],"TotalRecordCount":0,"StartIndex":0}""", false);
			}

			var fields = Values(q, "fields").ToHashSet(StringComparer.OrdinalIgnoreCase);
			var text = fields.Contains("Overview");
			var ids = Values(q, "ids");
			var start = int.TryParse(q["startIndex"], out var s) ? s : 0;
			var limit = int.TryParse(q["limit"], out var l) ? l : int.MaxValue;
			List<(bool Show, int I)> page;
			int total;
			if (ids.Length > 0)
			{
				page = [.. ids.Select(Guid.Parse).Where(_byId.ContainsKey).Select(x => _byId[x])];
				total = page.Count;
			}
			else if (q["parentId"] is { } parent)
			{
				var show = Guid.Parse(parent) == ShowsId;
				if (text && !show)
				{
					Interlocked.Increment(ref TextFilmRequests);
				}

				total = show ? _shows : _films;
				page = [.. Enumerable.Range(start, Math.Max(0, Math.Min(limit, total - start))).Select(i => (show, i))];
			}
			else
			{
				total = _films + _shows; // the index's check: how many, and the newest
				page = [(false, 0)];
			}

			var json = new StringBuilder(page.Count * (text ? 900 : 1300));
			json.Append("{\"Items\":[");
			for (var k = 0; k < page.Count; k++)
			{
				if (k > 0)
				{
					json.Append(',');
				}

				Item(json, page[k].Show, page[k].I, text ? Lean.Text : fields.Count > 0 ? Lean.Index : Lean.Bare);
			}

			json.Append(CultureInfo.InvariantCulture, $"],\"TotalRecordCount\":{total},\"StartIndex\":{start}}}");
			return (200, json.ToString(), text);
		}

		private enum Lean
		{
			Bare,
			Index,
			Text,
		}

		// an item as Jellyfin 12.4 answers it: its base fields, then what the request asked for
		private static void Item(StringBuilder json, bool show, int i, Lean shape)
		{
			var id = Id(show, i);
			var name = show ? $"A Show Title of Real Length {i}" : $"A Film Title of Real Length {i}";
			var year = 1950 + (i % 76);
			json.Append(CultureInfo.InvariantCulture,
				$"{{\"Name\":{JsonSerializer.Serialize(name)},\"ServerId\":\"fake-server-0123456789abcdef\",\"Id\":\"{id:N}\",\"PremiereDate\":\"{year}-03-01T00:00:00.0000000Z\",\"OfficialRating\":\"TV-14\",\"ChannelId\":null,\"CommunityRating\":{6 + (i % 30) / 10.0},\"RunTimeTicks\":{(show ? 13_200_000_000L : 64_800_000_000L)},\"ProductionYear\":{year},\"IsFolder\":{(show ? "true" : "false")},\"Type\":\"{(show ? "Series" : "Movie")}\",");
			if (show)
			{
				json.Append("\"Status\":\"Ended\",\"AirDays\":[],");
			}

			if (shape == Lean.Text)
			{
				json.Append("\"ImageTags\":{},\"BackdropImageTags\":[],\"LocationType\":\"FileSystem\",\"MediaType\":\"Unknown\"");
				if (show && i % 12 != 5)
				{
					var random = new Random(i);
					var sentences = Enumerable.Range(0, 3 + random.Next(3)).Select(_ => s_sentences[random.Next(s_sentences.Length)]);
					json.Append(CultureInfo.InvariantCulture, $",\"Overview\":{JsonSerializer.Serialize(string.Join(' ', sentences))}");
					json.Append(i % 3 == 0 ? ",\"Taglines\":[\"Every road ends somewhere.\"]" : ",\"Taglines\":[]");
				}
				else
				{
					json.Append(",\"Taglines\":[]");
				}

				json.Append('}');
				return;
			}

			var genres = show ? s_showGenres : s_filmGenres;
			json.Append(CultureInfo.InvariantCulture,
				$"\"ImageTags\":{{\"Primary\":\"{i:x8}0123456789abcdef01234567\"}},\"BackdropImageTags\":[\"{i:x8}fedcba9876543210fedcba98\"],\"ImageBlurHashes\":{{\"Primary\":{{\"{i:x8}0123456789abcdef01234567\":\"WEHV6nWB2yk8pyo0adR*.7kCMdnjS#M|%1%2Sis.slNHRjt7ayof\"}}}},\"LocationType\":\"FileSystem\",\"MediaType\":\"{(show ? "Unknown" : "Video")}\",");
			json.Append(CultureInfo.InvariantCulture,
				$"\"UserData\":{{\"PlaybackPositionTicks\":0,\"PlayCount\":0,\"IsFavorite\":false,\"Played\":{(i % 4 == 0 ? "true" : "false")},\"Key\":\"{i}\",\"ItemId\":\"{id:N}\"}}");
			if (shape == Lean.Index)
			{
				json.Append(CultureInfo.InvariantCulture,
					$",\"SortName\":\"{name.ToLowerInvariant()}\",\"DateCreated\":\"2024-01-01T00:00:00.0000000Z\",\"Genres\":[\"{genres[i % genres.Length]}\",\"{genres[(i / 3) % genres.Length]}\"],\"Tags\":[\"based on novel or book\",\"murder\",\"small town\"],\"Studios\":[{{\"Name\":\"Studio Name {i % 300}\",\"Id\":\"{Guid.Empty:N}\"}}],\"ProductionLocations\":[{(show ? "" : "\"United States of America\"")}],\"ProviderIds\":{{\"Tmdb\":\"{100000 + i}\",\"Imdb\":\"tt{1000000 + i}\"}}");
			}

			json.Append('}');
		}

		public void Dispose()
		{
			try
			{
				_listener.Stop();
				_listener.Close();
			}
			catch (ObjectDisposedException)
			{
			}

			_loop?.Wait(TimeSpan.FromSeconds(2));
		}
	}
}
