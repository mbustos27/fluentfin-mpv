using FluentFin.Converters;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FluentFin.Views;

/// <summary>
/// 1.6 colpage (C1, C5): what the Collection page hands its theme's header stage (the theme's CollectionStageTemplate binds it
/// with {Binding}): the art (the set's Backdrop, else its first member's, else a 2×2 mosaic of posters), where the art may
/// start (right of the header's words, so no word ever sits on art: the contrast floor holds over any picture), the stage's
/// height, and the words a theme dresses it with (the marquee, the count, the contents list). Made again whenever the facts or
/// the page's size change; the pictures are kept across, so a resize never loads them twice.
/// </summary>
public sealed class CollectionStage
{
	/// <summary>Narrower than this, the art region shows nothing (a sliver of picture beside the words reads as a mistake).</summary>
	public const double MinArtWidth = 320;

	/// <summary>The contents list ("01 … 06") needs this much art region beside the words.</summary>
	public const double MinContentsWidth = 560;

	public required CollectionHeaderFacts Facts { get; init; }

	public ImageSource? Backdrop { get; init; }

	public IReadOnlyList<ImageSource?> Mosaic { get; init; } = [];

	/// <summary>Where the art region starts, from the stage's left edge (its left margin).</summary>
	public double ArtLeft { get; init; }

	public double Height { get; init; }

	public double Width { get; init; }

	public double ArtWidth => Math.Max(0, Width - ArtLeft);

	/// <summary>The header's top (the page grid's title y in the page) and the page's right gutter: a theme's framed pieces line up with them.</summary>
	public double HeaderTop { get; init; }

	public double HeaderHeight { get; init; }

	public double Gutter { get; init; }

	/// <summary>A framed piece in the art region (Drive-in's lot, Editorial's contents): at the header's top, on the right gutter.</summary>
	public Thickness FrameMargin => new(0, HeaderTop, Gutter, 0);

	// Drive-in: the little lot's outdoor screen, 16:9, as tall as the header allows (the lot's sky over it, the cars under it)
	public const double LotTop = 22, LotBottom = 64, ScreenFrame = 16;

	public double ScreenHeight => Math.Floor(Math.Min(ScreenWidth * 9 / 16, Math.Max(0, HeaderHeight - LotTop - LotBottom - ScreenFrame)));

	public double ScreenWidth
	{
		get
		{
			var byHeight = Math.Max(0, HeaderHeight - LotTop - LotBottom - ScreenFrame) * 16 / 9;
			var room = ArtWidth - Gutter - ScreenFrame;
			var byWidth = room - (2 * Math.Max(76, Math.Round(room * 0.09)));
			return Math.Floor(Math.Clamp(Math.Min(byHeight, byWidth), 0, 640));
		}
	}

	/// <summary>Drive-in's lot shows when its screen is at least this wide.</summary>
	public const double MinScreenWidth = 240;

	public Visibility LotVisibility => ScreenWidth >= MinScreenWidth ? Visibility.Visible : Visibility.Collapsed;

	/// <summary>The count's digits for a theme's tubes ("6") and its word ("FEATURES").</summary>
	public string FeatureCount => Facts.Features.Split(' ', 2)[0];

	public string FeatureLabel => Facts.Features.Split(' ', 2) is { Length: 2 } parts ? parts[1] : "";

	public Thickness ArtMargin => new(ArtLeft, 0, 0, 0);

	public Visibility ArtVisibility => ArtWidth >= MinArtWidth && (Backdrop is not null || Mosaic.Count > 0) ? Visibility.Visible : Visibility.Collapsed;

	/// <summary>The lot, sky or panel a theme draws in the art region even with no picture (Drive-in's screen stays lit).</summary>
	public Visibility RegionVisibility => ArtWidth >= MinArtWidth ? Visibility.Visible : Visibility.Collapsed;

	public Visibility BackdropVisibility => Backdrop is not null ? Visibility.Visible : Visibility.Collapsed;

	public Visibility MosaicVisibility => Backdrop is null && Mosaic.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

	public ImageSource? Mosaic0 => Mosaic.Count > 0 ? Mosaic[0] : null;

	public ImageSource? Mosaic1 => Mosaic.Count > 1 ? Mosaic[1] : Mosaic0;

	public ImageSource? Mosaic2 => Mosaic.Count > 2 ? Mosaic[2] : Mosaic1;

	public ImageSource? Mosaic3 => Mosaic.Count > 3 ? Mosaic[3] : Mosaic0;

	public string Marquee => Facts.Marquee;

	/// <summary>The title frame's kicker over the title (Drive-in's board: "NOW SHOWING").</summary>
	public string Kicker => "NOW SHOWING";

	public Visibility KickerVisibility => Visibility.Visible;

	public string Features => Facts.Features;

	public string Title => Facts.Title;

	public string Eyebrow => Facts.Eyebrow;

	public IReadOnlyList<CollectionContentsLine> Contents => Facts.Contents;

	public string ContentsMore => Facts.ContentsMore;

	public Visibility ContentsMoreVisibility => string.IsNullOrEmpty(Facts.ContentsMore) ? Visibility.Collapsed : Visibility.Visible;

	public Visibility ContentsVisibility => ArtWidth >= MinContentsWidth && Facts.Contents.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

	public Visibility FeaturesVisibility => string.IsNullOrEmpty(Facts.Features) ? Visibility.Collapsed : Visibility.Visible;

	/// <summary>
	/// Where the art region starts: right of the header's widest line (<paramref name="wordsRight"/>, in the stage's
	/// coordinates) and a gap, never left of 40 % of the width (a short title still leaves the words their column); and the
	/// stage's height: the theme's, or the header's foot and 16 (the toolbar comes 24 under it) when the header is taller.
	/// </summary>
	public static (double ArtLeft, double Height) Measure(double width, double wordsRight, double headerBottom, double themeHeight, double gap = 48)
	{
		var left = Math.Clamp(Math.Max(wordsRight + gap, width * 0.4), 0, Math.Max(0, width));
		// the art fades out before the toolbar under the header (its Hide watched and Order sit at the right edge)
		var height = Math.Max(themeHeight, headerBottom + 16);
		return (Math.Round(left), Math.Round(height));
	}

	// the pictures, kept per item and size: a new stage for a resize or a changed fact reuses them
	private static readonly Dictionary<(Guid, ImageType, int), ImageSource?> s_pictures = [];

	public static ImageSource? Picture(BaseItemDto? item, ImageType type, int height)
	{
		if (item?.Id is not { } id)
		{
			return null;
		}

		if (s_pictures.TryGetValue((id, type, height), out var known))
		{
			return known;
		}

		if (s_pictures.Count > 64)
		{
			s_pictures.Clear();
		}

		var picture = BaseItemDtoConverters.GetImage(item, type, height);
		s_pictures[(id, type, height)] = picture;
		return picture;
	}
}
