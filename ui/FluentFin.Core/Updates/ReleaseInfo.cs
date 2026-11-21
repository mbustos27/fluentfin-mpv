using System.Text.Json;
using System.Text.RegularExpressions;

namespace FluentFin.Core.Updates;

/// <summary>Seaglass: where updates come from. Nothing else is ever downloaded or opened as an update.</summary>
public static class UpdateSource
{
	/// <summary>
	/// Nixie 1.5 (bug hunt BH-140X-m3): the repository, "owner/name", in one place. tools\release names the same one (a test pins
	/// them together). After a rename on GitHub, a release is still taken from the repository GitHub's redirect names
	/// (<see cref="RepositoryOf"/>, <see cref="ReleaseInfo.Parse(string?, bool)"/>).
	/// </summary>
	public const string Repository = "mbustos27/fluentfin-mpv";

	public const string RepositoryUrl = "https://github.com/" + Repository;
	public const string LatestReleaseApi = "https://api.github.com/repos/" + Repository + "/releases/latest";
	public const string ReleasesPage = RepositoryUrl + "/releases";

	/// <summary>
	/// Nixie 1.3.1: the harness's override of <see cref="LatestReleaseApi"/> (<c>NIXIE_UPDATE_API</c>, through
	/// <see cref="AppIdentity.ReadEnvironment"/>): its fake server's release answer, so no automated run asks GitHub.
	/// </summary>
	public const string ApiVariable = "UPDATE_API";

