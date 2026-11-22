using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using FluentFin.Core.Outside;

namespace FluentFin.Core.Tests.Outside;

/// <summary>
/// 1.7 keyless: the recorded real answers the live check wrote (tools\live\live_outside_sources.ps1 -Fixtures, 2026-11-22):
/// the export files' first lines and named lines, and Wikidata's answers cut to 40 rows. Never typed by hand.
/// </summary>
internal static partial class OutsideFixtures
{
	public static string Folder { get; } = Path.Combine(RepoRoot(), "ui", "FluentFin.Core.Tests", "Outside", "Fixtures");

	public static byte[] TmdbFile(string kind) => File.ReadAllBytes(Path.Combine(Folder, $"tmdb-{kind}-ids.sample.json.gz"));

	public static string Wikidata(string id) => File.ReadAllText(Path.Combine(Folder, $"wikidata-{id}.json"));

	private static string RepoRoot()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ui", "FluentFin.sln")))
		{
			dir = dir.Parent;
		}

		return dir?.FullName ?? throw new DirectoryNotFoundException("the repository above " + AppContext.BaseDirectory);
	}
}

/// <summary>
/// 1.7 keyless: files.tmdb.org as strict as the real host where Nixie depends on it (plan §2.1, the terms re-check): today's
/// file is 404 before 08:00 UTC (and any later day, and anything older than 3 months), files are gzip sent as files
/// (application/octet-stream, no Content-Encoding), and <see cref="RequireKey"/> answers 401 as a key requirement would.
/// Answers the real host's addresses and the harness's /__harness/outside/files.tmdb.org/... alike.
/// </summary>
internal sealed partial class StrictTmdbExports(Func<DateTimeOffset> clock) : HttpMessageHandler
{
	[GeneratedRegex(@"/p/exports/([a-z_]+)_ids_(\d\d)_(\d\d)_(\d{4})\.json\.gz$")]
	private static partial Regex FileName();

	public bool RequireKey { get; set; }

	private readonly List<string> _asked = [];
	private readonly Lock _lock = new();

	/// <summary>The paths asked so far, in order.</summary>
	public List<string> Asked
	{
		get
		{
			lock (_lock)
			{
				return [.. _asked];
			}
		}
	}

	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		var uri = request.RequestUri!;
		lock (_lock)
		{
			_asked.Add(uri.AbsolutePath);
		}

		if (RequireKey)
		{
			return Task.FromResult(Text(HttpStatusCode.Unauthorized, """{"status_code":7,"status_message":"Invalid API key: You must be granted a valid key.","success":false}""", "application/json"));
		}

		var m = FileName().Match(uri.AbsolutePath);
		var hostOk = uri.Host == "files.tmdb.org" || uri.AbsolutePath.StartsWith("/__harness/outside/files.tmdb.org/", StringComparison.Ordinal);
		if (!hostOk || !m.Success || m.Groups[1].Value is not ("collection" or "keyword")
			|| !DateOnly.TryParseExact($"{m.Groups[4].Value}-{m.Groups[2].Value}-{m.Groups[3].Value}", "yyyy-MM-dd", out var day))
		{
			return Task.FromResult(NotFound(uri));
		}

		var now = clock().ToUniversalTime();
		var today = DateOnly.FromDateTime(now.UtcDateTime);
		if (day > today || (day == today && now.Hour < TmdbExports.ReadyHourUtc) || today.DayNumber - day.DayNumber > 92)
		{
			return Task.FromResult(NotFound(uri));
		}

		var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(OutsideFixtures.TmdbFile(m.Groups[1].Value)) };
		response.Content.Headers.ContentType = new("application/octet-stream");
		return Task.FromResult(response);
	}

	private static HttpResponseMessage NotFound(Uri uri) =>
		Text(HttpStatusCode.NotFound, $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Error><Code>NoSuchKey</Code><Message>The specified key does not exist.</Message><Key>{uri.AbsolutePath.TrimStart('/')}</Key></Error>", "application/xml");

	internal static HttpResponseMessage Text(HttpStatusCode status, string body, string type)
	{
		var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) };
		response.Content.Headers.ContentType = new(type);
		return response;
	}
}
