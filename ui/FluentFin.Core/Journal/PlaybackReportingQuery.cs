using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluentFin.Core.Journal;

/// <summary>
/// The Playback Reporting plugin's play history, through its custom-query endpoint (<c>POST /user_usage_stats/submit_custom_query</c>,
/// the one its own "Custom Query" page uses; administrators only). The plugin is optional and its API is not documented:
/// the shape below is from its source and NOT verified against the user's server (docs/JOURNAL.md). Anything unreadable
/// is "not available", never an error the user sees.
/// </summary>
public static class PlaybackReportingQuery
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
	/// is read too). Values may be text or numbers. <c>DateCreated</c> without a zone is taken as UTC (not verified: it may be
	/// the server's local time, which would shift dates by hours). Rows that cannot be read are skipped.
	/// </summary>
	public static PlaybackHistory Parse(string json)
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
				Text(row, date) is { } dateText && DateTimeOffset.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var started) &&
				Text(row, duration) is { } durationText && double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
			{
				plays.Add(new PlaybackHistoryPlay(itemId, started, TimeSpan.FromSeconds(seconds)));
			}
		}

		return new PlaybackHistory(true, plays, null);
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
