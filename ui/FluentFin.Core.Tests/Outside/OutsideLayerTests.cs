using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentFin.Core.Outside;
using FluentFin.Core.Settings;
using FluentFin.Core.Updates;
using static FluentFin.Core.Tests.Outside.OutsideKit;

namespace FluentFin.Core.Tests.Outside;

/// <summary>
/// 1.7 keyless: the shared outside layer (Core\Outside\OutsideSource.cs): the User-Agent, gzip, one request at a time, the
/// pause on 429 with no retry inside the request, a run of failures stopping early, Nixie's own refusal words, and the
/// harness's routing that never reaches a real host.
/// </summary>
public sealed class OutsideLayerTests
{
	private static readonly Uri BestPicture = new("https://query.wikidata.org/sparql?query=SELECT%20%3Fitem");

	[Fact]
	public void The_user_agent_names_Nixie_its_version_and_the_repository_from_the_updaters_one_constant()
	{
		Assert.Equal("Nixie/1.7.0 (https://github.com/mbustos27/nixie)", OutsideSources.UserAgent("1.7.0"));
		Assert.Equal(UpdateSource.RepositoryUrl, OutsideSources.ContactUrl);
		Assert.Contains(UpdateSource.RepositoryUrl, OutsideSources.UserAgent("1.7.0"), StringComparison.Ordinal);
		Assert.Equal("Nixie/0.0.0 (https://github.com/mbustos27/nixie)", OutsideSources.UserAgent(" "));
	}

