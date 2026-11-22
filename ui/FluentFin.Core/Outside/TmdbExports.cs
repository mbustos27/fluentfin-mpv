using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace FluentFin.Core.Outside;

/// <summary>
/// 1.7: TMDb's daily export files (files.tmdb.org, no key): every collection's id and name, which names a library's
/// franchises (the server keeps TmdbCollection ids but never sends their names), and every keyword's name, which widens the
/// keyword allowlist. One public file a day per kind, the same for everyone: nothing about the library is sent (plan §3).
/// Files are generated from about 07:00 UTC and all are up by 08:00 UTC, so before 08:00 the newest is yesterday's; TMDb
/// keeps them 3 months, Nixie's cache 6 months at most (the API terms), refreshed weekly (the names barely change).
/// </summary>
public static class TmdbExports
{
	/// <summary>The hour (UTC) by which the day's files are all up.</summary>
	public const int ReadyHourUtc = 8;

	/// <summary>The file kinds Nixie reads.</summary>
	public const string Collections = "collection";

	public const string Keywords = "keyword";

	/// <summary>The cache names of the two files' parsed lists (<see cref="OutsideCache"/>).</summary>
	public static string CacheName(string kind) => kind + "-ids";

	/// <summary>The newest file's day at <paramref name="now"/>: today from 08:00 UTC, else yesterday.</summary>
	public static DateOnly NewestDay(DateTimeOffset now)
	{
		var utc = now.ToUniversalTime();
		var today = DateOnly.FromDateTime(utc.UtcDateTime);
		return utc.Hour < ReadyHourUtc ? today.AddDays(-1) : today;
	}

	/// <summary>"https://files.tmdb.org/p/exports/collection_ids_11_20_2026.json.gz".</summary>
	public static Uri FileUri(string kind, DateOnly day) =>
		new(OutsideSources.TmdbExports.RealBase, string.Create(CultureInfo.InvariantCulture, $"p/exports/{kind}_ids_{day:MM_dd_yyyy}.json.gz"));

	/// <summary>
	/// The id → name lines of an export file: gzip undone when the bytes are gzip (the files are sent as files, not as encoded
	/// answers), one JSON object a line; a line that isn't one is skipped. Names are trimmed; the first of a repeated id wins.
	/// Null when the gzip is cut or corrupt (review #3: a bad file is a failed answer, never a half list).
	/// </summary>
	public static Dictionary<int, string>? Parse(byte[] data)
	{
		try
		{
			return ParseLines(data);
		}
		catch (Exception ex) when (ex is InvalidDataException or IOException)
		{
			return null;
		}
	}

	/// <summary>
	/// The file's bytes, gzip undone, or <see cref="InvalidDataException"/> when the gzip is cut or corrupt. .NET's GZipStream
	/// ends quietly at a cut, so the size the gzip trailer records (its last 4 bytes) must match what came out.
	/// </summary>
	private static byte[] Gunzip(byte[] data)
	{
		using var input = new GZipStream(new MemoryStream(data, writable: false), CompressionMode.Decompress);
		using var output = new MemoryStream();
		input.CopyTo(output);
		if (data.Length < 18 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(data.Length - 4)) != (uint)output.Length)
		{
			throw new InvalidDataException("the gzip file is cut or corrupt");
		}

