using FluentFin.Plugins.Jellyseer.ViewModels;

namespace FluentFin.Plugins.Jellyseer;

/// <summary>
/// Seaglass 1.1.3: Jellyseerr's entry in Settings > Integrations (it left the rail). Set up for the signed-in server, the card
/// opens its requests; not set up, it says so and opens the same page, which asks for the address. Either way in the app.
/// </summary>
public static class JellyseerrEntry
{
	public const string Name = "Jellyseerr";

	/// <summary>The navigation key of the Jellyseerr page.</summary>
	public static string PageKey { get; } = typeof(JellyseerDashboardViewModel).FullName!;

	/// <summary>The Jellyseerr address saved for <paramref name="serverId"/>, or null when it is not set up there.</summary>
	public static string? Address(JellyseerOptions? options, string? serverId) =>
		serverId is not null && options?.ServerMapping is { } mapping && mapping.TryGetValue(serverId, out var url) && !string.IsNullOrWhiteSpace(url)
			? url
			: null;

	public static string Description(JellyseerOptions? options, string? serverId) => Address(options, serverId) is { } url
		? $"See and answer media requests from {url}."
		: "See and answer media requests. Not set up for this server yet: add your Jellyseerr address.";

	public static string ActionText(JellyseerOptions? options, string? serverId) => Address(options, serverId) is null ? "Set up" : "Open";
}
