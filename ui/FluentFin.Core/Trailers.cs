using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

	/// <summary>
	/// Another web address. Nixie 1.4.0: the theatre says Nixie can't play it, with an Open in browser button (the browser opens
	/// only when the user picks it, never by itself).
	/// </summary>
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
	/// <summary>The wrapper page (top level) or the embed (a frame inside it): let it load.</summary>
	Allow,

	/// <summary>A YouTube link the user clicked ("Watch on YouTube", the logo, the title): cancelled here, opened in the browser.</summary>
	OpenInBrowser,

	/// <summary>Anything else: cancelled.</summary>
	Block,
}

/// <summary>
/// Nixie 1.3.1: trailers play in the app. A local trailer (a file on the server) plays in the mpv player; a YouTube trailer plays
/// in the theatre window, a WebView2 that may load only the privacy-enhanced embed
/// (<c>https://www.youtube-nocookie.com/embed/&lt;id&gt;</c>). Nixie 1.4.0: the embed is a frame in Nixie's own page
/// (<see cref="WrapperHtml"/>, YouTube's Error 153), Nixie's message stands in for a trailer that can't play, and nothing
/// opens the browser but the user's pick (<see cref="Plan"/>).
/// </summary>
public static partial class Trailers
{
	/// <summary>The privacy-enhanced embed's host: the only frame the theatre's page loads.</summary>
	public const string EmbedHost = "www.youtube-nocookie.com";

	/// <summary>The embed's address before the video id.</summary>
	public static Uri DefaultEmbedBase { get; } = new("https://www.youtube-nocookie.com/embed/");

	/// <summary>The query every embed gets: start at once, related videos from the same channel only, inline.</summary>
	public const string EmbedQuery = "autoplay=1&rel=0&playsinline=1";

	/// <summary>
	/// Nixie 1.4.0 (YouTube's Error 153, "Video player configuration error"): YouTube plays an embed only for a client it can
	/// identify (the HTTP Referer, or the embedding page's origin). 1.3.1 loaded the embed as the web view's top page with a
	/// Referer header of its own: no embedding page, no referrer YouTube accepts, so every trailer showed Error 153. Now the web
	/// view loads a small page of Nixie's own (<see cref="WrapperHtml"/>, served by the app itself, nothing on disk or on the
	/// network) at this origin, YouTube's documented form for an app: the reverse-DNS app id as an https origin. The embed is a
	/// frame in it, told that origin (<c>origin=</c>) and sent it as its Referer.
	/// </summary>
	public const string WrapperHost = "io.github.mbustos27.nixie";

	/// <summary>The wrapper page's origin (<see cref="WrapperHost"/>): what the embed is told (<c>origin=</c>) and gets as its Referer.</summary>
	public const string WrapperOrigin = "https://" + WrapperHost;

	/// <summary>The wrapper page's one address: the only page the theatre's web view loads at its top.</summary>
	public static Uri WrapperUri { get; } = new(WrapperOrigin + "/trailer");

	/// <summary>The wrapper's address pattern for the web view's resource filter: everything on its host is the app's to answer.</summary>
	public const string WrapperFilter = WrapperOrigin + "/*";

	/// <summary>
	/// What the embed is asked for besides <see cref="EmbedQuery"/>: the IFrame Player API's messages (state and errors, read
	/// by the wrapper) and the origin they go to.
	/// </summary>
	public static string ApiQuery { get; } = "enablejsapi=1&origin=" + Uri.EscapeDataString(WrapperOrigin);

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

	/// <summary>
	/// The embed page for a video id: <c>&lt;base&gt;&lt;id&gt;?autoplay=1&amp;rel=0&amp;playsinline=1&amp;enablejsapi=1&amp;origin=</c>
	/// the wrapper's origin (<see cref="ApiQuery"/>).
	/// </summary>
	public static Uri EmbedUri(string videoId, Uri? embedBase = null)
	{
		if (!IsVideoId(videoId))
		{
			throw new ArgumentException("Not a YouTube video id", nameof(videoId));
		}

		return new Uri(embedBase ?? DefaultEmbedBase, $"{videoId}?{EmbedQuery}&{ApiQuery}");
	}

	/// <summary>Whether <paramref name="videoId"/> is a YouTube video id (11 of A-Z, a-z, 0-9, _ and -).</summary>
	public static bool IsVideoId(string? videoId) => videoId is not null && VideoIdPattern().IsMatch(videoId);

