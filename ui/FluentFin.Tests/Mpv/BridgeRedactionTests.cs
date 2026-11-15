using FluentFin.Core.Mpv;

namespace FluentFin.Tests.Mpv;

public sealed class BridgeRedactionTests
{
	[Fact]
	public void InterruptedPlayback_ToString_NeverShowsTheServersCredentialsOrQuery()
	{
		// round 6, item 7: records print every member by default, the server URL as given included
		var playback = new MpvInterruptedPlayback("0123456789abcdef0123456789abcdef", "ms-1", "ps-1", 5_000_000, "The mpv backend stopped unexpectedly (exit code 3)",
			"https://alice:pa55word@jf.example:8920/jellyfin?api_key=key-in-url", "a11ce000000000000000000000000001");

		var text = playback.ToString();

		Assert.DoesNotContain("pa55word", text);
		Assert.DoesNotContain("key-in-url", text);
		Assert.DoesNotContain("alice:", text);
		Assert.Contains("https://jf.example:8920", text);
		Assert.Contains("ps-1", text);
		Assert.Contains("5000000", text);
		Assert.Contains("a11ce000000000000000000000000001", text);
		Assert.Contains("exit code 3", text);
	}

	private const string Token = "4f1c0ffee9d84b5a9c3b2a1d0e9f8a7b";

	[Theory]
	[InlineData("""{"id":1,"cmd":"connect","args":{"accessToken":"4f1c0ffee9d84b5a9c3b2a1d0e9f8a7b","protocol":1}}""")]
	[InlineData("""connect args: {'accessToken': '4f1c0ffee9d84b5a9c3b2a1d0e9f8a7b', 'server': {...}}""")]
	[InlineData("""GET http://jf:8096/Videos/abc/stream?static=true&api_key=4f1c0ffee9d84b5a9c3b2a1d0e9f8a7b&x=1""")]
	[InlineData("""GET http://jf:8096/Videos/abc/master.m3u8?ApiKey=4f1c0ffee9d84b5a9c3b2a1d0e9f8a7b""")]
	[InlineData("""headers {'X-Emby-Token': '4f1c0ffee9d84b5a9c3b2a1d0e9f8a7b', 'Accept': 'application/json'}""")]
	[InlineData("""X-Emby-Token: 4f1c0ffee9d84b5a9c3b2a1d0e9f8a7b""")]
	[InlineData("""X-MediaBrowser-Token=4f1c0ffee9d84b5a9c3b2a1d0e9f8a7b""")]
	[InlineData("""Authorization: MediaBrowser Client="FluentFin", Device="PC", DeviceId="d", Version="1", Token="4f1c0ffee9d84b5a9c3b2a1d0e9f8a7b" """)]
	[InlineData("""{"Authorization": "MediaBrowser Client=\"FluentFin\", Token=\"4f1c0ffee9d84b5a9c3b2a1d0e9f8a7b\""}""")]
	[InlineData("""Authorization: Bearer 4f1c0ffee9d84b5a9c3b2a1d0e9f8a7b""")]
	[InlineData("""mpv: Opening https://jf/Videos/1/stream.mkv?Static=true&MediaSourceId=1&access_token=4f1c0ffee9d84b5a9c3b2a1d0e9f8a7b""")]
	public void TokensAreRemoved(string line)
	{
		var redacted = BridgeRedaction.Redact(line);

		Assert.DoesNotContain(Token, redacted);
		Assert.Contains(BridgeRedaction.Mask, redacted);
	}

	[Theory]
	[InlineData("2026-11-13 12:00:00 [INFO] player: Playing item 0123456789abcdef (DirectPlay)")]
	[InlineData("""{"event":"position","data":{"positionTicks":6001234567,"durationTicks":72000000000}}""")]
	[InlineData("")]
	public void OtherTextIsLeftAlone(string line)
	{
		Assert.Equal(line, BridgeRedaction.Redact(line));
	}

	[Fact]
	public void TheRestOfTheLineSurvives()
	{
		var redacted = BridgeRedaction.Redact("""GET /Items?api_key=4f1c0ffee9d84b5a9c3b2a1d0e9f8a7b&Fields=Overview 200""");

		Assert.Equal("GET /Items?api_key=***&Fields=Overview 200", redacted);
	}

	[Theory]
	[InlineData("https://alice:pa55word@jf.example:8920/jellyfin?api_key=k3y#frag", "https://jf.example:8920")]
	[InlineData("http://jf:8096", "http://jf:8096")]
	[InlineData("https://jf.example/", "https://jf.example")]
	[InlineData(null, "(none)")]
	[InlineData("", "(none)")]
	[InlineData("not a url", "(unreadable)")]
	public void ServerForLog_ShowsOnlySchemeHostAndPort(string? url, string shown)
	{
		Assert.Equal(shown, BridgeRedaction.ServerForLog(url));
	}
}
