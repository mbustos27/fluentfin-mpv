using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core;

/// <summary>One season in the season page's picker: <see cref="IsCurrent"/> is the season being shown.</summary>
public sealed record SeasonPill(BaseItemDto Dto, bool IsCurrent)
{
	public string Name => string.IsNullOrWhiteSpace(Dto.Name) ? $"Season {Dto.IndexNumber}" : Dto.Name;

	/// <summary>What Narrator reads: the season, and that it is the one on screen.</summary>
	public string AutomationName => IsCurrent ? $"{Name}, current season" : Name;
}

/// <summary>Seaglass: the other seasons of a series and the episode after this one, for the season and episode pages.</summary>
public static class ItemSiblings
{
	/// <summary>The series' seasons in order as pills; empty when there is only one (nothing to pick).</summary>
	public static List<SeasonPill> SeasonPills(IEnumerable<BaseItemDto>? seasons, Guid current)
	{
		var ordered = (seasons ?? []).Where(s => s.Type == BaseItemDto_Type.Season)
			.OrderBy(s => s.IndexNumber ?? int.MaxValue)
			.ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
		return ordered.Count < 2 ? [] : [.. ordered.Select(s => new SeasonPill(s, s.Id == current))];
	}

	/// <summary>
	/// Nixie 1.4.1: the seasons either side of <paramref name="current"/> in the pills' order (the one before, the one after),
	/// to be asked ahead: the next click is most often one of them. Empty when <paramref name="current"/> is not a pill.
	/// </summary>
	public static IReadOnlyList<BaseItemDto> Around(IEnumerable<BaseItemDto>? seasons, Guid current)
	{
		var pills = SeasonPills(seasons, current);
		var at = pills.FindIndex(p => p.IsCurrent);
		if (at < 0)
		{
			return [];
		}

		var around = new List<BaseItemDto>(2);
		if (at + 1 < pills.Count)
		{
			around.Add(pills[at + 1].Dto); // the next first: asked first
		}

		if (at > 0)
		{
			around.Add(pills[at - 1].Dto);
		}

		return around;
	}

	/// <summary>
	/// Nixie 1.5: the seasons beyond the ones either side of <paramref name="current"/> (<see cref="Around"/>), nearest first
	/// (the one after before the one before), for a show of at most <paramref name="most"/> seasons: a far jump (Season 1 to
	/// Season 9) waited for the server once. Empty for a longer show (each season is a request) or when
	/// <paramref name="current"/> is not a pill.
	/// </summary>
	public static IReadOnlyList<BaseItemDto> Further(IEnumerable<BaseItemDto>? seasons, Guid current, int most)
	{
		var pills = SeasonPills(seasons, current);
		var at = pills.FindIndex(p => p.IsCurrent);
		if (at < 0 || pills.Count > most)
		{
			return [];
		}

		var further = new List<BaseItemDto>(Math.Max(0, pills.Count - 3));
		for (var distance = 2; distance < pills.Count; distance++)
		{
			if (at + distance < pills.Count)
			{
				further.Add(pills[at + distance].Dto);
			}

			if (at - distance >= 0)
			{
				further.Add(pills[at - distance].Dto);
			}
		}

		return further;
	}

	/// <summary>The episode that follows <paramref name="current"/> in its season; null for the last one or when it is not listed.</summary>
	public static BaseItemDto? NextEpisode(IEnumerable<BaseItemDto>? episodes, Guid current)
	{
		var ordered = (episodes ?? []).Where(e => e.Type == BaseItemDto_Type.Episode)
			.OrderBy(e => e.IndexNumber ?? int.MaxValue)
			.ToList();
		var at = ordered.FindIndex(e => e.Id == current);
		return at >= 0 && at + 1 < ordered.Count ? ordered[at + 1] : null;
	}
}
