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
		// sg-cw: an episode watched again on Jellyfin 12 is watched and in progress at once: its card shows the progress bar,
		// and its name says so (a Continue Watching card named "watched")
		if (data?.Played == true && unplayed == 0 && !(data.PlaybackPositionTicks > 0 && data.PlayedPercentage is > 0 and < 100))
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
	/// Seaglass scrolling: the name Narrator reads for an episode row, the row's one keyboard stop ("S1 E4 Pilot, watched";
	/// "S2 E5–E6 Two Parter, 45% watched, favorite"); its place in the list ("4 of 8") comes from the row's position.
	/// </summary>
	public static string EpisodeRowName(BaseItemDto? episode)
	{
		if (episode is null)
		{
			return string.Empty;
		}

		var title = EpisodeSpoken(episode);
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
	/// 1.2.0 (1.1.3 UX audit, layout item 7): THE way the app writes an episode's number, everywhere (it had five):
	/// "S1 E4", "S2 E5–E6" for a double episode, "E7" without a season, empty without a number. Cards, rows, heroes, Play and the
	/// player all call this (the converters too: BaseItemDtoConverters, JellyfinSubtitleConverter).
	/// </summary>
	public static string EpisodeCode(BaseItemDto? episode) =>
		EpisodeCode(episode?.ParentIndexNumber, episode?.IndexNumber, episode?.IndexNumberEnd);

	/// <inheritdoc cref="EpisodeCode(BaseItemDto?)"/>
	public static string EpisodeCode(int? season, int? number, int? end = null)
	{
		var code = (season, number) switch
		{
			({ } s, { } n) => $"S{s} E{n}",
			(null, { } n) => $"E{n}",
			_ => "",
		};
		if (code.Length > 0 && end is { } last && last > number)
		{
			code += $"–E{last}";
		}

		return code;
	}

	/// <summary>
	/// 1.2.0: an episode's number and title as the app shows them, "S1 E4 · Pilot" (the code alone without a title, the
	/// title alone without a number). The one visible format; see <see cref="EpisodeCode(BaseItemDto?)"/>.
	/// </summary>
	public static string EpisodeTitle(BaseItemDto? episode) =>
		string.Join(" · ", new[] { EpisodeCode(episode), episode?.Name?.Trim() ?? "" }.Where(x => x.Length > 0));

	/// <summary>The same for Narrator, without the dot: "S1 E4 Pilot".</summary>
	internal static string EpisodeSpoken(BaseItemDto? episode) =>
		string.Join(" ", new[] { EpisodeCode(episode), episode?.Name?.Trim() ?? "" }.Where(x => x.Length > 0));

	/// <summary>
	/// 1.2.0: the item a card's or an item page's button acts on, in its name and tooltip, so thirty cards do not
	/// read as thirty "Play" buttons (Seaglass UX review, fix 4): "Inception"; "Season 2 of Lost"; an episode with its series
	/// and code, "The Office S2 E3 The Fight" (a name that only repeats the series is left out).
	/// </summary>
	public static string ActionTarget(BaseItemDto? dto)
	{
		if (dto is null)
		{
			return "";
		}

		var name = dto.Name?.Trim() ?? "";
		var series = dto.SeriesName?.Trim() ?? "";
		switch (dto.Type)
		{
			case BaseItemDto_Type.Episode:
				if (string.Equals(name, series, StringComparison.OrdinalIgnoreCase))
				{
					name = "";
				}
				return string.Join(" ", new[] { series, EpisodeCode(dto), name }.Where(x => x.Length > 0));
			case BaseItemDto_Type.Season when series.Length > 0:
				return name.Length > 0 ? $"{name} of {series}" : series;
			default:
				return name;
		}
	}

	/// <summary>"Play Inception" (a card's Play).</summary>
	/// <remarks>1.2.0 (Nixie button review, I3): "Resume Inception" when the item has a resume point, as the click does.</remarks>
	public static string PlayAction(BaseItemDto? dto) =>
		Phrase(ResumePosition(dto) is null || dto?.Type is BaseItemDto_Type.Series or BaseItemDto_Type.Season ? "Play" : "Resume", ActionTarget(dto), "");

	/// <summary>
	/// "Play Inception from start" (next to Resume on a movie or an episode page; 1.2.0, Nixie button review I37: the button
	/// reads "Play from start").
	/// </summary>
	public static string PlayFromBeginningAction(BaseItemDto? dto) => Phrase("Play", ActionTarget(dto), "from start");

	/// <summary>
	/// The watched toggle says what a click does now, as the item's menu does: "Mark Inception as watched", or "... as
	/// unwatched" while it shows watched. <paramref name="watched"/> is the toggle's state (what the user sees).
	/// </summary>
	public static string WatchedAction(BaseItemDto? dto, bool? watched) =>
		Phrase("Mark", ActionTarget(dto), watched == true ? "as unwatched" : "as watched");

	/// <summary>"Add Inception to favorites", or "Remove Inception from favorites" while the toggle shows a favourite.</summary>
	public static string FavoriteAction(BaseItemDto? dto, bool? favorite) =>
		favorite == true ? Phrase("Remove", ActionTarget(dto), "from favorites") : Phrase("Add", ActionTarget(dto), "to favorites");

	/// <summary>"More options for Inception".</summary>
	public static string MoreOptions(BaseItemDto? dto) => Phrase("More options", ActionTarget(dto).Length > 0 ? "for " + ActionTarget(dto) : "", "");

	/// <summary>
	/// An episode row's watched toggle, named as its Play is ("Mark as unwatched, episode 3, Pilot"); see <see cref="WatchedAction"/>.
	/// </summary>
	public static string EpisodeWatchedAction(BaseItemDto? episode, bool? watched) =>
		EpisodeAction(watched == true ? "Mark as unwatched" : "Mark as watched", episode);

	private static string Phrase(string verb, string target, string tail) =>
		string.Join(" ", new[] { verb, target, tail }.Where(x => x.Length > 0));

	/// <summary>
	/// "45% watched" for a progress bar that shows: never 0% (the bar shows for any progress) and never 100% (an item at
	/// 99.6% is not watched; the server says so with Played).
	/// </summary>
	internal static string WatchedPercent(double percent) =>
		$"{Math.Clamp((int)Math.Round(percent), 1, 99)}% watched";

	/// <summary>"Account, alice": the title bar button that shows only the user's picture.</summary>
	public static string AccountButtonName(string? userName) =>
		string.IsNullOrWhiteSpace(userName) ? "Account" : $"Account, {userName.Trim()}";

	/// <summary>The Account button's tooltip (button review T8): "Account (alice)".</summary>
	public static string AccountTooltip(string? userName) =>
		string.IsNullOrWhiteSpace(userName) ? "Account" : $"Account ({userName.Trim()})";

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
