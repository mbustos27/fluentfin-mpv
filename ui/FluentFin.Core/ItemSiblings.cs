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
