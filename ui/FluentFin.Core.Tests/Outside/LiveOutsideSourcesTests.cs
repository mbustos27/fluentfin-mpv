using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentFin.Core.Outside;

namespace FluentFin.Core.Tests.Outside;

/// <summary>A fact that runs only when the live check asks for it (<c>NIXIE_LIVE_OUTSIDE=1</c>, tools\live\live_outside_sources.ps1).</summary>
public sealed class LiveOutsideFactAttribute : FactAttribute
{
	public const string Variable = "NIXIE_LIVE_OUTSIDE";

	public LiveOutsideFactAttribute()
	{
		if (Environment.GetEnvironmentVariable(Variable) != "1")
		{
			Skip = $"the opt-in live check (tools\\live\\live_outside_sources.ps1 sets {Variable}=1)";
		}
	}
}

/// <summary>
/// 1.7 keyless: the opt-in live check (plan §6 phase 2): Nixie's real request shapes, through the real outside layer, to the
/// real services, once. It may reach only files.tmdb.org (yesterday's collection and keyword files) and Wikidata's query
/// endpoints (query.wikidata.org, query-next.wikidata.org), with Nixie's User-Agent; any other host fails the run. Writes
/// a summary (status codes, sizes, row counts: nothing else) to NIXIE_LIVE_OUTSIDE_OUT, and, when NIXIE_LIVE_OUTSIDE_FIXTURES
/// names a folder, the trimmed real answers the parser tests read. Never a whole export file.
/// </summary>
[Trait("Category", "LiveOutside")]
public sealed class LiveOutsideSourcesTests
{
	private static readonly string[] Hosts = ["files.tmdb.org", "query.wikidata.org", "query-next.wikidata.org"];

	// lines kept from the export files beside the first ones: franchises and keywords the tests name
	private static readonly int[] KeptCollections = [10, 119, 645, 1241, 1733, 9485, 86311, 131292];
	private static readonly string[] KeptKeywords = ["heist", "time travel", "slasher", "based on novel or book", "superhero", "dystopia", "christmas"];

	private readonly List<JsonObject> _summary = [];

	private static string? Out => Environment.GetEnvironmentVariable("NIXIE_LIVE_OUTSIDE_OUT");

	private static string? Fixtures => Environment.GetEnvironmentVariable("NIXIE_LIVE_OUTSIDE_FIXTURES");

