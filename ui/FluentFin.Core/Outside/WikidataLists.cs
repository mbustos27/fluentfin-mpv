using System.Globalization;
using System.Text.Json;
using FluentFin.Core.SmartCollections;

namespace FluentFin.Core.Outside;

/// <summary>1.7: a Wikidata list Nixie asks for: a fixed query, the same for everyone (plan §3: never built from the library).</summary>
public sealed record WikidataQuery(string Id, OutsideListKind Kind, string Title, string Why, string Query)
{
	public string? KeywordTag { get; init; }
}

/// <summary>1.7: the query endpoints Settings offers for Wikidata.</summary>
public enum WikidataEndpoint
{
	/// <summary>query.wikidata.org: today's service, available through June 2027 (slowdowns possible from February 2027).</summary>
	Wikidata,

	/// <summary>query-next.wikidata.org: the new service (QLever), early access until it opens to everyone.</summary>
	Next,

	/// <summary>The University of Freiburg's public QLever endpoint for Wikidata.</summary>
	QLever,

	/// <summary>An address the user gives (https, no credentials).</summary>
	Custom,
}

/// <summary>
/// 1.7: awards and finer genres from Wikidata (CC0), one fixed SPARQL query per list: explicit PREFIX lines, simple
/// OPTIONALs (a BIND inside an OPTIONAL timed out at 60 s) and no label service. Lists are cached a week. The endpoint is a
/// setting, because the query service is moving to a new backend.
/// </summary>
public static class WikidataLists
{
	public static Uri MainEndpoint { get; } = new("https://query.wikidata.org/sparql");

	public static Uri NextEndpoint { get; } = new("https://query-next.wikidata.org/sparql");

	public static Uri QLeverEndpoint { get; } = new("https://qlever.cs.uni-freiburg.de/api/wikidata");

	/// <summary>What the query service answers in (SPARQL 1.1 JSON results).</summary>
	public const string Accept = "application/sparql-results+json";

	private const string Prefixes = """
		PREFIX wd: <http://www.wikidata.org/entity/>
		PREFIX wdt: <http://www.wikidata.org/prop/direct/>
		PREFIX p: <http://www.wikidata.org/prop/>
		PREFIX ps: <http://www.wikidata.org/prop/statement/>
		PREFIX pq: <http://www.wikidata.org/prop/qualifier/>

		""";

	// the award's winners (P166, award received), the date of each award (P585, point in time), and the film's ids
	private static string Won(string award) => Prefixes + $$"""
		SELECT ?item ?tmdb ?imdb ?date WHERE {
		  ?item p:P166 ?st .
		  ?st ps:P166 {{award}} .
		  OPTIONAL { ?st pq:P585 ?date . }
		  OPTIONAL { ?item wdt:P4947 ?tmdb . }
		  OPTIONAL { ?item wdt:P345 ?imdb . }
		}
		""";

	// winners and nominees (P1411, nominated for)
	private static string WonOrNominated(string award) => Prefixes + $$"""
		SELECT ?item ?tmdb ?imdb ?date WHERE {
		  { ?item p:P166 ?st . ?st ps:P166 {{award}} . }
		  UNION
		  { ?item p:P1411 ?st . ?st ps:P1411 {{award}} . }
		  OPTIONAL { ?st pq:P585 ?date . }
		  OPTIONAL { ?item wdt:P4947 ?tmdb . }
		  OPTIONAL { ?item wdt:P345 ?imdb . }
		}
		""";

	// films and shows of a genre (P136) or a main subject (P921), with a TMDb id: TMDb's film (P4947) or show (P4983) id
	private static string About(string property, string subject) => Prefixes + $$"""
		SELECT ?item ?tmdb ?tmdbTv ?imdb WHERE {
		  { ?item wdt:{{property}} {{subject}} . ?item wdt:P4947 ?tmdb . }
		  UNION
		  { ?item wdt:{{property}} {{subject}} . ?item wdt:P4983 ?tmdbTv . }
		  OPTIONAL { ?item wdt:P345 ?imdb . }
		}
		""";

