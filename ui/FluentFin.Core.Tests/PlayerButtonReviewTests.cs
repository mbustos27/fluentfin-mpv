using System.Text.Json.Nodes;
using System.Xml.Linq;
using FluentFin.Core.Mpv;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.2.0: the button review (docs/reports/Nixie button review.md, sections 1.2 and 4, player) in the player's controls: the
/// labels, names, keys and sizes it settled, read from MpvPlayerControls.xaml and the controls' view model.
/// </summary>
public sealed class PlayerButtonReviewTests
{
	private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
	private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
	private static readonly XName Name = XName.Get("AutomationProperties.Name");
	private static readonly XName AcceleratorKey = XName.Get("AutomationProperties.AcceleratorKey");
	private static readonly XName ToolTip = XName.Get("ToolTipService.ToolTip");

	private static string FindUp(params string[] parts)
	{
		for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
		{
			var candidate = Path.Combine([dir.FullName, .. parts]);
			if (File.Exists(candidate))
			{
				return candidate;
			}
		}

		throw new FileNotFoundException(Path.Combine(parts));
	}

	private static XDocument Controls() => XDocument.Load(FindUp("ui", "FluentFin", "Controls", "MpvPlayerControls.xaml"));

	private static XElement? Named(XDocument doc, string name) =>
		doc.Descendants().SingleOrDefault(e => (string?)e.Attribute(Xaml + "Name") == name);

	private static XElement Button(XDocument doc, string name) => Named(doc, name) ?? throw new InvalidOperationException(name);

	private static IEnumerable<string> Glyphs(XElement element) =>
		element.Descendants(Presentation + "FontIcon").Select(i => (string?)i.Attribute("Glyph") ?? "");

	[Fact]
	public void The_tracks_button_announces_no_key_that_does_not_open_it_and_its_panel_stays_in_the_window()
	{
		var tracks = Button(Controls(), "TracksButton");
		// B, C (and V) change the tracks without the panel: they are its help text, not its accelerator
		Assert.Null(tracks.Attribute(AcceleratorKey));
		Assert.Contains("B changes the audio track", (string?)tracks.Attribute(XName.Get("AutomationProperties.HelpText")));
		Assert.DoesNotContain("(B, C)", (string?)tracks.Attribute(ToolTip) ?? "");
		Assert.Equal([""], Glyphs(tracks).Take(1));
		// centred over the button ("Top") it ran off the window's right edge at 1100 px; it opens from the button's right edge
		var flyout = tracks.Descendants(Presentation + "Flyout").Single();
		Assert.Equal("TopEdgeAlignedRight", (string?)flyout.Attribute("Placement"));
		Assert.Equal("{x:Bind VisibleIf(Controls.HasAudioChoice), Mode=OneWay}", (string?)Button(Controls(), "AudioColumn").Attribute("Visibility"));
	}

	[Fact]
	public void The_countdowns_other_button_says_what_it_does()
	{
		var doc = Controls();
		var keep = Button(doc, "CancelNextButton");
		Assert.Equal("{x:Bind Controls.CancelNextName, Mode=OneWay}", (string?)keep.Attribute(Name));
		Assert.Equal("{x:Bind Controls.CancelNextText, Mode=OneWay}", (string?)Button(doc, "CancelNextText").Attribute("Text"));
		Assert.DoesNotContain(keep.Descendants(), e => (string?)e.Attribute("Text") == "Cancel");
	}

	[Fact]
	public void The_transport_names_its_keys_and_moves_ten_seconds_both_ways()
	{
		var doc = Controls();
		var forward = Button(doc, "SkipForwardButton");
		Assert.Equal("Forward 10 seconds", (string?)forward.Attribute(Name));
		Assert.Equal("L", (string?)forward.Attribute(AcceleratorKey));
		Assert.Equal(10, MpvControlsViewModel.ForwardButtonSeconds);
		Assert.Equal(MpvControlsViewModel.SkipBackSeconds, MpvControlsViewModel.ForwardButtonSeconds);
		Assert.Equal(MpvControlsViewModel.LargeSeekSeconds, MpvControlsViewModel.ForwardButtonSeconds);

		// the volume says its level, and announces no key that opens it
		var volume = Button(doc, "VolumeButton");
		Assert.Null(volume.Attribute(AcceleratorKey));
		Assert.Equal("{x:Bind VolumeName(Controls.Volume, Controls.IsMuted), Mode=OneWay}", (string?)volume.Attribute(Name));

		// no shortcut sheet button over the video: ? and F1 open it from anywhere
		Assert.Null(Named(doc, "ShortcutsButton"));

		// the icons of the 44 px transport buttons are 20
		foreach (var name in new[] { "PreviousButton", "SkipBackButton", "SkipForwardButton", "NextButton", "TracksButton" })
		{
			var sizes = Button(doc, name).Descendants(Presentation + "FontIcon").Select(i => (string?)i.Attribute("FontSize"));
			Assert.All(sizes, size => Assert.Equal("20", size));
		}
	}

