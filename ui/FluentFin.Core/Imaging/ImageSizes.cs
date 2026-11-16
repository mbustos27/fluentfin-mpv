using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Imaging;

/// <summary>Seaglass: how wide artwork is decoded: at the size it is drawn, not at the server's.</summary>
public static class ImageSizes
{
	/// <summary>
	/// The decode width in pixels of an image the server sends <paramref name="fillHeight"/> tall: a poster is 2:3, a
	/// thumb or backdrop 16:9. 0 (the picture's own width) for any other kind of image, or no height.
	/// </summary>
	public static int DecodeWidth(ImageType type, double fillHeight)
	{
		if (fillHeight <= 0)
		{
			return 0;
		}

		return type switch
		{
			ImageType.Primary => (int)Math.Ceiling(fillHeight * 2 / 3),
			ImageType.Thumb or ImageType.Backdrop => (int)Math.Ceiling(fillHeight * 16 / 9),
			_ => 0,
		};
	}
}
