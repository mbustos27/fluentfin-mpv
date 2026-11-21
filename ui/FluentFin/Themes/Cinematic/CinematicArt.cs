using FluentFin.Core;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FluentFin.Themes.Cinematic;

/// <summary>
/// Cinematic's pictures for x:Bind, each decoded at the width it is drawn at (which picture: <see cref="CinematicArtwork"/>
/// and <see cref="ItemArtwork"/>).
/// </summary>
public static class CinematicArt
{
	// 1.5 qacheck: one join for every address (a sub-path, a "/" at its end)
	private static BitmapImage? Load(string? path, int decodeWidth) =>
		ServerAddress.Join(SessionInfo.BaseUrl, path) is { } uri ? new BitmapImage { DecodePixelWidth = decodeWidth, UriSource = uri } : null;

	/// <summary>A shelf's still: a film's or a show's wide picture; an episode's show's.</summary>
	public static BitmapImage? Still(BaseItemDto? dto, int width) => Load(CinematicArtwork.StillPath(dto, width), width);

	/// <summary>An episode on its season's shelf: its own picture first.</summary>
	public static BitmapImage? EpisodeStill(BaseItemDto? dto, int width) => Load(CinematicArtwork.StillPath(dto, width, episodeOwnFirst: true), width);

	/// <summary>An item page's or the preview stage's full-bleed backdrop (an episode's or a season's is its show's).</summary>
	public static BitmapImage? Backdrop(BaseItemDto? dto, int width) => Load(ItemArtwork.BackdropPath(dto, width), width);

	public static BaseItemViewModel Wrap(BaseItemDto dto) => BaseItemViewModel.FromDto(dto);

	public static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

	public static Visibility ShowIf(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

	public static Visibility ShowText(string? text) => string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
}
