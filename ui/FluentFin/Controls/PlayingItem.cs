using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Controls;

/// <summary>
/// Nixie 1.4.0: the playing item as the server has it (its chapters, for the bar's Chapters menu), asked for once per item: the
/// player's events carry ids and names only (docs/BRIDGE.md §5). A failed request gives null (the menu says "No chapters").
/// </summary>
internal static class PlayingItem
{
	private static string? _id;
	private static Task<BaseItemDto?>? _item;

	public static Task<BaseItemDto?> Get(string? id)
	{
		if (id is null || !Guid.TryParse(id, out var guid))
		{
			return Task.FromResult<BaseItemDto?>(null);
		}

		if (_item is null || !string.Equals(_id, id, StringComparison.OrdinalIgnoreCase))
		{
			_id = id;
			_item = Fetch(guid);
		}

		return _item;
	}

	private static async Task<BaseItemDto?> Fetch(Guid id)
	{
		try
		{
			return await App.GetService<IJellyfinClient>().GetItem(id);
		}
		catch (Exception)
		{
			return null;
		}
	}
}
