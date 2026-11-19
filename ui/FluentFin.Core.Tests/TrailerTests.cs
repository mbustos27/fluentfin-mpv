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
	public void TheEmbed_IsThePrivacyEnhancedOne_WithAutoplayAndNoOtherChannels()
	{
		Assert.Equal("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ?autoplay=1&rel=0&playsinline=1", Trailers.EmbedUri(Id).AbsoluteUri);
		Assert.Throws<ArgumentException>(() => Trailers.EmbedUri("../../x"));
	}

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
	public void AYouTubeTrailer_PlaysInTheTheatre_AndAnyOtherLink_InTheBrowser()
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

	[Theory]
	[InlineData("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ?autoplay=1&rel=0&playsinline=1", TrailerNavigation.Allow)]
	[InlineData("https://www.youtube-nocookie.com/embed/videoseries?list=x", TrailerNavigation.Allow)]
	[InlineData("https://www.youtube-nocookie.com/", TrailerNavigation.OpenInBrowser)]
	[InlineData("https://www.youtube-nocookie.com/embedded/x", TrailerNavigation.OpenInBrowser)]
	[InlineData("http://www.youtube-nocookie.com/embed/dQw4w9WgXcQ", TrailerNavigation.Block)]
	[InlineData("https://www.youtube-nocookie.com.evil.example/embed/dQw4w9WgXcQ", TrailerNavigation.Block)]
	[InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", TrailerNavigation.OpenInBrowser)]
	[InlineData("https://youtu.be/dQw4w9WgXcQ", TrailerNavigation.OpenInBrowser)]
	[InlineData("https://www.youtube.com/channel/UC123", TrailerNavigation.OpenInBrowser)]
	[InlineData("https://accounts.google.com/ServiceLogin", TrailerNavigation.Block)]
	[InlineData("https://evil.example/", TrailerNavigation.Block)]
	[InlineData("https://www.youtube.com.evil.example/watch?v=dQw4w9WgXcQ", TrailerNavigation.Block)]
	[InlineData("file:///c:/windows/win.ini", TrailerNavigation.Block)]
	[InlineData("about:blank", TrailerNavigation.Block)]
	public void TheAllowList_LoadsOnlyTheEmbed(string target, TrailerNavigation expected) =>
		Assert.Equal(expected, Trailers.Navigation(new Uri(target), Trailers.DefaultEmbedBase));

	[Fact]
	public void TheAllowList_FollowsTheHarnessBase_AndNothingElse()
	{
		var fake = new Uri("http://127.0.0.1:5123/__harness/trailer/");

		Assert.Equal(TrailerNavigation.Allow, Trailers.Navigation(Trailers.EmbedUri(Id, fake), fake));
		Assert.Equal(TrailerNavigation.Block, Trailers.Navigation(new Uri("http://127.0.0.1:5123/Items"), fake));
		Assert.Equal(TrailerNavigation.Block, Trailers.Navigation(new Uri("http://127.0.0.1:9999/__harness/trailer/x"), fake));
		Assert.NotEqual(TrailerNavigation.Allow, Trailers.Navigation(Trailers.EmbedUri(Id), fake));
		Assert.Equal(TrailerNavigation.Block, Trailers.Navigation(null, fake));
	}

	[Fact]
	public void NewWindows_OpenInTheBrowser_OnlyForWebAddresses()
	{
		Assert.True(Trailers.OpensInBrowser(new Uri("https://www.youtube.com/watch?v=dQw4w9WgXcQ")));
		Assert.False(Trailers.OpensInBrowser(new Uri("file:///c:/x.exe")));
		Assert.False(Trailers.OpensInBrowser(new Uri("ms-settings:privacy")));
		Assert.False(Trailers.OpensInBrowser(null));
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
