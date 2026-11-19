using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Mpv;

/// <summary>One chapter of the playing item, as the player's Chapters menu shows it.</summary>
/// <param name="Name">The chapter's name, or "Chapter 3" when the file names none.</param>
/// <param name="StartTicks">Where it starts.</param>
/// <param name="Time">Its start as the player writes times ("12:34", "1:02:03").</param>
/// <param name="IsCurrent">The position is in it now.</param>
public sealed record PlayerChapter(string Name, long StartTicks, string Time, bool IsCurrent)
{
	/// <summary>The menu item's words: the time, then the name ("12:34  The Lamp Room").</summary>
	public string Label => $"{Time}  {Name}";

	/// <summary>What Narrator reads for it: "Go to The Lamp Room, 12:34", and ", playing now" for the current one.</summary>
	public string SpokenName => $"Go to {Name}, {Time}{(IsCurrent ? ", playing now" : "")}";
}

/// <summary>
/// Nixie 1.4.0 (the UX review's E-M10, plan item 14): the shared bar's Chapters menu, so every theme reaches the item's chapters
/// (Editorial had no way to them; Cinematic had its panel's Chapters tab only). The chapters are the server's (the item's
/// <c>Chapters</c>), in order of their start; none, the button is disabled and says so.
/// </summary>
public static class PlayerChapters
{
	/// <summary>The chapters in order, each with its time, the one the position is in marked.</summary>
	public static IReadOnlyList<PlayerChapter> From(IEnumerable<ChapterInfo>? chapters, long positionTicks)
	{
		var ordered = (chapters ?? []).Where(c => c.StartPositionTicks is >= 0).OrderBy(c => c.StartPositionTicks).ToList();
		var current = ordered.FindLastIndex(c => c.StartPositionTicks <= positionTicks);
		return [.. ordered.Select((c, i) => new PlayerChapter(
			string.IsNullOrWhiteSpace(c.Name) ? $"Chapter {i + 1}" : c.Name.Trim(),
			c.StartPositionTicks ?? 0,
			MpvControlsViewModel.FormatTime(TimeSpan.FromTicks(c.StartPositionTicks ?? 0)),
			i == current))];
	}

	/// <summary>The button's tooltip: "Chapters", or why it does nothing ("No chapters").</summary>
	public static string ToolTip(int count) => count > 0 ? "Chapters" : "No chapters";

	/// <summary>The button's accessible name: what it opens and how many ("Chapters, 6"), or "Chapters, none".</summary>
	public static string ButtonName(int count) => count > 0 ? $"Chapters, {count}" : "Chapters, none";
}