	/// <summary>The awards, in the Awards row's order.</summary>
	public static IReadOnlyList<WikidataQuery> Awards { get; } =
	[
		new("award-best-picture", OutsideListKind.Award, "Best Picture winners", "Academy Award for Best Picture winners, from Wikidata, by year", Won("wd:Q102427")),
		new("award-best-picture-nominees", OutsideListKind.Award, "Best Picture nominees", "Films nominated for the Academy Award for Best Picture, from Wikidata, by year", WonOrNominated("wd:Q102427")),
		new("award-palme-dor", OutsideListKind.Award, "Palme d'Or winners", "Cannes' Palme d'Or winners, from Wikidata, by year", Won("wd:Q179808")),
		new("award-golden-globe-drama", OutsideListKind.Award, "Golden Globes: Drama", "Golden Globe for Best Motion Picture – Drama winners, from Wikidata, by year", Won("wd:Q1011509")),
		new("award-golden-globe-comedy", OutsideListKind.Award, "Golden Globes: Comedy", "Golden Globe for Best Motion Picture – Musical or Comedy winners, from Wikidata, by year", Won("wd:Q670282")),
		new("award-bafta-film", OutsideListKind.Award, "BAFTA Best Film winners", "BAFTA Award for Best Film winners, from Wikidata, by year", Won("wd:Q139184")),
		new("award-cesar-film", OutsideListKind.Award, "César Best Film winners", "César Award for Best Film winners, from Wikidata, by year", Won("wd:Q645595")),
		new("award-golden-bear", OutsideListKind.Award, "Golden Bear winners", "Berlin's Golden Bear winners, from Wikidata, by year", Won("wd:Q154590")),
		new("award-golden-lion", OutsideListKind.Award, "Golden Lion winners", "Venice's Golden Lion winners, from Wikidata, by year", Won("wd:Q209459")),
	];

	/// <summary>The finer genres: each joins the keyword collection of its TMDb keyword.</summary>
	public static IReadOnlyList<WikidataQuery> Genres { get; } =
	[
		new("genre-heist", OutsideListKind.Genre, "Heists", "heist films from Wikidata", About("P136", "wd:Q496523")) { KeywordTag = "heist" },
		new("genre-slasher", OutsideListKind.Genre, "Slashers", "slasher films from Wikidata", About("P136", "wd:Q853630")) { KeywordTag = "slasher" },
		new("genre-time-travel", OutsideListKind.Genre, "Time travel", "titles about time travel from Wikidata", About("P921", "wd:Q182154")) { KeywordTag = "time travel" },
	];

	public static IReadOnlyList<WikidataQuery> All { get; } = [.. Awards, .. Genres];

	/// <summary>
	/// The endpoint the setting names; a custom one only when it is a valid https address with no credentials
	/// (<see cref="CheckCustom"/>), else today's service.
	/// </summary>
	public static Uri EndpointFor(WikidataEndpoint choice, string? custom) => choice switch
	{
		WikidataEndpoint.Next => NextEndpoint,
		WikidataEndpoint.QLever => QLeverEndpoint,
		WikidataEndpoint.Custom when CheckCustom(custom, out var uri) is null => uri!,
		_ => MainEndpoint,
	};

