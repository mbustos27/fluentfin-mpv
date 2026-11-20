using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Shortcuts;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>Nixie 1.3.1: trailers play in the app (Trailers, TrailerWindowState, a local trailer's queue).</summary>
public sealed class TrailerTests
{
	private const string Id = "dQw4w9WgXcQ";

	[Theory]
	[InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
	[InlineData("http://www.youtube.com/watch?v=dQw4w9WgXcQ")]
	[InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ&t=42s")]
	[InlineData("https://www.youtube.com/watch?feature=share&v=dQw4w9WgXcQ")]
	[InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ")]
	[InlineData("https://WWW.YouTube.com/watch?v=dQw4w9WgXcQ")]
	[InlineData("https://youtu.be/dQw4w9WgXcQ")]
	[InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc")]
	[InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ")]
	[InlineData("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ?autoplay=1")]
	[InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ")]
	[InlineData("https://www.youtube.com/v/dQw4w9WgXcQ")]
	public void EveryYouTubeLinkForm_GivesTheVideoId(string link) =>
		Assert.Equal(Id, Trailers.YouTubeId(new Uri(link)));

	[Theory]
	[InlineData("https://www.youtube.com.evil.example/watch?v=dQw4w9WgXcQ")]
	[InlineData("https://evilyoutube.com/watch?v=dQw4w9WgXcQ")]
	[InlineData("https://youtube.co/watch?v=dQw4w9WgXcQ")]
	[InlineData("https://youtu.be.evil.example/dQw4w9WgXcQ")]
	[InlineData("https://notyoutu.be/dQw4w9WgXcQ")]
	[InlineData("https://www.youtube.com@evil.example/watch?v=dQw4w9WgXcQ")]
	[InlineData("https://user@www.youtube.com/watch?v=dQw4w9WgXcQ")]
	[InlineData("https://evil.example/www.youtube.com/watch?v=dQw4w9WgXcQ")]
	[InlineData("https://evil.example/watch?v=dQw4w9WgXcQ")]
	[InlineData("https://www.youtube.com:8443/watch?v=dQw4w9WgXcQ")]
	[InlineData("ftp://www.youtube.com/watch?v=dQw4w9WgXcQ")]
	[InlineData("javascript://www.youtube.com/watch?v=dQw4w9WgXcQ")]
	[InlineData("https://www.youtube.com/watch?v=short")]
	[InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ%22%3E")]
	[InlineData("https://www.youtube.com/watch")]
	[InlineData("https://www.youtube.com/channel/UCdQw4w9WgXcQ")]
	[InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ/extra")]
	[InlineData("https://youtu.be/")]
	[InlineData("https://www.imdb.com/video/vi12345")]
	public void LookAlikesAndOtherLinks_GiveNoId(string link) =>
		Assert.Null(Trailers.YouTubeId(new Uri(link)));

	[Fact]
	public void TheEmbed_IsThePrivacyEnhancedOne_WithAutoplayAndNoOtherChannels_ToldTheWrappersOrigin()
	{
		Assert.Equal("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ?autoplay=1&rel=0&playsinline=1&enablejsapi=1&origin=https%3A%2F%2Fio.github.mbustos27.nixie",
			Trailers.EmbedUri(Id).AbsoluteUri);
		Assert.Throws<ArgumentException>(() => Trailers.EmbedUri("../../x"));
		Assert.Equal("https://io.github.mbustos27.nixie", Trailers.WrapperOrigin);
		Assert.Equal("https://io.github.mbustos27.nixie/trailer", Trailers.WrapperUri.AbsoluteUri);
		Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", Trailers.WatchUri(Id).AbsoluteUri);
	}

	/// <summary>
	/// Nixie 1.4.0 (YouTube's Error 153): the embed is a frame in Nixie's own page, which sends YouTube its origin as the Referer
	/// (the policy on the frame and the page), tells the embed that origin, and runs no script but its own, allowed by hash.
	/// </summary>
	[Fact]
	public void TheWrapper_EmbedsTheTrailer_FromTheAppsOrigin_WithItsOwnScriptOnly()
	{
		var page = Trailers.WrapperHtml(Id);

		Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "<iframe "));
		Assert.Contains("src=\"https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ?autoplay=1&amp;rel=0&amp;playsinline=1&amp;enablejsapi=1&amp;origin=https%3A%2F%2Fio.github.mbustos27.nixie\"", page);
		Assert.Contains("referrerpolicy=\"strict-origin-when-cross-origin\"", page);
		Assert.Contains("allow=\"autoplay; encrypted-media; picture-in-picture; fullscreen\"", page);
		// one script, inline, the same text every time; nothing loaded from anywhere (no src, no link, no import)
		Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "<script"));
		Assert.Contains($"<script>{Trailers.WrapperScript}</script>", page);
		Assert.DoesNotContain("<script src", page);
		Assert.DoesNotContain("<link", page);
		Assert.DoesNotContain("import", Trailers.WrapperScript);
		Assert.DoesNotContain("\r", Trailers.WrapperScript);
		// it subscribes to the player's state changes and errors (YouTube sends them only then), and marks the user's touch
		Assert.Contains("func: 'addEventListener', args: [n]", Trailers.WrapperScript);
		Assert.Contains("if (!s.ready) { s.ready = true; subscribe(); }", Trailers.WrapperScript);
		Assert.Contains("['onReady', 'onStateChange', 'onError']", Trailers.WrapperScript);
		Assert.Contains("function touch() { if (document.activeElement === f) { s.touched = true; } }", Trailers.WrapperScript);
		Assert.Contains("setInterval(touch, 250);", Trailers.WrapperScript);
		Assert.Equal(page.Replace(Id, "ZZZZZZZZZZZ"), Trailers.WrapperHtml("ZZZZZZZZZZZ"));
		// the id is checked, never written into the page as it came
		Assert.Throws<ArgumentException>(() => Trailers.WrapperHtml("\"><script>x"));
		Assert.Throws<ArgumentException>(() => Trailers.WrapperHtml("dQw4w9WgXc'"));

		// the policy: nothing by default, the script and the style by their hashes, the embed's origin the one frame
		var policy = Trailers.WrapperPolicy();
		var hash = "sha256-" + Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Trailers.WrapperScript)));
		Assert.StartsWith("default-src 'none'; ", policy);
		Assert.Contains($"script-src '{hash}';", policy);
		Assert.Contains("frame-src https://www.youtube-nocookie.com;", policy);
		Assert.DoesNotContain("unsafe", policy);
		Assert.DoesNotContain("*", policy);
		var headers = Trailers.WrapperHeaders();
		Assert.Contains($"Content-Security-Policy: {policy}", headers);
		Assert.Contains("Referrer-Policy: strict-origin-when-cross-origin", headers);
		Assert.Contains("Content-Type: text/html; charset=utf-8", headers);

		// the harness's stand-in loads the same way, its loopback origin the frame
		var fake = new Uri("http://127.0.0.1:5123/__harness/trailer/");
		Assert.Contains("src=\"http://127.0.0.1:5123/__harness/trailer/dQw4w9WgXcQ?autoplay=1&amp;", Trailers.WrapperHtml(Id, fake));
		Assert.Contains("frame-src http://127.0.0.1:5123;", Trailers.WrapperPolicy(fake));
	}

	[Theory]
	[InlineData("https://io.github.mbustos27.nixie/trailer", true)]
	[InlineData("https://IO.GITHUB.MBUSTOS27.NIXIE/trailer", true)]
	[InlineData("https://io.github.mbustos27.nixie/trailer?x=1", false)]
	[InlineData("https://io.github.mbustos27.nixie/trailer/x", false)]
	[InlineData("https://io.github.mbustos27.nixie/", false)]
	[InlineData("http://io.github.mbustos27.nixie/trailer", false)]
	[InlineData("https://io.github.mbustos27.nixie:444/trailer", false)]
	[InlineData("https://u@io.github.mbustos27.nixie/trailer", false)]
	[InlineData("https://io.github.mbustos27.nixie.evil.example/trailer", false)]
	public void TheResourceFilter_AnswersOnlyTheWrappersOneAddress(string target, bool wrapper) =>
		Assert.Equal(wrapper, Trailers.IsWrapper(new Uri(target)));

	private static BaseItemDto Item(int local, params string[] remote) => new()
	{
		Id = Guid.NewGuid(),
		Name = "Heat",
		Type = BaseItemDto_Type.Movie,
		LocalTrailerCount = local,
		RemoteTrailers = [.. remote.Select(u => new MediaUrl { Url = u })],
	};

	[Fact]
	public void ALocalTrailer_IsPreferred_OverARemoteOne_WhichStaysTheFallback()
	{
		var choice = Trailers.Choose(Item(1, "https://youtu.be/dQw4w9WgXcQ"));

		Assert.Equal(TrailerKind.Local, choice.Kind);
		Assert.Equal(Id, choice.YouTubeId);
		var fallback = Trailers.WithoutLocal(choice);
		Assert.Equal(TrailerKind.YouTube, fallback.Kind);
		Assert.Equal(Id, fallback.YouTubeId);
	}

	[Fact]
	public void ALocalTrailerAlone_HasNoFallback()
	{
		var choice = Trailers.Choose(Item(2));

		Assert.Equal(TrailerKind.Local, choice.Kind);
		Assert.Equal(TrailerKind.None, Trailers.WithoutLocal(choice).Kind);
		Assert.True(Trailers.HasTrailer(Item(2)));
	}

	[Fact]
	public void AYouTubeTrailer_PlaysInTheTheatre_AndAnyOtherLink_IsTheBrowserKind()
	{
		Assert.Equal(TrailerKind.YouTube, Trailers.Choose(Item(0, "https://www.youtube.com/watch?v=dQw4w9WgXcQ")).Kind);

		var other = Trailers.Choose(Item(0, "https://www.imdb.com/video/vi12345"));
		Assert.Equal(TrailerKind.Browser, other.Kind);
		Assert.Equal("https://www.imdb.com/video/vi12345", other.Url!.AbsoluteUri);
		Assert.Equal(TrailerKind.Browser, Trailers.WithoutLocal(Trailers.Choose(Item(1, "https://www.imdb.com/video/vi12345"))).Kind);
		// a look-alike is not YouTube: it goes to the browser, never into the theatre
		Assert.Equal(TrailerKind.Browser, Trailers.Choose(Item(0, "https://youtube.com.evil.example/watch?v=dQw4w9WgXcQ")).Kind);
	}

	[Fact]
	public void NoTrailer_HidesTheButton()
	{
		Assert.Equal(TrailerKind.None, Trailers.Choose(Item(0)).Kind);
		Assert.Equal(TrailerKind.None, Trailers.Choose(Item(0, "file:///c:/x.exe", "not a url")).Kind);
		Assert.Equal(TrailerKind.None, Trailers.Choose(null).Kind);
		Assert.False(Trailers.HasTrailer(Item(0)));
	}

	/// <summary>Nixie 1.4.0: at the top only Nixie's page; in a frame only the embed (and an empty frame); nothing else.</summary>
	[Theory]
	[InlineData("https://io.github.mbustos27.nixie/trailer", false, TrailerNavigation.Allow)]
	[InlineData("https://io.github.mbustos27.nixie/trailer", true, TrailerNavigation.Block)]
	[InlineData("https://io.github.mbustos27.nixie/other", false, TrailerNavigation.Block)]
	[InlineData("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ?autoplay=1&rel=0&playsinline=1", true, TrailerNavigation.Allow)]
	[InlineData("https://www.youtube-nocookie.com/embed/videoseries?list=x", true, TrailerNavigation.Allow)]
	[InlineData("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ", false, TrailerNavigation.Block)]
	[InlineData("about:blank", true, TrailerNavigation.Allow)]
	[InlineData("about:srcdoc", true, TrailerNavigation.Allow)]
	[InlineData("about:blank", false, TrailerNavigation.Block)]
	[InlineData("https://www.youtube-nocookie.com/", true, TrailerNavigation.Block)]
	[InlineData("http://www.youtube-nocookie.com/embed/dQw4w9WgXcQ", true, TrailerNavigation.Block)]
	[InlineData("https://www.youtube-nocookie.com.evil.example/embed/dQw4w9WgXcQ", true, TrailerNavigation.Block)]
	[InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", false, TrailerNavigation.Block)]
	[InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", true, TrailerNavigation.Block)]
	[InlineData("https://googleads.g.doubleclick.net/pagead/id", true, TrailerNavigation.Block)]
	[InlineData("https://accounts.google.com/ServiceLogin", false, TrailerNavigation.Block)]
	[InlineData("https://evil.example/", false, TrailerNavigation.Block)]
	[InlineData("file:///c:/windows/win.ini", true, TrailerNavigation.Block)]
	[InlineData("javascript:alert(1)", true, TrailerNavigation.Block)]
	public void TheAllowList_LoadsOnlyTheWrapper_AndTheEmbedInIt(string target, bool frame, TrailerNavigation expected) =>
		Assert.Equal(expected, Trailers.Navigation(new Uri(target), Trailers.DefaultEmbedBase, frame, userInitiated: false));

	/// <summary>Nixie 1.4.0: a YouTube link opens in the browser only when the user clicked it; anything else stays blocked.</summary>
	[Theory]
	[InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", TrailerNavigation.OpenInBrowser)]
	[InlineData("https://youtu.be/dQw4w9WgXcQ", TrailerNavigation.OpenInBrowser)]
	[InlineData("https://www.youtube.com/channel/UC123", TrailerNavigation.OpenInBrowser)]
	[InlineData("https://www.youtube-nocookie.com/", TrailerNavigation.OpenInBrowser)]
	[InlineData("http://www.youtube.com/watch?v=dQw4w9WgXcQ", TrailerNavigation.Block)]
	[InlineData("https://www.youtube.com:8443/watch?v=dQw4w9WgXcQ", TrailerNavigation.Block)]
	[InlineData("https://www.youtube.com.evil.example/watch?v=dQw4w9WgXcQ", TrailerNavigation.Block)]
	[InlineData("https://accounts.google.com/ServiceLogin", TrailerNavigation.Block)]
	[InlineData("https://evil.example/", TrailerNavigation.Block)]
	public void AYouTubeLink_OpensInTheBrowser_OnlyWhenTheUserClickedIt(string target, TrailerNavigation clicked)
	{
		foreach (var frame in new[] { false, true })
		{
			Assert.Equal(clicked, Trailers.Navigation(new Uri(target), Trailers.DefaultEmbedBase, frame, userInitiated: true));
			Assert.Equal(TrailerNavigation.Block, Trailers.Navigation(new Uri(target), Trailers.DefaultEmbedBase, frame, userInitiated: false));
		}
	}

	[Fact]
	public void TheAllowList_FollowsTheHarnessBase_AndNothingElse()
	{
		var fake = new Uri("http://127.0.0.1:5123/__harness/trailer/");

		Assert.Equal(TrailerNavigation.Allow, Trailers.Navigation(Trailers.EmbedUri(Id, fake), fake, frame: true, userInitiated: false));
		Assert.Equal(TrailerNavigation.Block, Trailers.Navigation(Trailers.EmbedUri(Id, fake), fake, frame: false, userInitiated: false));
		Assert.Equal(TrailerNavigation.Allow, Trailers.Navigation(Trailers.WrapperUri, fake, frame: false, userInitiated: false));
		Assert.Equal(TrailerNavigation.Block, Trailers.Navigation(new Uri("http://127.0.0.1:5123/Items"), fake, frame: true, userInitiated: false));
		Assert.Equal(TrailerNavigation.Block, Trailers.Navigation(new Uri("http://127.0.0.1:9999/__harness/trailer/x"), fake, frame: true, userInitiated: false));
		Assert.Equal(TrailerNavigation.Block, Trailers.Navigation(Trailers.EmbedUri(Id), fake, frame: true, userInitiated: false));
		Assert.Equal(TrailerNavigation.Block, Trailers.Navigation(null, fake, frame: true, userInitiated: true));
	}

	[Fact]
	public void NewWindows_OpenInTheBrowser_OnlyForWebAddresses_TheUserClicked()
	{
		Assert.True(Trailers.OpensInBrowser(new Uri("https://www.youtube.com/watch?v=dQw4w9WgXcQ")));
		Assert.True(Trailers.OpensInBrowser(new Uri("https://www.youtube.com/watch?v=dQw4w9WgXcQ"), userInitiated: true));
		Assert.False(Trailers.OpensInBrowser(new Uri("https://www.youtube.com/watch?v=dQw4w9WgXcQ"), userInitiated: false));
		Assert.False(Trailers.OpensInBrowser(new Uri("file:///c:/x.exe")));
		Assert.False(Trailers.OpensInBrowser(new Uri("ms-settings:privacy")));
		Assert.False(Trailers.OpensInBrowser(new Uri("https://user@evil.example/")));
		Assert.False(Trailers.OpensInBrowser(null));
	}

	[Theory]
	[InlineData("\"{\\\"ready\\\":true,\\\"state\\\":1,\\\"error\\\":0}\"", true, 1, 0)]
	[InlineData("\"{\\\"ready\\\":true,\\\"state\\\":1,\\\"error\\\":0,\\\"touched\\\":true}\"", true, 1, 0, true)]
	[InlineData("\"{\\\"ready\\\":true,\\\"state\\\":1,\\\"error\\\":0,\\\"touched\\\":1}\"", true, 1, 0)]
	[InlineData("\"{\\\"ready\\\":true,\\\"state\\\":-1,\\\"error\\\":150}\"", true, -1, 150)]
	[InlineData("\"{\\\"ready\\\":false,\\\"state\\\":-2,\\\"error\\\":0}\"", false, -2, 0)]
	[InlineData("\"{\\\"state\\\":\\\"1\\\",\\\"error\\\":\\\"x\\\"}\"", false, -2, 0)]
	[InlineData("\"{}\"", false, -2, 0)]
	public void ThePoll_ReadsOnlyItsFourFields(string result, bool ready, int state, int error, bool touched = false) =>
		Assert.Equal(new TrailerPoll(ready, state, error, touched), Trailers.ReadPoll(result));

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("null")]
	[InlineData("\"null\"")]
	[InlineData("{\"ready\":true}")]
	[InlineData("\"[1,2]\"")]
	[InlineData("\"not json\"")]
	[InlineData("42")]
	[InlineData("garbage")]
	public void ThePoll_IsNothing_WhenTheWrapperIsntThere(string? result) =>
		Assert.Null(Trailers.ReadPoll(result));

	[Fact]
	public void AnOverlongPoll_IsNotRead() =>
		Assert.Null(Trailers.ReadPoll("\"" + new string(' ', 5000) + "\""));

	/// <summary>
	/// Nixie 1.4.0 (review): "didn't start" only after 20 s of total silence (no answer, no state, no error) while the user did
	/// nothing with the player. A player that answered and sits unstarted (a pre-roll ad) is left to play, however long.
	/// </summary>
	[Fact]
	public void ThePlayer_Starts_OrIsRefused_OrSaysNothingAtAllFor20s()
	{
		var quiet = Trailers.QuietLimit;
		Assert.Equal(TimeSpan.FromSeconds(20), quiet);

		Assert.Equal(TrailerVerdict.Waiting, Trailers.Judge(null, TimeSpan.FromSeconds(1), startedBefore: false));
		foreach (var state in new[] { 0, 1, 2, 3 })
		{
			Assert.Equal(TrailerVerdict.Started, Trailers.Judge(new TrailerPoll(true, state, 0), TimeSpan.FromSeconds(30), false));
		}

		// total silence: waiting until the limit, then "didn't start"
		foreach (var poll in new TrailerPoll?[] { null, new(false, -2, 0) })
		{
			Assert.Equal(TrailerVerdict.Waiting, Trailers.Judge(poll, quiet - TimeSpan.FromMilliseconds(1), false));
			Assert.Equal(TrailerVerdict.NotStarted, Trailers.Judge(poll, quiet, false));
			// the user did something with the player (moved the keyboard into it): never "didn't start"
			Assert.Equal(TrailerVerdict.Waiting, Trailers.Judge(poll, TimeSpan.FromMinutes(5), false, userActed: true));
		}

		// a sign of the player (it answered, a state, the user's touch inside it): left to play, however long (a pre-roll ad)
		foreach (var poll in new TrailerPoll[] { new(true, -1, 0), new(true, 5, 0), new(true, -2, 0), new(false, -1, 0), new(false, 5, 0), new(false, -2, 0, Touched: true) })
		{
			Assert.Equal(TrailerVerdict.Waiting, Trailers.Judge(poll, TimeSpan.FromMinutes(5), false));
		}

		// an error is a refusal whenever it comes, also after it started; once started, the limit no longer counts
		Assert.Equal(TrailerVerdict.Refused, Trailers.Judge(new TrailerPoll(true, -1, 153), TimeSpan.Zero, false));
		Assert.Equal(TrailerVerdict.Refused, Trailers.Judge(new TrailerPoll(false, -2, 150), TimeSpan.Zero, false));
		Assert.Equal(TrailerVerdict.Refused, Trailers.Judge(new TrailerPoll(true, 1, 5), TimeSpan.FromMinutes(2), true));
		Assert.Equal(TrailerVerdict.Started, Trailers.Judge(new TrailerPoll(true, 5, 0), TimeSpan.FromMinutes(2), true));
		Assert.Equal(TrailerVerdict.Started, Trailers.Judge(null, TimeSpan.FromMinutes(2), true));
	}

	/// <summary>
	/// Nixie 1.4.0 (review): a held Enter that closed the theatre on its Close: its repeats (which land on Trailer) are
	/// swallowed until Enter comes up; a fresh press is the user's again.
	/// </summary>
	[Fact]
	public void AHeldEnterThatClosedTheTheatre_DoesNotReopenIt_UntilTheKeyComesUp()
	{
		const int Enter = 0x0D, Space = 0x20, Down = 0x28;
		var guard = new TheaterRepeatGuard();

		Assert.False(guard.Swallow(Enter, repeat: true)); // not armed: nothing swallowed
		guard.Arm();
		Assert.True(guard.Swallow(Enter, repeat: true));
		Assert.True(guard.Swallow(Space, repeat: true));
		Assert.False(guard.Swallow(Down, repeat: true)); // another held key goes on
		Assert.True(guard.Swallow(Enter, repeat: true)); // still armed
		guard.KeyUp(Enter);
		Assert.False(guard.Swallow(Enter, repeat: true));

		guard.Arm();
		Assert.False(guard.Swallow(Enter, repeat: false)); // a fresh press: the user's own
		Assert.False(guard.Swallow(Enter, repeat: true));

		guard.Arm();
		guard.KeyUp(Down);
		Assert.True(guard.Swallow(Space, repeat: true));
		guard.KeyUp(Space);
		Assert.False(guard.Swallow(Space, repeat: true));
	}

	/// <summary>Nixie 1.4.0: the error codes of the IFrame Player API, in Nixie's words, each with Watch on YouTube.</summary>
	[Theory]
	[InlineData(101, "Its owner doesn't allow it to play outside YouTube, or it was removed.")]
	[InlineData(150, "Its owner doesn't allow it to play outside YouTube, or it was removed.")]
	[InlineData(100, "It was removed from YouTube, or made private.")]
	[InlineData(153, "YouTube refused to play it here.")]
	[InlineData(2, "YouTube refused to play it here.")]
	[InlineData(5, "YouTube refused to play it here.")]
	[InlineData(999, "YouTube refused to play it here.")]
	public void EachErrorCode_HasItsReason(int code, string reason)
	{
		var message = Trailers.Refused(code, Id);

		Assert.Equal("This trailer can't play in Nixie", message.Heading);
		Assert.Equal(reason, message.Reason);
		Assert.Equal("Watch on YouTube", message.LinkText);
		Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", message.Link.AbsoluteUri);
		Assert.Equal($"This trailer can't play in Nixie. {reason}", message.AutomationText);
	}

	[Fact]
	public void TheOtherMessages_SayWhy_AndNameTheSite()
	{
		Assert.Equal("YouTube didn't start it.", Trailers.NotStarted(Id).Reason);
		Assert.Contains("WebView2", Trailers.NoPlayer(Id).Reason);
		Assert.Equal("Watch on YouTube", Trailers.NoPlayer(Id).LinkText);
		Assert.Equal("Watch on YouTube", Trailers.NotReady(Id).LinkText);

		var vimeo = Trailers.OnlyInBrowser(new Uri("https://www.vimeo.com/76979871"));
		Assert.Equal("This trailer can't play in Nixie", vimeo.Heading);
		Assert.Equal("It's on vimeo.com, which Nixie can't play.", vimeo.Reason);
		Assert.Equal("Open vimeo.com in browser", vimeo.LinkText);
		Assert.Equal("https://www.vimeo.com/76979871", vimeo.Link.AbsoluteUri);
		Assert.Equal("imdb.com", Trailers.SiteName(new Uri("https://WWW.IMDB.com/video/vi1")));
		Assert.Equal("another site", Trailers.SiteName(new Uri("https://" + new string('a', 70) + ".example/")));
	}

	/// <summary>
	/// Nixie 1.4.0 (the user: "when it doesnt find a trailer sometimes it just opens a browser"): no step of the Trailer button
	/// opens the browser. YouTube plays in the theatre; another site gets the theatre's message (Open in browser is the user's
	/// choice); with no theatre, a dialog with the same choice.
	/// </summary>
	[Fact]
	public void TheTrailerButton_NeverOpensTheBrowser_ItsStepsAreTheTheatre_AMessage_OrADialog()
	{
		Assert.Equal(new TrailerStep(TrailerStepKind.Theater, Id), Trailers.Plan(Trailers.Choose(Item(0, "https://youtu.be/dQw4w9WgXcQ")), theaterUp: true));
		var noTheater = Trailers.Plan(Trailers.Choose(Item(0, "https://youtu.be/dQw4w9WgXcQ")), theaterUp: false);
		Assert.Equal(TrailerStepKind.Dialog, noTheater.Kind);
		Assert.Equal("Watch on YouTube", noTheater.Message!.LinkText);

		var other = Trailers.Plan(Trailers.Choose(Item(0, "https://vimeo.com/76979871")), theaterUp: true);
		Assert.Equal(TrailerStepKind.Message, other.Kind);
		Assert.Equal("Open vimeo.com in browser", other.Message!.LinkText);
		Assert.Equal(TrailerStepKind.Dialog, Trailers.Plan(Trailers.Choose(Item(0, "https://vimeo.com/76979871")), theaterUp: false).Kind);
		// a local trailer the server can't give, with another site's link: the message, never the browser
		Assert.Equal(TrailerStepKind.Message, Trailers.Plan(Trailers.WithoutLocal(Trailers.Choose(Item(1, "https://www.imdb.com/video/vi1"))), true).Kind);
		Assert.Equal(TrailerStepKind.Local, Trailers.Plan(Trailers.Choose(Item(1)), true).Kind);
		Assert.Equal(TrailerStepKind.Nothing, Trailers.Plan(Trailers.WithoutLocal(Trailers.Choose(Item(1))), true).Kind);
		Assert.Equal(TrailerStepKind.Nothing, Trailers.Plan(Trailers.Choose(null), true).Kind);
		Assert.Equal(TrailerStepKind.Nothing, Trailers.Plan(new TrailerChoice(TrailerKind.YouTube), true).Kind);
		Assert.Equal(TrailerStepKind.Nothing, Trailers.Plan(new TrailerChoice(TrailerKind.Browser), true).Kind);
		// and there is no step that opens the browser
		Assert.Equal(["Nothing", "Local", "Theater", "Message", "Dialog"], Enum.GetNames<TrailerStepKind>());
	}

	[Fact]
	public void TheEmbedBaseOverride_IsTheHarnesssOnly_OnThisMachine_NeverInARelease()
	{
		const string fake = "http://127.0.0.1:5123/__harness/trailer";

		Assert.Equal("http://127.0.0.1:5123/__harness/trailer/", Trailers.EmbedBase(fake, harnessOn: true, releasePackage: false).AbsoluteUri);
		Assert.Equal(Trailers.DefaultEmbedBase, Trailers.EmbedBase(fake, harnessOn: true, releasePackage: true));
		Assert.Equal(Trailers.DefaultEmbedBase, Trailers.EmbedBase(fake, harnessOn: false, releasePackage: false));
		Assert.Equal(Trailers.DefaultEmbedBase, Trailers.EmbedBase(null, harnessOn: true, releasePackage: false));
		Assert.Equal(Trailers.DefaultEmbedBase, Trailers.EmbedBase("https://evil.example/embed/", harnessOn: true, releasePackage: false));
		Assert.Equal(Trailers.DefaultEmbedBase, Trailers.EmbedBase("http://192.168.1.5/embed/", harnessOn: true, releasePackage: false));
		Assert.Equal(Trailers.DefaultEmbedBase, Trailers.EmbedBase("http://127.0.0.1/embed/?x=1", harnessOn: true, releasePackage: false));
	}

	[Fact]
	public void TheVideo_Is16By9_About60PercentOfTheWindow_AndFitsItsHeight()
	{
		var (w, h) = Trailers.VideoSize(1920, 1080, 200);
		Assert.Equal(1152, w);
		Assert.Equal(648, h);

		var (nw, nh) = Trailers.VideoSize(1100, 760, 200);
		Assert.True(nw < w && nw <= 660, $"{nw}");
		Assert.True(nh + 200 + 48 <= 760, $"{nh}");
		Assert.Equal(Math.Round(nw * 9 / 16), nh);
	}

	[Fact]
	public void Closing_GivesTheKeyboardBackToTheTrailerButton_Once()
	{
		var focused = new List<object>();
		var state = new TrailerWindowState(focused.Add);
		var button = new object();

		Assert.True(state.TryOpen("Heat", button, playerOpen: false));
		Assert.Equal("Trailer: Heat", state.AutomationName);
		Assert.True(state.Close());
		Assert.False(state.Close());

		Assert.Equal([button], focused);
		Assert.False(state.IsOpen);
	}

	[Fact]
	public void ASecondTrailer_KeepsTheFirstOnesWayBack()
	{
		var focused = new List<object>();
		var state = new TrailerWindowState(focused.Add);
		var first = new object();

		state.TryOpen("Heat", first, playerOpen: false);
		state.TryOpen("Ronin", new object(), playerOpen: false);
		state.Close();

		Assert.Equal([first], focused);
	}

	/// <summary>
	/// Nixie 1.4.0 (bug hunt M1): the theatre is modal: Esc and Back close it, its controls get Tab, Enter, Space and the arrows,
	/// and every other key is held back: each global shortcut but Back, and the pages' own keys (Home's N and C, Ctrl+Z).
	/// </summary>
	[Fact]
	public void OverTheTheatre_EscAndBackClose_ItsControlsKeepTheirKeys_EverythingElseIsHeldBack()
	{
		Assert.Equal(TheaterKey.Close, Trailers.KeyOverTheater(0x1B, false, false, false));
		Assert.Equal(TheaterKey.Close, Trailers.KeyOverTheater(0x1B, true, true, false));
		Assert.Equal(TheaterKey.Close, Trailers.KeyOverTheater(0x25, false, false, true));    // Alt+Left
		Assert.Equal(TheaterKey.Close, Trailers.KeyOverTheater(0xA6, false, false, false));   // the Back key

		foreach (var (key, shift) in new[] { (0x09, false), (0x09, true), (0x0D, false), (0x20, false), (0x25, false), (0x26, false), (0x27, false), (0x28, false), (0x10, true), (0x11, false), (0x12, false) })
		{
			Assert.Equal(TheaterKey.Pass, Trailers.KeyOverTheater(key, key == 0x11, shift, key == 0x12));
		}

		// every global shortcut the sheet lists, Back's aside, is held back
		foreach (var entry in ShortcutCatalog.Sections.SelectMany(s => s.Entries).Where(e => e.Scope == ShortcutScope.Global))
		{
			foreach (var chord in entry.Chords)
			{
				var back = AppShortcuts.For(chord.Key, chord.Control, chord.Shift, chord.Alt, inTextInput: false) == AppShortcut.Back;
				Assert.Equal(back ? TheaterKey.Close : TheaterKey.HoldBack, Trailers.KeyOverTheater(chord.Key, chord.Control, chord.Shift, chord.Alt));
			}
		}

		// the pages' own keys: N, C, Ctrl+Z; a letter typed; Alt+Right; Ctrl+Tab
		Assert.Equal(TheaterKey.HoldBack, Trailers.KeyOverTheater(0x4E, false, false, false));
		Assert.Equal(TheaterKey.HoldBack, Trailers.KeyOverTheater(0x43, false, false, false));
		Assert.Equal(TheaterKey.HoldBack, Trailers.KeyOverTheater(0x5A, true, false, false));
		Assert.Equal(TheaterKey.HoldBack, Trailers.KeyOverTheater(0x42, false, false, false));
		Assert.Equal(TheaterKey.HoldBack, Trailers.KeyOverTheater(0x27, false, false, true));
		Assert.Equal(TheaterKey.HoldBack, Trailers.KeyOverTheater(0x09, true, false, false));
	}

	/// <summary>
	/// Nixie 1.4.0: a held Enter or Space over the theatre is held back after its first press: the press on Trailer opened it,
	/// and its repeats pressed the theatre's Close (the message's, which has the keyboard at once), closing and reopening it.
	/// The arrows and Tab still repeat.
	/// </summary>
	[Fact]
	public void OverTheTheatre_AHeldEnterOrSpace_DoesNotRepeat()
	{
		Assert.Equal(TheaterKey.Pass, Trailers.KeyOverTheater(0x0D, false, false, false, repeat: false));
		Assert.Equal(TheaterKey.Pass, Trailers.KeyOverTheater(0x20, false, false, false, repeat: false));
		Assert.Equal(TheaterKey.HoldBack, Trailers.KeyOverTheater(0x0D, false, false, false, repeat: true));
		Assert.Equal(TheaterKey.HoldBack, Trailers.KeyOverTheater(0x20, false, false, false, repeat: true));
		foreach (var key in new[] { 0x09, 0x25, 0x26, 0x27, 0x28 })
		{
			Assert.Equal(TheaterKey.Pass, Trailers.KeyOverTheater(key, false, false, false, repeat: true));
		}

		Assert.Equal(TheaterKey.Close, Trailers.KeyOverTheater(0x1B, false, false, false, repeat: true));
	}

	[Fact]
	public void NoTrailer_WhileThePlayerIsOpen()
	{
		var state = new TrailerWindowState(_ => { });

		Assert.False(state.TryOpen("Heat", new object(), playerOpen: true));
		Assert.False(state.IsOpen);
	}

	[Fact]
	public async Task ALocalTrailer_PlaysAloneInTheMpvPlayer()
	{
		const BaseItemDto_Type type = BaseItemDto_Type.Trailer;
		var id = Guid.NewGuid();
		var client = new Mock<IJellyfinClient>(MockBehavior.Strict);
		client.Setup(x => x.GetItem(id)).ReturnsAsync(new BaseItemDto { Id = id, Type = type });

		var request = await MpvQueue.BuildAsync(client.Object, new BaseItemDto { Id = id, Type = type }, null, fromBeginning: true);

		Assert.Equal([id.ToString("N")], request!.ItemIds);
		Assert.Equal(0, request.StartIndex);
		Assert.Equal(0L, request.StartPositionTicks);
	}
}