	/// <summary>
	/// The API this run asks: <see cref="LatestReleaseApi"/>, unless the off-screen harness (<paramref name="harnessOn"/>, a dev
	/// build only: never in a release package) points it at its fake server on this machine (http, loopback).
	/// </summary>
	public static Uri ApiFor(string? overrideValue, bool harnessOn, bool releasePackage) =>
		!releasePackage && harnessOn && !string.IsNullOrWhiteSpace(overrideValue)
		&& Uri.TryCreate(overrideValue.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == "http" && uri.IsLoopback && uri.UserInfo.Length == 0
			? uri
			: new Uri(LatestReleaseApi);

	/// <summary>Nixie: the installer a release of <paramref name="version"/> carries, <c>Nixie-&lt;version&gt;-win-x64.msi</c> (Seaglass-... before 1.2.0).</summary>
	public static string InstallerName(string version) => $"{AppIdentity.Name}-{version}-win-x64.msi";

	/// <summary>Nixie: the MSI's registry key under HKCU (InstallFolder, StartMenuShortcut); <c>Software\Seaglass</c> before 1.2.0.</summary>
	public const string RegistryKey = @"Software\" + AppIdentity.Name;

	/// <summary>A release page of this repository (<see cref="Repository"/>).</summary>
	public static bool IsReleasePage(string? url) => IsReleasePage(url, Repository);

	/// <summary>Nixie 1.5: a release page of <paramref name="repository"/> ("owner/name"): https://github.com/owner/name/releases/...</summary>
	public static bool IsReleasePage(string? url, string repository) =>
		url is not null && IsRepositoryName(repository) && url.StartsWith("https://github.com/" + repository + "/releases/", StringComparison.Ordinal)
		&& Uri.TryCreate(url, UriKind.Absolute, out var uri)
		&& uri.Scheme == Uri.UriSchemeHttps && uri.Host == "github.com" && uri.UserInfo.Length == 0
		&& !url.Contains("..", StringComparison.Ordinal) && !url.Contains("%2e", StringComparison.OrdinalIgnoreCase) && !url.Contains('\\');

	/// <summary>
	/// Nixie 1.5: repositories a release is never taken from, whatever an answer says: upstream FluentFin's (its releases would
	/// replace this app and its mpv player).
	/// </summary>
	private static readonly string[] s_neverRepositories = ["insomniachi/FluentFin"];

	/// <summary>
	/// Nixie 1.5: whether <paramref name="repository"/> is "owner/name" as GitHub allows them (owner: letters, digits and single
	/// hyphens, at most 39; name: letters, digits, '.', '-', '_', at most 100, not "." or ".."), and not one of
	/// <see cref="s_neverRepositories"/>.
	/// </summary>
	public static bool IsRepositoryName(string? repository)
	{
		if (string.IsNullOrEmpty(repository) || repository.Split('/') is not [var owner, var name])
		{
			return false;
		}

		var ownerOk = owner.Length is > 0 and <= 39 && owner.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
			&& !owner.StartsWith('-') && !owner.EndsWith('-') && !owner.Contains("--", StringComparison.Ordinal);
		var nameOk = name.Length is > 0 and <= 100 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') && name is not ("." or "..");
		return ownerOk && nameOk && !s_neverRepositories.Contains(repository, StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Nixie 1.5 (BH-140X-m3): the repository ("owner/name") a release page names (https://github.com/owner/name/releases/...),
	/// or null when it is not such a page.
	/// </summary>
	public static string? RepositoryOf(string? pageUrl)
	{
		const string Github = "https://github.com/";
		if (pageUrl is null || !pageUrl.StartsWith(Github, StringComparison.Ordinal))
		{
			return null;
		}

		var parts = pageUrl[Github.Length..].Split('/');
		if (parts.Length < 4 || parts[2] != "releases")
		{
			return null;
		}

		var repository = parts[0] + "/" + parts[1];
		return IsRepositoryName(repository) && IsReleasePage(pageUrl, repository) ? repository : null;
	}

	/// <summary>
	/// Nixie 1.5 (BH-140X-m3): the repository an API URL of a release names (https://api.github.com/repos/owner/name/...), or
	/// null.
	/// </summary>
	public static string? RepositoryOfApi(string? apiUrl)
	{
		const string Api = "https://api.github.com/repos/";
		if (apiUrl is null || !apiUrl.StartsWith(Api, StringComparison.Ordinal))
		{
			return null;
		}

		var parts = apiUrl[Api.Length..].Split('/');
		return parts.Length >= 3 && IsRepositoryName(parts[0] + "/" + parts[1]) ? parts[0] + "/" + parts[1] : null;
	}

	/// <summary>
	/// Hosts a download may be served from after GitHub's redirect: the API names github.com, which answers 302 to
	/// release-assets.githubusercontent.com (objects.githubusercontent.com before 2025).
	/// </summary>
	private static readonly string[] s_assetHosts = ["release-assets.githubusercontent.com", "objects.githubusercontent.com"];

	/// <summary>
	/// A release asset of this repository: https, host github.com, no credentials or port, a path under
	/// <c>/mbustos27/fluentfin-mpv/releases/download/</c> with no dot segments, encoded dots, encoded or backslash separators.
	/// </summary>
	public static bool IsDownload(string? url) => IsDownload(url, Repository);

	/// <summary>Nixie 1.5 (BH-140X-m3): <see cref="IsDownload(string?)"/> for <paramref name="repository"/> ("owner/name").</summary>
	public static bool IsDownload(string? url, string repository)
	{
		if (string.IsNullOrEmpty(url) || !IsRepositoryName(repository) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
		{
			return false;
		}

		var downloadPath = "/" + repository + "/releases/download/";

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

		return uri.AbsolutePath.StartsWith(downloadPath, StringComparison.Ordinal) && uri.AbsolutePath.Length > downloadPath.Length;
	}

	/// <summary>Where a download that started at an <see cref="IsDownload"/> address may end up: github.com or GitHub's asset host, over https.</summary>
	public static bool IsDownloadHost(Uri? finalUri) =>
		finalUri is { Scheme: "https" }
		&& (string.Equals(finalUri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
			|| s_assetHosts.Contains(finalUri.Host, StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Seaglass: a newer release found on GitHub: its version, its page, its notes and, when the release has them, the installer
/// (<c>Nixie-X.Y.Z-win-x64.msi</c>, <see cref="UpdateSource.InstallerName"/>) and its checksum file. Only this repository's release assets are ever taken.
/// </summary>
public sealed record ReleaseInfo(string Version, string PageUrl, string Notes, string? MsiName, string? MsiUrl, string? Sha256Url)
{
	public bool HasInstaller => MsiName is not null && MsiUrl is not null && Sha256Url is not null;

	/// <summary>
	/// Nixie 1.5 (BH-140X-m3): the repository ("owner/name") the release came from: null for <see cref="UpdateSource.Repository"/>;
	/// another one only when GitHub's API redirected the check there (the repository was renamed or moved).
	/// </summary>
	public string? Repository { get; init; }

	/// <summary>Nixie 1.5: the installer's size in bytes as the release lists it ("1.5.1 is available · 187 MB"); null when unknown.</summary>
	public long? MsiSize { get; init; }

	/// <summary>
	/// Nixie 1.5 (second review): the installer's SHA-256 as GitHub's API records it for the asset (its <c>digest</c>,
	/// "sha256:&lt;hex&gt;"), lowercase hex; null when the API gave none. The download must match it and the .sha256 file both:
	/// the hash file sits beside the installer and nothing is code-signed, so GitHub's own record is the second witness.
	/// </summary>
	public string? MsiDigest { get; init; }

	/// <summary>Nixie 1.5: "sha256:&lt;64 hex&gt;" as lowercase hex; null for anything else.</summary>
	public static string? ParseDigest(string? digest)
	{
		const string Prefix = "sha256:";
		if (digest is null || !digest.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		var hex = digest[Prefix.Length..].Trim();
		return hex.Length == 64 && hex.All(char.IsAsciiHexDigit) ? hex.ToLowerInvariant() : null;
	}

	/// <summary>Nixie 1.5: the repository whose release pages and downloads this release may use.</summary>
	public string Source => Repository is { } other && UpdateSource.IsRepositoryName(other) ? other : UpdateSource.Repository;

	/// <summary>
	/// This release with whatever does not pass today's rules taken away: a stored one (settings can be edited, or come from an
	/// older build) is checked again before it is used. The installer goes only when both of its URLs and its name are valid.
	/// Nixie 1.5: a stored <see cref="Repository"/> stands only when its page names the same repository.
	/// </summary>
	public ReleaseInfo Validated()
	{
		var repository = Repository is { } other && UpdateSource.IsRepositoryName(other) && UpdateSource.RepositoryOf(PageUrl) == other ? other : null;
		var source = repository ?? UpdateSource.Repository;
		var page = UpdateSource.IsReleasePage(PageUrl, source) ? PageUrl : $"{UpdateSource.ReleasesPage}/tag/v{Version}";
		var ok = HasInstaller && UpdateSource.IsDownload(MsiUrl, source) && UpdateSource.IsDownload(Sha256Url, source)
			&& MsiName == UpdateSource.InstallerName(Version);
		var size = MsiSize is > 0 ? MsiSize : null;
		var digest = ParseDigest(MsiDigest is null ? null : "sha256:" + MsiDigest);
		return ok
			? this with { PageUrl = page, Repository = repository, MsiSize = size, MsiDigest = digest }
			: this with { PageUrl = page, Repository = repository, MsiName = null, MsiUrl = null, Sha256Url = null, MsiSize = null, MsiDigest = null };
	}

	/// <summary>The release as GitHub's "latest release" JSON says it; null for a draft, a pre-release, or what is not a release of this app.</summary>
	public static ReleaseInfo? Parse(string? json) => Parse(json, followRename: false);

	/// <summary>
	/// Nixie 1.5 (BH-140X-m3): <see cref="Parse(string?)"/>, where <paramref name="followRename"/> says GitHub's API redirected the
	/// request (a renamed or moved repository answers from /repositories/&lt;id&gt;/): the release is then taken from the
	/// repository its page names, when its API address names the same one. Without a redirect, only
	/// <see cref="UpdateSource.Repository"/>'s assets are taken, as before.
	/// </summary>
	public static ReleaseInfo? Parse(string? json, bool followRename)
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
			string? renamed = null;
			if (followRename && UpdateSource.RepositoryOf(page) is { } named && named != UpdateSource.Repository
				&& (Text(root, "url") is not { } api || UpdateSource.RepositoryOfApi(api) == named))
			{
				renamed = named;
			}

			var source = renamed ?? UpdateSource.Repository;
			if (!UpdateSource.IsReleasePage(page, source))
			{
				page = $"{UpdateSource.ReleasesPage}/tag/v{versionText}";
			}

			// only the installer this version's release builds: Nixie-<version>-win-x64.msi and its .sha256, from this repository
			var msiName = UpdateSource.InstallerName(versionText);
			string? msiUrl = null, shaUrl = null;
			long? msiSize = null;
			string? msiDigest = null;
			if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
			{
				foreach (var asset in assets.EnumerateArray())
				{
					var name = Text(asset, "name");
					var url = Text(asset, "browser_download_url");
					if (!UpdateSource.IsDownload(url, source))
					{
						continue;
					}

					if (string.Equals(name, msiName, StringComparison.Ordinal))
					{
						msiUrl = url;
						msiSize = asset.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var bytes) && bytes > 0 ? bytes : null;
						msiDigest = ParseDigest(Text(asset, "digest"));
					}
					else if (string.Equals(name, msiName + ".sha256", StringComparison.Ordinal))
					{
						shaUrl = url;
					}
				}
			}

			var haveBoth = msiUrl is not null && shaUrl is not null;
			return new ReleaseInfo(versionText, page!, Text(root, "body") ?? "", haveBoth ? msiName : null, haveBoth ? msiUrl : null, haveBoth ? shaUrl : null)
			{
				Repository = renamed,
				MsiSize = haveBoth ? msiSize : null,
				MsiDigest = haveBoth ? msiDigest : null,
			};
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
