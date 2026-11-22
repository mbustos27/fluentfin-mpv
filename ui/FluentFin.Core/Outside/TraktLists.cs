using System.Globalization;
using System.Text.Json;

namespace FluentFin.Core.Outside;

/// <summary>
/// 1.7 trending: Trakt's public lists (trending, popular and anticipated films and shows, and the US box office), read with
/// the app's Client ID only: <c>trakt-api-key</c>, <c>trakt-api-version: 2</c> and Nixie's User-Agent (Trakt may block a
/// request without one). No sign-in, no Client Secret, no Trakt account, ever (the smart collections plan, Appendix A).
/// <para>
/// Every request is one of the fixed list addresses below, chosen from the enabled settings alone: never a title or an id of
/// the user's library, so what is asked and when says nothing about what the user owns (plan section 3). The answers are
/// matched on the PC.
/// </para>
/// Pure functions over the answer's text and header values; the requests go through the outside layer (OutsideSource).
/// </summary>
public static class TraktLists
{
	/// <summary>Trakt's API. A harness run asks the fake server's instead (the outside layer's harness origin).</summary>
	public const string ApiBase = "https://api.trakt.tv";

	/// <summary>
	/// The <c>limit</c> Nixie asks for. 250 is a common cap, not a promise (the terms re-check of 2026-11-21, Trakt's
	/// pagination guide): the answer's <c>X-Pagination-Limit</c> says what it used, and a full page is judged by that.
	/// </summary>
	public const int MaxLimit = 250;

	/// <summary>
	/// The pages read of one list at most (1,000 titles at 250 a page), whatever the page count says: a request count that
	/// never depends on the library and stays far under Trakt's 500 GETs per 5 minutes.
	/// </summary>
	public const int MaxPages = 4;

	/// <summary>
	/// When a list read at <paramref name="readAt"/> is asked again: after Trakt's <see cref="OutsideSourceInfo.RefreshAfter"/>
	/// (6 hours: cache aggressively, its Use Policy). The box office changes once a week, on Monday mornings (the terms
	/// re-check of 2026-11-21): it is asked again at its next update, Monday 18:00 UTC (a US Monday morning), or when a read
	/// would pass Trakt's <see cref="OutsideSourceInfo.MaxAge"/> (a day: no list is kept longer), whichever comes first.
	/// </summary>
	public static DateTimeOffset FreshUntil(Endpoint endpoint, DateTimeOffset readAt)
	{
		var info = OutsideSources.Trakt;
		if (endpoint.Paged)
		{
			return readAt + info.RefreshAfter;
		}

		var utc = readAt.ToUniversalTime();
		var monday = new DateTimeOffset(utc.Date, TimeSpan.Zero).AddDays(((int)DayOfWeek.Monday - (int)utc.DayOfWeek + 7) % 7).AddHours(18);
		if (monday <= utc)
		{
			monday = monday.AddDays(7);
		}

		var kept = readAt + info.MaxAge;
		return monday < kept ? monday : kept;
	}

	/// <summary>The API version every request names.</summary>
	public const string ApiVersion = "2";

	/// <summary>One of Trakt's public lists.</summary>
	public sealed record Endpoint(TrendingList List, ListedKind Kind)
	{
		/// <summary>The box office is one page of ten titles (not paged, the terms re-check); the others page.</summary>
		public bool Paged => List != TrendingList.BoxOffice;

