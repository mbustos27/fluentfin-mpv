using System.Reflection;

namespace FluentFin.Core.Outside;

/// <summary>
/// 1.7 trending: the Trakt Client ID this build carries. A release build gets it at build time: tools\release\build-release.ps1
/// reads the user environment variable <c>NIXIE_TRAKT_CLIENT_ID</c> (else <c>NIXIE_LIVE_TRAKT_CLIENT_ID</c>) and hands it to
/// the publish, which writes it into Nixie.dll as the assembly metadata <see cref="MetadataKey"/>. It is never in a committed
/// file, the source archive or a generated tracked file, and the script prints only whether it was found.
/// <para>
/// A Client ID identifies the app, not the user, and Trakt issues no secret for it; Nixie never signs anyone in (the smart
/// collections plan, Appendix A). Forks register their own app. Without one, Trakt's switch reads
/// <see cref="NotAvailable"/>. A harness run (a dev build pointed at the fake server) uses <see cref="HarnessValue"/>, which
/// only the fake accepts.
/// </para>
/// </summary>
public static class TraktClientId
{
	/// <summary>The assembly metadata key the release build writes (FluentFin.csproj, FluentFinTraktClientId).</summary>
	public const string MetadataKey = "NixieTraktClientId";

	/// <summary>The Client ID a harness run sends; the fake Trakt accepts it and nothing else (tools\ui-harness\fakeserver\trakt.py).</summary>
	public const string HarnessValue = "nixie-harness-trakt-client-id";

	/// <summary>Trakt's switch's words in a build without a Client ID (a dev build, a fork's).</summary>
	public const string NotAvailable = "Not available in this build";

	/// <summary>The running build's Client ID; null when it has none.</summary>
	public static string? Current(bool harness) =>
		Resolve(harness, Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>() ?? []);

	/// <summary>The harness's value in a harness run, else the one in <paramref name="metadata"/> (trimmed); null when none.</summary>
	public static string? Resolve(bool harness, IEnumerable<AssemblyMetadataAttribute> metadata)
	{
		if (harness)
		{
			return HarnessValue;
		}

		var value = metadata.FirstOrDefault(x => x.Key == MetadataKey)?.Value?.Trim();
		return string.IsNullOrEmpty(value) ? null : value;
	}
}
