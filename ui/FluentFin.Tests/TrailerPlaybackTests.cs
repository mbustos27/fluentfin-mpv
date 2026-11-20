using System.Xml.Linq;
using FluentFin.Core;
using FluentFin.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.4.0 (the user: "when it doesnt find a trailer sometimes it just opens a browser"): the Trailer button never opens
/// the browser. Every way it can go (YouTube, another site, a local trailer the server can't give, no theatre) ends in the
/// player, the theatre, Nixie's message or a dialog; the browser opens only through <see cref="TrailerBrowser"/>, from the
/// user's click, and never under the harness.
/// </summary>
public sealed class TrailerPlaybackTests
{
	private const string YouTube = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
	private const string Vimeo = "https://vimeo.com/76979871";

	private static string App(params string[] parts) => Path.Combine([FlyleafEngineTests.RepoRoot, "FluentFin", .. parts]);

	private static BaseItemDto Item(int local, params string[] remote) => new()
	{
		Id = Guid.NewGuid(),
		Name = "Heat",
		Type = BaseItemDto_Type.Movie,
		LocalTrailerCount = local,
		RemoteTrailers = [.. remote.Select(u => new MediaUrl { Url = u })],
	};

	/// <summary>A stage that records what the Trailer button did.</summary>
	private sealed class Stage(bool theater, BaseItemDto? local = null, bool localFails = false) : TrailerPlayback.ITrailerStage
	{
		public List<string> Done { get; } = [];

		public bool HasTheater => theater;

		public Task<BaseItemDto?> LocalTrailer(Guid itemId)
		{
			Done.Add("ask local");
			return localFails ? Task.FromException<BaseItemDto?>(new HttpRequestException("down")) : Task.FromResult(local);
		}

		public Task PlayLocal(BaseItemDto trailer)
		{
			Done.Add("play local");
			return Task.CompletedTask;
		}

		public void OpenTheater(string videoId, string title) => Done.Add($"theatre {videoId} {title}");

		public void ShowMessage(TrailerMessage message, string title) => Done.Add($"message {message.LinkText}");

		public Task ShowDialog(TrailerMessage message)
		{
			Done.Add($"dialog {message.LinkText}");
			return Task.CompletedTask;
		}
	}

	public static TheoryData<string, BaseItemDto?, bool, BaseItemDto?, bool, string[]> Cases()
	{
		var listed = new BaseItemDto { Id = Guid.NewGuid(), Name = "Heat trailer" };
		return new()
		{
			{ "YouTube", Item(0, YouTube), true, null, false, ["theatre dQw4w9WgXcQ Heat"] },
			{ "YouTube, no theatre", Item(0, YouTube), false, null, false, ["dialog Watch on YouTube"] },
			{ "another site", Item(0, Vimeo), true, null, false, ["message Open vimeo.com in browser"] },
			{ "another site, no theatre", Item(0, Vimeo), false, null, false, ["dialog Open vimeo.com in browser"] },
			{ "local", Item(1, Vimeo), true, listed, false, ["ask local", "play local"] },
			{ "local not listed, YouTube", Item(1, YouTube), true, null, false, ["ask local", "theatre dQw4w9WgXcQ Heat"] },
			{ "local not listed, another site", Item(1, Vimeo), true, null, false, ["ask local", "message Open vimeo.com in browser"] },
			{ "local request failed, another site", Item(1, Vimeo), true, null, true, ["ask local", "message Open vimeo.com in browser"] },
			{ "local not listed, nothing else", Item(1), true, null, false, ["ask local"] },
			{ "none", Item(0), true, null, false, [] },
			{ "no item", null, true, null, false, [] },
		};
	}

	[Theory]
	[MemberData(nameof(Cases))]
	public async Task TheTrailerButton_NeverOpensTheBrowser(string name, BaseItemDto? dto, bool theater, BaseItemDto? local, bool localFails, string[] expected)
	{
		var launched = new List<Uri>();
		var launch = TrailerBrowser.Launch;
		TrailerBrowser.Launch = uri => { launched.Add(uri); return Task.FromResult(true); };
		try
		{
			var stage = new Stage(theater, local, localFails);

			await TrailerPlayback.PlayAsync(dto, stage, null);

			Assert.Equal(expected, stage.Done);
			Assert.True(launched.Count == 0, $"{name}: the browser was opened ({string.Join(", ", launched)})");
		}
		finally
		{
			TrailerBrowser.Launch = launch;
		}
	}

	[Fact]
	public async Task TheBrowser_OpensForTheUsersChoice_AWebAddressOnly_AndNeverUnderTheHarness()
	{
		var launched = new List<Uri>();
		var (launch, harness) = (TrailerBrowser.Launch, TrailerBrowser.HarnessOn);
		TrailerBrowser.Launch = uri => { launched.Add(uri); return Task.FromResult(true); };
		try
		{
			TrailerBrowser.HarnessOn = () => false;
			Assert.True(await TrailerBrowser.Open(new Uri(YouTube), "test", null));
			Assert.False(await TrailerBrowser.Open(new Uri("file:///c:/windows/notepad.exe"), "test", null));
			Assert.False(await TrailerBrowser.Open(new Uri("ms-settings:privacy"), "test", null));
			Assert.False(await TrailerBrowser.Open(null, "test", null));
			Assert.Equal([new Uri(YouTube)], launched);

			TrailerBrowser.HarnessOn = () => true;
			Assert.False(await TrailerBrowser.Open(new Uri(YouTube), "test", null));
			Assert.Single(launched);
		}
		finally
		{
			(TrailerBrowser.Launch, TrailerBrowser.HarnessOn) = (launch, harness);
		}
	}

