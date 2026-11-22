using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FluentFin.Core.Outside;

/// <summary>
/// 1.7 trending: TMDb's trending films and shows this week (<c>/3/trending/{movie,tv}/week</c>), read with the user's own
/// API Read Access Token, sent only as <c>Authorization: Bearer</c>: never <c>api_key=</c> in an address, never in a log
/// (the smart collections plan, section 3 and phase 3).
/// <para>
/// The requests are the two fixed addresses below and their first pages, chosen from the enabled settings alone; the answers
/// are matched on the PC by TMDb id. No TMDb image is ever fetched: the cards show the user's own server's art.
/// </para>
/// Pure functions over the answer's text; the requests go through the outside layer (OutsideSource).
/// </summary>
public static partial class TmdbLists
{
	/// <summary>TMDb's API. A harness run asks the fake server's instead (the outside layer's harness origin).</summary>
	public const string ApiBase = "https://api.themoviedb.org";

	/// <summary>The pages read of each list (20 titles a page): the week's first 100 films and 100 shows.</summary>
	public const int Pages = 5;

	/// <summary>When a list read at <paramref name="readAt"/> is asked again: a day later (TMDb's <see cref="OutsideSourceInfo.RefreshAfter"/>; its trending week changes daily).</summary>
	public static DateTimeOffset FreshUntil(DateTimeOffset readAt) => readAt + OutsideSources.TmdbTrending.RefreshAfter;

	/// <summary>The two lists, films and shows.</summary>
	public static IReadOnlyList<ListedKind> Kinds { get; } = [ListedKind.Movie, ListedKind.Series];

	/// <summary>"tmdb/trending/movie/week": the list's name in the cache and the log.</summary>
	public static string Name(ListedKind kind) => $"tmdb/trending/{(kind == ListedKind.Movie ? "movie" : "tv")}/week";

	/// <summary>The request's path and query: "/3/trending/movie/week?page=1". Nothing else ever goes in the query.</summary>
	public static string PathAndQuery(ListedKind kind, int page)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
		return $"/3/trending/{(kind == ListedKind.Movie ? "movie" : "tv")}/week?page={page.ToString(CultureInfo.InvariantCulture)}";
	}

	/// <summary>The headers TMDb needs besides the User-Agent (the outside layer adds that): the token as a Bearer, and JSON.</summary>
	public static IReadOnlyList<KeyValuePair<string, string>> Headers(string token)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(token);
		return
		[
			new("Authorization", "Bearer " + token.Trim()),
			new("Accept", "application/json"),
		];
	}

	/// <summary>One answer page: its titles and how many pages TMDb says the list has.</summary>
	public sealed record Page(IReadOnlyList<ListedTitle> Titles, int? TotalPages);

	/// <summary>
	/// The titles of one answer page, ranked from <paramref name="firstRank"/>: <c>{"page": 1, "results": [{"id": …,
	/// "title"/"name": …, "release_date"/"first_air_date": …, "media_type": …}], "total_pages": …}</c>. A result of another
	/// media type (a person, in an "all" list) is left out. Image paths are ignored: Nixie never fetches them.
	/// </summary>
	/// <exception cref="ListUnreadable">The answer has no results list.</exception>
	public static Page ParsePage(string json, ListedKind kind, int firstRank = 1)
	{
		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(json);
		}
		catch (JsonException)
		{
			throw new ListUnreadable("TMDb answered something that isn't a list.");
		}

		using (document)
		{
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
			{
				throw new ListUnreadable("TMDb answered something that isn't a list.");
			}

			var mediaType = kind == ListedKind.Movie ? "movie" : "tv";
			var titles = new List<ListedTitle>();
			var rank = firstRank;
			foreach (var result in results.EnumerateArray())
			{
				if (result.ValueKind != JsonValueKind.Object
					|| (result.TryGetProperty("media_type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() != mediaType)
					|| !result.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var tmdb) || tmdb <= 0)
				{
					continue;
				}

				var name = Text(result, kind == ListedKind.Movie ? "title" : "name") ?? "";
				var date = Text(result, kind == ListedKind.Movie ? "release_date" : "first_air_date");
				int? year = date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : null;
				titles.Add(new ListedTitle(rank++, kind, tmdb.ToString(CultureInfo.InvariantCulture), null, null, name, year));
			}

			int? total = root.TryGetProperty("total_pages", out var pages) && pages.ValueKind == JsonValueKind.Number && pages.TryGetInt32(out var p) ? p : null;
			return new Page(titles, total);
		}
	}

	/// <summary>Whether to ask for the page after <paramref name="page"/>: while it brought titles, TMDb has more, and up to <see cref="Pages"/>.</summary>
	public static bool ReadNext(int page, Page answer) => answer.Titles.Count > 0 && page < Pages && (answer.TotalPages is not { } total || page < total);

	/// <summary>
	/// Nixie's words for a refusal (never TMDb's page). TMDb answers a bad token with 401 (its status_code 7, "Invalid API
	/// key"), and asks to slow down with 429.
	/// </summary>
	public static string Refusal(int status) => status switch
	{
		401 => "TMDb didn't accept your token. Check the API Read Access Token in Settings.",
		403 => "TMDb refused the request for this token.",
		429 => "TMDb asked Nixie to slow down. Nixie asks again later.",
		>= 500 => "TMDb is having trouble right now. Nixie asks again later.",
		_ => $"TMDb couldn't give its list (error {status.ToString(CultureInfo.InvariantCulture)}). Nixie asks again later.",
	};

	/// <summary>
	/// What's wrong with a pasted token, in plain words; null when it looks like an API Read Access Token (a JWT: three
	/// dot-separated base64url parts, "eyJ…"). The short API key (32 hex digits) only works in an address, which Nixie never
	/// does, so it is turned away here with a hint.
	/// </summary>
	public static string? TokenProblem(string? token)
	{
		var t = token?.Trim() ?? "";
		if (t.Length == 0)
		{
			return null;
		}

		if (ApiKeyShape().IsMatch(t))
		{
			return "That's TMDb's short API key. Paste the API Read Access Token (the long one) from the same page.";
		}

		return ReadTokenShape().IsMatch(t) ? null : "That doesn't look like a TMDb API Read Access Token.";
	}

	private static string? Text(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	[GeneratedRegex("^[0-9a-fA-F]{32}$")]
	private static partial Regex ApiKeyShape();

	[GeneratedRegex(@"^eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$")]
	private static partial Regex ReadTokenShape();
}
