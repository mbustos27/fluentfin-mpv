using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Mpv;

/// <summary>
/// 1.5 qacheck: the player page opened for a queue the page that opened it built (a playlist in its own order,
/// <see cref="GlobalCommands.PlayInOrder"/>): what the backend plays, and the item it starts with, so the loading screen
/// names it as it names an item page's Play.
/// </summary>
public sealed record QueuePlayRequest(BaseItemDto First, BridgePlayRequest Queue);
