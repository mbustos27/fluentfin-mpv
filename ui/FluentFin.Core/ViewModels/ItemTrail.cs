using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

// 1.7 upnav
/// <summary>
/// Nixie 1.7: one step up from an item page, a button over its title (TrailEyebrow): <see cref="Label"/> is what it shows,
/// <see cref="Name"/> what Narrator reads ("Go to series What We Do in the Shadows"), <see cref="Target"/> the page it opens
/// (GlobalCommands.DisplayDto: the series or season page of the theme shown).
/// </summary>
public sealed record TrailCrumb(string Label, string Name, BaseItemDto Target);

/// <summary>
/// Nixie 1.7 (the owner, 2026-11-21: an episode opened from Continue watching had no way back to its show): the way up from an
/// item page, after the library crumb (TopNavigation.Eyebrow, which keeps its own rule). An episode shows its series and its
/// season ("Shows › What We Do in the Shadows › Season 3"), a season its series ("Shows › What We Do in the Shadows"); a
/// series, a film and anything else show none. The crumbs always show, however the page was reached. They come from the
/// item's own ids (SeriesId, SeasonId, ParentIndexNumber); an item that does not carry them is asked for once
/// (<see cref="ResolveAsync"/>, kept by TopNavigation).
/// </summary>
public sealed record ItemTrail(TrailCrumb? Series, TrailCrumb? Season)
{
	public static ItemTrail None { get; } = new(null, null);

	public bool IsEmpty => Series is null && Season is null;

	/// <summary>The season crumb's words: "Specials" for season 0, "Season 3", else the season's own name, else "Season".</summary>
	public static string SeasonLabel(int? index, string? name) => index switch
	{
		0 => "Specials",
		> 0 => $"Season {index}",
		_ => string.IsNullOrWhiteSpace(name) ? "Season" : name.Trim(),
	};

	/// <summary>
	/// Whether the item's trail needs the server: an episode that does not carry its series (its id and name) or its season's
	/// id, a season that does not carry its series. Anything else has no trail to look for.
	/// </summary>
	public static bool NeedsLookup(BaseItemDto item) => item.Type switch
	{
		BaseItemDto_Type.Episode => !CarriesSeries(item) || item.SeasonId is null,
		BaseItemDto_Type.Season => !CarriesSeries(item),
		_ => false,
	};

	private static bool CarriesSeries(BaseItemDto item) => item.SeriesId is not null && !string.IsNullOrWhiteSpace(item.SeriesName);

	/// <summary>
	/// The trail from what <paramref name="item"/> carries; a crumb it cannot point at is left out (an episode without its
	/// season's id has its series only).
	/// </summary>
	/// <param name="season">The season found for an episode that does not carry its season's id (<see cref="ResolveAsync"/>).</param>
	/// <param name="type">The item's type when <paramref name="item"/> does not say it (the server's copy of the item opened).</param>
	public static ItemTrail From(BaseItemDto item, BaseItemDto? season = null, BaseItemDto_Type? type = null)
	{
		type = item.Type ?? type;
		if (type is not (BaseItemDto_Type.Episode or BaseItemDto_Type.Season) || item.SeriesId is not { } seriesId)
		{
			return None;
		}

		var seriesName = string.IsNullOrWhiteSpace(item.SeriesName) ? null : item.SeriesName.Trim();
		var series = new TrailCrumb(
			seriesName ?? "Series",
			seriesName is null ? "Go to the series" : $"Go to series {seriesName}",
			new BaseItemDto { Id = seriesId, Type = BaseItemDto_Type.Series });
		if (type == BaseItemDto_Type.Season || (item.SeasonId ?? season?.Id) is not { } seasonId)
		{
			return new ItemTrail(series, null);
		}

		var label = SeasonLabel(item.ParentIndexNumber ?? season?.IndexNumber, item.SeasonName ?? season?.Name);
		// no Name: a season page opened with a name shows what it was opened with until the server's season comes (SeasonViewModel),
		// and this one has no pictures or overview to show; the series' id and name give the season page its own trail at once
		var target = new BaseItemDto { Id = seasonId, Type = BaseItemDto_Type.Season, SeriesId = seriesId, SeriesName = seriesName };
		return new ItemTrail(series, new TrailCrumb(label, $"Go to {label}", target));
	}

	/// <summary>
	/// The trail of an item that does not carry its ids (<see cref="NeedsLookup"/>): the item asked for once when it does not
	/// carry its series; then, for an episode still without its season's id, the series' seasons, the one with its number.
	/// An item that carries them asks nothing.
	/// </summary>
	/// <returns>
	/// The trail; null when a request it needed had no answer (the client answers null for a timeout or any error): the page
	/// shows what the item carries meanwhile, and the next time it is shown asks again (review of wip/170-upnav, item 1).
	/// </returns>
	public static async Task<ItemTrail?> ResolveAsync(BaseItemDto item, Func<Guid, Task<BaseItemDto?>> itemOf, Func<Guid, Task<IReadOnlyList<BaseItemDto>?>> seasonsOf)
	{
		if (!NeedsLookup(item))
		{
			return From(item);
		}

		var known = item;
		if (!CarriesSeries(item) && item.Id is { } id)
		{
			if (await itemOf(id) is not { } asked)
			{
				return null;
			}

			known = asked;
		}

		BaseItemDto? season = null;
		if ((known.Type ?? item.Type) == BaseItemDto_Type.Episode && known.SeasonId is null && known.SeriesId is { } seriesId && known.ParentIndexNumber is { } number)
		{
			if (await seasonsOf(seriesId) is not { } seasons)
			{
				return null;
			}

			season = seasons.FirstOrDefault(s => s.Id is not null && s.IndexNumber == number && s.Type is null or BaseItemDto_Type.Season);
		}

		return From(known, season, item.Type);
	}
}
// end 1.7 upnav
