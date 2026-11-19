using System.Runtime.InteropServices.WindowsRuntime;
using FluentFin.Core;
using FluentFin.Core.ViewModels;
using Flurl;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FluentFin.Converters;

public static class BaseItemDtoConverters
{
	public static string GetCardTitle(this BaseItemDto? dto)
	{
		if (dto is null)
		{
			return string.Empty;
		}

		if (dto.Type == BaseItemDto_Type.Episode)
		{
			return dto.SeriesName ?? "";
		}

		return dto.Name ?? "";
	}

	public static string GetCardSubtitle(this BaseItemDto? dto)
	{
		if (dto is null)
		{
			return string.Empty;
		}

		// 1.2.0 (1.1.3 UX audit, layout items 7 and 23): the one episode format, "S3 E4 · After the Machine"
		// (ItemLabels.EpisodeTitle), and a year range with an en dash, "1996–2000", "2011–present"
		if (dto.Type == BaseItemDto_Type.Episode)
		{
			return ItemLabels.EpisodeTitle(dto);
		}
		if (dto.Type == BaseItemDto_Type.Movie)
		{
			return $"{dto.ProductionYear}";
		}
		if (dto.Type == BaseItemDto_Type.Series)
		{
			return SeriesYears(dto);
		}

		return "";
	}

	public static string ToSessionInfoItemName(this BaseItemDto? dto)
	{
		if (dto is null)
		{
			return string.Empty;
		}

		if (dto.Type == BaseItemDto_Type.Episode)
		{
			return ItemLabels.EpisodeTitle(dto);
		}
		if (dto.Type == BaseItemDto_Type.Movie)
		{
			return $"{dto.Name}";
		}

		return "";
	}

	public static string GetSeasonAndEpisodeNumber(this BaseItemDto? dto)
	{
		if (dto is null)
		{
			return string.Empty;
		}

		if (dto.Type is not BaseItemDto_Type.Episode)
		{
			return string.Empty;
		}

		return ItemLabels.EpisodeCode(dto);
	}

	/// <summary>
	/// 1.2.0 (1.1.3 UX audit, layout item 23): "1996–2000", "2011–present", "2004" (one year); an en dash with no
	/// spaces. Empty without a first year.
	/// </summary>
	public static string SeriesYears(BaseItemDto? dto)
	{
		if (dto?.ProductionYear is not { } start)
		{
			return "";
		}

		if (string.Equals(dto.Status, "Continuing", StringComparison.OrdinalIgnoreCase))
		{
			return $"{start}–present";
		}

		return dto.EndDate?.Year is { } end && end != start ? $"{start}–{end}" : $"{start}";
	}

	public static BitmapImage? GetImage(BaseItemPerson personDto, double height)
	{
		return new BitmapImage(SessionInfo.BaseUrl.AppendPathSegment($"/Items/{personDto.Id}/Images/Primary").SetQueryParam("fillHeight", height).ToUri());
	}

	public static BitmapImage? GetImage(VirtualFolderInfo folderInfo)
	{
		return new BitmapImage(SessionInfo.BaseUrl.AppendPathSegment($"/Items/{folderInfo.ItemId}/Images/Primary").ToUri());
	}


	public static WriteableBitmap? GetBlurHash(BaseItemDto? dto, ImageType imageType, double height)
	{
		if (dto is null)
		{
			return null;
		}

		if (dto.ImageTags is null)
		{
			return null;
		}

		var hasRequestTag = dto.ImageTags.AdditionalData.TryGetValue($"{imageType}", out _);

		if (imageType == ImageType.Thumb && !hasRequestTag)
		{
			imageType = ImageType.Primary;
		}

		string imageTypeStr = imageType.ToString();
		if (!dto.ImageTags.AdditionalData.TryGetValue(imageTypeStr, out object? imageTagObj))
		{
			return null;
		}

		string imageTag = $"{imageTagObj}";

		// This is a little gross, but there doesn't seem to be a better way to do it.
		IAdditionalDataHolder? blurHashesForType = imageType switch
		{
			ImageType.Art => dto.ImageBlurHashes?.Art,
			ImageType.Banner => dto.ImageBlurHashes?.Banner,
			ImageType.Backdrop => dto.ImageBlurHashes?.Backdrop,
			ImageType.Box => dto.ImageBlurHashes?.Box,
			ImageType.BoxRear => dto.ImageBlurHashes?.BoxRear,
			ImageType.Chapter => dto.ImageBlurHashes?.Chapter,
			ImageType.Disc => dto.ImageBlurHashes?.Disc,
			ImageType.Logo => dto.ImageBlurHashes?.Logo,
			ImageType.Menu => dto.ImageBlurHashes?.Menu,
			ImageType.Primary => dto.ImageBlurHashes?.Primary,
			ImageType.Profile => dto.ImageBlurHashes?.Profile,
			ImageType.Screenshot => dto.ImageBlurHashes?.Screenshot,
			ImageType.Thumb => dto.ImageBlurHashes?.Thumb,
			_ => null,
		};

		string blurHash = "";
		if (blurHashesForType is not null
			&& blurHashesForType.AdditionalData.TryGetValue(imageTag, out object? blurHashObj))
		{
			blurHash = $"{blurHashObj}";
		}

		if (string.IsNullOrEmpty(blurHash))
		{
			return null;
		}

		// Seaglass: our own decoder (FluentFin.Core.Imaging.BlurHash), at the artwork's shape: a few hundred pixels are all a blur needs
		var (w, h) = imageType is ImageType.Primary or ImageType.Logo ? (20, 30) : (32, 18);
		if (Core.Imaging.BlurHash.Decode(blurHash, w, h) is not { } pixels)
		{
			return null;
		}

		var bitmap = new WriteableBitmap(w, h);
		using (var stream = bitmap.PixelBuffer.AsStream())
		{
			stream.Write(pixels, 0, pixels.Length);
		}
		return bitmap;
	}

