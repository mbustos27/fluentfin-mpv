using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluentFin.Core.Journal;

/// <summary>
/// The Playback Reporting plugin's play history, through its custom-query endpoint (<c>POST /user_usage_stats/submit_custom_query</c>,
/// the one its own "Custom Query" page uses; <c>RequiresElevation</c>, so administrators only). The plugin is optional and
/// its API is not documented: the shape below is from its source (jellyfin-plugin-playbackreporting, master, read
/// 2026-11-15), not tried against the user's server (docs/JOURNAL.md). Anything unreadable is "not available", never an
/// error the user sees.
/// </summary>
public static partial class PlaybackReportingQuery
{
	public const string Path = "user_usage_stats/submit_custom_query";

	/// <summary>
	/// The episodes this user played, oldest first. The user id is a <see cref="Guid"/> we format, so nothing from outside goes
	/// into the SQL. The plugin's tables keep ids as text, with or without dashes: compared without them.
	/// </summary>
	public static string For(Guid userId) =>
		"SELECT DateCreated, ItemId, PlayDuration FROM PlaybackActivity " +
		$"WHERE replace(lower(UserId), '-', '') = '{userId:N}' AND ItemType = 'Episode' AND PlayDuration > 0 ORDER BY DateCreated";

	/// <summary>
	/// Reads the plugin's answer, <c>{"colums": [...], "results": [[...], ...], "message": ""}</c> (its own spelling; "columns"
	/// is read too). Values may be text or numbers. Rows that cannot be read are skipped.
	/// <para>
	/// The plugin records a play's <c>DateCreated</c> as <c>DateTime.Now</c> on the server and writes a local time with no
	/// zone (<c>yyyy-MM-dd HH:mm:ss.FFFFFFF</c>; its EventMonitorEntryPoint and SqliteExtensions.ToDateTimeParamValue, read
	/// 2026-11-15). So a time with no zone is the server's wall clock: read in <paramref name="serverZone"/> (the caller
	/// passes this PC's zone, and the sync corrects it with <see cref="ClockShift"/>). A time with a zone is taken as it is.
	/// </para>
	/// </summary>
	public static PlaybackHistory Parse(string json, TimeZoneInfo serverZone)
	{
		JsonObject? root;
		try
		{
			root = JsonNode.Parse(json) as JsonObject;
		}
		catch (JsonException)
		{
			return PlaybackHistory.Unavailable("not JSON");
		}

		if (root is null || (root["colums"] ?? root["columns"]) is not JsonArray columns || root["results"] is not JsonArray results)
		{
			var message = root?["message"]?.GetValueKind() == JsonValueKind.String ? root["message"]!.GetValue<string>() : null;
			return PlaybackHistory.Unavailable(string.IsNullOrWhiteSpace(message) ? "no results" : message);
		}

		int Column(string name)
		{
			for (var i = 0; i < columns.Count; i++)
			{
				if (columns[i]?.GetValueKind() == JsonValueKind.String && string.Equals(columns[i]!.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase))
				{
					return i;
				}
			}

			return -1;
		}

		var date = Column("DateCreated");
		var item = Column("ItemId");
		var duration = Column("PlayDuration");
		if (date < 0 || item < 0 || duration < 0)
		{
			return PlaybackHistory.Unavailable("unexpected columns");
		}

		var plays = new List<PlaybackHistoryPlay>();
		foreach (var row in results.OfType<JsonArray>())
		{
			if (Text(row, item) is { } itemText && Guid.TryParse(itemText, out var itemId) &&
				Text(row, date) is { } dateText && ReadTime(dateText, serverZone) is { } started &&
				Text(row, duration) is { } durationText && double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
			{
				plays.Add(new PlaybackHistoryPlay(itemId, started, TimeSpan.FromSeconds(seconds)));
			}
		}

		return new PlaybackHistory(true, plays, null);
	}

	private static DateTimeOffset? ReadTime(string text, TimeZoneInfo zone)
	{
		text = text.Trim();
		if (HasZone().IsMatch(text))
		{
			return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var zoned) ? zoned : null;
		}

		if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall))
		{
			return null;
		}

		// a wall-clock time; in a daylight-saving gap or overlap the zone's standard offset (never throws)
		wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
		return new DateTimeOffset(wall, zone.GetUtcOffset(wall)).ToUniversalTime();
	}

	[System.Text.RegularExpressions.GeneratedRegex(@"(Z|[+-]\d\d:?\d\d)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
	private static partial System.Text.RegularExpressions.Regex HasZone();

	/// <summary>
	/// How far the plugin's times, read in this PC's zone, are from Jellyfin's own dates of the same plays: the median of
	/// (<c>LastPlayedDate</c> - start of the episode's last play), over pairs within 14 hours of each other (Jellyfin sets
	/// <c>LastPlayedDate</c> when a play starts, the plugin records it then too). Zero with fewer than three pairs, or a median
	/// under 30 minutes (same zone); otherwise rounded to a quarter hour: the server runs in another zone.
	/// </summary>
	public static TimeSpan ClockShift(IEnumerable<(DateTimeOffset PlayStarted, DateTimeOffset LastPlayed)> pairs)
	{
		var differences = pairs.Select(x => x.LastPlayed - x.PlayStarted).Where(x => x.Duration() <= TimeSpan.FromHours(14)).Order().ToList();
		if (differences.Count < 3)
		{
			return TimeSpan.Zero;
		}

		var median = differences[differences.Count / 2];
		if (median.Duration() < TimeSpan.FromMinutes(30))
		{
			return TimeSpan.Zero;
		}

		var quarter = TimeSpan.FromMinutes(15).Ticks;
		return TimeSpan.FromTicks((long)Math.Round(median.Ticks / (double)quarter, MidpointRounding.AwayFromZero) * quarter);
	}

	private static string? Text(JsonArray row, int index)
	{
		if (index >= row.Count || row[index] is not JsonValue value)
		{
			return null;
		}

		return value.GetValueKind() switch
		{
			JsonValueKind.String => value.GetValue<string>(),
			JsonValueKind.Number => value.ToJsonString(),
			_ => null,
		};
	}
}
