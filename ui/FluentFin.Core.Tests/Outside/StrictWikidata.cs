using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using FluentFin.Core.Outside;

namespace FluentFin.Core.Tests.Outside;

/// <summary>
/// 1.7 keyless: Wikidata's query service as strict as the real one where Nixie depends on it (plan §2.1, the terms re-check,
/// the live check's answers): 403 with the robot-policy text for a User-Agent with no contact URL or e-mail; query-next's
/// 403 "Unauthorized User-Agent" until it opens; 429 with Retry-After (<see cref="RetryAfter"/>); more than 30 error
/// queries a minute from one User-Agent answer 429; <see cref="Hang"/> holds the answer past any timeout; 500 for
/// <see cref="Fail"/>. A known query answers its recorded fixture (gzip, as the service sends it), or <see cref="Answer"/>'s; any other, 400.
/// </summary>
internal sealed partial class StrictWikidata(Func<DateTimeOffset> clock) : HttpMessageHandler
{
	public const string NoContact = "Please set a user-agent and respect our robot policy https://w.wiki/4wJS. See also https://phabricator.wikimedia.org/T400119.\n";

	[GeneratedRegex(@"https?://\S+|[\w.+-]+@[\w-]+\.[\w.-]+")]
	private static partial Regex Contact();

	private readonly Dictionary<string, List<DateTimeOffset>> _errors = [];

	public bool NextOpen { get; set; }

	public TimeSpan? RetryAfter { get; set; }

	public bool Hang { get; set; }

	public bool Fail { get; set; }

	public int Answered { get; private set; }

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		var uri = request.RequestUri!;
		var agent = request.Headers.TryGetValues("User-Agent", out var values) ? string.Join(" ", values) : "";
		var path = uri.AbsolutePath.Replace("/__harness/outside/", "/", StringComparison.Ordinal);
		var host = uri.IsLoopback ? path.Split('/')[1] : uri.Host;
		if (!agent.Contains("http", StringComparison.Ordinal) && !agent.Contains('@') || !Contact().IsMatch(agent))
		{
			return Error(agent, StrictTmdbExports.Text(HttpStatusCode.Forbidden, NoContact, "text/plain"));
		}

		if (host == "query-next.wikidata.org" && !NextOpen)
		{
			return Error(agent, StrictTmdbExports.Text(HttpStatusCode.Forbidden, "Unauthorized User-Agent (3732189)", "text/plain"));
		}

		var now = clock();
		var recent = _errors.TryGetValue(agent, out var list) ? list.Where(t => now - t < TimeSpan.FromMinutes(1)).ToList() : [];
		if (recent.Count >= 30)
		{
			var tooMany = StrictTmdbExports.Text(HttpStatusCode.TooManyRequests, "Too Many Requests", "text/plain");
			tooMany.Headers.RetryAfter = new(TimeSpan.FromSeconds(60));
			return tooMany;
		}

		if (RetryAfter is { } wait)
		{
			var limited = StrictTmdbExports.Text(HttpStatusCode.TooManyRequests, "Too Many Requests", "text/plain");
			limited.Headers.RetryAfter = new(wait);
			return limited;
		}

		if (Hang)
		{
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
		}

		if (Fail)
		{
			return Error(agent, StrictTmdbExports.Text(HttpStatusCode.InternalServerError, "java.util.concurrent.TimeoutException", "text/plain"));
		}

		var query = System.Web.HttpUtility.ParseQueryString(uri.Query)["query"];
		var known = WikidataLists.All.FirstOrDefault(x => x.Query == query);
		if (known is null)
		{
			return Error(agent, StrictTmdbExports.Text(HttpStatusCode.BadRequest, "MalformedQueryException", "text/plain"));
		}

		Answered++;
		using var zipped = new MemoryStream();
		using (var gz = new GZipStream(zipped, CompressionLevel.Fastest, leaveOpen: true))
		{
			gz.Write(Encoding.UTF8.GetBytes(Answer(known)));
		}

		var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipped.ToArray()) };
		response.Content.Headers.ContentType = new("application/sparql-results+json") { CharSet = "utf-8" };
		response.Content.Headers.ContentEncoding.Add("gzip");
		return response;
	}

	/// <summary>The answer to a known list's query: its fixture, unless a test gives its own.</summary>
	public Func<WikidataQuery, string> Answer { get; set; } = query => OutsideFixtures.Wikidata(query.Id);

	private HttpResponseMessage Error(string agent, HttpResponseMessage response)
	{
		(_errors.TryGetValue(agent, out var list) ? list : _errors[agent] = []).Add(clock());
		return response;
	}
}
