using System.Text.RegularExpressions;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core;

/// <summary>Nixie 1.3.1: how an item's Trailer button plays its trailer.</summary>
public enum TrailerKind
{
	/// <summary>No trailer: the button is hidden.</summary>
	None,

	/// <summary>A trailer file on the server (<c>/Items/{id}/LocalTrailers</c>): played by the mpv player, like any video.</summary>
	Local,

	/// <summary>A YouTube trailer: played in the theatre window (the privacy-enhanced embed).</summary>
	YouTube,

	/// <summary>Another web address: opened in the browser, as before 1.3.1.</summary>
	Browser,
}

/// <summary>
/// What the Trailer button does. <see cref="Url"/> is the first remote trailer that is a web address (also for a local one: the
/// fallback when the server's local trailer cannot be had), <see cref="YouTubeId"/> its video id when it is a YouTube link.
/// </summary>
public sealed record TrailerChoice(TrailerKind Kind, Uri? Url = null, string? YouTubeId = null);

/// <summary>What the theatre's web view does with a navigation (<see cref="Trailers.Navigation"/>).</summary>
public enum TrailerNavigation
{
	/// <summary>The embed page itself: let it load.</summary>
	Allow,

	/// <summary>A YouTube link ("Watch on YouTube"): cancelled here, opened in the system browser.</summary>
	OpenInBrowser,

	/// <summary>Anything else: cancelled.</summary>
	Block,
}

/// <summary>
/// Nixie 1.3.1: trailers play in the app. A local trailer (a file on the server) plays in the mpv player; a YouTube trailer plays
/// in the theatre window, a WebView2 that may load only the privacy-enhanced embed
/// (<c>https://www.youtube-nocookie.com/embed/&lt;id&gt;</c>); any other link opens in the browser.
/// </summary>
public static partial class Trailers
{
	/// <summary>The privacy-enhanced embed's host: the only page the theatre's web view loads.</summary>
	public const string EmbedHost = "www.youtube-nocookie.com";

	/// <summary>The embed's address before the video id.</summary>
	public static Uri DefaultEmbedBase { get; } = new("https://www.youtube-nocookie.com/embed/");

	/// <summary>The query every embed gets: start at once, related videos from the same channel only, inline.</summary>
	public const string EmbedQuery = "autoplay=1&rel=0&playsinline=1";

	/// <summary>
	/// The harness's override of <see cref="DefaultEmbedBase"/> (<c>NIXIE_TRAILER_EMBED_BASE</c>, read through
	/// <see cref="AppIdentity.ReadEnvironment"/>): a page of the fake server, so no automated run ever contacts YouTube.
	/// </summary>
	public const string EmbedBaseVariable = "TRAILER_EMBED_BASE";

	/// <summary>The hosts a YouTube link may have (lower case; <see cref="Uri.Host"/> is).</summary>
	private static readonly HashSet<string> YouTubeHosts =
	[
		"youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com",
		"youtube-nocookie.com", "www.youtube-nocookie.com", "youtu.be", "www.youtu.be",
	];

