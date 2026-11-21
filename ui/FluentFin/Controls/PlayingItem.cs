using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Controls;

/// <summary>
/// Nixie 1.4.0: the playing item as the server has it (its chapters, for the bar's Chapters menu), asked for once per item: the
/// player's events carry ids and names only (docs/BRIDGE.md §5). A failed request gives null (the menu says "No chapters").
/// </summary>
internal static class PlayingItem
{
	private static readonly ItemOnce s_item = new(id => App.GetService<IJellyfinClient>().GetItem(id), () => Environment.TickCount64);

	public static Task<BaseItemDto?> Get(string? id) => s_item.Get(id);
}

/// <summary>
/// One item's answer, asked for once while it plays. 1.5 (BH-140-m7): a failed answer (null, or the request threw) is not kept
/// for the whole session: it is asked again on the next <see cref="Get"/> once <see cref="RetryAfter"/> has passed since it was
/// asked, so one network blip as an episode starts no longer leaves its Chapters disabled until another item plays.
/// </summary>
internal sealed class ItemOnce(Func<Guid, Task<BaseItemDto?>> fetch, Func<long> nowMs)
{
	/// <summary>How long a failed answer stands before it is asked again (the player bar asks again when it next shows).</summary>
	public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(5);

	private readonly Lock _lock = new();
	private string? _id;
	private Task<BaseItemDto?>? _item;
	private long _askedAt;

	public Task<BaseItemDto?> Get(string? id)
	{
		if (id is null || !Guid.TryParse(id, out var guid))
		{
			return Task.FromResult<BaseItemDto?>(null);
		}

		lock (_lock)
		{
			var now = nowMs();
			var another = _item is null || !string.Equals(_id, id, StringComparison.OrdinalIgnoreCase);
			var failed = _item is { IsCompletedSuccessfully: true, Result: null } || _item is { IsFaulted: true } || _item is { IsCanceled: true };
			if (another || (failed && now - _askedAt >= (long)RetryAfter.TotalMilliseconds))
			{
				_id = id;
				_askedAt = now;
				_item = Fetch(guid);
			}

			return _item!;
		}
	}

	private async Task<BaseItemDto?> Fetch(Guid id)
	{
		try
		{
			return await fetch(id);
		}
		catch (Exception)
		{
			return null;
		}
	}
}