	/// <summary>The one launcher: no other trailer code calls the system's launcher itself.</summary>
	[Fact]
	public void OnlyTrailerBrowser_LaunchesAnything()
	{
		foreach (var file in new[] { App("Controls", "TrailerTheater.cs"), App("Services", "TrailerPlayback.cs"), App("Controls", "TrailerMessageView.xaml.cs") })
		{
			var source = File.ReadAllText(file);
			Assert.DoesNotContain("LaunchUriAsync", source);
			Assert.DoesNotContain("Process.Start", source);
		}

		Assert.Contains("LaunchUriAsync", File.ReadAllText(App("Services", "TrailerBrowser.cs")));
	}

	/// <summary>The theatre's web view stays locked (1.3.1's settings) and gets no bridge to the app; it is muted under the harness.</summary>
	[Fact]
	public void TheWebView_StaysLocked_WithNoBridgeIntoTheApp()
	{
		var source = File.ReadAllText(App("Controls", "TrailerTheater.cs"));

		foreach (var setting in new[] { "AreHostObjectsAllowed = false", "IsWebMessageEnabled = false", "AreDefaultContextMenusEnabled = false",
			"AreDevToolsEnabled = !AppVersion.IsReleasePackage" })
		{
			Assert.Contains(setting, source);
		}

		// the permission and download handlers themselves (a bare "e.Cancel = true" also matched the navigation handlers)
		Assert.Single(System.Text.RegularExpressions.Regex.Matches(source, @"core\.PermissionRequested \+= \(_, e\) => e\.State = CoreWebView2PermissionState\.Deny;"));
		Assert.Single(System.Text.RegularExpressions.Regex.Matches(source, @"core\.DownloadStarting \+= \(_, e\) => e\.Cancel = true;"));
		Assert.Single(System.Text.RegularExpressions.Regex.Matches(source, @"DownloadStarting"));

		// Nixie 1.4.0 (review): InPrivate, so YouTube's stored data never outlives the web view (a lasting per-install id)
		Assert.Contains("controller.IsInPrivateModeEnabled = true;", source);
		Assert.Contains("await web.EnsureCoreWebView2Async(environment, controller);", source);

		Assert.DoesNotContain("AddHostObjectToScript", source);
		Assert.DoesNotContain("WebMessageReceived", source);
		Assert.DoesNotContain("PostWebMessage", source);
		Assert.DoesNotContain("Referer", source.Replace("a Referer it accepts", ""));
		Assert.Contains("core.IsMuted = true", source);
		// the user's click on Trailer starts it: no second click inside YouTube's player (it sat unstarted, state -1, live)
		Assert.Contains("AdditionalBrowserArguments = Trailers.BrowserArguments", source);
		Assert.Equal("--autoplay-policy=no-user-gesture-required", FluentFin.Core.Trailers.BrowserArguments);
		Assert.Contains("AddWebResourceRequestedFilter(Trailers.WrapperFilter", source);
		Assert.Contains("FrameNavigationStarting", source);
		Assert.Contains("e.IsUserInitiated", source);
	}

	/// <summary>
	/// The message: the button system's Secondary for both buttons (no Primary, no pill), Close named as it reads, the dark
	/// colours on the screen's black, and Narrator told as it appears.
	/// </summary>
	[Fact]
	public void TheMessage_UsesTheButtonSystem_AndNarratorHearsIt()
	{
		var xaml = XDocument.Load(App("Controls", "TrailerMessageView.xaml"));
		XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
		var buttons = xaml.Descendants().Where(e => e.Name.LocalName == "Button").ToList();

		Assert.Equal(["LinkButton", "CloseButton"], buttons.Select(b => (string?)b.Attribute(x + "Name")));
		Assert.All(buttons, b => Assert.Equal("{StaticResource NixieButtonStyle}", (string?)b.Attribute("Style")));
		Assert.All(buttons, b => Assert.Null(b.Attribute("CornerRadius")));
		Assert.Equal("Close", (string?)buttons[1].Attribute("Content"));
		Assert.Equal("Close", (string?)buttons[1].Attribute("AutomationProperties.Name"));
		Assert.Equal("Dark", (string?)xaml.Root!.Attribute("RequestedTheme"));
		Assert.Contains(xaml.Descendants(), e => (string?)e.Attribute("AutomationProperties.LiveSetting") == "Assertive");
		Assert.DoesNotContain("NixiePrimary", xaml.ToString());

		var code = File.ReadAllText(App("Controls", "TrailerMessageView.xaml.cs"));
		Assert.Contains("RaiseNotificationEvent", code);
		Assert.Contains("AutomationProperties.SetName(this, message.AutomationText)", code);
		Assert.Contains("CloseButton.Focus(how)", code);
	}
}
