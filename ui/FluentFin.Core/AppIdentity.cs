namespace FluentFin.Core;

/// <summary>
/// Nixie (1.2.0): the app's name as a person or a Jellyfin server sees it (formerly "Seaglass", and before that "FluentFin
/// MPV"). The namespaces and project names stay FluentFin.* so upstream merges still apply.
/// </summary>
public static class AppIdentity
{
	/// <summary>The app's display name.</summary>
	public const string Name = "Nixie";

	/// <summary>The client name sent to Jellyfin (the UI's client and the backend's, which is given this one in <c>connect</c>).</summary>
	public const string ClientName = Name;

	/// <summary>The app's earlier names, newest first.</summary>
	public static IReadOnlyList<string> FormerNames { get; } = ["Seaglass", "FluentFin MPV"];

	/// <summary>The prefix of the app's own environment variables (<c>NIXIE_THEME</c>, <c>NIXIE_FULLSCREEN</c>).</summary>
	public const string EnvironmentPrefix = "NIXIE_";

	/// <summary>The prefixes the same variables had before, still read when the new one is not set (<c>SEAGLASS_THEME</c>).</summary>
	public static IReadOnlyList<string> FormerEnvironmentPrefixes { get; } = ["SEAGLASS_"];

	/// <summary>
	/// The app's environment variable <paramref name="name"/> (without its prefix: "THEME"): <c>NIXIE_&lt;name&gt;</c>, else the
	/// old <c>SEAGLASS_&lt;name&gt;</c>, so scripts written for Seaglass keep working. An empty value counts as not set. Null
	/// when neither is set. <paramref name="environment"/> stands for <see cref="System.Environment.GetEnvironmentVariable(string)"/>.
	/// </summary>
	public static string? ReadEnvironment(string name, Func<string, string?>? environment = null)
	{
		environment ??= System.Environment.GetEnvironmentVariable;
		foreach (var prefix in new[] { EnvironmentPrefix }.Concat(FormerEnvironmentPrefixes))
		{
			if (environment(prefix + name) is { Length: > 0 } value)
			{
				return value;
			}
		}

		return null;
	}
}