	/// <summary>The video's own page on YouTube ("Watch on YouTube").</summary>
	public static Uri WatchUri(string videoId) =>
		IsVideoId(videoId) ? new Uri($"https://www.youtube.com/watch?v={videoId}") : throw new ArgumentException("Not a YouTube video id", nameof(videoId));

	/// <summary>
	/// The wrapper's one script (inline, the same text for every trailer: the Content-Security-Policy allows exactly it, by its
	/// hash). It speaks the IFrame Player API's message protocol with the embed (the protocol YouTube's own iframe_api script
	/// uses, so no script is loaded from anywhere): it says it is listening until the player answers, asks for the player's
	/// state changes and errors, and keeps the latest in <c>window.nixieTrailer</c> ({ready, state, error}) for the app to
	/// read (<see cref="ReadPoll"/>). Messages count only from the embed's own frame and origin, and only numbers are kept.
	/// </summary>
	public static string WrapperScript { get; } = """
		(function () {
		  var f = document.getElementById('player'), o = new URL(f.src).origin;
		  var s = window.nixieTrailer = { ready: false, state: -2, error: 0 };
		  function send(m) { try { f.contentWindow.postMessage(JSON.stringify(m), o); } catch (e) { } }
		  function listen() { send({ event: 'listening', id: 1, channel: 'widget' }); }
		  function subscribe() {
		    ['onReady', 'onStateChange', 'onError'].forEach(function (n) {
		      send({ event: 'command', func: 'addEventListener', args: [n], id: 1, channel: 'widget' });
		    });
		  }
		  var tries = 0, t = setInterval(function () { if (s.ready || ++tries > 120) { clearInterval(t); } else { listen(); } }, 250);
		  f.addEventListener('load', listen);
		  window.addEventListener('message', function (e) {
		    if (e.origin !== o || e.source !== f.contentWindow) { return; }
		    var d;
		    try { d = typeof e.data === 'string' ? JSON.parse(e.data) : e.data; } catch (x) { return; }
		    if (!d || typeof d !== 'object') { return; }
		    if (!s.ready) { s.ready = true; subscribe(); }
		    var i = d.info;
		    if (d.event === 'onStateChange' && typeof i === 'number') { s.state = i; }
		    if ((d.event === 'infoDelivery' || d.event === 'initialDelivery') && i && typeof i.playerState === 'number') { s.state = i.playerState; }
		    if (d.event === 'onError' && typeof i === 'number' && i > 0) { s.error = i; }
		  });
		})();
		""".ReplaceLineEndings("\n");

	/// <summary>The wrapper's style: the frame full size on black.</summary>
	public const string WrapperStyle = "html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#000}iframe{display:block;border:0;width:100%;height:100%}";

	/// <summary>
	/// The wrapper page for <paramref name="videoId"/>: the embed (<see cref="EmbedUri"/>) in a full-size frame on black, with
	/// the referrer policy that sends YouTube the wrapper's origin, and <see cref="WrapperScript"/>. Nothing else: no other
	/// script, no style or image from anywhere. The id is checked (and the address escaped all the same).
	/// </summary>
	public static string WrapperHtml(string videoId, Uri? embedBase = null)
	{
		var src = WebUtility.HtmlEncode(EmbedUri(videoId, embedBase).AbsoluteUri);
		return "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Trailer</title>"
			+ $"<style>{WrapperStyle}</style></head><body>"
			+ $"<iframe id=\"player\" title=\"Trailer\" src=\"{src}\" referrerpolicy=\"strict-origin-when-cross-origin\" "
			+ "allow=\"autoplay; encrypted-media; picture-in-picture; fullscreen\" allowfullscreen></iframe>"
			+ $"<script>{WrapperScript}</script></body></html>";
	}

	/// <summary>
	/// The wrapper's Content-Security-Policy (sent with it): nothing by default; its own script and style by their hashes; the
	/// embed's origin as the one frame; no base, no forms, no plugins.
	/// </summary>
	public static string WrapperPolicy(Uri? embedBase = null)
	{
		var embed = embedBase ?? DefaultEmbedBase;
		return $"default-src 'none'; script-src '{Hash(WrapperScript)}'; style-src '{Hash(WrapperStyle)}'; "
			+ $"frame-src {embed.GetLeftPart(UriPartial.Authority)}; base-uri 'none'; form-action 'none'; object-src 'none'";
	}

