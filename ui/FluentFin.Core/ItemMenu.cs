using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core;

/// <summary>Seaglass 1.1.3: a command of an item's More menu (what <see cref="ItemMenu"/> offers).</summary>
public enum ItemMenuCommand
{
	Play,
	ToggleWatched,
	ToggleFavorite,
	ResetWatchProgress,
	/// <summary>Seaglass 1.1.4: a card of Home's Continue Watching row: out of the row, its resume point cleared, with an Undo.</summary>
	RemoveFromContinueWatching,
	MediaInfo,
	EditMetadata,
	EditImages,
	EditSegments,
	EditSubtitles,
	Identify,
	RefreshMetadata,
	Delete,
}

/// <summary>Seaglass 1.1.3: one line of an item's More menu.</summary>
public sealed record ItemMenuEntry(ItemMenuCommand Command, string Text);

/// <summary>Seaglass 1.1.3: what a destructive action asks before it runs; the dialog's default button is Cancel.</summary>
public sealed record ConfirmPrompt(string Title, string Message, string ConfirmText);

/// <summary>
/// Seaglass 1.1.3 (UX review, prioritised fix 3): what an item's More menu holds, in groups (Converters\JellyfinFlyoutConverter
/// draws it, a separator between two groups). Every entry does something: upstream's "Play all from here", "Shuffle", "Add to
/// playlist" and "Add to collection" had no command and are gone; "Copy Download URL" is gone because the URL carries the
/// access token (<c>api_key</c>) and the clipboard is readable by every app; "Play On" is gone, and "Play" is only on cards
/// (a card is one keyboard stop, and its menu is the keyboard's way to play it: item pages and episode rows have a Play
/// button). Editing and refreshing the server's metadata are for administrators, as jellyfin-web has it.
/// </summary>
public static class ItemMenu
{
	/// <summary>The groups for <paramref name="dto"/>; none is empty. <paramref name="user"/>'s policy decides the administrative entries.</summary>
	/// <param name="inContinueWatching">
	/// Seaglass 1.1.4: the card is in Home's Continue Watching row: "Remove from Continue Watching" (with an Undo) instead of
	/// "Reset Watch Progress", which does the same on the server.
	/// </param>
	/// <param name="includeToggles">
	/// Seaglass 1.1.4 (layout audit, item 23): Mark as watched and the favorite; an item page leaves them out (its own Watched
	/// and Favorite buttons are beside the menu), cards and episode rows keep them.
	/// </param>
	public static IReadOnlyList<IReadOnlyList<ItemMenuEntry>> For(BaseItemDto dto, UserDto? user, bool includePlay, bool canEditSegments, bool inContinueWatching = false, bool includeToggles = true)
	{
		if (dto.Type is not { } type)
		{
			return [];
		}

		var policy = user?.Policy;
		var admin = policy?.IsAdministrator == true;
		var playable = type is BaseItemDto_Type.Movie or BaseItemDto_Type.Season or BaseItemDto_Type.Series or BaseItemDto_Type.Episode;
		var video = type is BaseItemDto_Type.Movie or BaseItemDto_Type.Episode;

		List<ItemMenuEntry> yours = [];
		if (playable)
		{
			if (includePlay)
			{
				yours.Add(new(ItemMenuCommand.Play, "Play"));
			}

			if (includeToggles)
			{
				yours.Add(new(ItemMenuCommand.ToggleWatched, dto.UserData?.Played == true ? "Mark as unwatched" : "Mark as watched"));
				yours.Add(new(ItemMenuCommand.ToggleFavorite, dto.UserData?.IsFavorite == true ? "Remove from favorites" : "Add to favorites"));
			}
		}

		if (inContinueWatching && dto.Id is not null)
		{
			yours.Add(new(ItemMenuCommand.RemoveFromContinueWatching, "Remove from Continue Watching"));
		}
		else if (video && dto.UserData?.PlaybackPositionTicks is > 0 && dto.Id is not null)
		{
			yours.Add(new(ItemMenuCommand.ResetWatchProgress, "Reset watch progress"));
		}

		List<ItemMenuEntry> metadata = [];
		if (video)
		{
			metadata.Add(new(ItemMenuCommand.MediaInfo, "Media info"));
		}

		if (admin)
		{
			metadata.Add(new(ItemMenuCommand.EditMetadata, "Edit metadata…"));
			metadata.Add(new(ItemMenuCommand.EditImages, "Edit images…"));
		}

		if (admin && canEditSegments)
		{
			metadata.Add(new(ItemMenuCommand.EditSegments, "Edit segments"));
		}

		// jellyfin-web: subtitle management is its own permission, which administrators always have
		if (video && (admin || policy?.EnableSubtitleManagement == true))
		{
			metadata.Add(new(ItemMenuCommand.EditSubtitles, "Edit subtitles…"));
		}

		if (admin && type is not BaseItemDto_Type.CollectionFolder)
		{
			metadata.Add(new(ItemMenuCommand.Identify, "Identify…"));
		}

		if (admin)
		{
			metadata.Add(new(ItemMenuCommand.RefreshMetadata, "Refresh metadata…"));
		}

		List<ItemMenuEntry> delete = [];
		if (admin && DeleteText(type) is { } deleteText)
		{
			delete.Add(new(ItemMenuCommand.Delete, deleteText));
		}

		return [.. new[] { yours, metadata, delete }.Where(x => x.Count > 0)];
	}

	/// <summary>
	/// The one way a menu entry runs (the converter's every item goes through it): Delete asks first with
	/// <see cref="DeletePrompt"/> and runs only on a yes; the others run at once. True when <paramref name="run"/> ran.
	/// </summary>
	public static async Task<bool> RunAsync(ItemMenuEntry entry, BaseItemDto dto, Func<ConfirmPrompt, Task<bool>> confirm, Func<ItemMenuCommand, BaseItemDto, Task> run)
	{
		if (entry.Command == ItemMenuCommand.Delete && !await confirm(DeletePrompt(dto)))
		{
			return false;
		}

		await run(entry.Command, dto);
		return true;
	}

	private static string? DeleteText(BaseItemDto_Type type) => type switch
	{
		BaseItemDto_Type.Movie => "Delete movie…",
		BaseItemDto_Type.Series => "Delete series…",
		BaseItemDto_Type.Season => "Delete season…",
		_ => null,
	};

	/// <summary>What Delete asks first: it deletes the files on the server, for every user, so the item is named.</summary>
	public static ConfirmPrompt DeletePrompt(BaseItemDto dto)
	{
		var name = string.IsNullOrWhiteSpace(dto.Name) ? "this item" : dto.Name.Trim();
		var what = dto.Type switch
		{
			BaseItemDto_Type.Series => "series",
			BaseItemDto_Type.Season when !string.IsNullOrWhiteSpace(dto.SeriesName) => $"season of {dto.SeriesName.Trim()}",
			BaseItemDto_Type.Season => "season",
			_ => "item",
		};

		return new($"Delete {name}?",
			$"This {what} and its files are deleted from the server, for every user. This can't be undone.",
			"Delete");
	}
}
