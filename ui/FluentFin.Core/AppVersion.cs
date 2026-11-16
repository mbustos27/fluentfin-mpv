using System.Reflection;

namespace FluentFin.Core;

/// <summary>
/// fluentfin-mpv: the version the app shows (title bar, dashboard) and sends to Jellyfin as its client version: the
/// &lt;Version&gt; of ui\FluentFin\FluentFin.csproj ("1.0.0"), not the four-part assembly version ("1.0.0.0").
/// docs\RELEASE.md says where else a release sets it.
/// </summary>
public static class AppVersion
{
	/// <summary>The running app's (the entry assembly's) version.</summary>
	public static string Current { get; } = Of(Assembly.GetEntryAssembly());

	/// <summary>
	/// The assembly's informational version without build metadata ("1.0.0+abc" → "1.0.0"), else its version as
	/// major.minor.build; "0.0.0" when there is neither.
	/// </summary>
	public static string Of(Assembly? assembly)
	{
		var informational = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
		if (!string.IsNullOrWhiteSpace(informational))
		{
			var plus = informational.IndexOf('+');
			return (plus >= 0 ? informational[..plus] : informational).Trim();
		}

		return Of(assembly?.GetName().Version);
	}

	public static string Of(Version? version) => version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";

	/// <summary>The assembly metadata key tools\release\build-release.ps1 sets (FluentFin.csproj, FluentFinMpvRelease=true).</summary>
	public const string ReleaseMarker = "FluentFinMpvRelease";

	/// <summary>Whether the running app is a release package rather than a dev build (it then never looks for a checkout).</summary>
	public static bool IsReleasePackage { get; } = IsRelease(Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>() ?? []);

	public static bool IsRelease(IEnumerable<AssemblyMetadataAttribute> metadata) =>
		metadata.Any(x => x.Key == ReleaseMarker && string.Equals(x.Value, "true", StringComparison.OrdinalIgnoreCase));
}
