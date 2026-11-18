using Flurl;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

/// <summary>
/// Seaglass: one slide of Home's hero: an item with the artwork to show it with and the words under its title. Made by
/// <see cref="HeroSlides.Pick"/>; the control draws it.
/// </summary>
public sealed record HeroSlide(
	BaseItemDto Dto,
	string Title,
	string Meta,
	string Overview,
	bool IsResume,
	Guid BackdropItemId,
	string BackdropTag,
	Guid? LogoItemId,
	string? LogoTag)
{
	/// <summary>The item the slide stands for: a series for an episode, so one show is one slide.</summary>
	public Guid Key => Dto.Type == BaseItemDto_Type.Episode && Dto.SeriesId is { } series ? series : Dto.Id ?? Guid.Empty;

	public string PlayLabel => IsResume ? "Resume" : "Play";

	public bool HasLogo => LogoItemId is not null && !string.IsNullOrEmpty(LogoTag);

	/// <summary>The backdrop at <paramref name="width"/> pixels wide (the window's), cached by the server under its tag.</summary>
	public Uri BackdropUri(string baseUrl, int width) =>
		baseUrl.AppendPathSegment($"/Items/{BackdropItemId}/Images/Backdrop").SetQueryParam("fillWidth", width).SetQueryParam("tag", BackdropTag).ToUri();

	public Uri? LogoUri(string baseUrl, int width) => HasLogo
		? baseUrl.AppendPathSegment($"/Items/{LogoItemId}/Images/Logo").SetQueryParam("fillWidth", width).SetQueryParam("tag", LogoTag).ToUri()
		: null;

	/// <summary>The blur hash Jellyfin keeps for the slide's backdrop, or null.</summary>
	public string? BackdropBlurHash => HeroSlides.BlurHashOf(Dto, BackdropTag);
}

public static class HeroSlides
{
	public const int MaxSlides = 5;

	/// <summary>
	/// The hero's slides: up to <paramref name="max"/> items new to the user (Seaglass 1.1.4, the UX review's fix 10: the
	/// hero no longer repeats the Continue Watching row under it). First the latest items not yet watched or started (the
	/// rows' first items, then their second, and so on, so each library is heard), then Next Up. Nothing of Continue Watching
	/// is featured: not its items, not their shows. An item needs a backdrop to be a slide; those that also have a logo come
	/// first within their group; one show is one slide.
	/// </summary>
	public static List<HeroSlide> Pick(IEnumerable<BaseItemDto> continueWatching, IEnumerable<IReadOnlyList<BaseItemDto>> latestRows, IEnumerable<BaseItemDto> nextUp, int max = MaxSlides)
	{
		// what Continue Watching shows: its items and their shows (an episode in progress is the whole show there)
		var seen = new HashSet<Guid>();
		foreach (var dto in continueWatching)
		{
			if (dto.Id is { } id)
			{
				seen.Add(id);
			}

			if (dto.SeriesId is { } series)
			{
				seen.Add(series);
			}
		}

		var slides = new List<HeroSlide>();

		void AddFrom(IEnumerable<BaseItemDto> group)
		{
			var made = group.Where(dto => dto.Id is not { } id || !seen.Contains(id)).Select(Make).OfType<HeroSlide>().Where(s => seen.Add(s.Key)).ToList();
			// stable: slides with a logo first, each kept in the order it came
			foreach (var slide in made.Where(s => s.HasLogo).Concat(made.Where(s => !s.HasLogo)))
			{
				if (slides.Count < max)
				{
					slides.Add(slide);
				}
			}
		}

		var rows = latestRows.ToList();
		AddFrom(Enumerable.Range(0, rows.Count == 0 ? 0 : rows.Max(r => r.Count)).SelectMany(i => rows.Where(r => i < r.Count).Select(r => r[i])).Where(IsNew));
		if (slides.Count < max)
		{
			AddFrom(nextUp.Where(IsNew));
		}

		return slides;
	}

