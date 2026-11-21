namespace FluentFin.Core;

/// <summary>
/// 1.5 qacheck: a server address and a path on it. The address is kept as the user typed it, a reverse proxy's sub-path and
/// a "/" at its end included (https://host/jellyfin/); every URL Nixie builds by string joins it here, so the sub-path stays
/// and no path gets "//" (tools\qa-servers\subpath-probe.ps1 checks the whole app against a real server). The SDK and Flurl
/// join it the same way (ServerAddressTests).
/// </summary>
public static class ServerAddress
{
	/// <summary>
	/// <paramref name="path"/> ("/Items/{id}/Images/Primary?fillWidth=300", its leading "/" optional) on the server at
	/// <paramref name="baseUrl"/>; null when either is missing or the result is not an absolute URL.
	/// </summary>
	public static Uri? Join(string? baseUrl, string? path)
	{
		if (string.IsNullOrWhiteSpace(baseUrl) || path is null)
		{
			return null;
		}

		var joined = baseUrl.Trim().TrimEnd('/') + "/" + path.TrimStart('/');
		return Uri.TryCreate(joined, UriKind.Absolute, out var uri) ? uri : null;
	}
}