	[Fact]
	public void The_subtitle_buttons_are_words()
	{
		var doc = Controls();
		Assert.Equal("Reset size", (string?)Button(doc, "SubtitleSizeReset").Attribute("Content"));
		Assert.Equal("Earlier", (string?)Button(doc, "SubtitleEarlierButton").Attribute("Content"));
		Assert.Equal("Later", (string?)Button(doc, "SubtitleLaterButton").Attribute("Content"));
		var reset = Button(doc, "SubtitleDelayReset");
		Assert.Equal("Reset delay", (string?)reset.Attribute("Content"));
		Assert.Equal("{x:Bind NotZero(Controls.SubtitleDelay), Mode=OneWay}", (string?)reset.Attribute("IsEnabled"));
	}

	private const string Item = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee1";
	private static long Ticks(double seconds) => TimeSpan.FromSeconds(seconds).Ticks;

	private static MpvControlsViewModel Episode(double position, bool credits)
	{
		var vm = new MpvControlsViewModel((_, _) => Task.CompletedTask, () => new DateTimeOffset(2026, 11, 17, 21, 0, 0, TimeSpan.Zero));
		vm.OnEvent(new BridgeEvent(Bridge.Events.Item, JsonNode.Parse($$"""{"itemId": "{{Item}}", "name": "Pilot", "type": "Episode", "runTimeTicks": {{Ticks(1320)}}}""")));
		vm.OnState(new BridgeState { State = Bridge.States.Playing, ItemId = Item, DurationTicks = Ticks(1320) });
		vm.OnEvent(new BridgeEvent(Bridge.Events.Next, JsonNode.Parse($$"""{"itemId": "{{Item}}", "nextItemId": "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee2", "autoPlay": true}""")));
		if (credits)
		{
			vm.OnEvent(new BridgeEvent(Bridge.Events.Segments, JsonNode.Parse($$"""
				{"itemId": "{{Item}}", "segments": [{"type": "Outro", "startTicks": {{Ticks(1230)}}, "endTicks": {{Ticks(1320)}}, "action": "ask"}], "current": null}
				""")));
		}

		vm.OnPosition(new BridgePosition(Ticks(position), Ticks(1320)));
		return vm;
	}

	[Fact]
	public void Keep_watching_or_watch_credits()
	{
		var inCredits = Episode(1232, credits: true);
		Assert.True(inCredits.IsCountingDown);
		Assert.Equal("Watch credits", inCredits.CancelNextText);
		Assert.Equal("Watch credits: the next episode won't start by itself", inCredits.CancelNextName);

		var atTheEnd = Episode(1312, credits: false);
		Assert.True(atTheEnd.IsCountingDown);
		Assert.Equal("Keep watching", atTheEnd.CancelNextText);
	}

	[Fact]
	public void The_prompts_name_starts_with_what_it_says()
	{
		var vm = Episode(1232, credits: true);
		Assert.StartsWith(vm.NextPromptText, vm.NextPromptName);
		Assert.Equal("Next episode in 8", vm.NextPromptName);
	}

	[Fact]
	public void Previous_and_next_say_episode_only_for_episodes()
	{
		var vm = Episode(10, credits: false);
		Assert.Equal("Previous episode", vm.PreviousName);
		Assert.Equal("Next episode", vm.NextButtonName);

		vm.OnEvent(new BridgeEvent(Bridge.Events.Item, JsonNode.Parse("""{"itemId": "s1", "name": "A song", "type": "Audio"}""")));
		Assert.Equal("Previous", vm.PreviousName);
		Assert.Equal("Next", vm.NextButtonName);
	}
}