	[Fact]
	public async Task A_request_carries_the_user_agent_and_asks_for_gzip_and_a_gzip_answer_is_undone()
	{
		HttpRequestMessage? seen = null;
		var handler = new FuncHandler(r =>
		{
			seen = r;
			var zipped = new MemoryStream();
			using (var gz = new GZipStream(zipped, CompressionLevel.Fastest, leaveOpen: true))
			{
				gz.Write(Encoding.UTF8.GetBytes("""{"head":{}}"""));
			}

			var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipped.ToArray()) };
			response.Content.Headers.ContentEncoding.Add("gzip");
			return response;
		});
		var source = Source(handler, new TestClock(Noon));

		var result = await source.GetAsync(BestPicture);

		Assert.True(result.Ok);
		Assert.Equal("""{"head":{}}""", result.Text);
		Assert.Equal("Nixie/1.7.0 (https://github.com/mbustos27/nixie)", string.Join(" ", seen!.Headers.GetValues("User-Agent")));
		Assert.Contains(seen.Headers.AcceptEncoding, x => x.Value == "gzip");
	}

	[Fact]
	public async Task A_caller_cannot_replace_the_user_agent()
	{
		HttpRequestMessage? seen = null;
		var source = Source(new FuncHandler(r => { seen = r; return Answer(HttpStatusCode.OK, "{}"); }), new TestClock(Noon));

		await source.GetAsync(BestPicture, new Dictionary<string, string> { ["User-Agent"] = "curl/8", ["Accept"] = "application/sparql-results+json" });

		Assert.Equal(source.UserAgent, string.Join(" ", seen!.Headers.GetValues("User-Agent")));
		Assert.Equal("application/sparql-results+json", string.Join(",", seen.Headers.GetValues("Accept")));
	}

	[Fact]
	public async Task A_429_pauses_the_source_for_Retry_After_and_is_never_retried_inside_the_request()
	{
		var clock = new TestClock(Noon);
		var handler = new FuncHandler(_ => Answer(HttpStatusCode.TooManyRequests, "", r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(90))));
		var source = Source(handler, clock);

		var first = await source.GetAsync(BestPicture);

		Assert.Equal(OutsideOutcome.RateLimited, first.Outcome);
		Assert.Equal(1, handler.Calls); // no retry inside the request
		Assert.Equal(Noon.AddSeconds(90), first.PausedUntil);
		Assert.Equal(Noon.AddSeconds(90), source.PausedUntil);
		Assert.Contains("asked Nixie to slow down", first.Message, StringComparison.Ordinal);

		var second = await source.GetAsync(BestPicture);
		Assert.Equal(OutsideOutcome.Paused, second.Outcome);
		Assert.Equal(1, handler.Calls); // nothing sent while paused

		clock.Now = Noon.AddSeconds(91);
		Assert.Null(source.PausedUntil);
		await source.GetAsync(BestPicture);
		Assert.Equal(2, handler.Calls);
	}

	[Fact]
	public async Task A_429_with_a_date_pauses_until_it_and_a_bare_429_pauses_the_default()
	{
		var clock = new TestClock(Noon);
		var dated = Source(new FuncHandler(_ => Answer(HttpStatusCode.TooManyRequests, "", r => r.Headers.RetryAfter = new RetryConditionHeaderValue(Noon.AddMinutes(10)))), clock);
		Assert.Equal(Noon.AddMinutes(10), (await dated.GetAsync(BestPicture)).PausedUntil);

		var bare = Source(new FuncHandler(_ => Answer(HttpStatusCode.TooManyRequests)), clock, OutsideSources.Trakt);
		Assert.Equal(Noon + OutsideSources.DefaultPause, (await bare.GetAsync(new Uri("https://api.trakt.tv/movies/trending"))).PausedUntil);

		Assert.Equal(OutsideSources.LongestPause, OutsideSource.PauseFor(new RetryConditionHeaderValue(TimeSpan.FromDays(30)), Noon));
		Assert.Equal(TimeSpan.FromSeconds(1), OutsideSource.PauseFor(new RetryConditionHeaderValue(Noon.AddHours(-1)), Noon));
	}

	[Fact]
	public async Task A_pause_on_one_source_leaves_the_others_alone()
	{
		var clock = new TestClock(Noon);
		var handler = new FuncHandler(r => r.RequestUri!.Host == "query.wikidata.org"
			? Answer(HttpStatusCode.TooManyRequests, "", x => x.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(5)))
			: Answer(HttpStatusCode.OK, "x"));
		var hub = new OutsideHub(new HttpClient(handler), OutsideRouting.Real, "1.7.0", new OutsideCache(TempFolder(), clock.Func), new MemoryStore(), clock.Func);

		await hub[OutsideSourceId.Wikidata].GetAsync(BestPicture);
		var tmdb = await hub[OutsideSourceId.TmdbExports].GetAsync(new Uri("https://files.tmdb.org/p/exports/collection_ids_11_20_2026.json.gz"));

		Assert.NotNull(hub[OutsideSourceId.Wikidata].PausedUntil);
		Assert.True(tmdb.Ok);
	}

	[Fact]
	public async Task One_request_at_a_time_per_source()
	{
		var running = 0;
		var most = 0;
		var handler = new FuncHandler(async (_, ct) =>
		{
			var now = Interlocked.Increment(ref running);
			InterlockedMax(ref most, now);
			await Task.Delay(40, ct);
			Interlocked.Decrement(ref running);
			return Answer(HttpStatusCode.OK, "{}");
		});
		var source = Source(handler, new TestClock(Noon));

		var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => source.GetAsync(BestPicture)));

		Assert.All(results, r => Assert.True(r.Ok));
		Assert.Equal(5, handler.Calls);
		Assert.Equal(1, most);
	}

	private static void InterlockedMax(ref int target, int value)
	{
		int seen;
		while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
		{
		}
	}

	[Fact]
	public async Task A_request_that_waited_behind_a_429_is_not_sent()
	{
		var release = new TaskCompletionSource();
		var handler = new FuncHandler(async (_, _) =>
		{
			await release.Task;
			return Answer(HttpStatusCode.TooManyRequests, "", r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(60)));
		});
		var source = Source(handler, new TestClock(Noon));

		var first = source.GetAsync(BestPicture);
		var second = source.GetAsync(BestPicture);
		release.SetResult();

		Assert.Equal(OutsideOutcome.RateLimited, (await first).Outcome);
		Assert.Equal(OutsideOutcome.Paused, (await second).Outcome);
		Assert.Equal(1, handler.Calls);
	}

	[Fact]
	public async Task No_answer_within_the_sources_timeout_is_timed_out_in_Nixies_words()
	{
		var quick = OutsideSources.Wikidata with { Timeout = TimeSpan.FromMilliseconds(100) };
		var source = Source(new FuncHandler(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return Answer(HttpStatusCode.OK); }), new TestClock(Noon), quick);

		var result = await source.GetAsync(BestPicture);

		Assert.Equal(OutsideOutcome.TimedOut, result.Outcome);
		Assert.StartsWith("Wikidata didn't answer within", result.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Cancelling_throws_rather_than_reporting_a_timeout()
	{
		using var cancel = new CancellationTokenSource();
		var source = Source(new FuncHandler(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return Answer(HttpStatusCode.OK); }), new TestClock(Noon));

		var request = source.GetAsync(BestPicture, cancellation: cancel.Token);
		cancel.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
	}

	[Theory]
	[InlineData(403, "Wikidata refused the request (403).")]
	[InlineData(401, "Wikidata refused the request (401).")]
	[InlineData(404, "Wikidata doesn't have that list right now (404).")]
	[InlineData(500, "Wikidata is having trouble (500).")]
	[InlineData(400, "Wikidata didn't give the list (400).")]
	public async Task A_refusal_says_so_in_Nixies_words_never_the_services_page(int status, string start)
	{
		var source = Source(new FuncHandler(_ => Answer((HttpStatusCode)status, "<html>Please set a user-agent and respect our robot policy</html>")), new TestClock(Noon));

		var result = await source.GetAsync(BestPicture);

		Assert.Equal(OutsideOutcome.Refused, result.Outcome);
		Assert.Equal(status, result.Status);
		Assert.StartsWith(start, result.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("<html>", result.Message, StringComparison.Ordinal);
		Assert.Contains("robot policy", result.Text, StringComparison.Ordinal); // kept for the parser and the log, not shown
	}

	[Fact]
	public async Task A_network_failure_says_so_in_Nixies_words()
	{
		var source = Source(new FuncHandler((_, _) => throw new HttpRequestException("No such host is known.")), new TestClock(Noon));

		var result = await source.GetAsync(BestPicture);

		Assert.Equal(OutsideOutcome.Failed, result.Outcome);
		Assert.Equal("Nixie couldn't reach Wikidata. Check your connection; Nixie keeps the lists it has and tries again later.", result.Message);
	}

	[Fact]
	public async Task A_run_of_failures_pauses_the_source_well_before_Wikidatas_30_errors_a_minute()
	{
		var clock = new TestClock(Noon);
		var handler = new FuncHandler(_ => Answer(HttpStatusCode.InternalServerError, "java.util.concurrent.TimeoutException"));
		var source = Source(handler, clock);

		var results = new List<OutsideResult>();
		for (var i = 0; i < 11; i++)
		{
			results.Add(await source.GetAsync(BestPicture));
		}

		Assert.Equal(OutsideSources.ErrorsInARow, handler.Calls);
		Assert.True(handler.Calls < 30);
		Assert.Equal(Noon + OutsideSources.ErrorPause, results[OutsideSources.ErrorsInARow - 1].PausedUntil);
		Assert.All(results.Skip(OutsideSources.ErrorsInARow), r => Assert.Equal(OutsideOutcome.Paused, r.Outcome));
	}

	[Fact]
	public async Task A_success_resets_the_run_of_failures()
	{
		var calls = 0;
		var source = Source(new FuncHandler(_ => ++calls % 3 == 0 ? Answer(HttpStatusCode.OK, "{}") : Answer(HttpStatusCode.BadGateway)), new TestClock(Noon));

		for (var i = 0; i < 9; i++)
		{
			await source.GetAsync(BestPicture);
		}

		Assert.Equal(9, calls);
		Assert.Null(source.PausedUntil);
	}

	[Fact]
	public async Task An_answer_bigger_than_the_limit_fails_instead_of_filling_memory()
	{
		var source = Source(new FuncHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new EndlessStream()) }), new TestClock(Noon));

		var result = await source.GetAsync(BestPicture);

		Assert.Equal(OutsideOutcome.Failed, result.Outcome);
	}

	private sealed class EndlessStream : Stream
	{
		private long _read;

		public override bool CanRead => true;

		public override bool CanSeek => false;

		public override bool CanWrite => false;

		public override long Length => throw new NotSupportedException();

		public override long Position { get => _read; set => throw new NotSupportedException(); }

		public override void Flush() { }

		public override int Read(byte[] buffer, int offset, int count)
		{
			Array.Fill(buffer, (byte)'a', offset, count);
			_read += count;
			return count;
		}

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

		public override void SetLength(long value) => throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}

	// ---------- routing: real hosts over https, or the harness's fake and nothing else

	[Fact]
	public void A_real_run_sends_https_only_and_never_credentials()
	{
		Assert.Equal(BestPicture, OutsideRouting.Real.Resolve(BestPicture, out _));
		Assert.Null(OutsideRouting.Real.Resolve(new Uri("http://query.wikidata.org/sparql"), out var why));
		Assert.Equal("the address isn't https", why);
		Assert.Null(OutsideRouting.Real.Resolve(new Uri("https://user:secret@example.org/sparql"), out why));
		Assert.Equal("the address has credentials in it", why);
	}

	[Fact]
	public void A_harness_run_maps_every_real_host_to_the_fake_server()
	{
		var routing = OutsideRouting.For("http://127.0.0.1:5123", harnessOn: true, releasePackage: false);

		Assert.True(routing.IsHarness);
		Assert.Equal("http://127.0.0.1:5123/__harness/outside/query.wikidata.org/sparql?query=SELECT%20%3Fitem", routing.Resolve(BestPicture, out _)!.AbsoluteUri);
		Assert.Equal("http://127.0.0.1:5123/__harness/outside/files.tmdb.org/p/exports/collection_ids_11_20_2026.json.gz",
			routing.Resolve(new Uri("https://files.tmdb.org/p/exports/collection_ids_11_20_2026.json.gz"), out _)!.AbsoluteUri);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("https://127.0.0.1:5123")]
	[InlineData("http://example.org:5123")]
	[InlineData("http://user:pw@127.0.0.1:5123")]
	[InlineData("not a url")]
	public void A_harness_run_without_a_good_fake_goes_to_a_closed_loopback_port_never_a_real_host(string? value)
	{
		var routing = OutsideRouting.For(value, harnessOn: true, releasePackage: false);

		Assert.Equal(OutsideRouting.ClosedOrigin, routing.HarnessOrigin);
		Assert.True(routing.Resolve(BestPicture, out _)!.IsLoopback);
	}

	[Fact]
	public void A_release_package_and_a_run_outside_the_harness_ask_the_real_hosts()
	{
		Assert.Same(OutsideRouting.Real, OutsideRouting.For("http://127.0.0.1:5123", harnessOn: true, releasePackage: true));
		Assert.Same(OutsideRouting.Real, OutsideRouting.For("http://127.0.0.1:5123", harnessOn: false, releasePackage: false));
	}

	[Fact]
	public async Task In_harness_mode_no_real_host_is_reachable_from_any_source()
	{
		// the recorder sends nothing that isn't on loopback: a request for a real host would fail the run
		var fake = new FuncHandler(_ => Answer(HttpStatusCode.OK, "{}"));
		var recorder = new OutsideRequestRecorder(fake) { AllowHost = uri => uri.IsLoopback };
		var clock = new TestClock(Noon);
		var routing = OutsideRouting.For("http://127.0.0.1:5123", harnessOn: true, releasePackage: false);
		var hub = new OutsideHub(new HttpClient(recorder), routing, "1.7.0", new OutsideCache(TempFolder(), clock.Func), new MemoryStore(), clock.Func);
		Uri[] real =
		[
			new("https://files.tmdb.org/p/exports/collection_ids_11_20_2026.json.gz"),
			new("https://query.wikidata.org/sparql?query=x"),
			new("https://query-next.wikidata.org/sparql?query=x"),
			new("https://qlever.cs.uni-freiburg.de/api/wikidata?query=x"),
			new("https://sparql.example.org/custom?query=x"),
			new("https://api.trakt.tv/movies/trending?limit=100"),
			new("https://api.themoviedb.org/3/trending/movie/week"),
		];

		foreach (var source in hub.Sources)
		{
			foreach (var uri in real)
			{
				Assert.True((await source.GetAsync(uri)).Ok);
			}
		}

		Assert.Equal(hub.Sources.Count() * real.Length, recorder.Requests.Count);
		Assert.All(recorder.Requests, r =>
		{
			Assert.True(r.Url.IsLoopback, r.Url.AbsoluteUri);
			Assert.StartsWith("http://127.0.0.1:5123/__harness/outside/", r.Url.AbsoluteUri, StringComparison.Ordinal);
		});
	}

	[Fact]
	public async Task In_harness_mode_an_answer_redirected_to_a_real_host_is_not_used()
	{
		var routing = OutsideRouting.For("http://127.0.0.1:5123", harnessOn: true, releasePackage: false);
		var handler = new FuncHandler(r =>
		{
			var response = Answer(HttpStatusCode.OK, "{}");
			response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://query.wikidata.org/sparql");
			return response;
		});
		var source = Source(handler, new TestClock(Noon), routing: routing);

		var result = await source.GetAsync(BestPicture);

		Assert.Equal(OutsideOutcome.Blocked, result.Outcome);
		Assert.Null(result.Body);
	}

	// ---------- off by default

	[Fact]
	public void Every_source_is_off_by_default_and_nothing_is_sent_for_one_that_is_off()
	{
		Assert.False(SettingKeys.LookUpTmdbExports.Default.Value);
		Assert.False(SettingKeys.LookUpWikidata.Default.Value);
		Assert.False(SettingKeys.LookUpTrakt.Default.Value);
		Assert.False(SettingKeys.LookUpTmdbTrending.Default.Value);
		var hub = new OutsideHub(new HttpClient(new FuncHandler(_ => throw new InvalidOperationException("sent"))), OutsideRouting.Real, "1.7.0",
			new OutsideCache(TempFolder()), new MemoryStore());
		Assert.All(Enum.GetValues<OutsideSourceId>(), id => Assert.False(hub.IsOn(id)));
		Assert.Equal(["LookUpTmdbExports", "LookUpWikidata", "LookUpTrakt", "LookUpTmdbTrending"], Enum.GetValues<OutsideSourceId>().Select(id => OutsideSettingKeys.For(id).Name));
	}

	[Fact]
	public void A_switch_saves_its_setting_and_says_so()
	{
		var store = new MemoryStore();
		var hub = new OutsideHub(new HttpClient(new FuncHandler(_ => Answer(HttpStatusCode.OK))), OutsideRouting.Real, "1.7.0", new OutsideCache(TempFolder()), store);
		var changed = new List<OutsideSourceId>();
		hub.SwitchChanged += changed.Add;
		var settings = new OutsideSourcesSettingsViewModel(hub);

		Assert.Equal("Off. Nothing is sent.", settings.Wikidata.Status);
		settings.Wikidata.IsOn = true;
		settings.Wikidata.IsOn = true;

		Assert.True(hub.IsOn(OutsideSourceId.Wikidata));
		Assert.Equal("true", store.Values["LookUpWikidata"]!.ToJsonString());
		Assert.Equal([OutsideSourceId.Wikidata], changed);
		Assert.False(settings.TmdbExports.IsOn);
		Assert.Equal("On. Nixie fetches its lists when they are needed and keeps them on this PC.", settings.Wikidata.Status);
	}

	[Fact]
	public async Task The_status_line_shows_the_last_refusal_in_Nixies_words_and_detaching_stops_following()
	{
		var clock = new TestClock(Noon);
		var store = new MemoryStore();
		var hub = new OutsideHub(new HttpClient(new FuncHandler(_ => Answer(HttpStatusCode.Forbidden))), OutsideRouting.Real, "1.7.0", new OutsideCache(TempFolder(), clock.Func), store, clock.Func);
		var settings = new OutsideSourcesSettingsViewModel(hub);
		settings.Wikidata.IsOn = true;
		var raised = 0;
		settings.Wikidata.PropertyChanged += (_, e) => raised += e.PropertyName == nameof(OutsideSourceSwitch.Status) ? 1 : 0;

		await hub[OutsideSourceId.Wikidata].GetAsync(BestPicture);

		Assert.Equal(1, raised);
		Assert.StartsWith("Wikidata refused the request (403).", settings.Wikidata.Status, StringComparison.Ordinal);
		settings.Detach();
		await hub[OutsideSourceId.Wikidata].GetAsync(BestPicture);
		Assert.Equal(1, raised);
	}
}