	[LiveOutsideFact]
	public async Task Live_TMDb_exports_and_Wikidata_answer_as_the_fakes_model()
	{
		var recorder = new OutsideRequestRecorder(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None })
		{
			AllowHost = uri => Hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) && uri.Scheme == Uri.UriSchemeHttps,
		};
		var http = new HttpClient(recorder) { Timeout = Timeout.InfiniteTimeSpan };
		var cache = new OutsideCache(OutsideKit.TempFolder());
		var hub = new OutsideHub(http, OutsideRouting.Real, "1.7.0", cache, new MemoryStore());
		try
		{
			await Tmdb(hub);
			await Wikidata(hub, http);
		}
		finally
		{
			var summary = new JsonObject { ["requests"] = new JsonArray([.. _summary]) };
			if (Out is { } folder)
			{
				Directory.CreateDirectory(folder);
				await File.WriteAllTextAsync(Path.Combine(folder, "live-outside-summary.json"), summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
			}
		}

		Assert.All(recorder.Requests, r => Assert.Contains(r.Url.Host, Hosts));
	}

	private void Note(string what, OutsideResult result, long ms, JsonObject? extra = null)
	{
		var entry = new JsonObject
		{
			["what"] = what,
			["status"] = result.Status,
			["outcome"] = result.Outcome.ToString(),
			["bytes"] = result.Body?.Length ?? 0,
			["ms"] = ms,
			["contentType"] = result.Header("content-type"),
			["contentEncoding"] = result.Header("content-encoding"),
		};
		foreach (var (k, v) in extra ?? new JsonObject())
		{
			entry[k] = v?.DeepClone();
		}

		_summary.Add(entry);
	}

	private async Task Tmdb(OutsideHub hub)
	{
		var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
		foreach (var kind in new[] { TmdbExports.Collections, TmdbExports.Keywords })
		{
			var clock = System.Diagnostics.Stopwatch.StartNew();
			var result = await hub[OutsideSourceId.TmdbExports].GetAsync(TmdbExports.FileUri(kind, yesterday));
			var names = (result.Body is { } body ? TmdbExports.Parse(body) : null) ?? [];
			Note($"tmdb {kind} {yesterday:yyyy-MM-dd}", result, clock.ElapsedMilliseconds, new JsonObject { ["lines"] = names.Count });
			Assert.True(result.Ok, $"{kind}: {result.Status} {result.Message}");
			Assert.True(names.Count > 1000, $"{kind}: {names.Count} lines");

			if (Fixtures is { } folder)
			{
				// the file's own lines, as TMDb wrote them: the first 120 and the ones the tests name, gzip again
				var lines = Encoding.UTF8.GetString(Gunzip(result.Body!)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
				var keep = lines.Take(120).Concat(lines.Where(line => kind == TmdbExports.Collections
					? KeptCollections.Any(id => line.StartsWith($"{{\"id\":{id},", StringComparison.Ordinal))
					: KeptKeywords.Any(k => line.Contains($"\"name\":\"{k}\"", StringComparison.Ordinal)))).Distinct();
				Directory.CreateDirectory(folder);
				await File.WriteAllBytesAsync(Path.Combine(folder, $"tmdb-{kind}-ids.sample.json.gz"), Gzip(string.Join("\n", keep) + "\n"));
			}
		}
	}

	// each query's item by its English label, to check the constants name the awards and genres meant
	private static readonly Dictionary<string, string> Labels = new()
	{
		["award-best-picture"] = "Academy Award for Best Picture",
		["award-palme-dor"] = "Palme d'Or",
		["award-golden-globe-drama"] = "Golden Globe Award for Best Motion Picture – Drama",
		["award-golden-globe-comedy"] = "Golden Globe Award for Best Motion Picture – Musical or Comedy",
		["award-bafta-film"] = "BAFTA Award for Best Film",
		["award-cesar-film"] = "César Award for Best Film",
		["award-golden-bear"] = "Golden Bear",
		["award-golden-lion"] = "Golden Lion",
		["genre-heist"] = "heist film",
		["genre-slasher"] = "slasher film",
		["genre-time-travel"] = "time travel",
	};

	private async Task Wikidata(OutsideHub hub, HttpClient http)
	{
		var source = hub[OutsideSourceId.Wikidata];
		var headers = new Dictionary<string, string> { ["Accept"] = WikidataLists.Accept };

		// one fixed query: the items with those labels (a label can name more than one item: all are listed)
		var values = string.Join(" ", Labels.Values.Distinct().Select(l => "\"" + l.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"@en"));
		var byLabel = new WikidataQuery("by-label", OutsideListKind.Genre, "", "",
			"PREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>\n" + $"SELECT ?x ?label WHERE {{ VALUES ?label {{ {values} }} ?x rdfs:label ?label . }}");
		var byLabelClock = System.Diagnostics.Stopwatch.StartNew();
		var byLabelResult = await source.GetAsync(WikidataLists.RequestUri(WikidataLists.MainEndpoint, byLabel), headers);
		var found = new Dictionary<string, List<string>>();
		if (byLabelResult.Ok)
		{
			foreach (var row in JsonNode.Parse(byLabelResult.Text)!["results"]!["bindings"]!.AsArray())
			{
				var label = row!["label"]!["value"]!.GetValue<string>();
				(found.TryGetValue(label, out var list) ? list : found[label] = []).Add(row["x"]!["value"]!.GetValue<string>().Split('/')[^1]);
			}
		}

		Note("wikidata items by label", byLabelResult, byLabelClock.ElapsedMilliseconds,
			new JsonObject { ["found"] = new JsonObject([.. found.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, string.Join(" ", kv.Value)))]) });

		foreach (var constant in WikidataLists.All)
		{
			// a constant whose item isn't the one its label names (and that label names one item only) is asked with that one
			var query = constant;
			var asked = System.Text.RegularExpressions.Regex.Match(constant.Query, @"wd:(Q\d+)").Groups[1].Value;
			var fixedId = Labels.TryGetValue(constant.Id, out var label) && found.TryGetValue(label, out var items1) && items1.Count == 1 && items1[0] != asked ? items1[0] : null;
			if (fixedId is not null)
			{
				query = constant with { Query = constant.Query.Replace("wd:" + asked + " ", "wd:" + fixedId + " ", StringComparison.Ordinal) };
			}

			var clock = System.Diagnostics.Stopwatch.StartNew();
			var result = await source.GetAsync(WikidataLists.RequestUri(WikidataLists.MainEndpoint, query), headers);
			var items = result.Ok ? WikidataLists.Parse(result.Text, query.Kind) : null;
			Note($"wikidata {query.Id}", result, clock.ElapsedMilliseconds, new JsonObject
			{
				["items"] = items?.Count ?? 0,
				["withTmdb"] = items?.Count(x => x.Ref.Tmdb is not null) ?? 0,
				["asked"] = fixedId ?? asked,
				["constantWas"] = fixedId is null ? null : asked,
			});
			if (query.Id == "award-best-picture")
			{
				Assert.True(result.Ok, $"{query.Id}: {result.Status} {result.Message}");
				Assert.True(items!.Count(x => x.Ref.Tmdb is not null) >= 90, $"Best Picture: {items!.Count(x => x.Ref.Tmdb is not null)} with a TMDb id");
				Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60));
			}

			if (Fixtures is { } folder && result.Ok)
			{
				await File.WriteAllTextAsync(Path.Combine(folder, $"wikidata-{query.Id}.json"), Trim(result.Text, query.Id == "award-best-picture" ? int.MaxValue : 40));
			}
		}

		// which item each query names, to check the ids are the awards and genres meant (one fixed query)
		var ids = string.Join(" ", WikidataLists.All.Select(q => System.Text.RegularExpressions.Regex.Match(q.Query, @"wd:Q\d+").Value).Distinct());
		var labels = new WikidataQuery("labels", OutsideListKind.Genre, "", "",
			"PREFIX wd: <http://www.wikidata.org/entity/>\nPREFIX rdfs: <http://www.w3.org/2000/01/rdf-schema#>\n" +
			$"SELECT ?x ?label WHERE {{ VALUES ?x {{ {ids} }} ?x rdfs:label ?label . FILTER(LANG(?label) = \"en\") }}");
		var labelClock = System.Diagnostics.Stopwatch.StartNew();
		var labelResult = await source.GetAsync(WikidataLists.RequestUri(WikidataLists.MainEndpoint, labels), headers);
		var named = new JsonObject();
		if (labelResult.Ok)
		{
			foreach (var row in JsonNode.Parse(labelResult.Text)!["results"]!["bindings"]!.AsArray())
			{
				named[row!["x"]!["value"]!.GetValue<string>().Split('/')[^1]] = row["label"]!["value"]!.GetValue<string>();
			}
		}

		Note("wikidata labels of the queried items", labelResult, labelClock.ElapsedMilliseconds, new JsonObject { ["labels"] = named });

		// no User-Agent at all: the 403 the fake models (sent past the layer, which always names Nixie)
		using (var bare = new HttpRequestMessage(HttpMethod.Get, WikidataLists.RequestUri(WikidataLists.MainEndpoint, WikidataLists.Awards[0])))
		{
			bare.Headers.Accept.ParseAdd(WikidataLists.Accept);
			var clock = System.Diagnostics.Stopwatch.StartNew();
			using var response = await http.SendAsync(bare);
			var text = await response.Content.ReadAsStringAsync();
			_summary.Add(new JsonObject
			{
				["what"] = "wikidata without a User-Agent",
				["status"] = (int)response.StatusCode,
				["bytes"] = text.Length,
				["ms"] = clock.ElapsedMilliseconds,
				["body"] = text.Length > 300 ? text[..300] : text,
			});
			Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
		}

		// query-next, which answers "Unauthorized User-Agent" until it opens (the week of 2026-11-23)
		var nextClock = System.Diagnostics.Stopwatch.StartNew();
		var next = await source.GetAsync(WikidataLists.RequestUri(WikidataLists.NextEndpoint, WikidataLists.Awards[0]), headers);
		var nextItems = next.Ok ? WikidataLists.Parse(next.Text, OutsideListKind.Award) : null;
		Note("wikidata query-next best picture", next, nextClock.ElapsedMilliseconds, new JsonObject
		{
			["items"] = nextItems?.Count ?? 0,
			["body"] = next.Ok ? null : next.Text.Length > 300 ? next.Text[..300] : next.Text,
		});
	}

	// a SPARQL answer cut to its first rows, the rows as the service sent them
	private static string Trim(string json, int rows)
	{
		var root = JsonNode.Parse(json)!;
		var bindings = root["results"]!["bindings"]!.AsArray();
		while (bindings.Count > rows)
		{
			bindings.RemoveAt(bindings.Count - 1);
		}

		return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
	}

	private static byte[] Gunzip(byte[] data)
	{
		if (data.Length < 2 || data[0] != 0x1f || data[1] != 0x8b)
		{
			return data;
		}

		using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
		using var output = new MemoryStream();
		input.CopyTo(output);
		return output.ToArray();
	}

	private static byte[] Gzip(string text)
	{
		using var output = new MemoryStream();
		using (var gz = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
		{
			gz.Write(Encoding.UTF8.GetBytes(text));
		}

		return output.ToArray();
	}
}
