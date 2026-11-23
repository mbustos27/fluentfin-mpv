using System.Globalization;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

// Nixie 1.3.0, the Drive-in layout theme (ui\FluentFin\Themes\DriveIn): what its Home shows, over the same view model as
// every theme. Additive only: the view reads these; nothing here changes what the shared Home loads or shows.
public partial class HomeViewModel
{
	/// <summary>Drive-in: the outdoor screen's features (<see cref="DriveInHome.Features"/>), from what the page shows now.</summary>
	public IReadOnlyList<HeroSlide> DriveInFeatures() => DriveInHome.Features(ContinueItems.Select(x => x.Dto), Hero);

	/// <summary>
	/// Drive-in: how many items "All of Up next" opens: Next Up, the sign's own list (its entries are its first three or four).
	/// Nixie 1.7.3: it counted Continue Watching too, so with Next Up empty or short the link read "All of Up next (3)" over fewer
	/// entries, and opened what the Continue watching row already shows.
	/// </summary>
	public int DriveInUpNextCount => DriveInUpNext().Count;

	/// <summary>Drive-in: what "All of Up next (N)" opens: Next Up, the list the sign's entries come from.</summary>
	public IReadOnlyList<BaseItemViewModel> DriveInUpNext() => [.. NextUpItems];
}

/// <summary>
/// Nixie 1.3.0, Drive-in: Home is a drive-in at dusk. The featured title plays on the outdoor screen with Resume (or Play)
/// focused when Home opens; a motel sign lists what is coming up (Next Up), one click each; Continue Watching runs under
/// the lot with what the screen does not already show.
/// </summary>
public static class DriveInHome
{
	/// <summary>The per-PC setting "Compact Home": the lot folds into a strip and the sign's entries become a row (default off).</summary>
	public const string CompactHomeKey = "DriveInCompactHome";

	/// <summary>
	/// Makes <paramref name="shown"/> hold <paramref name="target"/> (same items, same order) by removing, moving and inserting
	/// only what differs, so a row sees a removal as a removal (its focus follows to the next card) and keeps the cards it
	/// still shows.
	/// </summary>
	public static void Sync<T>(System.Collections.ObjectModel.ObservableCollection<T> shown, IReadOnlyList<T> target) where T : class
	{
		for (var i = shown.Count - 1; i >= 0; i--)
		{
			if (!target.Contains(shown[i]))
			{
				shown.RemoveAt(i);
			}
		}

		for (var i = 0; i < target.Count; i++)
		{
			var at = shown.IndexOf(target[i]);
			if (at == i)
			{
				continue;
			}

			if (at >= 0)
			{
				shown.Move(at, i);
			}
			else
			{
				shown.Insert(i, target[i]);
			}
		}
	}

	/// <summary>How many Next Up items the Coming up sign lists: four on a wide window, three on a narrow one.</summary>
	public static int SignEntries(bool wide) => wide ? 4 : 3;

	/// <summary>
	/// The screen's features: the last thing the user was watching first (Continue Watching's first item, which the server
	/// orders by when it was played), so Resume is one Enter away on arrival; then the shared hero's slides (new to the user,
	/// never Continue Watching's), up to <paramref name="max"/>. An item with no backdrop to project is left out.
	/// </summary>
	public static IReadOnlyList<HeroSlide> Features(IEnumerable<BaseItemDto> continueWatching, IReadOnlyList<HeroSlide> hero, int max = HeroSlides.MaxSlides)
	{
		var features = new List<HeroSlide>(max);
		if (continueWatching.FirstOrDefault() is { } last && HeroSlides.Make(last) is { } resume)
		{
			features.Add(resume);
		}

		foreach (var slide in hero)
		{
			if (features.Count >= max)
			{
				break;
			}

			if (features.All(f => f.Key != slide.Key))
			{
				features.Add(slide);
			}
		}

		return features;
	}

	/// <summary>The Continue watching row under the lot: Continue Watching less the item the screen features.</summary>
	public static List<T> ContinueRow<T>(IEnumerable<T> items, Func<T, Guid?> id, IReadOnlyList<HeroSlide> features)
	{
		var featured = features.FirstOrDefault(f => f.IsResume)?.Dto.Id;
		return [.. items.Where(x => featured is null || id(x) != featured)];
	}

	/// <summary>"5 in progress" beside the Continue watching marquee; empty when nothing is.</summary>
	public static string InProgressText(int count) => count <= 0 ? "" : $"{count} in progress";

	/// <summary>The sign's link to everything up next: "All of Up next (9)" (the grid it opens is "Up next" too).</summary>
	public static string AllUpNextText(int count) => $"All of Up next ({count})";

	/// <summary>
	/// What Narrator reads for one of the sign's entries, the button that opens it: what the entry shows, the series, the code
	/// and the episode ("A Signal in Winter S2 E1 Static"), as a card is named. Nixie 1.4.0 (the UX review's X12): Home's
	/// next-up item opens its episode in every theme (in 1.3.0 the sign's entries played it, Classic's and Cinematic's opened it).
	/// </summary>
	public static string UpNextName(BaseItemDto? dto) => ItemLabels.ActionTarget(dto);

	/// <summary>The title a sign entry or a card shows on its first line: the series of an episode, else the item's name.</summary>
	public static string EntryTitle(BaseItemDto? dto) =>
		dto is null ? "" : dto.Type == BaseItemDto_Type.Episode && !string.IsNullOrWhiteSpace(dto.SeriesName) ? dto.SeriesName.Trim() : dto.Name?.Trim() ?? "";

	/// <summary>The second line's words after the nixie code: an episode's name (empty for a movie).</summary>
	public static string EntryDetail(BaseItemDto? dto) =>
		dto?.Type == BaseItemDto_Type.Episode ? dto.Name?.Trim() ?? "" : "";
}

