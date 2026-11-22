using System.Reflection;

namespace FluentFin.Core.Outside;

/// <summary>
/// 1.7 trending (the owner, 2026-11-22: "ya lets do that"): Nixie's own TMDb API Read Access Token (v4) for TMDb trending,
/// built into a release as the Trakt Client ID is: tools\release\build-release.ps1 reads the environment variable
/// <c>NIXIE_TMDB_TOKEN</c> (tools\release\tmdb-token.ps1) and hands it to the publish, which writes it into Nixie.dll as the
/// assembly metadata <see cref="MetadataKey"/>. It is never in a committed file, the source archive or a generated tracked
/// file, and the script prints only "TMDb: app token" or "TMDb: no app token".
/// <para>
/// At run time the user's own token (Settings, <see cref="TmdbToken"/>) wins; without one this one is used; without either,
/// TMDb trending reads "Not available in this build". It is sent only as <c>Authorization: Bearer</c>, never logged or put in
/// an address (<see cref="TmdbLists.Headers"/>). A harness run uses <see cref="HarnessValue"/>, which only the fake accepts.
/// </para>
/// </summary>
public static class TmdbAppToken
{
	/// <summary>The assembly metadata key the release build writes (FluentFin.csproj, FluentFinTmdbToken).</summary>
	public const string MetadataKey = "NixieTmdbToken";

	/// <summary>
	/// The token a harness run builds in (a JWT's shape, as a Read Access Token has); the fake TMDb accepts it and nothing else
	/// (tools\ui-harness\fakeserver\outside_tmdb_trending.py HARNESS_TOKEN).
	/// </summary>
	public const string HarnessValue = "eyJhbGciOiJIUzI1NiJ9.eyJuaXhpZSI6Imhhcm5lc3MifQ.bml4aWUtaGFybmVzcy1zaWduYXR1cmU";

	/// <summary>The running build's token; null when it has none.</summary>
	public static string? Current(bool harness) =>
		Resolve(harness, Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>() ?? []);

	/// <summary>
	/// The harness's value in a harness run, else the one in <paramref name="metadata"/> (trimmed) when it is shaped like a Read
	/// Access Token; null when none.
	/// </summary>
	public static string? Resolve(bool harness, IEnumerable<AssemblyMetadataAttribute> metadata)
	{
		if (harness)
		{
			return HarnessValue;
		}

		var value = metadata.FirstOrDefault(x => x.Key == MetadataKey)?.Value?.Trim();
		return string.IsNullOrEmpty(value) || TmdbLists.TokenProblem(value) is not null ? null : value;
	}
}