		return output.ToArray();
	}

	private static Dictionary<int, string> ParseLines(byte[] data)
	{
		var names = new Dictionary<int, string>();
		using var stream = new MemoryStream(data.Length >= 2 && data[0] == 0x1f && data[1] == 0x8b ? Gunzip(data) : data, writable: false);
		using var reader = new StreamReader(stream, Encoding.UTF8);
		while (reader.ReadLine() is { } line)
		{
			if (line.Length == 0)
			{
				continue;
			}

			try
			{
				using var doc = JsonDocument.Parse(line);
				var root = doc.RootElement;
				if (root.ValueKind == JsonValueKind.Object
					&& root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var number)
					&& root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
					&& name.GetString()?.Trim() is { Length: > 0 } text)
				{
					names.TryAdd(number, text);
				}
			}
			catch (JsonException)
			{
				// a cut or odd line: the rest still counts
			}
		}

		return names;
	}

	/// <summary>A parsed list as the cache keeps it: "id\tname" lines (smaller than the file's JSON, and quick to read back).</summary>
	public static byte[] ToCache(IReadOnlyDictionary<int, string> names)
	{
		var text = new StringBuilder();
		foreach (var (id, name) in names.OrderBy(x => x.Key))
		{
			text.Append(id.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(name.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ')).Append('\n');
		}

		return Encoding.UTF8.GetBytes(text.ToString());
	}

	public static Dictionary<int, string> FromCache(byte[] data)
	{
		var names = new Dictionary<int, string>();
		foreach (var line in Encoding.UTF8.GetString(data).Split('\n'))
		{
			var tab = line.IndexOf('\t');
			if (tab > 0 && int.TryParse(line.AsSpan(0, tab), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && line.Length > tab + 1)
			{
				names.TryAdd(id, line[(tab + 1)..]);
			}
		}

		return names;
	}

	/// <summary>
	/// The newest <paramref name="kind"/> file's names: the cached list while it is fresh, else the newest file (yesterday's
	/// before 08:00 UTC; yesterday's too when today's isn't up yet after all), kept in the cache. When the file can't be had,
	/// the cached list as long as it is within its maximum age, else null. <paramref name="result"/> is the request's, if one was sent.
	/// <paramref name="stillOn"/> (the app's: the source's switch) is read before each request and before the file is kept:
	/// once it says no, nothing more is sent or written (BH-170-M1: the switch turned off mid-round stops it at once).
	/// </summary>
	public static async Task<(Dictionary<int, string>? Names, OutsideResult? Result)> LoadAsync(OutsideHub hub, string kind, Func<DateTimeOffset> clock, CancellationToken cancellation = default, Func<bool>? stillOn = null)
	{
		var info = OutsideSources.TmdbExports;
		var cached = hub.Cache.Read(info, CacheName(kind));
		if (cached is not null && hub.Cache.IsFresh(cached))
		{
			return (FromCache(cached.Data), null);
		}

		var source = hub[OutsideSourceId.TmdbExports];
		if (source.FailedWithin(OutsideSources.RetryAfterFailure))
		{
			// it failed a moment ago: the cached list (within its 6 months) until the hour is over
			return (cached is null ? null : FromCache(cached.Data), null);
		}

		// BH-170-M1: turned off meanwhile (the other file's request was in flight): nothing is sent, nothing kept
		Dictionary<int, string>? Kept() => cached is null ? null : FromCache(cached.Data);
		if (stillOn?.Invoke() == false)
		{
			return (Kept(), null);
		}

		var day = NewestDay(clock());
		var result = await source.GetAsync(FileUri(kind, day), cancellation: cancellation).ConfigureAwait(false);
		if (result is { Outcome: OutsideOutcome.Refused, Status: 404 } && stillOn?.Invoke() != false)
		{
			// generation runs late now and then: the day before's file is still there (TMDb keeps 3 months)
			result = await source.GetAsync(FileUri(kind, day.AddDays(-1)), cancellation: cancellation).ConfigureAwait(false);
		}

		if (stillOn?.Invoke() == false)
		{
			return (Kept(), result);
		}

		if (result is { Ok: true, Body: { } body })
		{
			if (Parse(body) is { Count: > 0 } names)
			{
				if (!hub.Cache.TryWrite(info, CacheName(kind), ToCache(names)))
				{
					source.Note($"the {kind} names couldn't be kept on disk; they're used until Nixie closes");
				}

				return (names, result);
			}

			// a 200 with a cut or corrupt file: a failure like a refusal, and the cached list stays
			result = source.Unreadable(FileUri(kind, day), result);
		}

		return (cached is null ? null : FromCache(cached.Data), result);
	}
}