	public static BitmapImage? GetImage(BaseItemDto? dto, ImageType imageType, double height)
	{
		if (dto is null)
		{
			return null;
		}

		if (dto.Id is not { } id)
		{
			return null;
		}

		if (dto.ImageTags is null)
		{
			return null;
		}

		var requestedType = imageType;
		var hasRequestTag = dto.ImageTags.AdditionalData.TryGetValue($"{imageType}", out object? requestTag);
		var backdropTag = dto.BackdropImageTags?.FirstOrDefault();
		var parentBackdropTag = dto.ParentBackdropImageTags?.FirstOrDefault();

		var tag = "";
		if (hasRequestTag == true)
		{
			tag = $"{requestTag}";
		}
		else if (!string.IsNullOrEmpty(backdropTag))
		{
			tag = $"{backdropTag}";
		}
		else if (!string.IsNullOrEmpty(parentBackdropTag))
		{
			tag = $"{parentBackdropTag}";
		}

		if (imageType == ImageType.Backdrop && dto.Type is BaseItemDto_Type.Season or BaseItemDto_Type.Episode && dto.SeriesId is { } pid)
		{
			id = pid;
		}

		if (imageType == ImageType.Thumb && !hasRequestTag)
		{
			imageType = ImageType.Primary;
			if (dto.ImageTags.AdditionalData.TryGetValue($"{ImageType.Primary}", out var primaryTag))
			{
				tag = $"{primaryTag}";
			}
		}

		else if (imageType == ImageType.Primary && dto.Type == BaseItemDto_Type.Episode)
		{
			if (!string.IsNullOrEmpty(dto.SeriesPrimaryImageTag) && dto.SeriesId is { } seriesId)
			{
				id = seriesId;
				tag = $"{dto.SeriesPrimaryImageTag}";
			}
		}
		else if (imageType == ImageType.Logo && dto.Type == BaseItemDto_Type.Episode)
		{
			if (!string.IsNullOrEmpty(dto.SeriesPrimaryImageTag) && dto.SeriesId is { } seriesId)
			{
				id = seriesId;
				tag = $"{dto.ParentLogoImageTag}";
			}
		}

		var uri = SessionInfo.BaseUrl.AppendPathSegment($"/Items/{id}/Images/{imageType}");

		if (height is { } h)
		{
			uri.SetQueryParam("fillHeight", h);
		}

		if (!string.IsNullOrEmpty(tag))
		{
			uri.SetQueryParam("tag", tag);
		}


		// Seaglass: decoded at the size it is drawn (the server sends fillHeight tall), not at the picture's own
		return new BitmapImage(uri.ToUri()) { DecodePixelWidth = Core.Imaging.ImageSizes.DecodeWidth(requestedType, height) };
	}

	// 1.4.0 (the library UX review, X10): two marks, never one capsule for both meanings (Core.ItemBadges)

	/// <summary>The dim neutral check, top right: watched, nothing left to watch.</summary>
	public static Visibility WatchedMarkVisibility(BaseItemViewModel? vm) =>
		ItemBadges.Watched(vm?.UserData?.Played, vm?.UserData?.UnplayedItemCount) ? Visibility.Visible : Visibility.Collapsed;

	/// <summary>The neutral square count, top left: how many are left to watch ("5", "99+").</summary>
	public static string ToWatchText(BaseItemViewModel? vm) => ItemBadges.ToWatchText(vm?.UserData?.UnplayedItemCount);

	public static Visibility ToWatchVisibility(BaseItemViewModel? vm) =>
		ItemBadges.ToWatch(vm?.UserData?.UnplayedItemCount) > 0 ? Visibility.Visible : Visibility.Collapsed;
}