	/// <summary>Null when <paramref name="text"/> is a usable endpoint (https, a host, no credentials, query or fragment); else why not, in Nixie's words.</summary>
	public static string? CheckCustom(string? text, out Uri? uri)
	{
		uri = null;
		if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var parsed))
		{
			return "Enter the endpoint's full address, starting with https://.";
		}

		if (parsed.Scheme != Uri.UriSchemeHttps)
		{
			return "The address must start with https://.";
		}

		if (parsed.UserInfo.Length > 0)
		{
			return "Leave the user name and password out of the address.";
		}

		if (parsed.Query.Length > 0 || parsed.Fragment.Length > 0 || string.IsNullOrEmpty(parsed.Host))
		{
			return "Enter the endpoint's address without anything after ? or #.";
		}

		uri = parsed;
		return null;
	}

	/// <summary>The request for <paramref name="query"/> at <paramref name="endpoint"/>: GET with the query in the address.</summary>
	public static Uri RequestUri(Uri endpoint, WikidataQuery query) =>
		new($"{endpoint.GetLeftPart(UriPartial.Path)}?query={Uri.EscapeDataString(query.Query)}");

	/// <summary>
	/// The titles of a SPARQL JSON answer: ?tmdb is a film's TMDb id, ?tmdbTv a show's, ?imdb either's, ?date the award's
	/// date. Rows of one item join (several ids or dates); an item with no id is dropped. In list order: by year, oldest
	/// first, for an award; else as the service sent them. Null when the text isn't a SPARQL JSON answer.
	/// </summary>
	public static IReadOnlyList<OutsideListItem>? Parse(string json, OutsideListKind kind)
	{
		try
		{
			using var doc = JsonDocument.Parse(json);
			if (!doc.RootElement.TryGetProperty("results", out var results) || !results.TryGetProperty("bindings", out var bindings) || bindings.ValueKind != JsonValueKind.Array)
			{
				return null;
			}

			var items = new List<(string Key, string? Tmdb, string? TmdbTv, string? Imdb, int? Year, int Order)>();
			var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
			var order = 0;
			foreach (var row in bindings.EnumerateArray())
			{
				var tmdb = Digits(Value(row, "tmdb"));
				var tmdbTv = Digits(Value(row, "tmdbTv"));
				var imdb = Value(row, "imdb") is { } i && i.StartsWith("tt", StringComparison.OrdinalIgnoreCase) && i.Length > 2 && i[2..].All(char.IsAsciiDigit) ? i : null;
				var year = Year(Value(row, "date"));
				var key = Value(row, "item") ?? $"row{order}";
				if (byKey.TryGetValue(key, out var at))
				{
					var was = items[at];
					items[at] = (key, was.Tmdb ?? tmdb, was.TmdbTv ?? tmdbTv, was.Imdb ?? imdb, Min(was.Year, year), was.Order);
				}
				else
				{
					byKey[key] = items.Count;
					items.Add((key, tmdb, tmdbTv, imdb, year, order++));
				}
			}

			var kept = items.Where(x => x.Tmdb is not null || x.TmdbTv is not null || x.Imdb is not null);
			if (kind == OutsideListKind.Award)
			{
				kept = kept.OrderBy(x => x.Year ?? int.MaxValue).ThenBy(x => x.Order);
			}

			return [.. kept.Select(x => new OutsideListItem(
				x.Tmdb is not null ? new OutsideRef(Tmdb: x.Tmdb, Imdb: x.Imdb, Kind: IndexKind.Movie)
				: x.TmdbTv is not null ? new OutsideRef(Tmdb: x.TmdbTv, Imdb: x.Imdb, Kind: IndexKind.Series)
				: new OutsideRef(Imdb: x.Imdb), x.Year))];
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static int? Min(int? a, int? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);

	private static string? Value(JsonElement row, string name) =>
		row.TryGetProperty(name, out var cell) && cell.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;

	private static string? Digits(string? text) => text is { Length: > 0 } && text.All(char.IsAsciiDigit) ? text.TrimStart('0') is { Length: > 0 } t ? t : null : null;

	// "1998-03-23T00:00:00Z" → 1998; a date before the common era, or not a year, → null
	private static int? Year(string? date)
	{
		if (string.IsNullOrEmpty(date) || date[0] == '-')
		{
			return null;
		}

		var text = date.TrimStart('+');
		return text.Length >= 4 && int.TryParse(text.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y is > 1800 and < 2200 ? y : null;
	}

	/// <summary>
	/// Every Wikidata list, one query at a time (the source's gate): the cached list while it is fresh, else the endpoint's
	/// answer, kept in the cache; a list the endpoint refuses keeps its cached copy while it lasts. Stops asking once the
	/// source pauses (a 429, or a run of failures), so a bad day costs a handful of requests, far under the service's limits.
	/// <paramref name="stillOn"/> (the app's: the source's switch) is read before each request and before each list is kept:
	/// once it says no, nothing more is sent or written and the lists so far are returned (BH-170-M1: the switch turned off
	/// mid-round stops the round at once, as PRIVACY.md says).
	/// </summary>
	public static async Task<(IReadOnlyList<OutsideList> Lists, OutsideResult? Last)> LoadAsync(OutsideHub hub, Uri endpoint, CancellationToken cancellation = default, Func<bool>? stillOn = null)
	{
		var info = OutsideSources.Wikidata;
		var source = hub[OutsideSourceId.Wikidata];
		var lists = new List<OutsideList>();
		OutsideResult? last = null;
		var headers = new Dictionary<string, string> { ["Accept"] = Accept };
		// it failed a moment ago: the cached lists (within their 60 days) until the hour is over
		var backingOff = source.FailedWithin(OutsideSources.RetryAfterFailure);
		foreach (var query in All)
		{
			var cached = hub.Cache.Read(info, query.Id);
			IReadOnlyList<OutsideListItem>? items = cached is null ? null : Parse(System.Text.Encoding.UTF8.GetString(cached.Data), query.Kind);
			if (cached is null || !hub.Cache.IsFresh(cached) || items is null)
			{
				if (source.PausedUntil is null && !backingOff)
				{
					// BH-170-M1: turned off meanwhile (an earlier query was in flight): no further query
					if (stillOn?.Invoke() == false)
					{
						break;
					}

					var uri = RequestUri(endpoint, query);
					var result = last = await source.GetAsync(uri, headers, cancellation).ConfigureAwait(false);
					// BH-170-M1: turned off while this query was in flight: its answer is neither kept nor used
					if (stillOn?.Invoke() == false)
					{
						break;
					}

					if (result is { Ok: true } && Parse(result.Text, query.Kind) is { } fresh)
					{
						if (!hub.Cache.TryWrite(info, query.Id, result.Body!))
						{
							source.Note($"the list {query.Id} couldn't be kept on disk; it's used until Nixie closes");
						}

						items = fresh;
					}
					else if (result is { Ok: true })
					{
						last = source.Unreadable(uri, result); // a 200 that isn't SPARQL JSON: a failure, the cached list stays
					}
				}
			}

			if (items is not null)
			{
				lists.Add(new OutsideList(query.Id, OutsideSourceId.Wikidata, query.Kind, query.Title, query.Why, items) { KeywordTag = query.KeywordTag });
			}
		}

		return (lists, last);
	}
}