	/// <summary>
	/// Not watched and not started: a movie or an episode with no play and no resume point; a show with episodes left to
	/// watch (the server counts them: <c>UnplayedItemCount</c>).
	/// </summary>
	public static bool IsNew(BaseItemDto dto)
	{
		var data = dto.UserData;
		if (data?.Played == true)
		{
			return false;
		}

		if (dto.Type is BaseItemDto_Type.Series or BaseItemDto_Type.Season)
		{
			return data?.UnplayedItemCount is not 0;
		}

		return data?.PlaybackPositionTicks is null or 0;
	}

	/// <summary>The slide for an item, or null when it has no backdrop (or no id) to show.</summary>
	public static HeroSlide? Make(BaseItemDto dto)
	{
		if (dto.Id is not { } id)
		{
			return null;
		}

		Guid backdropItem;
		string backdropTag;
		if (dto.BackdropImageTags?.FirstOrDefault(t => !string.IsNullOrEmpty(t)) is { } own)
		{
			(backdropItem, backdropTag) = (id, own);
		}
		else if (dto.ParentBackdropImageTags?.FirstOrDefault(t => !string.IsNullOrEmpty(t)) is { } parent && (dto.ParentBackdropItemId ?? dto.SeriesId) is { } parentId)
		{
			(backdropItem, backdropTag) = (parentId, parent);
		}
		else
		{
			return null;
		}

		Guid? logoItem = null;
		string? logoTag = null;
		if (dto.ImageTags?.AdditionalData.TryGetValue("Logo", out var own_logo) == true && $"{own_logo}" is { Length: > 0 } ownLogo)
		{
			(logoItem, logoTag) = (id, ownLogo);
		}
		else if (!string.IsNullOrEmpty(dto.ParentLogoImageTag) && (dto.ParentLogoItemId ?? dto.SeriesId) is { } logoParent)
		{
			(logoItem, logoTag) = (logoParent, dto.ParentLogoImageTag);
		}

		var title = dto.Type == BaseItemDto_Type.Episode && !string.IsNullOrEmpty(dto.SeriesName) ? dto.SeriesName : dto.Name ?? "";
		return new HeroSlide(dto, title, Meta(dto), dto.Overview ?? "", ItemLabels.ResumePosition(dto) is not null, backdropItem, backdropTag, logoItem, logoTag);
	}

	/// <summary>
	/// "S2 E3 · 2019 · TV-14 · 45m": the episode's place, the year, the rating and the runtime, those the item has. The episode's
	/// place is ItemLabels.EpisodeCode's ("S2 E3", "S2 E5–E6"; the layout audit's item 7).
	/// </summary>
	public static string Meta(BaseItemDto dto)
	{
		var parts = new List<string>();
		if (dto.Type == BaseItemDto_Type.Episode && dto.ParentIndexNumber is not null && ItemLabels.EpisodeCode(dto) is { Length: > 0 } code)
		{
			parts.Add(code);
		}
		if (dto.ProductionYear is { } year and > 0)
		{
			parts.Add($"{year}");
		}
		if (!string.IsNullOrWhiteSpace(dto.OfficialRating))
		{
			parts.Add(dto.OfficialRating);
		}
		if (dto.RunTimeTicks is > 0 and var ticks && Runtime(TimeSpan.FromTicks(ticks)) is { } runtime)
		{
			parts.Add(runtime);
		}
		return string.Join(" · ", parts);
	}

	/// <summary>"1h 52m" or "45m"; null under a minute.</summary>
	public static string? Runtime(TimeSpan length)
	{
		var minutes = (int)Math.Round(length.TotalMinutes);
		return minutes switch
		{
			< 1 => null,
			< 60 => $"{minutes}m",
			_ => minutes % 60 == 0 ? $"{minutes / 60}h" : $"{minutes / 60}h {minutes % 60}m",
		};
	}

	internal static string? BlurHashOf(BaseItemDto dto, string backdropTag)
	{
		if (dto.ImageBlurHashes?.Backdrop?.AdditionalData.TryGetValue(backdropTag, out var hash) == true && $"{hash}" is { Length: > 0 } text)
		{
			return text;
		}
		return null;
	}
}