	/// <summary>The response headers the wrapper is sent with (CRLF-separated, as WebView2's response takes them).</summary>
	public static string WrapperHeaders(Uri? embedBase = null) =>
		"Content-Type: text/html; charset=utf-8\r\n"
		+ $"Content-Security-Policy: {WrapperPolicy(embedBase)}\r\n"
		+ "Referrer-Policy: strict-origin-when-cross-origin\r\n"
		+ "Cache-Control: no-store";

	private static string Hash(string text) => "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

	/// <summary>Whether a request the web view's resource filter caught is the wrapper's one address (anything else there: 404).</summary>
	public static bool IsWrapper(Uri? target) =>
		target is { IsAbsoluteUri: true, Scheme: "https" } && target.IsDefaultPort && string.IsNullOrEmpty(target.UserInfo)
		&& string.Equals(target.Host, WrapperHost, StringComparison.OrdinalIgnoreCase)
		&& target.AbsolutePath == WrapperUri.AbsolutePath && string.IsNullOrEmpty(target.Query);

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
	/// The theatre's allow-list (Nixie 1.4.0). At the top only the wrapper page loads (<see cref="IsWrapper"/>); in a frame
	/// (<paramref name="frame"/>) only the embed (the base's scheme, host and port, under its path) and an empty frame
	/// (<c>about:blank</c>, <c>about:srcdoc</c>: nothing loaded). A YouTube link the user clicked (<paramref name="userInitiated"/>:
	/// the player's logo, its title, "Watch on YouTube") opens in the system browser; one the page follows by itself is cancelled
	/// like everything else, so the browser never opens unasked.
	/// </summary>
	public static TrailerNavigation Navigation(Uri? target, Uri embedBase, bool frame, bool userInitiated)
	{
		if (target is not { IsAbsoluteUri: true })
		{
			return TrailerNavigation.Block;
		}

		if (!frame && IsWrapper(target))
		{
			return TrailerNavigation.Allow;
		}

		if (frame && (IsEmbed(target, embedBase) || target.AbsoluteUri is "about:blank" or "about:srcdoc"))
		{
			return TrailerNavigation.Allow;
		}

		return userInitiated && target.Scheme == "https" && string.IsNullOrEmpty(target.UserInfo) && target.IsDefaultPort && IsYouTubeHost(target.Host)
			? TrailerNavigation.OpenInBrowser
			: TrailerNavigation.Block;
	}

	/// <summary>Whether <paramref name="target"/> is an embed page under <paramref name="embedBase"/> (same scheme, host and port).</summary>
	private static bool IsEmbed(Uri target, Uri embedBase) =>
		target.Scheme == embedBase.Scheme && string.Equals(target.Host, embedBase.Host, StringComparison.OrdinalIgnoreCase)
		&& target.Port == embedBase.Port && string.IsNullOrEmpty(target.UserInfo)
		&& target.AbsolutePath.StartsWith(embedBase.AbsolutePath, StringComparison.Ordinal) && target.AbsolutePath.Length > embedBase.AbsolutePath.Length;

	/// <summary>
	/// Whether a new window the page asks for opens in the system browser: a web address, and only when the user clicked for it
	/// (Nixie 1.4.0: a window the page opens by itself is refused).
	/// </summary>
	public static bool OpensInBrowser(Uri? target, bool userInitiated = true) =>
		userInitiated && target is { IsAbsoluteUri: true, Scheme: "https" or "http" } && string.IsNullOrEmpty(target.UserInfo);

	/// <summary>How long the player has to start (or say why it won't) once the wrapper has loaded.</summary>
	public static TimeSpan StartLimit { get; } = TimeSpan.FromSeconds(8);

