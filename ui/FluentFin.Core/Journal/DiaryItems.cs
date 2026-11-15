using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Flurl;

namespace FluentFin.Core.Journal;

/// <summary>One diary line as the Diary page shows it.</summary>
public sealed partial class DiaryEntryViewModel : ObservableObject
{
	public DiaryEntryViewModel(DiaryEntry entry, int rating, string? serverUrl, TimeZoneInfo timeZone, CultureInfo culture)
	{
		Entry = entry;
		Rating = StarRating.Normalize(rating);
		ImageUrl = JournalImages.ForEntry(serverUrl, entry, 180);
		Local = entry.WatchedAt is { } watched ? TimeZoneInfo.ConvertTime(watched, timeZone) : null;
		DayText = Local?.ToString("%d", culture) ?? "?";
		WeekdayText = Local?.ToString("ddd", culture) ?? "";
		DateText = Local is { } local
			? (entry.IsDateApproximate ? "about " : "") + local.ToString("D", culture) + (entry.IsDateApproximate ? "" : ", " + local.ToString("t", culture))
			: "Date unknown";
		DateNote = entry.DateSource switch
		{
			DiaryDateSource.Session => "Logged when it finished playing here",
			DiaryDateSource.PlaybackReporting => "Date from the Playback Reporting plugin",
			DiaryDateSource.LastPlayed => "Approximate: Jellyfin's last-played date",
			DiaryDateSource.Manual => "Date set by you",
			_ => "Jellyfin has it as played, with no date",
		};
	}

	public DiaryEntry Entry { get; }

	public long Id => Entry.Id;

	/// <summary>The time it was watched in the user's time zone; null when unknown.</summary>
	public DateTimeOffset? Local { get; }

	public string SeriesName => string.IsNullOrWhiteSpace(Entry.SeriesName) ? "Unknown series" : Entry.SeriesName;

	public string EpisodeCode => JournalText.EpisodeCode(Entry.SeasonNumber, Entry.EpisodeNumber);

	public string Title => Entry.Title ?? "";

	public string DayText { get; }

	public string WeekdayText { get; }

	public string DateText { get; }

	public string DateNote { get; }

	public bool IsApproximate => Entry.IsDateApproximate;

	public bool Rewatch => Entry.Rewatch;

	public bool NotOnServer => !Entry.OnServer;

	public string? Review => Entry.Review;

	public bool HasReview => !string.IsNullOrWhiteSpace(Entry.Review);

	public string? ImageUrl { get; }

	/// <summary>The episode's rating, 0-10.</summary>
	[ObservableProperty]
	public partial int Rating { get; set; }

	public string RatingText => StarRating.Describe(Rating);

	partial void OnRatingChanged(int value) => OnPropertyChanged(nameof(RatingText));
}

/// <summary>A month of the diary (or the entries with no date), newest first.</summary>
public sealed class DiaryMonthGroup(string title, IEnumerable<DiaryEntryViewModel> entries) : List<DiaryEntryViewModel>(entries)
{
	public string Title { get; } = title;

	public string CountText => Count == 1 ? "1 episode" : $"{Count} episodes";
}

/// <summary>An entry of the year or show filter. "All" has no year and no show.</summary>
public sealed record DiaryFilterOption(string Label, int? Year = null, string? ShowKey = null)
{
	public override string ToString() => Label;
}

public static class JournalText
{
	/// <summary>"S02 E05"; "S00" is a special; parts that are not known are left out.</summary>
	public static string EpisodeCode(int? season, int? episode)
	{
		var parts = new List<string>(2);
		if (season is { } s)
		{
			parts.Add(s == 0 ? "Special" : $"S{s:00}");
		}

		if (episode is { } e)
		{
			parts.Add($"E{e:00}");
		}

		return string.Join(' ', parts);
	}

	/// <summary>How a show is told apart in the filter: its series id, or its name when the entry has no id.</summary>
	public static string ShowKey(DiaryEntry entry) => entry.SeriesId is { } id ? id.ToString("N") : "name:" + (entry.SeriesName ?? "").Trim().ToLowerInvariant();
}

public static class JournalImages
{
	/// <summary>
	/// The episode's still, else the series' poster (<c>/Items/{id}/Images/Primary</c>, no token: Jellyfin serves images
	/// without one). Null without a server url or anything to show.
	/// </summary>
	public static string? ForEntry(string? serverUrl, DiaryEntry entry, int height)
	{
		if (string.IsNullOrEmpty(serverUrl))
		{
			return null;
		}

		if (!string.IsNullOrEmpty(entry.ImageTag))
		{
			return Primary(serverUrl, entry.ItemId, entry.ImageTag, height);
		}

		return entry.SeriesId is { } seriesId ? Primary(serverUrl, seriesId, entry.SeriesImageTag, height) : null;
	}

	private static string Primary(string serverUrl, Guid id, string? tag, int height)
	{
		var url = serverUrl.AppendPathSegments("Items", id.ToString("N"), "Images", "Primary").SetQueryParam("fillHeight", height);
		if (!string.IsNullOrEmpty(tag))
		{
			url.SetQueryParam("tag", tag);
		}

		return url.ToString();
	}
}
