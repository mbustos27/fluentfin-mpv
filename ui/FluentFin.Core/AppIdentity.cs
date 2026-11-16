namespace FluentFin.Core;

/// <summary>
/// Seaglass: the app's name as a person or a Jellyfin server sees it (formerly "FluentFin MPV"). The namespaces and project
/// names stay FluentFin.* so upstream merges still apply.
/// </summary>
public static class AppIdentity
{
	/// <summary>The app's display name.</summary>
	public const string Name = "Seaglass";

	/// <summary>The client name sent to Jellyfin (the UI's client and the backend's, which is given this one in <c>connect</c>).</summary>
	public const string ClientName = Name;
}