/// <summary>
/// Nixie 1.3.0, Drive-in: the numbers it shows in nixie tubes, and the words Narrator hears for them. Orange nixie digits
/// are numbers you read (the theme's colour rule); every tube readout exposes its text ("44 minutes left").
/// </summary>
public static class DriveInText
{
	/// <summary>
	/// An episode's code as tubes show it, "S1 E03": the app's one shape ("S1 E4", ItemLabels.EpisodeCode), the episode in two
	/// digits as a showtime board pads it (Nixie 1.4.0, the UX review's DI-m5: it was "S1·E03"); "E07" with no season; empty
	/// without a number.
	/// </summary>
	public static string NixieCode(BaseItemDto? dto) => NixieCode(dto?.ParentIndexNumber, dto?.IndexNumber, dto?.IndexNumberEnd);

	/// <inheritdoc cref="NixieCode(BaseItemDto?)"/>
	public static string NixieCode(int? season, int? episode, int? end = null)
	{
		if (episode is not { } number)
		{
			return "";
		}

		var code = string.Create(CultureInfo.InvariantCulture, $"E{number:00}");
		if (end is { } last && last > number)
		{
			code += string.Create(CultureInfo.InvariantCulture, $"-{last:00}");
		}

		return season is { } s ? string.Create(CultureInfo.InvariantCulture, $"S{s} {code}") : code;
	}

	/// <summary>Two-digit episode number for a showtime board's tubes: "03"; "12"; "103".</summary>
	public static string EpisodeNumber(int? number) => number is { } n ? n.ToString("00", CultureInfo.InvariantCulture) : "";

	/// <summary>What is left of an item, or null when it has no resume point or no runtime.</summary>
	public static TimeSpan? Left(BaseItemDto? dto)
	{
		var position = dto?.UserData?.PlaybackPositionTicks ?? 0;
		var runtime = dto?.RunTimeTicks ?? 0;
		if (position <= 0 || runtime <= 0)
		{
			return null;
		}

		var left = TimeSpan.FromTicks(Math.Max(0, runtime - position));
		return left > TimeSpan.Zero ? left : null;
	}

	/// <summary>
	/// The time left as tubes show it: minutes under an hour ("44" with the unit "MIN LEFT"), hours and minutes from an hour
	/// ("1:12" with "LEFT"); the spoken words for Narrator ("44 minutes left", "1 hour 12 minutes left"). Rounded up to the
	/// minute, so the last seconds still read "1".
	/// </summary>
	public static (string Digits, string Unit, string Spoken)? TimeLeft(TimeSpan? left)
	{
		if (left is not { } value || value <= TimeSpan.Zero)
		{
			return null;
		}

		var minutes = (int)Math.Ceiling(value.TotalMinutes);
		if (minutes < 60)
		{
			return (minutes.ToString(CultureInfo.InvariantCulture), "MIN LEFT", minutes == 1 ? "1 minute left" : $"{minutes} minutes left");
		}

		var hours = minutes / 60;
		var rest = minutes % 60;
		var spoken = (hours == 1 ? "1 hour" : $"{hours} hours") + (rest == 0 ? "" : rest == 1 ? " 1 minute" : $" {rest} minutes") + " left";
		return (string.Create(CultureInfo.InvariantCulture, $"{hours}:{rest:00}"), "LEFT", spoken);
	}

	/// <summary>A runtime as a showtime board prints it: "52 MIN"; "1 H 58 MIN" from an hour; empty without one.</summary>
	public static string Runtime(long? ticks)
	{
		if (ticks is not { } value || value <= 0)
		{
			return "";
		}

		var minutes = (int)Math.Round(TimeSpan.FromTicks(value).TotalMinutes);
		return minutes < 60 ? $"{Math.Max(1, minutes)} MIN" : $"{minutes / 60} H {minutes % 60:00} MIN";
	}

	/// <summary>A date as a showtime board prints it, "14 MAR 2022" (invariant month names); empty without one.</summary>
	public static string ShowDate(DateTimeOffset? date) =>
		date is { } d ? d.ToString("d MMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant() : "";

	/// <summary>A clock time for the player's "ends at" tubes, "10:42" in 12-hour time, and its half of the day ("PM").</summary>
	public static (string Digits, string Half) Clock(DateTime time) =>
		(time.ToString("h:mm", CultureInfo.InvariantCulture), time.ToString("tt", CultureInfo.InvariantCulture).ToUpperInvariant());

	/// <summary>A playback position for the console's tubes: "0:50:52" (hours always shown, as a console's counter is).</summary>
	public static string Counter(TimeSpan position)
	{
		if (position < TimeSpan.Zero)
		{
			position = TimeSpan.Zero;
		}

		return string.Create(CultureInfo.InvariantCulture, $"{(int)position.TotalHours}:{position.Minutes:00}:{position.Seconds:00}");
	}

	/// <summary>The same position in words, for Narrator: "50 minutes 52 seconds", "1 hour 2 minutes 3 seconds".</summary>
	public static string CounterSpoken(TimeSpan position)
	{
		if (position < TimeSpan.Zero)
		{
			position = TimeSpan.Zero;
		}

		var parts = new List<string>();
		var hours = (int)position.TotalHours;
		if (hours > 0)
		{
			parts.Add(hours == 1 ? "1 hour" : $"{hours} hours");
		}

		if (position.Minutes > 0)
		{
			parts.Add(position.Minutes == 1 ? "1 minute" : $"{position.Minutes} minutes");
		}

		parts.Add(position.Seconds == 1 ? "1 second" : $"{position.Seconds} seconds");
		return string.Join(" ", parts);
	}
}
