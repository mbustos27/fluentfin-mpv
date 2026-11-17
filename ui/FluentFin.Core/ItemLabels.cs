using System.Globalization;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core;

/// <summary>
/// fluentfin-mpv: words for what the UI shows as pictures, badges and icons: the name Narrator reads for a media card, the
/// label of an item page's play button and the names of buttons repeated on every row of a list
/// (docs/research/ux-review.md A1, I2, I3).
/// </summary>
public static class ItemLabels
{
	/// <summary>"Inception, 2010, 45% watched, favorite": the card's title and subtitle, then what its badges and progress bar show.</summary>
	public static string CardName(BaseItemViewModel? item)
	{
		if (item?.Dto is not { } dto)
		{
			return string.Empty;
		}

		List<string> parts = [];
		void Add(string? part)
		{
			if (!string.IsNullOrWhiteSpace(part))
			{
				parts.Add(part.Trim());
			}
		}

		var title = dto.Type == BaseItemDto_Type.Episode ? dto.SeriesName : dto.Name;
		Add(title);

		switch (dto.Type)
		{
			case BaseItemDto_Type.Episode:
				Add(EpisodeNumber(dto));
				if (!string.Equals(dto.Name?.Trim(), title?.Trim(), StringComparison.OrdinalIgnoreCase))
				{
					Add(dto.Name);
				}
				break;
			case BaseItemDto_Type.Movie:
				Add(dto.ProductionYear?.ToString(CultureInfo.InvariantCulture));
				break;
			case BaseItemDto_Type.Series:
				Add(SeriesYears(dto));
				break;
		}

		var data = item.UserData;
		var unplayed = data?.UnplayedItemCount ?? 0;
		if (data?.Played == true && unplayed == 0)
		{
			Add("watched");
		}
		else if (unplayed > 0)
		{
			var what = dto.Type is BaseItemDto_Type.Series or BaseItemDto_Type.Season ? "episode" : "item";
			Add(unplayed == 1 ? $"1 unwatched {what}" : $"{unplayed} unwatched {what}s");
		}
		else if (data?.PlayedPercentage is > 0 and < 100 and var percent)
		{
			Add(WatchedPercent(percent));
		}

		if (data?.IsFavorite == true)
		{
			Add("favorite");
		}

		return string.Join(", ", parts);
	}

	/// <summary>Where playback resumes, or null when the item has no resume point (the server keeps one only between its min and max resume percentages).</summary>
	public static TimeSpan? ResumePosition(BaseItemDto? dto) =>
		dto?.UserData?.PlaybackPositionTicks is > 0 and var ticks ? TimeSpan.FromTicks(ticks) : null;

	/// <summary>The item page's play button: "Resume at 12:34" when there is a resume point, else "Play".</summary>
	public static string PlayLabel(BaseItemDto? dto) =>
		ResumePosition(dto) is { } position ? $"Resume at {FormatPosition(position)}" : "Play";

	/// <summary>A position as a player shows it: m:ss under an hour, h:mm:ss from one hour.</summary>
	public static string FormatPosition(TimeSpan position)
	{
		if (position < TimeSpan.Zero)
		{
			position = TimeSpan.Zero;
		}

		return position.TotalHours >= 1
			? string.Create(CultureInfo.InvariantCulture, $"{(int)position.TotalHours}:{position.Minutes:00}:{position.Seconds:00}")
			: string.Create(CultureInfo.InvariantCulture, $"{position.Minutes}:{position.Seconds:00}");
	}

	/// <summary>
	/// The name of a button that every row of an episode list repeats ("Play, episode 3, Pilot"), so a screen reader user who
	/// tabs from row to row knows which episode it acts on.
	/// </summary>
	public static string EpisodeAction(string action, BaseItemDto? episode)
	{
		if (episode is null)
		{
			return action;
		}

		var which = string.Join(", ", new[] { episode.IndexNumber is { } n ? $"episode {n}" : null, episode.Name?.Trim() }
			.Where(x => !string.IsNullOrEmpty(x)));

		return which.Length == 0 ? action : $"{action}, {which}";
	}

	/// <summary>
	/// Seaglass scrolling: the name Narrator reads for an episode row, the row's one keyboard stop ("S1E4 Pilot, watched";
	/// "S2E5-E6 Two Parter, 45% watched, favorite"); its place in the list ("4 of 8") comes from the row's position.
	/// </summary>
	public static string EpisodeRowName(BaseItemDto? episode)
	{
		if (episode is null)
		{
			return string.Empty;
		}

		var code = (episode.ParentIndexNumber, episode.IndexNumber) switch
		{
			({ } season, { } number) => $"S{season}E{number}",
			(null, { } number) => $"E{number}",
			_ => "",
		};
		if (code.Length > 0 && episode.IndexNumberEnd is { } end && end > episode.IndexNumber)
		{
			code += $"-E{end}";
		}

		var title = string.Join(" ", new[] { code, episode.Name?.Trim() ?? "" }.Where(x => x.Length > 0));
		List<string> parts = title.Length > 0 ? [title] : [];

		var data = episode.UserData;
		if (data?.Played == true)
		{
			parts.Add("watched");
		}
		else if (data?.PlayedPercentage is > 0 and < 100 and var percent)
		{
			parts.Add(WatchedPercent(percent));
		}

		if (data?.IsFavorite == true)
		{
			parts.Add("favorite");
		}

		return string.Join(", ", parts);
	}

	/// <summary>
	/// "45% watched" for a progress bar that shows: never 0% (the bar shows for any progress) and never 100% (an item at
	/// 99.6% is not watched; the server says so with Played).
	/// </summary>
	private static string WatchedPercent(double percent) =>
		$"{Math.Clamp((int)Math.Round(percent), 1, 99)}% watched";

	/// <summary>"Account, alice": the title bar button that shows only the user's picture.</summary>
	public static string AccountButtonName(string? userName) =>
		string.IsNullOrWhiteSpace(userName) ? "Account" : $"Account, {userName.Trim()}";

	private static string? EpisodeNumber(BaseItemDto dto) => (dto.ParentIndexNumber, dto.IndexNumber) switch
	{
		({ } season, { } episode) => $"season {season} episode {episode}",
		(null, { } episode) => $"episode {episode}",
		_ => null,
	};

	private static string? SeriesYears(BaseItemDto dto)
	{
		if (dto.ProductionYear is not { } start)
		{
			return null;
		}

		if (string.Equals(dto.Status, "Continuing", StringComparison.OrdinalIgnoreCase))
		{
			return $"{start} to present";
		}

		return dto.EndDate?.Year is { } end && end != start ? $"{start} to {end}" : start.ToString(CultureInfo.InvariantCulture);
	}
}
