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

	public static bool IsReleasePage(string? url) => url is not null && url.StartsWith(PagePrefix, StringComparison.Ordinal) && Uri.TryCreate(url, UriKind.Absolute, out _);

	public static bool IsDownload(string? url) => url is not null && url.StartsWith(DownloadPrefix, StringComparison.Ordinal) && Uri.TryCreate(url, UriKind.Absolute, out _);
}

/// <summary>
/// Seaglass: a newer release found on GitHub: its version, its page, its notes and, when the release has them, the installer
/// (<c>Seaglass-X.Y.Z-win-x64.msi</c>) and its checksum file. Only this repository's release assets are ever taken.
/// </summary>
public sealed record ReleaseInfo(string Version, string PageUrl, string Notes, string? MsiName, string? MsiUrl, string? Sha256Url)
{
	public bool HasInstaller => MsiName is not null && MsiUrl is not null && Sha256Url is not null;

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
