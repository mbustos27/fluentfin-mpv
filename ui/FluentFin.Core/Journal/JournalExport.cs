using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FluentFin.Core.Journal;

/// <summary>
/// The journal as files the user keeps (Serializd has no export; this one is complete): JSON with everything, and a diary
/// CSV for spreadsheets. Deleted entries are not exported. Ratings are written both as 1-10 (as stored) and as stars.
/// </summary>
public static class JournalExport
{
	public const string Format = "fluentfin-mpv-journal";
	public const int Version = 1;

	public static readonly string[] CsvColumns =
	[
		"watched_at_utc", "watched_date_local", "date_approximate", "date_source", "series", "season", "episode", "title",
		"rating_stars", "rating_10", "rewatch", "review", "spoiler", "source", "on_server", "item_id", "series_id",
	];

	public static string ToJson(JournalProfile profile, IEnumerable<DiaryEntry> entries, IEnumerable<ItemRating> ratings, DateTimeOffset exportedAt)
	{
		var ratingList = ratings.ToList();
		var byItem = ratingList.ToDictionary(x => x.ItemId);
		using var stream = new MemoryStream();
		using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
		{
			json.WriteStartObject();
			json.WriteString("format", Format);
			json.WriteNumber("version", Version);
			json.WriteString("exportedAt", exportedAt.ToUniversalTime());
			json.WriteStartObject("journal");
			json.WriteString("serverId", profile.ServerId);
			WriteOptional(json, "serverName", profile.ServerName);
			json.WriteString("userId", profile.UserId.ToString("N"));
			WriteOptional(json, "userName", profile.UserName);
			json.WriteEndObject();

			json.WriteStartArray("entries");
			foreach (var entry in entries.Where(x => x.DeletedAt is null))
			{
				json.WriteStartObject();
				json.WriteNumber("id", entry.Id);
				json.WriteString("itemId", entry.ItemId.ToString("N"));
				WriteOptional(json, "seriesId", entry.SeriesId?.ToString("N"));
				WriteOptional(json, "seasonId", entry.SeasonId?.ToString("N"));
				WriteOptional(json, "seriesName", entry.SeriesName);
				WriteOptional(json, "seasonNumber", entry.SeasonNumber);
				WriteOptional(json, "episodeNumber", entry.EpisodeNumber);
				WriteOptional(json, "title", entry.Title);
				if (entry.WatchedAt is { } watched)
				{
					json.WriteString("watchedAt", watched.ToUniversalTime());
				}
				else
				{
					json.WriteNull("watchedAt");
				}

				json.WriteBoolean("dateApproximate", entry.IsDateApproximate);
				json.WriteString("dateSource", JournalStore.Name(entry.DateSource));
				json.WriteString("source", JournalStore.Name(entry.Source));
				json.WriteBoolean("rewatch", entry.Rewatch);
				if (byItem.TryGetValue(entry.ItemId, out var rating))
				{
					json.WriteNumber("rating", rating.Value);
				}

				WriteOptional(json, "review", entry.Review);
				json.WriteBoolean("spoiler", entry.Spoiler);
				json.WriteBoolean("onServer", entry.OnServer);
				json.WriteEndObject();
			}

			json.WriteEndArray();

			json.WriteStartArray("ratings");
			foreach (var rating in ratingList)
			{
				json.WriteStartObject();
				json.WriteString("itemId", rating.ItemId.ToString("N"));
				json.WriteString("kind", JournalStore.Name(rating.Kind));
				json.WriteNumber("rating", rating.Value);
				json.WriteNumber("stars", rating.Value / 2.0);
				WriteOptional(json, "seriesId", rating.SeriesId?.ToString("N"));
				WriteOptional(json, "name", rating.Name);
				json.WriteString("updatedAt", rating.UpdatedAt.ToUniversalTime());
				json.WriteEndObject();
			}

			json.WriteEndArray();
			json.WriteEndObject();
		}

		return Encoding.UTF8.GetString(stream.ToArray());
	}

	/// <summary>RFC 4180 CSV, one line per diary entry. Local dates in <paramref name="timeZone"/>.</summary>
	public static string ToCsv(IEnumerable<DiaryEntry> entries, IEnumerable<ItemRating> ratings, TimeZoneInfo timeZone)
	{
		var byItem = ratings.ToDictionary(x => x.ItemId);
		var csv = new StringBuilder();
		csv.Append(string.Join(',', CsvColumns)).Append("\r\n");
		foreach (var entry in entries.Where(x => x.DeletedAt is null))
		{
			var rating = byItem.TryGetValue(entry.ItemId, out var r) ? r.Value : 0;
			string?[] fields =
			[
				entry.WatchedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
				entry.WatchedAt is { } watched ? TimeZoneInfo.ConvertTime(watched, timeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
				entry.IsDateApproximate ? "true" : "false",
				JournalStore.Name(entry.DateSource),
				Text(entry.SeriesName),
				entry.SeasonNumber?.ToString(CultureInfo.InvariantCulture),
				entry.EpisodeNumber?.ToString(CultureInfo.InvariantCulture),
				Text(entry.Title),
				StarRating.ToStars(rating),
				rating == 0 ? null : rating.ToString(CultureInfo.InvariantCulture),
				entry.Rewatch ? "true" : "false",
				Text(entry.Review),
				entry.Spoiler ? "true" : "false",
				JournalStore.Name(entry.Source),
				entry.OnServer ? "true" : "false",
				entry.ItemId.ToString("N"),
				entry.SeriesId?.ToString("N"),
			];
			csv.Append(string.Join(',', fields.Select(Quote))).Append("\r\n");
		}

		return csv.ToString();
	}

	/// <summary>Free text that a spreadsheet would run as a formula gets a leading apostrophe (CSV injection).</summary>
	private static string? Text(string? value) =>
		value is { Length: > 0 } && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;

	private static string Quote(string? value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return "";
		}

		return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
	}

	private static void WriteOptional(Utf8JsonWriter json, string name, string? value)
	{
		if (value is null)
		{
			json.WriteNull(name);
		}
		else
		{
			json.WriteString(name, value);
		}
	}

	private static void WriteOptional(Utf8JsonWriter json, string name, int? value)
	{
		if (value is { } v)
		{
			json.WriteNumber(name, v);
		}
		else
		{
			json.WriteNull(name);
		}
	}
}
