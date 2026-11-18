using FluentFin.Core;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;

namespace FluentFin.Converters;

/// <summary>Seaglass: the small functions the episode rows and the season pills bind to.</summary>
public static class ItemRowConverters
{
	public static string Runtime(BaseItemDto? dto) => dto?.RunTimeTicks is > 0 and var ticks ? ItemChips.Runtime(ticks) : "";

	public static string EpisodeTitle(BaseItemDto? dto) => ItemMeta.EpisodeTitle(dto);

	/// <summary>0 to 100: how far into the episode the viewer is (0 when watched or not started).</summary>
	public static double Progress(BaseItemDto? dto) => dto?.UserData is { Played: not true, PlayedPercentage: > 0 and var percent } ? Math.Min(100, percent) : 0;

	public static Visibility InProgress(BaseItemDto? dto) => Progress(dto) > 0 ? Visibility.Visible : Visibility.Collapsed;

	public static Visibility Watched(BaseItemDto? dto) => dto?.UserData?.Played == true ? Visibility.Visible : Visibility.Collapsed;

	public static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

	/// <summary>1.2.0 (Nixie button review, I5 and I25): the favourite toggle's heart, an outline when off, filled when on.</summary>
	public static string FavoriteGlyph(bool? on) => on == true ? "" : "";

	public static Visibility Show(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

}
