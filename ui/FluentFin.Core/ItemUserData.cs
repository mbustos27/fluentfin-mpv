using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core;

/// <summary>An item's user data changed (or a change failed and this is what it still is): every surface showing it updates.</summary>
/// <param name="partial">One episode of an Undo that puts many back: its card or row updates; a page waits for the end
/// (<paramref name="mixed"/>) to refresh once.</param>
/// <param name="mixed">A series or season whose episodes were put back one by one (an Undo): not all the same, so nothing
/// marks them all; pages show it as the server has it.</param>
public sealed class ItemUserDataChangedEventArgs(Guid itemId, UserItemDataDto userData, bool failed, bool partial = false, bool mixed = false) : EventArgs
{
	public bool Partial { get; } = partial;

	public bool Mixed { get; } = mixed;

	public Guid ItemId { get; } = itemId;

	/// <summary>The item's user data as the server has it (after a failure: as it was).</summary>
	public UserItemDataDto UserData { get; } = userData;

	/// <summary>The request failed: a toggle that flipped on the click springs back.</summary>
	public bool Failed { get; } = failed;
}

/// <summary>
/// 1.2.0 (1.1.3 UX audit, interaction item 3): the one way the app marks an item watched or a favourite, from a
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

	/// <summary>
	/// 1.2.0 (Nixie button review, I4 and I24): marking a whole series or season (un)watched is one click, and offers an Undo
	/// that puts each of its episodes back as it was (watched or not, its resume point). Shown by the window (UndoToast).
	/// </summary>
	public static event EventHandler<UndoOffer>? UndoOffered;

	/// <summary>
	/// sg-cw: the server changed an item's user data without one of these toggles (the mpv player's backend reported a play
	/// of it, docs/BRIDGE.md §6): <paramref name="userData"/> is what the server has now, and every surface showing the item
	/// updates (cards, rows, the series and season pages' Play and "Up next"). Raised on the calling thread (the UI thread).
	/// </summary>
	public static void Publish(Guid itemId, UserItemDataDto userData) =>
		Changed?.Invoke(null, new ItemUserDataChangedEventArgs(itemId, userData, failed: false));

	/// <returns>Whether the server took it.</returns>
	public static Task<bool> SetPlayedAsync(IJellyfinClient client, BaseItemDto dto, bool played) =>
		dto is { Type: BaseItemDto_Type.Series or BaseItemDto_Type.Season, Id: { } id }
			? SetPlayedOfManyAsync(client, dto, id, played)
			: SetAsync(dto, () => client.SetPlayed(dto, played));

	private static async Task<bool> SetPlayedOfManyAsync(IJellyfinClient client, BaseItemDto dto, Guid id, bool played)
	{
		// each episode as it is now: what an Undo puts back (no Undo when they cannot be read)
		IReadOnlyList<BaseItemDto>? before;
		try
		{
			before = dto.Type == BaseItemDto_Type.Series ? (await client.GetSeriesEpisodes(id))?.Items : (await client.GetItems(dto))?.Items;
		}
		catch (Exception)
		{
			before = null;
		}

		var took = await SetAsync(dto, () => client.SetPlayed(dto, played));
		if (!took || before is null)
		{
			return took;
		}

		// the episodes the mark changed: another watched state, or a resume point it cleared
		var changed = before
			.Where(e => e.Id is not null && ((e.UserData?.Played == true) != played || e.UserData?.PlaybackPositionTicks is > 0))
			.Select(e => new EpisodeState(e.Id!.Value, e.UserData?.Played == true, e.UserData?.PlaybackPositionTicks ?? 0))
			.ToList();
		if (changed.Count > 0)
		{
			UndoOffered?.Invoke(null, new UndoOffer(UndoOffer.Said(dto, played, changed.Count), () => UndoAsync(client, dto, id, changed)));
		}

		return took;
	}

	/// <summary>One episode as it was before a series or season was marked.</summary>
	public sealed record EpisodeState(Guid Id, bool Played, long PositionTicks);

	private static async Task<bool> UndoAsync(IJellyfinClient client, BaseItemDto dto, Guid id, IReadOnlyList<EpisodeState> states)
	{
		var all = true;
		foreach (var state in states)
		{
			UserItemDataDto? answer;
			try
			{
				answer = await client.SetUserData(state.Id, state.Played, state.PositionTicks);
			}
			catch (Exception)
			{
				answer = null;
			}

			if (answer is null)
			{
				all = false;
				continue;
			}

			Changed?.Invoke(null, new ItemUserDataChangedEventArgs(state.Id, answer, failed: false, partial: true));
		}

		// the series or season itself as the server has it now (some episodes watched, some not): its pages refresh once
		BaseItemDto? now;
		try
		{
			now = await client.GetItem(id);
		}
		catch (Exception)
		{
			now = null;
		}

		if (now?.UserData is { } data)
		{
			dto.UserData = data;
			Changed?.Invoke(null, new ItemUserDataChangedEventArgs(id, data, failed: false, mixed: true));
		}

		return all;
	}

	// 1.5 fixes
	/// <summary>
	/// 1.5 (the button review's L38): "Reset watch progress" offers an Undo, as Remove from Continue Watching and marking a
	/// series do: the Undo puts the resume point back where it was. Where it was is the server's, asked just before the reset
	/// (the review of 1.5, m6: the card's copy can be stale); no Undo when there was none, the server could not say, or it
	/// refused the reset. The Undo does nothing (false) when the item was played since the reset: a newer resume point or a
	/// newer last-played date is not overwritten with the old one.
	/// </summary>
	/// <returns>Whether the server took it.</returns>
	public static async Task<bool> ResetProgressAsync(IJellyfinClient client, BaseItemDto dto)
	{
		if (dto.Id is not { } id)
		{
			return false;
		}

		var before = await TryGetUserData(client, id);
		var ticks = before?.PlaybackPositionTicks ?? 0;
		var lastPlayed = before?.LastPlayedDate;
		bool took;
		try
		{
			took = await client.SetResumePosition(id, 0);
		}
		catch (Exception)
		{
			took = false;
		}

		if (!took)
		{
			return false;
		}

		// BH-150-m1: the item as the reset left it replaces its copy and every surface hears it (as the toggles do), so its
		// cards and rows lose their progress bar and its menu stops offering Reset. It is also the baseline for "played since"
		// (in case the server dates the reset as a play).
		var after = await TryGetUserData(client, id);
		Show(dto, id, after, 0);
		if (ticks > 0)
		{
			lastPlayed = after?.LastPlayedDate ?? lastPlayed;
			UndoOffered?.Invoke(null, new UndoOffer(UndoOffer.ResetSaid(dto), async () =>
			{
				var now = await TryGetUserData(client, id);
				if (now is null || PlayedSince(now, lastPlayed))
				{
					return false;
				}

				bool undone;
				try
				{
					undone = await client.SetResumePosition(id, ticks);
				}
				catch (Exception)
				{
					undone = false;
				}

				if (undone)
				{
					Show(dto, id, await TryGetUserData(client, id), ticks);   // BH-150-m1: the resume point is back on screen too
				}

				return undone;
			}));
		}

		return true;
	}

	/// <summary>
	/// BH-150-m1: <paramref name="dto"/>'s copy becomes <paramref name="server"/> (the server's answer), or, when the server
	/// could not say, its own copy at <paramref name="ticks"/>; then <see cref="Changed"/> tells every surface showing it.
	/// </summary>
	private static void Show(BaseItemDto dto, Guid id, UserItemDataDto? server, long ticks)
	{
		if (server is null)
		{
			server = dto.UserData ?? new UserItemDataDto();
			server.PlaybackPositionTicks = ticks;
			if (ticks == 0)
			{
				server.PlayedPercentage = null;
			}
		}

		dto.UserData = server;
		Changed?.Invoke(null, new ItemUserDataChangedEventArgs(id, server, failed: false));
	}

	/// <summary>The review of 1.5 (m6): the item was played after the reset: it has a resume point again, or a newer last play.</summary>
	public static bool PlayedSince(UserItemDataDto now, DateTimeOffset? lastPlayedAtReset) =>
		now.PlaybackPositionTicks is > 0 || (now.LastPlayedDate is { } at && (lastPlayedAtReset is not { } then || at > then));

	private static async Task<UserItemDataDto?> TryGetUserData(IJellyfinClient client, Guid id)
	{
		try
		{
			return (await client.GetItem(id))?.UserData;
		}
		catch (Exception)
		{
			return null;
		}
	}
	// END 1.5 fixes

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

/// <summary>
/// 1.2.0: the Undo offered after a whole series or season was marked: <see cref="Message"/> says what happened, and
/// <see cref="Undo"/> puts each episode back (true when the server took all of it).
/// </summary>
public sealed record UndoOffer(string Message, Func<Task<bool>> Undo)
{
	/// <summary>"Marked 8 episodes of Season 1 of Lost as watched."</summary>
	public static string Said(BaseItemDto dto, bool played, int episodes) =>
		$"Marked {episodes} {(episodes == 1 ? "episode" : "episodes")} of {ItemLabels.ActionTarget(dto)} as {(played ? "watched" : "unwatched")}.";

	/// <summary>1.5: "Reset the watch progress of Lost, S1 E4."</summary>
	public static string ResetSaid(BaseItemDto dto) => $"Reset the watch progress of {ItemLabels.ActionTarget(dto)}.";
}