		/// <summary>"movies/trending": also the list's name in the cache and the log.</summary>
		public string Name => $"{(Kind == ListedKind.Movie ? "movies" : "shows")}/{List switch
		{
			TrendingList.Trending => "trending",
			TrendingList.Popular => "popular",
			TrendingList.Anticipated => "anticipated",
			_ => "boxoffice",
		}}";
	}

	/// <summary>Every list Nixie reads from Trakt when its switch is on, in this order.</summary>
	public static IReadOnlyList<Endpoint> All { get; } =
	[
		new(TrendingList.Trending, ListedKind.Movie),
		new(TrendingList.Trending, ListedKind.Series),
		new(TrendingList.Popular, ListedKind.Movie),
		new(TrendingList.Popular, ListedKind.Series),
		new(TrendingList.Anticipated, ListedKind.Movie),
		new(TrendingList.Anticipated, ListedKind.Series),
		new(TrendingList.BoxOffice, ListedKind.Movie),
	];

	/// <summary>The request's path and query: "/movies/trending?page=1&amp;limit=250"; the box office "/movies/boxoffice".</summary>
	public static string PathAndQuery(Endpoint endpoint, int page)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
		return endpoint.Paged
			? $"/{endpoint.Name}?page={page.ToString(CultureInfo.InvariantCulture)}&limit={MaxLimit.ToString(CultureInfo.InvariantCulture)}"
			: $"/{endpoint.Name}";
	}

	/// <summary>
	/// The headers Trakt needs besides the User-Agent (the outside layer adds that): <c>Content-Type: application/json</c>
	/// (a content header: the layer sends it on an empty body), the API version and the Client ID (Trakt's required-headers
	/// guide, confirmed by the terms re-check of 2026-11-21).
	/// </summary>
	public static IReadOnlyList<KeyValuePair<string, string>> Headers(string clientId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
		return
		[
			new("Content-Type", "application/json"),
			new("trakt-api-version", ApiVersion),
			new("trakt-api-key", clientId),
		];
	}

	/// <summary>
	/// The titles of one answer page, ranked from <paramref name="firstRank"/>. Trending and the box office wrap each title
	/// (<c>{"watchers": 9, "movie": {…}}</c>, <c>{"revenue": 1, "movie": {…}}</c>), anticipated too (<c>list_count</c>);
	/// popular answers the titles bare. A title without a single id a library can match is left out (its rank is not used).
	/// </summary>
	/// <exception cref="ListUnreadable">The answer is not a JSON list.</exception>
	public static Page ParsePage(string json, Endpoint endpoint, int firstRank = 1)
	{
		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(json);
		}
		catch (JsonException)
		{
			throw new ListUnreadable("Trakt answered something that isn't a list.");
		}

		using (document)
		{
			if (document.RootElement.ValueKind != JsonValueKind.Array)
			{
				throw new ListUnreadable("Trakt answered something that isn't a list.");
			}

			var wrapper = endpoint.Kind == ListedKind.Movie ? "movie" : "show";
			var titles = new List<ListedTitle>();
			var rank = firstRank;
			var entries = 0;
			foreach (var element in document.RootElement.EnumerateArray())
			{
				entries++;
				if (element.ValueKind != JsonValueKind.Object)
				{
					continue;
				}

				var title = element.TryGetProperty(wrapper, out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : element;
				if (!title.TryGetProperty("ids", out var ids) || ids.ValueKind != JsonValueKind.Object)
				{
					continue;
				}

				var listed = new ListedTitle(rank, endpoint.Kind, Id(ids, "tmdb"), ImdbId(Id(ids, "imdb")),
					endpoint.Kind == ListedKind.Series ? Id(ids, "tvdb") : null, Text(title, "title") ?? "", Year(title));
				if (listed.HasIds)
				{
					titles.Add(listed);
					rank++;
				}
			}

			return new Page(titles, entries);
		}
	}

	/// <summary>One answer page: the titles kept, and how many entries the page held (a full page is judged by those).</summary>
	public sealed record Page(IReadOnlyList<ListedTitle> Titles, int Entries);

	/// <summary>Trakt's paging headers as it sent them (null where one is missing or not a number).</summary>
	public sealed record Paging(int? Page, int? Limit, int? PageCount, int? ItemCount);

	/// <summary>The X-Pagination-* headers of an answer (any case); null values for those missing or not numbers.</summary>
	public static Paging ParsePaging(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
	{
		int? page = null, limit = null, pageCount = null, itemCount = null;
		foreach (var (name, values) in headers)
		{
			var value = values.FirstOrDefault()?.Trim();
			int? number = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
			switch (name.ToLowerInvariant())
			{
				case "x-pagination-page": page = number; break;
				case "x-pagination-limit": limit = number; break;
				case "x-pagination-page-count": pageCount = number; break;
				case "x-pagination-item-count": itemCount = number; break;
			}
		}

		return new Paging(page, limit, pageCount, itemCount);
	}

	/// <summary>
	/// Whether to ask for the page after <paramref name="page"/>. Trakt's pagination guide (the terms re-check of 2026-11-21):
	/// follow <c>X-Pagination-Page-Count</c>, and page until an empty page only where the count is a placeholder. A count is
	/// taken as a placeholder when it can't be right: missing, or naming this page the last although it came full (as many
	/// titles as the answer's <c>X-Pagination-Limit</c>, else as asked). Never past <see cref="MaxPages"/>, never after an
	/// empty page, and never when a page brought no title not seen before (a server ignoring <c>page</c>).
	/// </summary>
	/// <param name="onPage">The entries the page held (<see cref="Page.Entries"/>).</param>
	/// <param name="newTitles">Of those, the ones not on an earlier page.</param>
	public static bool ReadNext(Endpoint endpoint, int page, Paging paging, int onPage, int newTitles)
	{
		if (!endpoint.Paged || onPage == 0 || newTitles == 0 || page >= MaxPages)
		{
			return false;
		}

		if (paging.PageCount is { } count && page < count)
		{
			return true;
		}

		var full = onPage >= Math.Min(paging.Limit is > 0 and var l ? l : MaxLimit, MaxLimit);
		return full; // no count, or a count that says a full page was the last: a placeholder, so read on until empty
	}

	/// <summary>
	/// The pages of one list joined: titles seen on an earlier page left out, ranks from 1 in the list's order. Returns how
	/// many of <paramref name="page"/>'s titles were new.
	/// </summary>
	public static int Join(List<ListedTitle> list, IReadOnlyList<ListedTitle> page)
	{
		var seen = list.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var added = 0;
		foreach (var title in page)
		{
			if (seen.Add(Key(title)))
			{
				list.Add(title with { Rank = list.Count + 1 });
				added++;
			}
		}

		return added;
	}

	/// <summary>
	/// Nixie's words for a refusal (never Trakt's page): 401 and 403 mean the Client ID was turned away (an unapproved or
	/// lapsed registration), 429 a pause, 5xx and Cloudflare's 52x Trakt's own trouble.
	/// </summary>
	public static string Refusal(int status) => status switch
	{
		401 or 403 => "Trakt turned this copy of Nixie away. Its registration may have lapsed; a Nixie update fixes that.",
		429 => "Trakt asked Nixie to slow down. Nixie asks again later.",
		>= 500 => "Trakt is having trouble right now. Nixie asks again later.",
		_ => $"Trakt couldn't give its lists (error {status.ToString(CultureInfo.InvariantCulture)}). Nixie asks again later.",
	};

	private static string Key(ListedTitle t) => $"{t.Kind}|{t.Tmdb}|{t.Imdb}|{t.Tvdb}";

	private static string? Id(JsonElement ids, string name)
	{
		if (!ids.TryGetProperty(name, out var value))
		{
			return null;
		}

		var text = value.ValueKind switch
		{
			JsonValueKind.Number when value.TryGetInt64(out var n) && n > 0 => n.ToString(CultureInfo.InvariantCulture),
			JsonValueKind.String => value.GetString()?.Trim(),
			_ => null,
		};
		return string.IsNullOrEmpty(text) ? null : text;
	}

	/// <summary>An IMDb id as Jellyfin keeps it ("tt0111161"); anything else is no IMDb id.</summary>
	internal static string? ImdbId(string? value) =>
		value is { Length: > 2 } v && v.StartsWith("tt", StringComparison.OrdinalIgnoreCase) && v[2..].All(char.IsAsciiDigit)
			? "tt" + v[2..]
			: null;

	private static string? Text(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	private static int? Year(JsonElement element) =>
		element.TryGetProperty("year", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var year) ? year : null;
}
