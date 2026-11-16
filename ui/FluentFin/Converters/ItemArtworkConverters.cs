using FluentFin.Core;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FluentFin.Converters;

/// <summary>
/// Seaglass: the pictures of the item pages, each decoded at the width it is drawn at (a 4K backdrop decoded in full costs
/// tens of megabytes). Which picture and where it is on the server is <see cref="ItemArtwork"/>'s; these only load it.
/// </summary>
public static class ItemArtworkConverters
{
	private static Uri? ToUri(string? path) =>
		path is null || string.IsNullOrEmpty(SessionInfo.BaseUrl) ? null : new Uri(SessionInfo.BaseUrl.TrimEnd('/') + path);

	private static BitmapImage? Load(string? path, int decodeWidth) =>
		ToUri(path) is { } uri ? new BitmapImage { DecodePixelWidth = decodeWidth, UriSource = uri } : null;

	public static BitmapImage? Backdrop(BaseItemDto? dto, int width) => Load(ItemArtwork.BackdropPath(dto, width), width);

	public static BitmapImage? Logo(BaseItemDto? dto, int width) => Load(ItemArtwork.LogoPath(dto, width), width);

	public static BitmapImage? Thumb(BaseItemDto? dto, int width) => Load(ItemArtwork.ThumbPath(dto, width), width);

	public static BitmapImage? Person(BaseItemPerson? person, int width) => Load(ItemArtwork.PersonPath(person, width), width);

	/// <summary>The small copy of the backdrop the window's ambient layer blurs (it is drawn about 64 px wide).</summary>
	public static Uri? AmbientBackdrop(BaseItemDto? dto) => ToUri(ItemArtwork.BackdropPath(dto, 400));

	public static Visibility HasLogo(BaseItemDto? dto) => ItemArtwork.HasLogo(dto) ? Visibility.Visible : Visibility.Collapsed;

	public static Visibility NoLogo(BaseItemDto? dto) => ItemArtwork.HasLogo(dto) ? Visibility.Collapsed : Visibility.Visible;
}
