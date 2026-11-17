using System.Text.Json;
using System.Text.RegularExpressions;

namespace FluentFin.Core.Updates;

/// <summary>Seaglass: where updates come from. Nothing else is ever downloaded or opened as an update.</summary>
public static class UpdateSource
{
	public const string RepositoryUrl = "https://github.com/mbustos27/fluentfin-mpv";
	public const string LatestReleaseApi = "https://api.github.com/repos/mbustos27/fluentfin-mpv/releases/latest";
	public const string ReleasesPage = RepositoryUrl + "/releases";
	private const string DownloadPrefix = RepositoryUrl + "/releases/download/";
	private const string PagePrefix = RepositoryUrl + "/releases/";

	public static bool IsReleasePage(string? url) =>
		url is not null && url.StartsWith(PagePrefix, StringComparison.Ordinal) && Uri.TryCreate(url, UriKind.Absolute, out var uri)
		&& uri.Scheme == Uri.UriSchemeHttps && uri.Host == "github.com" && uri.UserInfo.Length == 0
		&& !url.Contains("..", StringComparison.Ordinal) && !url.Contains("%2e", StringComparison.OrdinalIgnoreCase) && !url.Contains('\\');

	private const string DownloadPath = "/mbustos27/fluentfin-mpv/releases/download/";

	/// <summary>
	/// Hosts a download may be served from after GitHub's redirect: the API names github.com, which answers 302 to
	/// release-assets.githubusercontent.com (objects.githubusercontent.com before 2025).
	/// </summary>
	private static readonly string[] s_assetHosts = ["release-assets.githubusercontent.com", "objects.githubusercontent.com"];

	/// <summary>
	/// A release asset of this repository: https, host github.com, no credentials or port, a path under
	/// <c>/mbustos27/fluentfin-mpv/releases/download/</c> with no dot segments, encoded dots, encoded or backslash separators.
	/// </summary>
	public static bool IsDownload(string? url)
	{
		if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
		{
			return false;
		}

		if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
			|| !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
		{
			return false;
		}

		// Uri collapses dot segments and decodes %2e in AbsolutePath, so look at the text as it was given
		var raw = url;
		var afterHost = raw.IndexOf("//", StringComparison.Ordinal) is var i and >= 0 ? raw[(i + 2)..] : raw;
		var slash = afterHost.IndexOf('/');
		var path = slash < 0 ? "" : afterHost[slash..];
		var q = path.IndexOfAny(['?', '#']);
		if (q >= 0)
		{
			path = path[..q];
		}

		if (path.Contains("..", StringComparison.Ordinal) || path.Contains('\\') || path.Contains("//", StringComparison.Ordinal)
			|| path.Contains("%2e", StringComparison.OrdinalIgnoreCase) || path.Contains("%2f", StringComparison.OrdinalIgnoreCase)
			|| path.Contains("%5c", StringComparison.OrdinalIgnoreCase) || path.Contains("%00", StringComparison.Ordinal))
		{
			return false;
		}

		return uri.AbsolutePath.StartsWith(DownloadPath, StringComparison.Ordinal) && uri.AbsolutePath.Length > DownloadPath.Length;
	}

	/// <summary>Where a download that started at an <see cref="IsDownload"/> address may end up: github.com or GitHub's asset host, over https.</summary>
	public static bool IsDownloadHost(Uri? finalUri) =>
		finalUri is { Scheme: "https" }
		&& (string.Equals(finalUri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
			|| s_assetHosts.Contains(finalUri.Host, StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Seaglass: a newer release found on GitHub: its version, its page, its notes and, when the release has them, the installer
/// (<c>Seaglass-X.Y.Z-win-x64.msi</c>) and its checksum file. Only this repository's release assets are ever taken.
/// </summary>
public sealed record ReleaseInfo(string Version, string PageUrl, string Notes, string? MsiName, string? MsiUrl, string? Sha256Url)
{
	public bool HasInstaller => MsiName is not null && MsiUrl is not null && Sha256Url is not null;

	/// <summary>
	/// This release with whatever does not pass today's rules taken away: a stored one (settings can be edited, or come from an
	/// older build) is checked again before it is used. The installer goes only when both of its URLs and its name are valid.
	/// </summary>
	public ReleaseInfo Validated()
	{
		var page = UpdateSource.IsReleasePage(PageUrl) ? PageUrl : $"{UpdateSource.ReleasesPage}/tag/v{Version}";
		var ok = HasInstaller && UpdateSource.IsDownload(MsiUrl) && UpdateSource.IsDownload(Sha256Url)
			&& MsiName == $"Seaglass-{Version}-win-x64.msi";
		return ok ? this with { PageUrl = page } : this with { PageUrl = page, MsiName = null, MsiUrl = null, Sha256Url = null };
	}

	/// <summary>The release as GitHub's "latest release" JSON says it; null for a draft, a pre-release, or what is not a Seaglass release.</summary>
	public static ReleaseInfo? Parse(string? json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return null;
		}

		try
		{
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Object || Flag(root, "draft") || Flag(root, "prerelease"))
			{
				return null;
			}

			var tag = Text(root, "tag_name");
			if (!UpdateVersion.TryParse(tag, out var version) || version.Pre.Length > 0)
			{
				return null;
			}

			var versionText = version.ToString();
			var page = Text(root, "html_url");
			if (!UpdateSource.IsReleasePage(page))
			{
				page = $"{UpdateSource.ReleasesPage}/tag/v{versionText}";
			}

			// only the installer this version's release builds: Seaglass-<version>-win-x64.msi and its .sha256, from this repository
			var msiName = $"Seaglass-{versionText}-win-x64.msi";
			string? msiUrl = null, shaUrl = null;
			if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
			{
				foreach (var asset in assets.EnumerateArray())
				{
					var name = Text(asset, "name");
					var url = Text(asset, "browser_download_url");
					if (!UpdateSource.IsDownload(url))
					{
						continue;
					}

					if (string.Equals(name, msiName, StringComparison.Ordinal))
					{
						msiUrl = url;
					}
					else if (string.Equals(name, msiName + ".sha256", StringComparison.Ordinal))
					{
						shaUrl = url;
					}
				}
			}

			var haveBoth = msiUrl is not null && shaUrl is not null;
			return new ReleaseInfo(versionText, page!, Text(root, "body") ?? "", haveBoth ? msiName : null, haveBoth ? msiUrl : null, haveBoth ? shaUrl : null);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static string? Text(JsonElement element, string name) =>
		element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

	private static bool Flag(JsonElement element, string name) =>
		element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>Seaglass: the <c>.sha256</c> file next to an installer: "&lt;64 hex digits&gt; *&lt;file name&gt;".</summary>
public static partial class Sha256File
{
	[GeneratedRegex(@"^\s*([0-9a-fA-F]{64})(?:\s+\*?(\S.*?))?\s*$")]
	private static partial Regex Line();

	/// <summary>The hash in the file's first line, lowercase; null when it is not one, or names another file than <paramref name="fileName"/>.</summary>
	public static string? Parse(string? text, string fileName)
	{
		var first = (text ?? "").Split('\n', 2)[0];
		var m = Line().Match(first.TrimEnd('\r'));
		if (!m.Success)
		{
			return null;
		}

		var named = m.Groups[2].Success ? m.Groups[2].Value : null;
		return named is null || string.Equals(named, fileName, StringComparison.Ordinal) ? m.Groups[1].Value.ToLowerInvariant() : null;
	}
}
