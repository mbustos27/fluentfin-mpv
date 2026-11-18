using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

/// <summary>
/// Nixie 1.3.0, the Editorial theme's series page (Themes\Editorial\Views\EditorialSeriesPage): a Seasons panel with each
/// season's progress, and the shown season as a grid of stills. Additive: read from the episodes the page already keeps for
/// Play's label (SeriesEpisodes), so nothing more is asked of the server.
/// </summary>
public partial class SeriesViewModel
{
	/// <summary>The Seasons panel's lines, in order, the shown season marked.</summary>
	[ObservableProperty]
	public partial IReadOnlyList<EditorialSeasonLine> EditorialSeasons { get; set; } = [];

	/// <summary>"2 of 24 watched" (the whole series), "" when unknown.</summary>
	[ObservableProperty]
	public partial string EditorialWatched { get; set; } = "";

	/// <summary>What is left of the episode Play resumes ("44 min left"), "" when it starts one afresh.</summary>
	[ObservableProperty]
	public partial string EditorialPlayLeft { get; set; } = "";

	/// <summary>"SERIES · 3 SEASONS · 24 EPISODES".</summary>
	[ObservableProperty]
	public partial string EditorialKicker { get; set; } = "SERIES";

	/// <summary>Reads the kept episodes again (after a load, a pick or a change of watched state) into the panel's lines.</summary>
	public async Task EditorialRefresh()
	{
		if (Dto?.Id is not { } id)
		{
			return;
		}

		var episodes = await _seriesEpisodes.Get(id);
		if (Dto?.Id != id)
		{
			return;
		}

		var seasons = Seasons?.Select(s => s.Dto).ToList() ?? [];
		EditorialSeasons = EditorialSeries.Lines(seasons, episodes, SelectedSeason?.Id, DateTimeOffset.UtcNow);
		EditorialWatched = EditorialSeries.Watched(episodes);
		EditorialPlayLeft = EditorialSeries.PlayLeft(episodes);
		EditorialKicker = EditorialSeries.Kicker("Series", seasons.Count(s => s.Type == BaseItemDto_Type.Season), episodes?.Count);
	}
}

/// <summary>Nixie 1.3.0, the Editorial theme's season page: the same Seasons panel (its seasons are the series').</summary>
public partial class SeasonViewModel
{
	[ObservableProperty]
	public partial IReadOnlyList<EditorialSeasonLine> EditorialSeasons { get; set; } = [];

	[ObservableProperty]
	public partial string EditorialWatched { get; set; } = "";

	[ObservableProperty]
	public partial string EditorialPlayLeft { get; set; } = "";

	/// <summary>"THE IRON CIRCUS · SEASON 1 · 8 EPISODES".</summary>
	[ObservableProperty]
	public partial string EditorialKicker { get; set; } = "SEASON";

	public async Task EditorialRefresh()
	{
		if (Dto?.Id is not { } id)
		{
			return;
		}

		var episodes = Dto.SeriesId is { } series ? await _seriesEpisodes.Get(series) : null;
		if (Dto?.Id != id)
		{
			return;
		}

		var seasons = SeasonPills.Count > 0 ? SeasonPills.Select(p => p.Dto).ToList() : [Dto];
		EditorialSeasons = EditorialSeries.Lines(seasons, episodes, id, DateTimeOffset.UtcNow);
		EditorialWatched = EditorialSeries.Watched(episodes?.Where(e => e.SeasonId == id).ToList());
		EditorialPlayLeft = EditorialSeries.PlayLeft(episodes);
		var count = Episodes?.Count ?? episodes?.Count(e => e.SeasonId == id);
		EditorialKicker = string.Join(" · ", new[] { Dto.SeriesName?.Trim(), count is > 0 ? (count == 1 ? "1 episode" : $"{count} episodes") : null }
			.Where(x => !string.IsNullOrEmpty(x))).ToUpperInvariant();
	}
}

/// <summary>One line of the Seasons panel: "01 · Season 1 · 2022 · 8 episodes · 2 of 8 watched".</summary>
public sealed record EditorialSeasonLine(BaseItemDto Season, string Number, string Name, string Meta, string Progress, double Fraction, bool IsCurrent)
{
	public bool HasFraction => Fraction > 0 && Fraction < 1;

	/// <summary>Narrator: "Season 1, 2 of 8 watched".</summary>
	public string AutomationName => Progress.Length > 0 ? $"{Name}, {Progress}" : Name;
}

