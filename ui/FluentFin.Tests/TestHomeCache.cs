using System.Reactive.Linq;
using FluentFin.Core.Services;
using Jellyfin.Sdk.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;

namespace FluentFin.Tests;

/// <summary>fluentfin-mpv: a HomeCache in a temporary folder of its own, created only if a page is saved (the caller deletes it).</summary>
internal static class TestHomeCache
{
	public static string NewFolder() => Path.Combine(Path.GetTempPath(), "fluentfin-tests-homecache-" + Guid.NewGuid().ToString("N"));

	public static HomeCache In(string folder) => new(folder, Observable.Never<IOutboundWebSocketMessage>(), NullLogger<HomeCache>.Instance, TimeProvider.System);

	/// <summary>For a test that does not look at Home: nothing saves into it, so its folder is never created.</summary>
	public static HomeCache Unused() => In(NewFolder());
}
