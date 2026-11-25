using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Outside;
using FluentFin.Core.Settings;

namespace FluentFin.Core.Tests.Outside;

/// <summary>A fact that runs only when the live trending check asks for it (<c>NIXIE_LIVE_TRENDING=1</c>, tools\live\live_trending_sources.ps1).</summary>
public sealed class LiveTrendingFactAttribute : FactAttribute
{
	public const string Variable = "NIXIE_LIVE_TRENDING";

	public LiveTrendingFactAttribute()
	{
		if (Environment.GetEnvironmentVariable(Variable) != "1")
		{
			Skip = $"the opt-in live check (tools\\live\\live_trending_sources.ps1 sets {Variable}=1)";
		}
	}
}

/// <summary>
/// Nixie 1.7.5 (stream fx1, BH-170-q2): the opt-in live check of Trakt and TMDb trending through the app's own requests: the
/// real <see cref="TrendingSources"/> on the real outside layer (<see cref="OutsideHub"/>: Nixie's User-Agent, gzip only,
/// redirects by hand, Trakt's Content-Type on an empty body), once, read-only. 1.7.0's script built its own requests, so the
/// app's GET had never met real Trakt. It may reach only api.trakt.tv and api.themoviedb.org; any other host fails the run.
/// <para>
/// The keys come from this Windows user's variables, never from an argument: Trakt's Client ID from
/// NIXIE_LIVE_TRAKT_CLIENT_ID, else NIXIE_TRAKT_CLIENT_ID (a release's); TMDb's token from NIXIE_LIVE_TMDB_TOKEN, else
/// NIXIE_TMDB_TOKEN (Nixie's app token). A source without one is skipped. Writes a summary (status codes, sizes, paging
/// headers, list counts, and a refusal's first words: never a key) to NIXIE_LIVE_TRENDING_OUT, with each list's first page cut
/// to its first 12 titles (the parsers' recorded fixtures). Then the contrast requests that prove the strict fakes'
/// refusals are real: Trakt with a made-up Client ID and without a User-Agent (403), TMDb without the token (401).
/// </para>
/// </summary>
[Trait("Category", "LiveTrending")]
public sealed class LiveTrendingSourcesTests
{
	private static readonly string[] Hosts = ["api.trakt.tv", "api.themoviedb.org"];

	private const int Keep = 12;

	private static string? Out => Environment.GetEnvironmentVariable("NIXIE_LIVE_TRENDING_OUT");

	// a variable of this process, else of this Windows user (the release build's are set there); never printed
	private static string? Secret(params string[] names) =>
		names.Select(n => Environment.GetEnvironmentVariable(n) ?? Environment.GetEnvironmentVariable(n, EnvironmentVariableTarget.User))
			.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

