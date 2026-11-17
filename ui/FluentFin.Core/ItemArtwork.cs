using System.Globalization;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core;

/// <summary>
/// Seaglass: which picture an item page draws as its backdrop and its logo, and where it is on the server. An episode or a
/// season has none of its own and shows its series'. Null when there is none, so the page shows the title as text instead.
/// </summary>
public static class ItemArtwork
{
	/// <summary>The backdrop's path and query on the server (relative, "/Items/{id}/Images/Backdrop?tag=…&amp;maxWidth=…"), or null.</summary>
	public static string? BackdropPath(BaseItemDto? dto, int maxWidth) => Path(dto, "Backdrop", maxWidth);

	/// <summary>The logo's path and query on the server, or null when the item and its series have none.</summary>
	public static string? LogoPath(BaseItemDto? dto, int maxWidth) => Path(dto, "Logo", maxWidth);

	/// <summary>An episode's (or any item's) wide picture: its Thumb, else its Primary; null when it has neither.</summary>
	public static string? ThumbPath(BaseItemDto? dto, int maxWidth)
	{
		if (dto?.Id is not { } id || dto.ImageTags is not { } tags)
		{
			return null;
		}

		foreach (var kind in new[] { "Thumb", "Primary" })
		{
			if (tags.AdditionalData.TryGetValue(kind, out var tag) && $"{tag}" is { Length: > 0 } text)
			{
				return Url(id, kind, text, maxWidth);
			}
		}

		return null;
	}

	/// <summary>A cast member's photo; null when the server has none (the page shows the initials).</summary>
	public static string? PersonPath(BaseItemPerson? person, int maxWidth) =>
		person?.Id is { } id && !string.IsNullOrEmpty(person.PrimaryImageTag) ? Url(id, "Primary", person.PrimaryImageTag, maxWidth) : null;

	public static bool HasLogo(BaseItemDto? dto) => LogoPath(dto, 1) is not null;

	/// <summary>The BlurHash Jellyfin keeps for the item's own backdrop (the palette's source), or null (none, or the backdrop is its parent's).</summary>
	public static string? BackdropBlurHash(BaseItemDto? dto)
	{
		if (dto?.BackdropImageTags?.FirstOrDefault(t => !string.IsNullOrEmpty(t)) is { } tag
			&& dto.ImageBlurHashes?.Backdrop?.AdditionalData.TryGetValue(tag, out var hash) == true && $"{hash}" is { Length: > 0 } text)
		{
			return text;
		}

		return null;
	}

	public static bool HasBackdrop(BaseItemDto? dto) => BackdropPath(dto, 1) is not null;

	private static string? Path(BaseItemDto? dto, string kind, int maxWidth)
	{
		if (dto?.Id is not { } id)
		{
			return null;
		}

		var (owner, tag) = kind == "Logo" ? Logo(dto, id) : Backdrop(dto, id);
		if (owner is null)
		{
			return null;
		}

		return Url(owner.Value, kind, tag ?? "", maxWidth);
	}

	private static string Url(Guid owner, string kind, string tag, int maxWidth) =>
		$"/Items/{owner:D}/Images/{kind}?tag={Uri.EscapeDataString(tag)}&maxWidth={maxWidth.ToString(CultureInfo.InvariantCulture)}";

	private static (Guid? Owner, string? Tag) Logo(BaseItemDto dto, Guid id)
	{
		if (dto.ImageTags?.AdditionalData.TryGetValue("Logo", out var own) == true && $"{own}" is { Length: > 0 } ownTag)
		{
			return (id, ownTag);
		}

		return dto.ParentLogoItemId is { } parent && !string.IsNullOrEmpty(dto.ParentLogoImageTag) ? (parent, dto.ParentLogoImageTag) : (null, null);
	}

	private static (Guid? Owner, string? Tag) Backdrop(BaseItemDto dto, Guid id)
	{
		if (dto.BackdropImageTags?.FirstOrDefault(t => !string.IsNullOrEmpty(t)) is { } own)
		{
			return (id, own);
		}

		return dto.ParentBackdropItemId is { } parent && dto.ParentBackdropImageTags?.FirstOrDefault(t => !string.IsNullOrEmpty(t)) is { } tag
			? (parent, tag)
			: (null, null);
	}
}
