using System.Globalization;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

// Nixie 1.3.0, the Drive-in layout theme: what its series, season, movie and episode pages show, over the shared view
// models. Additive only.
public partial class SeriesViewModel
{
	/// <summary>Drive-in: the episode of the shown season Play would start (in progress, else the series' Next Up), for the board's NEXT and the time left.</summary>
	public BaseItemDto? DriveInNextEpisode() => DriveInItems.NextEpisode(Episodes, NextUp?.Dto);
}

public partial class SeasonViewModel
{
	/// <summary>Drive-in: the episode of this season Play would start (in progress, else the first not watched).</summary>
	public BaseItemDto? DriveInNextEpisode() => DriveInItems.NextEpisode(Episodes ?? [], null);
}

/// <summary>Nixie 1.3.0, Drive-in: the item pages' marquee board and the SHOWTIMES board of a season.</summary>
public static class DriveInItems
{
	/// <summary>
	/// The episode Play starts among <paramref name="episodes"/>: one in progress first, else <paramref name="nextUp"/> when it
	/// is one of them, else the first not watched (null when all are, or there are none).
	/// </summary>
	public static BaseItemDto? NextEpisode(IReadOnlyList<BaseItemDto> episodes, BaseItemDto? nextUp)
	{
		if (episodes.FirstOrDefault(e => e.UserData is { Played: not true, PlaybackPositionTicks: > 0 }) is { } resume)
		{
			return resume;
		}

		if (nextUp?.Id is { } id && episodes.FirstOrDefault(e => e.Id == id) is { } next)
		{
			return next;
		}

		return episodes.FirstOrDefault(e => e.UserData?.Played != true);
	}

	/// <summary>
	/// The board's line beside the season tubes: "8 episodes · 6 h 12 m · 2 watched" (parts left out when unknown). Nixie 1.4.0:
	/// empty for no list too: Back to the season page from the player bound it before the episodes came (an unhandled
	/// NullReferenceException in every overlay stress run).
	/// </summary>
	public static string SeasonSummary(IReadOnlyList<BaseItemDto>? episodes)
	{
		if (episodes is not { Count: > 0 })
		{
			return "";
		}

		var parts = new List<string> { episodes.Count == 1 ? "1 episode" : $"{episodes.Count} episodes" };
		var ticks = episodes.Sum(e => e.RunTimeTicks ?? 0);
		if (ticks > 0)
		{
			var total = TimeSpan.FromTicks(ticks);
			var hours = (int)total.TotalHours;
			parts.Add(hours > 0 ? string.Create(CultureInfo.InvariantCulture, $"{hours} h {total.Minutes} m") : $"{Math.Max(1, (int)Math.Round(total.TotalMinutes))} m");
		}

		var watched = episodes.Count(e => e.UserData?.Played == true);
		if (watched > 0)
		{
			parts.Add($"{watched} watched");
		}

		return string.Join(" · ", parts);
	}

	/// <summary>A season's tube: its number ("1"), or null for a season with none or 0 (Specials: shown by its name, "Extras" on the board).</summary>
	public static string? SeasonTube(BaseItemDto? season) =>
		season?.IndexNumber is > 0 and < 1000 and var number ? number.ToString(CultureInfo.InvariantCulture) : null;

	/// <summary>The name a season without a tube shows: "Extras" for Specials (season 0), else its own name.</summary>
	public static string SeasonLabel(BaseItemDto? season) =>
		season?.IndexNumber is 0 ? "Extras" : string.IsNullOrWhiteSpace(season?.Name) ? "Season" : season.Name.Trim();

	/// <summary>"Starring" on the art: the first three actors (empty without any).</summary>
	public static string Starring(BaseItemDto? dto) =>
		string.Join(", ", (dto?.People ?? []).Where(p => p.Type is BaseItemPerson_Type.Actor or BaseItemPerson_Type.GuestStar)
			.Select(p => p.Name?.Trim()).Where(n => !string.IsNullOrEmpty(n)).Distinct().Take(3));

	/// <summary>"Created by" (a series) or "Directed by" (anything else), and who: the first two creators or directors.</summary>
	public static (string Label, string Names) MadeBy(BaseItemDto? dto)
	{
		var series = dto?.Type == BaseItemDto_Type.Series;
		var kinds = series ? new[] { BaseItemPerson_Type.Creator, BaseItemPerson_Type.Director, BaseItemPerson_Type.Writer } : [BaseItemPerson_Type.Director];
		foreach (var kind in kinds)
		{
			var names = (dto?.People ?? []).Where(p => p.Type == kind).Select(p => p.Name?.Trim()).Where(n => !string.IsNullOrEmpty(n)).Distinct().Take(2).ToList();
			if (names.Count > 0)
			{
				return (series ? "Created by" : "Directed by", string.Join(", ", names));
			}
		}

		return ("", "");
	}

	/// <summary>
	/// The board's kicker over the title: "NOW SHOWING" on every item page (a season's and an episode's title is already the
	/// series', their own name under it), "FEATURE PRESENTATION" with nothing to show.
	/// </summary>
	public static string Kicker(BaseItemDto? dto) => dto is null ? "FEATURE PRESENTATION" : "NOW SHOWING";

	/// <summary>A showtime's facts line: "52 MIN · 28 MAR 2022".</summary>
	public static string ShowtimeFacts(BaseItemDto? episode) =>
		string.Join(" · ", new[] { DriveInText.Runtime(episode?.RunTimeTicks), DriveInText.ShowDate(episode?.PremiereDate) }.Where(x => x.Length > 0));
}