/// <summary>Editorial: the series page's words and numbers, apart so they can be tested.</summary>
public static class EditorialSeries
{
	/// <summary>A season added this recently is "New".</summary>
	public static readonly TimeSpan NewFor = TimeSpan.FromDays(30);

	/// <summary>
	/// The panel's lines: each season in order with its number ("01"; specials "SP"), its year and episode count, and how much
	/// of it is watched ("2 of 8 watched", "Not started", "Watched", with "New · " for a season added in the last
	/// <see cref="NewFor"/>). The counts come from the series' episodes (null: the season's own unplayed count, if any).
	/// </summary>
	public static IReadOnlyList<EditorialSeasonLine> Lines(IEnumerable<BaseItemDto> seasons, IReadOnlyList<BaseItemDto>? episodes, Guid? current, DateTimeOffset now)
	{
		var bySeason = (episodes ?? []).Where(e => e.SeasonId is not null).GroupBy(e => e.SeasonId!.Value).ToDictionary(g => g.Key, g => g.ToList());
		return [.. seasons.Where(s => s.Type == BaseItemDto_Type.Season || s.Type is null)
			.OrderBy(s => s.IndexNumber is 0 ? int.MaxValue : s.IndexNumber ?? int.MaxValue - 1)
			.ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
			.Select(season =>
			{
				var own = season.Id is { } id && bySeason.TryGetValue(id, out var list) ? list : null;
				var total = own?.Count ?? season.ChildCount ?? 0;
				var watched = own?.Count(e => e.UserData?.Played == true)
					?? (season.ChildCount is { } c && season.UserData?.UnplayedItemCount is { } unplayed ? Math.Max(0, c - unplayed) : 0);
				var name = string.IsNullOrWhiteSpace(season.Name) ? $"Season {season.IndexNumber}" : season.Name.Trim();
				var number = season.IndexNumber switch { 0 => "SP", { } n => EditorialHome.Number(n), _ => "" };
				var year = season.ProductionYear ?? season.PremiereDate?.Year ?? own?.Select(e => e.PremiereDate?.Year).FirstOrDefault(y => y is not null);
				var meta = string.Join(" · ", new[] { year?.ToString(CultureInfo.InvariantCulture), total > 0 ? (total == 1 ? "1 episode" : $"{total} episodes") : null }
					.Where(x => !string.IsNullOrEmpty(x)));
				var progress = total == 0 ? "" : watched >= total ? "Watched" : watched == 0 ? "Not started" : $"{watched} of {total} watched";
				if (season.DateCreated is { } added && now - added < NewFor && watched == 0 && progress.Length > 0)
				{
					progress = $"New · {progress}";
				}

				return new EditorialSeasonLine(season, number, name, meta, progress, total == 0 ? 0 : (double)watched / total, season.Id == current);
			})];
	}

	/// <summary>"2 of 24 watched"; "" without episodes.</summary>
	public static string Watched(IReadOnlyList<BaseItemDto>? episodes) =>
		episodes is { Count: > 0 } ? $"{episodes.Count(e => e.UserData?.Played == true)} of {episodes.Count} watched" : "";

	/// <summary>What is left of the episode Play resumes, as the hero says it ("44 min left"); "" when Play starts afresh.</summary>
	public static string PlayLeft(IReadOnlyList<BaseItemDto>? episodes)
	{
		if (episodes is not { Count: > 0 })
		{
			return "";
		}

		var start = episodes[ItemPlay.StartIndex(episodes)];
		return start.UserData?.PlaybackPositionTicks is > 0 && start.UserData.Played != true ? EditorialHome.TimeLeft(start) : "";
	}

	/// <summary>"SERIES · 3 SEASONS · 24 EPISODES" (what is known of it).</summary>
	public static string Kicker(string what, int seasons, int? episodes) =>
		string.Join(" · ", new[]
		{
			what,
			seasons > 0 ? (seasons == 1 ? "1 season" : $"{seasons} seasons") : null,
			episodes is > 0 ? (episodes == 1 ? "1 episode" : $"{episodes} episodes") : null,
		}.Where(x => x is not null)).ToUpperInvariant();

