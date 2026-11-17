using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core;

/// <summary>An item's user data changed (or a change failed and this is what it still is): every surface showing it updates.</summary>
public sealed class ItemUserDataChangedEventArgs(Guid itemId, UserItemDataDto userData, bool failed) : EventArgs
{
	public Guid ItemId { get; } = itemId;

	/// <summary>The item's user data as the server has it (after a failure: as it was).</summary>
	public UserItemDataDto UserData { get; } = userData;

	/// <summary>The request failed: a toggle that flipped on the click springs back.</summary>
	public bool Failed { get; } = failed;
}

/// <summary>
/// Seaglass 1.1.4 (1.1.3 UX audit, interaction item 3): the one way the app marks an item watched or a favourite, from a
/// card's toggles, the item page's, an episode row's and the More menu's. The server's answer (the item's user data after the
/// change) replaces the item's copy, so the next click goes the other way and a menu built afterwards says the right thing;
/// then <see cref="Changed"/> tells every card, hero and row showing that item, which show it as the server has it. When the
/// request fails the item keeps its old state and <see cref="Changed"/> still fires with it, so a toggle that flipped on the
/// click springs back. Before, each surface sent "the opposite of the copy it was given", a copy nothing updated: a second
/// click sent the first click's request again.
/// </summary>
public static class ItemUserData
{
	/// <summary>Raised on the thread that asked (the UI thread in the app), after the item's copy is updated.</summary>
	public static event EventHandler<ItemUserDataChangedEventArgs>? Changed;

	/// <returns>Whether the server took it.</returns>
	public static Task<bool> SetPlayedAsync(IJellyfinClient client, BaseItemDto dto, bool played) =>
		SetAsync(dto, () => client.SetPlayed(dto, played));

	/// <returns>Whether the server took it.</returns>
	public static Task<bool> SetFavoriteAsync(IJellyfinClient client, BaseItemDto dto, bool favorite) =>
		SetAsync(dto, () => client.SetIsFavorite(dto, favorite));

	private static async Task<bool> SetAsync(BaseItemDto dto, Func<Task<UserItemDataDto?>> send)
	{
		if (dto.Id is not { } id)
		{
			return false;
		}

		UserItemDataDto? answer;
		try
		{
			answer = await send();
		}
		catch (Exception)
		{
			// the client logs its own failures and answers null; anything else is a failure too
			answer = null;
		}

		if (answer is not null)
		{
			dto.UserData = answer;
		}

		Changed?.Invoke(null, new ItemUserDataChangedEventArgs(id, dto.UserData ?? new UserItemDataDto(), failed: answer is null));
		return answer is not null;
	}
}
