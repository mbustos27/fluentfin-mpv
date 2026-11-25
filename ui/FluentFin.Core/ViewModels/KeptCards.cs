using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

/// <summary>
/// Nixie 1.7.4: a kept page refreshed behind (<see cref="Contracts.ViewModels.IKeptOnBack.Returned"/>) keeps its cards when the
/// server lists the same items in the same order, so the focus and the scroll stay where Back put them: each card's record takes
/// the server's answer, and a card whose watch state or favourite changed hears it as from any other surface
/// (<see cref="ItemUserData.Publish"/>: its badge, its name for Narrator). Anything else (an item added, removed or moved) is the
/// page's to show anew. Home keeps its rows the same way (HomeViewModel.KeepCards).
/// </summary>
public static class KeptCards
{
	/// <summary>
	/// Whether <paramref name="shown"/> lists the same items as <paramref name="fresh"/>, in the same order: then each takes its
	/// fresh record and the changed ones are announced; else nothing is changed (false: the caller replaces the list).
	/// </summary>
	public static bool Refresh(IReadOnlyList<BaseItemViewModel> shown, IReadOnlyList<BaseItemDto> fresh)
	{
		if (!SameItems(shown.Select(x => x.Dto?.Id), fresh.Select(x => x.Id)))
		{
			return false;
		}

		List<(Guid Id, UserItemDataDto Data)> changed = [];
		for (var i = 0; i < fresh.Count; i++)
		{
			var card = shown[i];
			var answer = fresh[i];
			if (answer.UserData is { } data && answer.Id is { } id && !SameUserData(card.Dto?.UserData, data))
			{
				changed.Add((id, data));
			}

			card.Dto = answer;
			card.UserData = UserItemDataViewModel.FromDto(answer.UserData);
		}

		foreach (var (id, data) in changed)
		{
			ItemUserData.Publish(id, data);
		}

		return true;
	}

	/// <summary>
	/// The same for a list of records (an episode list): the same items in the same order keep their records, which take the
	/// server's watch state and favourite (the rows showing them hear it); false: the caller replaces the list.
	/// </summary>
	public static bool RefreshRecords(IReadOnlyList<BaseItemDto> shown, IReadOnlyList<BaseItemDto> fresh)
	{
		if (!SameItems(shown.Select(x => x.Id), fresh.Select(x => x.Id)))
		{
			return false;
		}

		for (var i = 0; i < fresh.Count; i++)
		{
			RefreshItem(shown[i], fresh[i]);
		}

		return true;
	}

	/// <summary>
	/// The item a page shows, as the server has it now: its watch state, favourite and resume point taken into the record shown,
	/// and told (<see cref="ItemUserData.Publish"/>: the page's hero, its toggles, the cards and rows of it). True when it changed.
	/// </summary>
	public static bool RefreshItem(BaseItemDto? shown, BaseItemDto? fresh)
	{
		if (shown?.Id is not { } id || fresh?.Id != id || fresh.UserData is not { } data || SameUserData(shown.UserData, data))
		{
			return false;
		}

		shown.UserData = data;
		ItemUserData.Publish(id, data);
		return true;
	}

	/// <summary>The same ids, in the same order (an item with no id matches nothing).</summary>
	public static bool SameItems(IEnumerable<Guid?> shown, IEnumerable<Guid?> fresh)
	{
		var a = shown.ToList();
		var b = fresh.ToList();
		return a.Count == b.Count && a.Zip(b).All(x => x.First is { } left && x.First == x.Second);
	}

	/// <summary>
	/// Nixie 1.7.5 (the release gate's playback-basics): the watch states told while a kept page was refreshed behind whose last
	/// word is older than one told before it. Back from the player shows the season page at once and asks the server behind it,
	/// before the player's Stopped report has reached the server; the play's own word (RecentPlays, after the report: 20 %
	/// watched) came first, then the refresh's older answer (1 %) was told over it, and the row kept 1 %. Each item here is
	/// told again with its newest data (by <see cref="UserItemDataDto.LastPlayedDate"/>), only when strictly newer than the last.
	/// </summary>
	/// <param name="told">What was told since the refresh began, in order (the oldest first).</param>
	public static IReadOnlyList<(Guid Id, UserItemDataDto Data)> NewerThanLastTold(IEnumerable<(Guid Id, UserItemDataDto Data)> told) =>
		[.. told.GroupBy(x => x.Id)
			.Select(g => (Id: g.Key, Last: g.Last().Data, Newest: g.Where(x => x.Data.LastPlayedDate is not null).Select(x => x.Data).OrderBy(x => x.LastPlayedDate).LastOrDefault()))
			// (a last word with no date, a mark as unwatched, is the user's: never told over)
			.Where(x => x.Newest is { LastPlayedDate: { } newest } && !ReferenceEquals(x.Newest, x.Last) && x.Last.LastPlayedDate is { } last && newest > last)
			.Select(x => (x.Id, x.Newest!))];

	/// <summary>
	/// Nixie 1.7.5: each item's word to tell a kept page that missed it: its last, unless one told before it is newer
	/// (<see cref="NewerThanLastTold"/>), in the order the items were first told.
	/// </summary>
	public static IReadOnlyList<(Guid Id, UserItemDataDto Data)> LatestTold(IEnumerable<(Guid Id, UserItemDataDto Data)> told)
	{
		var list = told.ToList();
		var newer = NewerThanLastTold(list).ToDictionary(x => x.Id, x => x.Data);
		return [.. list.GroupBy(x => x.Id).Select(g => (g.Key, newer.TryGetValue(g.Key, out var n) ? n : g.Last().Data))];
	}

	/// <summary>What a card shows of the user's data: watched, favourite, how far in, and what is left to watch.</summary>
	public static bool SameUserData(UserItemDataDto? a, UserItemDataDto? b) =>
		a?.Played == b?.Played && a?.IsFavorite == b?.IsFavorite && a?.PlaybackPositionTicks == b?.PlaybackPositionTicks &&
		a?.PlayedPercentage == b?.PlayedPercentage && a?.UnplayedItemCount == b?.UnplayedItemCount;
}
