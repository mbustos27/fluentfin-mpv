using System.Text.RegularExpressions;

namespace FluentFin.Core.Mpv;

/// <summary>
/// Removes access tokens from text that goes to a log: the backend's stderr and protocol lines FluentFin logs.
/// Covers the forms docs/BRIDGE.md names (<c>accessToken</c>, <c>api_key=</c>, <c>X-Emby-Token</c>, <c>Authorization</c>)
/// as JSON, Python reprs, query strings and HTTP headers.
/// </summary>
public static partial class BridgeRedaction
{
	public const string Mask = "***";

	public static string Redact(string? text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return text ?? "";
		}

		// "accessToken": "x" / 'AccessToken': 'x' / "X-Emby-Token": "x" / "Authorization": "MediaBrowser ... Token=\"x\""
		text = QuotedKeyValue().Replace(text, m => m.Groups["pre"].Value + Mask + m.Groups["quote"].Value);

		// Authorization: MediaBrowser Client="FluentFin", ..., Token="x"  (header text, also inside other quotes)
		text = TokenAttribute().Replace(text, m => m.Groups["pre"].Value + Mask);

		// X-Emby-Token: x / X-MediaBrowser-Token=x / Authorization: Bearer x (header lines)
		text = HeaderValue().Replace(text, m => m.Groups["pre"].Value + Mask);

		// ?api_key=x / &ApiKey=x / accessToken=x
		text = QueryValue().Replace(text, Mask);

		return text;
	}

	/// <summary>
	/// A server URL as a log may show it: scheme, host and port only. No user info (a URL may carry a password), no path, no
	/// query (it may carry an api_key). "(none)" for no URL, "(unreadable)" for one that does not parse.
	/// </summary>
	public static string ServerForLog(string? url) =>
		string.IsNullOrWhiteSpace(url) ? "(none)"
		: Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host) ? $"{uri.Scheme}://{uri.Authority}"
		: "(unreadable)";

	[GeneratedRegex("""(?<pre>(?<kq>["'])(?:access_?token|api_?key|x-emby-token|x-mediabrowser-token|authorization|token)\k<kq>\s*:\s*(?<quote>["']))(?:\\.|(?!\k<quote>).)*\k<quote>""", RegexOptions.IgnoreCase)]
	private static partial Regex QuotedKeyValue();

	[GeneratedRegex("""(?<pre>\bToken=\\?["']?)[^"'\\,\s]+""", RegexOptions.IgnoreCase)]
	private static partial Regex TokenAttribute();

	[GeneratedRegex("""(?<pre>\b(?:x-emby-token|x-mediabrowser-token)\s*[:=]\s*|\bauthorization\s*[:=]\s*(?:bearer|basic)\s+)[^\s"',;&]+""", RegexOptions.IgnoreCase)]
	private static partial Regex HeaderValue();

	[GeneratedRegex("""(?<=\b(?:api_?key|access_?token)=)[^&\s'"]+""", RegexOptions.IgnoreCase)]
	private static partial Regex QueryValue();
}