	/// <summary>
	/// The wrapper's state as the app reads it (<c>ExecuteScriptAsync</c> of <c>JSON.stringify(window.nixieTrailer)</c>, which
	/// gives a JSON string holding the object's JSON): null when it isn't there or isn't that shape. Only the three numbers
	/// are read; nothing the page says is ever shown.
	/// </summary>
	public static TrailerPoll? ReadPoll(string? scriptResult)
	{
		if (string.IsNullOrWhiteSpace(scriptResult) || scriptResult.Length > 4096)
		{
			return null;
		}

		try
		{
			using var outer = JsonDocument.Parse(scriptResult);
			if (outer.RootElement.ValueKind != JsonValueKind.String || outer.RootElement.GetString() is not { } inner)
			{
				return null;
			}

			using var doc = JsonDocument.Parse(inner);
			var root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				return null;
			}

			var ready = root.TryGetProperty("ready", out var r) && r.ValueKind == JsonValueKind.True;
			var state = root.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.Number && st.TryGetInt32(out var sv) ? sv : -2;
			var error = root.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.Number && er.TryGetInt32(out var ev) ? ev : 0;
			return new TrailerPoll(ready, state, error);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	/// <summary>
	/// What the theatre makes of the latest poll: an error from the player (any code) is a refusal; a player that is playing,
	/// buffering, paused or ended has started; one that hasn't, <see cref="StartLimit"/> after the wrapper loaded, didn't start
	/// (YouTube never answered, or answered with an error page it doesn't report). Before that: waiting.
	/// </summary>
	public static TrailerVerdict Judge(TrailerPoll? poll, TimeSpan sinceLoaded, bool startedBefore)
	{
		if (poll is { Error: > 0 })
		{
			return TrailerVerdict.Refused;
		}

		if (startedBefore || poll is { State: 0 or 1 or 2 or 3 })
		{
			return TrailerVerdict.Started;
		}

		return sinceLoaded >= StartLimit ? TrailerVerdict.NotStarted : TrailerVerdict.Waiting;
	}

	/// <summary>The heading of every message the theatre shows instead of a trailer.</summary>
	public const string CantPlayHeading = "This trailer can't play in Nixie";

	/// <summary>
	/// Nixie's own message for a YouTube trailer the player refused (the IFrame Player API's error codes): the owner allows it
	/// only on YouTube (101, 150), it is gone (100), or YouTube refused the player (153, 2, 5 and anything else).
	/// </summary>
	public static TrailerMessage Refused(int code, string videoId) => new(CantPlayHeading, code switch
	{
		101 or 150 => "Its owner doesn't allow it to play outside YouTube.",
		100 => "It was removed from YouTube, or made private.",
		_ => "YouTube refused to play it here.",
	}, WatchOnYouTube, WatchUri(videoId));

	/// <summary>Nixie's message for a YouTube trailer that didn't start within <see cref="StartLimit"/>.</summary>
	public static TrailerMessage NotStarted(string videoId) =>
		new(CantPlayHeading, "YouTube didn't start it.", WatchOnYouTube, WatchUri(videoId));

	/// <summary>Nixie's message when the theatre's player (WebView2) can't start on this PC.</summary>
	public static TrailerMessage NoPlayer(string videoId) =>
		new(CantPlayHeading, "The trailer player (Microsoft Edge WebView2) didn't start on this PC.", WatchOnYouTube, WatchUri(videoId));

	/// <summary>Nixie's message when the theatre isn't there to play it (the window isn't up yet).</summary>
	public static TrailerMessage NotReady(string videoId) =>
		new(CantPlayHeading, "Nixie's trailer player isn't ready.", WatchOnYouTube, WatchUri(videoId));

	/// <summary>Nixie's message for a trailer on another site (<see cref="TrailerKind.Browser"/>): it names the site.</summary>
	public static TrailerMessage OnlyInBrowser(Uri url)
	{
		var site = SiteName(url);
		return new(CantPlayHeading, $"It's on {site}, which Nixie can't play.", $"Open {site} in browser", url);
	}

	/// <summary>The button that opens the video on YouTube.</summary>
	public const string WatchOnYouTube = "Watch on YouTube";

	/// <summary>A site's name as a person says it: the host without <c>www.</c> ("vimeo.com").</summary>
	public static string SiteName(Uri url)
	{
		var host = url.IsAbsoluteUri ? url.IdnHost.ToLowerInvariant() : "";
		host = host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
		return host.Length is > 0 and <= 60 ? host : "another site";
	}

	/// <summary>
	/// What the Trailer button does with a choice (Nixie 1.4.0): a local trailer plays in the player, a YouTube one in the
	/// theatre, any other link shows Nixie's message in the theatre (or, with no theatre, in a dialog), nothing for none. There
	/// is no step that opens the browser: only the user's click on the message's button (or inside YouTube's player) does.
	/// </summary>
	public static TrailerStep Plan(TrailerChoice choice, bool theaterUp) => choice.Kind switch
	{
		TrailerKind.Local => new TrailerStep(TrailerStepKind.Local),
		TrailerKind.YouTube when choice.YouTubeId is { } id => theaterUp
			? new TrailerStep(TrailerStepKind.Theater, id)
			: new TrailerStep(TrailerStepKind.Dialog, id, NotReady(id)),
		TrailerKind.Browser when choice.Url is { } url => new TrailerStep(theaterUp ? TrailerStepKind.Message : TrailerStepKind.Dialog, null, OnlyInBrowser(url)),
		_ => new TrailerStep(TrailerStepKind.Nothing),
	};

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
	/// Nixie 1.4.0: a held Enter or Space (<paramref name="repeat"/>) is held back too: the press that opened the theatre on the
	/// Trailer button must not go on to press the theatre's Close (or the message's), which then reopened it, and so on.
	/// </summary>
	public static TheaterKey KeyOverTheater(int virtualKey, bool control, bool shift, bool alt, bool repeat = false)
	{
		const int Tab = 0x09, Enter = 0x0D, Escape = 0x1B, Space = 0x20, Left = 0x25, Down = 0x28, GamepadB = 0xC4;
		if (virtualKey is Escape or GamepadB
			|| Shortcuts.AppShortcuts.For(virtualKey, control, shift, alt, inTextInput: false) == Shortcuts.AppShortcut.Back)
		{
			return TheaterKey.Close;
		}

		if (repeat && virtualKey is Enter or Space)
		{
			return TheaterKey.HoldBack;
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

/// <summary>Nixie 1.4.0: the wrapper's state, read by the app (<see cref="Trailers.ReadPoll"/>): the IFrame Player API's numbers.</summary>
/// <param name="Ready">The player answered.</param>
/// <param name="State">Its state: -2 none yet, -1 unstarted, 0 ended, 1 playing, 2 paused, 3 buffering, 5 cued.</param>
/// <param name="Error">Its error code (0 none): 2, 5, 100, 101, 150, 153.</param>
public sealed record TrailerPoll(bool Ready, int State, int Error);

/// <summary>Nixie 1.4.0: what the theatre makes of the player (<see cref="Trailers.Judge"/>).</summary>
public enum TrailerVerdict
{
	/// <summary>Not started yet, within <see cref="Trailers.StartLimit"/>.</summary>
	Waiting,

	/// <summary>Playing (or buffering, paused, ended): YouTube's player is the user's.</summary>
	Started,

	/// <summary>The player reported an error: Nixie's message instead (<see cref="Trailers.Refused"/>).</summary>
	Refused,

	/// <summary>Nothing started within the limit: Nixie's message instead (<see cref="Trailers.NotStarted"/>).</summary>
	NotStarted,
}

/// <summary>
/// Nixie 1.4.0: the theatre's own message when a trailer can't play there: the heading, a one-line reason, and the one way to
/// watch it elsewhere (<see cref="LinkText"/>, opening <see cref="Link"/> in the browser only when the user picks it).
/// </summary>
public sealed record TrailerMessage(string Heading, string Reason, string LinkText, Uri Link)
{
	/// <summary>What Narrator reads: the heading and the reason.</summary>
	public string AutomationText => $"{Heading}. {Reason}";
}

/// <summary>Nixie 1.4.0: what the Trailer button does (<see cref="Trailers.Plan"/>). None of them opens the browser.</summary>
public enum TrailerStepKind
{
	/// <summary>No trailer: nothing.</summary>
	Nothing,

	/// <summary>The server's trailer file plays in the player (or, when the server can't give it, the remote one's step).</summary>
	Local,

	/// <summary>A YouTube trailer plays in the theatre.</summary>
	Theater,

	/// <summary>The theatre shows Nixie's message (a trailer on another site).</summary>
	Message,

	/// <summary>No theatre: a small dialog with the same message and choice.</summary>
	Dialog,
}

/// <summary>Nixie 1.4.0: a step of the Trailer button (<see cref="Trailers.Plan"/>).</summary>
public sealed record TrailerStep(TrailerStepKind Kind, string? VideoId = null, TrailerMessage? Message = null);

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