	[GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
	private static partial Regex VideoIdPattern();

	/// <summary>Whether the item has a trailer the button can play (a local one, or a remote web address).</summary>
	public static bool HasTrailer(BaseItemDto? dto) => Choose(dto).Kind != TrailerKind.None;

	/// <summary>
	/// What the Trailer button does for <paramref name="dto"/>: a local trailer first (when the server has one), then the first
	/// remote one: a YouTube link plays in the theatre, any other web address opens in the browser.
	/// </summary>
	public static TrailerChoice Choose(BaseItemDto? dto)
	{
		var url = ItemMeta.TrailerUrl(dto);
		var id = YouTubeId(url);
		if (dto?.LocalTrailerCount is > 0 && dto.Id is not null)
		{
			return new TrailerChoice(TrailerKind.Local, url, id);
		}

		return url is null ? new TrailerChoice(TrailerKind.None)
			: id is not null ? new TrailerChoice(TrailerKind.YouTube, url, id)
			: new TrailerChoice(TrailerKind.Browser, url);
	}

	/// <summary>
	/// What to do when the local trailer cannot be played (the server listed none, or the request failed): the remote one, if
	/// any, the way <see cref="Choose"/> would have played it without a local one.
	/// </summary>
	public static TrailerChoice WithoutLocal(TrailerChoice choice) =>
		choice.Kind != TrailerKind.Local ? choice
		: choice.Url is null ? new TrailerChoice(TrailerKind.None)
		: choice.YouTubeId is not null ? new TrailerChoice(TrailerKind.YouTube, choice.Url, choice.YouTubeId)
		: new TrailerChoice(TrailerKind.Browser, choice.Url);

	/// <summary>Whether <paramref name="host"/> is one of YouTube's own (exactly: <c>youtube.com.example</c> is not).</summary>
	public static bool IsYouTubeHost(string? host) => host is not null && YouTubeHosts.Contains(host.ToLowerInvariant());

	/// <summary>
	/// The video id of a YouTube link: <c>youtube.com/watch?v=</c>, <c>youtu.be/</c>, <c>/embed/</c>, <c>/shorts/</c>,
	/// <c>/v/</c> and <c>/live/</c>, on YouTube's own hosts only, over http or https on the default port; null for anything else
	/// (a look-alike host, a malformed id).
	/// </summary>
	public static string? YouTubeId(Uri? url)
	{
		if (url is not { IsAbsoluteUri: true } || url.Scheme is not ("https" or "http") || !url.IsDefaultPort
			|| !string.IsNullOrEmpty(url.UserInfo) || !IsYouTubeHost(url.Host))
		{
			return null;
		}

		var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
		string? id;
		if (url.Host.EndsWith("youtu.be", StringComparison.Ordinal))
		{
			id = segments.Length == 1 ? segments[0] : null;
		}
		else if (segments is ["watch"])
		{
			id = Query(url, "v");
		}
		else if (segments is ["embed" or "shorts" or "v" or "live", var tail])
		{
			id = tail;
		}
		else
		{
			id = null;
		}

		return id is not null && VideoIdPattern().IsMatch(id) ? id : null;
	}

	/// <summary>The embed page for a video id: <c>&lt;base&gt;&lt;id&gt;?autoplay=1&amp;rel=0&amp;playsinline=1</c>.</summary>
	public static Uri EmbedUri(string videoId, Uri? embedBase = null)
	{
		if (!VideoIdPattern().IsMatch(videoId))
		{
			throw new ArgumentException("Not a YouTube video id", nameof(videoId));
		}

		return new Uri(embedBase ?? DefaultEmbedBase, $"{videoId}?{EmbedQuery}");
	}

	/// <summary>
	/// The embed base this run uses: <see cref="DefaultEmbedBase"/>, unless the off-screen harness (<paramref name="harnessOn"/>,
	/// a dev build only: never in a release package) points it at a page of its fake server on this machine (http, loopback).
	/// </summary>
	public static Uri EmbedBase(string? overrideValue, bool harnessOn, bool releasePackage)
	{
		if (releasePackage || !harnessOn || string.IsNullOrWhiteSpace(overrideValue)
			|| !Uri.TryCreate(overrideValue.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "http" || !uri.IsLoopback
			|| !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
		{
			return DefaultEmbedBase;
		}

		return uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
	}

	/// <summary>
	/// The theatre's allow-list: only the embed page loads (the base's scheme, host and port, under its path); a YouTube link
	/// (the player's "Watch on YouTube") opens in the system browser; everything else is cancelled.
	/// </summary>
	public static TrailerNavigation Navigation(Uri? target, Uri embedBase)
	{
		if (target is not { IsAbsoluteUri: true })
		{
			return TrailerNavigation.Block;
		}

		if (target.Scheme == embedBase.Scheme && string.Equals(target.Host, embedBase.Host, StringComparison.OrdinalIgnoreCase)
			&& target.Port == embedBase.Port && string.IsNullOrEmpty(target.UserInfo)
			&& target.AbsolutePath.StartsWith(embedBase.AbsolutePath, StringComparison.Ordinal) && target.AbsolutePath.Length > embedBase.AbsolutePath.Length)
		{
			return TrailerNavigation.Allow;
		}

		return target.Scheme == "https" && string.IsNullOrEmpty(target.UserInfo) && target.IsDefaultPort && IsYouTubeHost(target.Host)
			? TrailerNavigation.OpenInBrowser
			: TrailerNavigation.Block;
	}

	/// <summary>Whether a new window the embed asks for may open in the system browser (a web address only).</summary>
	public static bool OpensInBrowser(Uri? target) =>
		target is { IsAbsoluteUri: true, Scheme: "https" or "http" } && string.IsNullOrEmpty(target.UserInfo);

	/// <summary>
	/// The theatre's video size in a window of <paramref name="width"/> x <paramref name="height"/> DIP: 16:9, 60 % of the width
	/// (so smaller in a narrow window), never taller than the height left after the frame's own parts (<paramref name="chrome"/>)
	/// and a margin; at least 320 wide.
	/// </summary>
	public static (double Width, double Height) VideoSize(double width, double height, double chrome)
	{
		var byWidth = width * 0.6;
		var byHeight = Math.Max(0, height - chrome - 48) * 16 / 9;
		var w = Math.Floor(Math.Max(320, Math.Min(byWidth, byHeight)));
		return (w, Math.Round(w * 9 / 16));
	}

	/// <summary>
	/// Nixie 1.4.0 (bug hunt M1: Ctrl+F over a playing trailer moved the keyboard into the hidden search box, and Esc then never
	/// closed the theatre): the theatre is modal. Esc and Back close it from anywhere; Tab, Enter, Space, the arrows and the
	/// modifiers go to its controls; every other key (Ctrl+F, /, R, F1, Alt+Home, Ctrl+, and a page's N, C or Ctrl+Z) is held back.
	/// Virtual-key codes, as <see cref="Shortcuts.AppShortcuts.For"/> takes them.
	/// </summary>
	public static TheaterKey KeyOverTheater(int virtualKey, bool control, bool shift, bool alt)
	{
		const int Tab = 0x09, Enter = 0x0D, Escape = 0x1B, Space = 0x20, Left = 0x25, Down = 0x28, GamepadB = 0xC4;
		if (virtualKey is Escape or GamepadB
			|| Shortcuts.AppShortcuts.For(virtualKey, control, shift, alt, inTextInput: false) == Shortcuts.AppShortcut.Back)
		{
			return TheaterKey.Close;
		}

		var modifierAlone = Shortcuts.AppShortcuts.IsModifierKey(virtualKey);
		var gamepad = virtualKey is >= 0xC3 and <= 0xDA;
		var plain = !control && !alt;
		return modifierAlone || gamepad || (plain && virtualKey is Tab or Enter or Space or (>= Left and <= Down))
			? TheaterKey.Pass
			: TheaterKey.HoldBack;
	}

	private static string? Query(Uri url, string name)
	{
		foreach (var part in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
		{
			var eq = part.IndexOf('=');
			if (eq > 0 && part[..eq] == name)
			{
				return Uri.UnescapeDataString(part[(eq + 1)..]);
			}
		}

		return null;
	}
}

/// <summary>Nixie 1.4.0 (bug hunt M1): what a key does while the theatre window is open (<see cref="Trailers.KeyOverTheater"/>).</summary>
public enum TheaterKey
{
	/// <summary>The theatre's own controls take it: Tab and Shift+Tab (they cycle inside), Enter, Space, the arrows, a modifier alone.</summary>
	Pass,

	/// <summary>It closes the theatre: Esc, and Back (Alt+Left, the Back key).</summary>
	Close,

	/// <summary>Held back: handled at the window and nothing runs (no shortcut, no page key, no key accelerator under the dim).</summary>
	HoldBack,
}

/// <summary>
/// Nixie 1.3.1: the theatre window's state: open with a title, never while the player is open, and on close the keyboard goes
/// back where it came from (the Trailer button) through <paramref name="refocus"/>, once.
/// </summary>
public sealed class TrailerWindowState(Action<object> refocus)
{
	public bool IsOpen { get; private set; }

	public string Title { get; private set; } = "";

	/// <summary>What had the keyboard when it opened (the Trailer button), given focus back on close.</summary>
	public object? ReturnTo { get; private set; }

	/// <summary>Narrator's name of the window: "Trailer: Heat".</summary>
	public string AutomationName => string.IsNullOrWhiteSpace(Title) ? "Trailer" : $"Trailer: {Title}";

	/// <summary>Opens the window for <paramref name="title"/>; false (nothing changes) while the player is open.</summary>
	public bool TryOpen(string? title, object? returnTo, bool playerOpen)
	{
		if (playerOpen)
		{
			return false;
		}

		Title = title?.Trim() ?? "";
		// a second trailer opened over the first keeps the first one's way back
		ReturnTo = IsOpen ? ReturnTo ?? returnTo : returnTo;
		IsOpen = true;
		return true;
	}

	/// <summary>Closes the window and gives the keyboard back; false when it was not open.</summary>
	public bool Close()
	{
		if (!IsOpen)
		{
			return false;
		}

		IsOpen = false;
		var back = ReturnTo;
		ReturnTo = null;
		if (back is not null)
		{
			refocus(back);
		}

		return true;
	}
}