	// the app's version, as the release's User-Agent says it
	private static string Version()
	{
		var csproj = System.Xml.Linq.XDocument.Load(Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "FluentFin", "FluentFin.csproj"));
		return csproj.Descendants("Version").First(x => x.Parent?.Attribute("Condition")?.Value.Contains("== ''", StringComparison.Ordinal) != false).Value.Trim();
	}

	[LiveTrendingFact]
	public async Task Live_Trakt_and_TMDb_answer_the_apps_own_requests()
	{
		var clientId = Secret("NIXIE_LIVE_TRAKT_CLIENT_ID", "NIXIE_TRAKT_CLIENT_ID");
		var token = Secret("NIXIE_LIVE_TMDB_TOKEN", "NIXIE_TMDB_TOKEN");
		Assert.True(clientId is not null || token is not null, "no Trakt Client ID or TMDb token in this Windows user's variables");

		var recorder = new OutsideRequestRecorder(OutsideHub.CreateHandler())
		{
			AllowHost = uri => Hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) && uri.Scheme == Uri.UriSchemeHttps,
		};
		using var http = new HttpClient(recorder) { Timeout = Timeout.InfiniteTimeSpan };
		var store = new MemoryStore();
		store.SaveSetting(SettingKeys.LookUpTrakt, clientId is not null);
		store.SaveSetting(SettingKeys.LookUpTmdbTrending, token is not null);
		var hub = new OutsideHub(http, OutsideRouting.Real, Version(), new OutsideCache(OutsideKit.TempFolder()), store);
		var answers = new List<(OutsideSourceId Source, OutsideResult Result, long Ms)>();
		var clock = System.Diagnostics.Stopwatch.StartNew();
		foreach (var source in hub.Sources)
		{
			source.Answered += (s, result) =>
			{
				lock (answers)
				{
					answers.Add((s.Info.Id, result, clock.ElapsedMilliseconds));
					clock.Restart();
				}
			};
		}

		var summary = new JsonObject { ["userAgent"] = hub[OutsideSourceId.Trakt].UserAgent };
		var requests = new JsonArray();
		summary["requests"] = requests;
		try
		{
			// the app's own round: every list of each source that is on, as Home and the Collections page ask for them
			var sources = new TrendingSources(hub, null, clientId, tmdbAppToken: token);
			await sources.RefreshAsync();

			var asked = recorder.Requests;
			for (var i = 0; i < answers.Count; i++)
			{
				var (source, result, ms) = answers[i];
				var request = i < asked.Count ? asked[i] : null;
				var entry = new JsonObject
				{
					["what"] = request is null ? source.ToString() : $"{request.Method} {request.Url.Host}{request.Url.PathAndQuery}",
					["status"] = result.Status,
					["outcome"] = result.Outcome.ToString(),
					["bytes"] = result.Body?.Length ?? 0,
					["ms"] = ms,
					["contentType"] = result.Header("content-type"),
					["contentEncoding"] = result.Header("content-encoding"),
					["pagination"] = string.Join(", ", result.Headers.Where(h => h.Key.StartsWith("x-pagination-", StringComparison.Ordinal)).Select(h => $"{h.Key[13..]}={h.Value}")),
					// what the app sent with it: the header names, a body's length (Trakt's Content-Type rides on an empty one)
					["sent"] = request is null ? null : string.Join(", ", request.Headers.Select(h => h.Key).Distinct()),
					["sentBody"] = request?.Body.Length,
				};
				if (!result.Ok)
				{
					entry["answer"] = result.Text.Length > 300 ? result.Text[..300] : result.Text; // the source's own words
				}

				requests.Add(entry);
			}

			summary["lists"] = new JsonArray([.. sources.Current().Lists.Select(l => (JsonNode)new JsonObject
			{
				["source"] = l.Source.ToString(),
				["list"] = l.List.ToString(),
				["kind"] = l.Kind.ToString(),
				["titles"] = l.Titles.Count,
			})]);
			summary["problems"] = new JsonObject
			{
				["trakt"] = sources.Problem(OutsideSourceId.Trakt),
				["tmdb"] = sources.Problem(OutsideSourceId.TmdbTrending),
			};
			await Fixtures(asked, answers.Select(x => x.Result).ToList());

			// the contrast requests: each refusal the strict fakes model, from the real services
			var contrast = new JsonArray();
			summary["contrast"] = contrast;
			if (clientId is not null)
			{
				var made = await new OutsideSource(OutsideSources.Trakt, http, OutsideRouting.Real, Version())
					.GetAsync(new Uri(TraktLists.ApiBase + "/movies/trending?page=1&limit=1"), TraktLists.Headers(new string('0', 64)).ToDictionary(h => h.Key, h => h.Value));
				contrast.Add(new JsonObject { ["what"] = "Trakt with a made-up Client ID (403 expected)", ["status"] = made.Status });
				contrast.Add(await Bare(http, TraktLists.ApiBase + "/movies/trending?page=1&limit=1", "Trakt without a User-Agent (403 expected)",
					new() { ["trakt-api-version"] = TraktLists.ApiVersion, ["trakt-api-key"] = clientId }));
			}

			if (token is not null)
			{
				var none = await new OutsideSource(OutsideSources.TmdbTrending, http, OutsideRouting.Real, Version())
					.GetAsync(new Uri(TmdbLists.ApiBase + TmdbLists.PathAndQuery(ListedKind.Movie, 1)), new Dictionary<string, string> { ["Accept"] = "application/json" });
				contrast.Add(new JsonObject { ["what"] = "TMDb without the token (401 expected)", ["status"] = none.Status });
			}
		}
		finally
		{
			var text = summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
			// never a key in the summary. 1.7.5 review: a bool, not DoesNotContain, whose failure message prints what it found
			Assert.False(clientId is not null && text.Contains(clientId, StringComparison.Ordinal), "a key is in the summary");
			Assert.False(token is not null && text.Contains(token, StringComparison.Ordinal), "a token is in the summary");
			if (Out is { } folder)
			{
				Directory.CreateDirectory(folder);
				await File.WriteAllTextAsync(Path.Combine(folder, "live-trending-summary.json"), text);
			}
		}

		// only the two hosts, and every list the app asked came: Trakt took its GET with an empty body and a Content-Type
		Assert.All(recorder.Requests, r => Assert.Contains(r.Url.Host, Hosts));
		Assert.False(clientId is not null && recorder.Requests.Any(r => r.Url.OriginalString.Contains(clientId, StringComparison.Ordinal)), "a key is in an address");
		Assert.False(token is not null && recorder.Requests.Any(r => r.Url.OriginalString.Contains(token, StringComparison.Ordinal)), "a token is in an address");
		foreach (var entry in requests.OfType<JsonObject>().Where(e => ((string?)e["what"])?.StartsWith("GET api.", StringComparison.Ordinal) == true))
		{
			Assert.True((int?)entry["status"] == 200, $"{entry["what"]}: {entry["status"]} {entry["answer"]}");
		}
	}

	// one GET with no User-Agent at all (past the layer, which always names Nixie): the 403 the fake models
	private static async Task<JsonObject> Bare(HttpClient http, string url, string what, Dictionary<string, string> headers)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, url);
		foreach (var (name, value) in headers)
		{
			request.Headers.TryAddWithoutValidation(name, value);
		}

		request.Content = new ByteArrayContent([]);
		request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
		using var response = await http.SendAsync(request);
		return new JsonObject { ["what"] = what, ["status"] = (int)response.StatusCode };
	}

	// each list's first page as it came, cut to its first titles (Trakt's answer is a list; TMDb's has "results"), to Out
	private static async Task Fixtures(IReadOnlyList<OutsideRequestRecord> asked, IReadOnlyList<OutsideResult> results)
	{
		if (Out is not { } folder)
		{
			return;
		}

		Directory.CreateDirectory(folder);
		for (var i = 0; i < Math.Min(asked.Count, results.Count); i++)
		{
			var url = asked[i].Url;
			if (!results[i].Ok || url.Query.Contains("page=", StringComparison.Ordinal) && !url.Query.Contains("page=1", StringComparison.Ordinal))
			{
				continue;
			}

			JsonNode? body;
			try
			{
				body = JsonNode.Parse(results[i].Text);
			}
			catch (JsonException)
			{
				continue;
			}

			if (body is JsonArray list)
			{
				while (list.Count > Keep)
				{
					list.RemoveAt(list.Count - 1);
				}
			}
			else if (body?["results"] is JsonArray found)
			{
				while (found.Count > Keep)
				{
					found.RemoveAt(found.Count - 1);
				}
			}

			var name = (url.Host.StartsWith("api.trakt", StringComparison.Ordinal) ? "trakt" : "tmdb") + url.AbsolutePath.Replace('/', '-');
			await File.WriteAllTextAsync(Path.Combine(folder, name + ".json"), body?.ToJsonString() ?? "");
		}
	}
}