	/// <summary>The heading's line beside "Season 1": "8 episodes · 6 h 12 m".</summary>
	public static string SeasonCaption(IReadOnlyList<BaseItemDto>? episodes)
	{
		if (episodes is not { Count: > 0 })
		{
			return "";
		}

		var minutes = (int)Math.Round(TimeSpan.FromTicks(episodes.Sum(e => e.RunTimeTicks ?? 0)).TotalMinutes);
		var count = episodes.Count == 1 ? "1 episode" : $"{episodes.Count} episodes";
		return minutes switch
		{
			<= 0 => count,
			< 60 => $"{count} · {minutes} m",
			_ => $"{count} · {minutes / 60} h {minutes % 60} m",
		};
	}

	/// <summary>The grid's episodes: in order or newest first, without the watched ones when asked.</summary>
	public static IReadOnlyList<BaseItemDto> Shown(IReadOnlyList<BaseItemDto>? episodes, bool hideWatched, bool newestFirst)
	{
		IEnumerable<BaseItemDto> shown = episodes ?? [];
		if (hideWatched)
		{
			shown = shown.Where(e => e.UserData?.Played != true);
		}

		return newestFirst ? [.. shown.Reverse()] : [.. shown];
	}

	/// <summary>The grid's episode the keyboard lands on first: the first not watched, else the first.</summary>
	public static int FocusIndex(IReadOnlyList<BaseItemDto> shown)
	{
		for (var i = 0; i < shown.Count; i++)
		{
			if (shown[i].UserData?.Played != true)
			{
				return i;
			}
		}

		return 0;
	}
}

/// <summary>Editorial: the item pages' big title and facts line (EditorialItemHeader).</summary>
public static class EditorialItemTitle
{
	/// <summary>The title's size: 96 (64 in a narrow window) for a short title, smaller as it grows, so two lines hold it.</summary>
	public static double Size(string? title, bool narrow)
	{
		var length = title?.Trim().Length ?? 0;
		var size = narrow ? 64.0 : 96.0;
		var scale = length switch
		{
			<= 16 => 1.0,
			<= 24 => 0.8,
			<= 36 => 0.66,
			_ => 0.54,
		};
		return Math.Max(narrow ? 36 : 44, Math.Round(size * scale));
	}

	/// <summary>
	/// "2022 – 2025 · TV-14 · Mystery, Drama · ★ 8.4" (a series), "2015 · PG-13 · 2h 04m · Romance, Drama · ★ 7.1" (a film),
	/// "28 Mar 2022 · TV-14 · 52m" (an episode).
	/// </summary>
	public static string Meta(BaseItemDto? dto)
	{
		if (dto is null)
		{
			return "";
		}

		var parts = new List<string>();
		switch (dto.Type)
		{
			case BaseItemDto_Type.Series when dto.ProductionYear is > 0 and var start:
				var end = dto.EndDate?.Year;
				parts.Add(end is { } last && last != start ? $"{start} – {last}"
					: dto.Status?.Equals("Continuing", StringComparison.OrdinalIgnoreCase) == true ? $"{start} – present" : start.ToString(CultureInfo.InvariantCulture));
				break;
			case BaseItemDto_Type.Episode when dto.PremiereDate is { } aired:
				parts.Add(aired.ToString("d MMM yyyy", CultureInfo.InvariantCulture));
				break;
			default:
				if (dto.ProductionYear is > 0 and var year)
				{
					parts.Add(year.ToString(CultureInfo.InvariantCulture));
				}
				break;
		}

		if (!string.IsNullOrWhiteSpace(dto.OfficialRating))
		{
			parts.Add(dto.OfficialRating.Trim());
		}

		if (dto.RunTimeTicks is > 0 and var ticks && dto.Type is not (BaseItemDto_Type.Series or BaseItemDto_Type.Season))
		{
			parts.Add(ItemChips.Runtime(ticks));
		}

		if (dto.Type != BaseItemDto_Type.Episode && dto.Genres?.Where(g => !string.IsNullOrWhiteSpace(g)).Take(2).Select(g => g.Trim()).ToList() is { Count: > 0 } genres)
		{
			parts.Add(string.Join(", ", genres));
		}

		if (dto.CommunityRating is > 0 and var rating)
		{
			parts.Add($"★ {rating.ToString("0.0", CultureInfo.InvariantCulture)}");
		}

		return string.Join(" · ", parts);
	}
}

/// <summary>Editorial: how many stills a season's grid puts across a width, and the gap between them.</summary>
public static class EditorialEpisodeColumns
{
	public static (int Columns, double Spacing) For(double width) => width switch
	{
		>= 1500 => (4, 40),
		>= 1000 => (3, 28),
		_ => (2, 24),
	};
}
