using System.Text.Json;
using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Settings;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.6 player: the Quality menu (docs/BRIDGE.md §4 <c>set_quality</c>, §5 <c>stream</c>): the choices by the file's size,
/// the header that says what plays now, the commands sent, and Settings' defaults at home and away (Auto, and never moved
/// by Nixie itself).
/// </summary>
public sealed class PlayerQualityTests
{
	private const string Item = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee1";
	private const string Other = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee2";

	private DateTimeOffset _now = new(2026, 11, 21, 20, 0, 0, TimeSpan.Zero);
	private readonly List<(string Command, JsonObject? Args)> _sent = [];
	private readonly MpvControlsViewModel _vm;

	public PlayerQualityTests()
	{
		_vm = new MpvControlsViewModel((command, args) =>
		{
			_sent.Add((command, args?.DeepClone() as JsonObject));
			return Task.CompletedTask;
		}, () => _now);
		_vm.OnEvent(new BridgeEvent(Bridge.Events.Item, JsonNode.Parse($$"""{"itemId": "{{Item}}", "name": "Film", "type": "Movie"}""")));
	}

	/// <summary>The backend's <c>stream</c> event, as test_bridge_quality.py's golden shape.</summary>
	private static BridgeEvent StreamEvent(string json) => new(Bridge.Events.Stream, JsonNode.Parse(json));

	private const string DirectPlay1080 = """
		{"itemId": "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee1", "playSessionId": "ps-1", "playMethod": "DirectPlay", "transcodeReasons": [],
		 "bitrate": 12000000, "sourceBitrate": 12000000, "sourceWidth": 1920, "sourceHeight": 1080,
		 "quality": {"bitrate": null, "original": false}, "isLocal": true}
		""";

	private const string Transcode8 = """
		{"itemId": "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee1", "playSessionId": "ps-2", "playMethod": "Transcode",
		 "transcodeReasons": ["ContainerBitrateExceedsLimit"], "bitrate": 8000000, "sourceBitrate": 20000000,
		 "sourceWidth": 1920, "sourceHeight": 1080, "quality": {"bitrate": 8000000, "original": false}, "isLocal": false}
		""";

	// -- the choices -------------------------------------------------------------------------------------------------------

	private static long[] Caps(int? width, int? height) => [.. QualityChoices.For(width, height).Where(c => c.IsCap).Select(c => c.Bitrate!.Value)];

	[Fact]
	public void Auto_and_Original_come_first_whatever_the_file()
	{
		foreach (var (w, h) in new (int?, int?)[] { (3840, 2160), (1920, 1080), (1280, 720), (720, 480), (null, null), (0, 0) })
		{
			var choices = QualityChoices.For(w, h);
			Assert.Equal(QualityChoice.Auto, choices[0]);
			Assert.Equal(QualityChoice.Original, choices[1]);
		}
	}

	[Fact]
	public void Only_the_caps_at_or_below_the_files_own_size()
	{
		long[] all = [120_000_000, 80_000_000, 60_000_000, 40_000_000, 20_000_000, 10_000_000, 8_000_000, 6_000_000, 4_000_000, 3_000_000, 1_500_000, 720_000];
		Assert.Equal(all, Caps(3840, 2160));
		Assert.Equal(all, Caps(null, null));
		Assert.Equal(all[3..], Caps(1920, 1080));
		Assert.Equal(all[3..], Caps(1920, 800));        // a scope film is 1080p by its width
		Assert.Equal(all[3..], Caps(2560, 1440));
		Assert.Equal(all[7..], Caps(1280, 720));
		Assert.Equal(all[9..], Caps(720, 480));
		Assert.Equal(all[9..], Caps(320, 240));
		Assert.Equal(all[3..], Caps(null, 1080));        // the height when the width is not known
	}

	[Theory]
	[InlineData(120_000_000L, "120 Mbps")]
	[InlineData(8_000_000L, "8 Mbps")]
	[InlineData(1_500_000L, "1.5 Mbps")]
	[InlineData(12_345_678L, "12 Mbps")]
	[InlineData(2_500_000L, "2.5 Mbps")]
	[InlineData(800_000L, "800 kbps")]
	public void Bitrates_read_as_people_say_them(long bps, string text) => Assert.Equal(text, QualityChoices.Mbps(bps));

	// -- the header --------------------------------------------------------------------------------------------------------

	[Fact]
	public void The_header_says_what_plays_now()
	{
		Assert.Equal("", QualityChoices.Header(null));
		Assert.Equal("Direct play · 1080p · 12 Mbps", QualityChoices.Header(JsonSerializer.Deserialize<BridgeStream>(DirectPlay1080, Bridge.JsonOptions)));
		Assert.Equal("Transcoding · 8 Mbps · the file is over the bitrate limit", QualityChoices.Header(JsonSerializer.Deserialize<BridgeStream>(Transcode8, Bridge.JsonOptions)));
		Assert.Equal("Direct stream · 4K · 60 Mbps · the audio format isn't supported",
			QualityChoices.Header(new BridgeStream(Item, PlayMethod: "DirectStream", TranscodeReasons: ["AudioCodecNotSupported"], Bitrate: 60_000_000, SourceWidth: 3840, SourceHeight: 2160)));
		// nothing known but the method
		Assert.Equal("Transcoding", QualityChoices.Header(new BridgeStream(Item, PlayMethod: "Transcode")));
		Assert.Equal("", QualityChoices.Header(new BridgeStream(Item, PlayMethod: "Something new")));
	}

	[Fact]
	public void Under_a_cap_the_bitrate_is_the_reason_said()
	{
		// Jellyfin 12.4 lists the target's codecs beside the bitrate for a capped transcode (the QA server, 2026-11-21)
		string[] reasons = ["VideoCodecNotSupported", "AudioCodecNotSupported", "ContainerBitrateExceedsLimit"];
		Assert.Equal("the file is over the bitrate limit", QualityChoices.Reason(reasons));
		Assert.Equal("the file is over the bitrate limit; the video format isn't supported; the audio format isn't supported", QualityChoices.Reasons(reasons));
		Assert.Equal("the video format isn't supported", QualityChoices.Reason(["VideoCodecNotSupported"]));
	}

	[Theory]
	[InlineData("VideoCodecNotSupported", "the video format isn't supported")]
	[InlineData("SubtitleCodecNotSupported", "the subtitles are drawn into the picture")]
	[InlineData("VideoRangeTypeNotSupported", "the video can't be sent as it is")]
	[InlineData("SomeFutureReason", "some future reason")]
	public void Reasons_are_plain_words(string reason, string words) => Assert.Equal(words, QualityChoices.ReasonText(reason));

	// -- the menu in the player --------------------------------------------------------------------------------------------

	[Fact]
	public void The_stream_event_fills_the_menu_and_marks_the_choice_in_effect()
	{
		Assert.Equal("", _vm.QualityHeader);
		Assert.Equal("Quality, Auto", _vm.QualityName);
		_vm.OnEvent(StreamEvent(Transcode8));
		Assert.Equal("Transcoding · 8 Mbps · the file is over the bitrate limit", _vm.QualityHeader);
		Assert.Equal("Quality, 8 Mbps", _vm.QualityName);
		Assert.Equal("Quality: 8 Mbps. Transcoding · 8 Mbps · the file is over the bitrate limit", _vm.QualityToolTip);
		var menu = _vm.QualityMenu;
		Assert.Equal(["Auto", "Original", "40 Mbps", "20 Mbps", "10 Mbps", "8 Mbps", "6 Mbps", "4 Mbps", "3 Mbps", "1.5 Mbps", "720 kbps"], menu.Select(m => m.Label));
		Assert.Equal("8 Mbps, 1080p, current", menu.Single(m => m.IsCurrent).Name);
		Assert.Equal("Original, the file as it is", menu[1].Name);
		Assert.Equal("Auto", menu[0].Name);
	}

	[Fact]
	public void Another_items_stream_event_is_not_this_ones()
	{
		_vm.OnEvent(StreamEvent(DirectPlay1080));
		_vm.OnEvent(StreamEvent(Transcode8.Replace(Item, Other, StringComparison.Ordinal)));
		Assert.Equal("Direct play · 1080p · 12 Mbps", _vm.QualityHeader);
	}

	[Fact]
	public void A_pick_sends_set_quality_once_and_the_mark_follows_the_backend()
	{
		_vm.OnEvent(StreamEvent(DirectPlay1080));
		_vm.SetQuality(new QualityChoice(8_000_000));
		Assert.Equal([("set_quality", """{"bitrate":8000000,"original":false}""")], _sent.Select(s => (s.Command, s.Args!.ToJsonString())));
		Assert.Equal("Quality: 8 Mbps", _vm.ToastText);
		Assert.Equal(QualityChoice.Auto, _vm.CurrentQuality);       // not taken until the backend says
		_vm.OnEvent(StreamEvent(Transcode8));
		Assert.Equal(new QualityChoice(8_000_000), _vm.CurrentQuality);
		// the same again: nothing
		_vm.SetQuality(new QualityChoice(8_000_000));
		Assert.Single(_sent);
		_vm.SetQuality(QualityChoice.Original);
		_vm.SetQuality(QualityChoice.Auto);
		Assert.Equal(["""{"bitrate":8000000,"original":false}""", """{"bitrate":null,"original":true}""", """{"bitrate":null,"original":false}"""],
			_sent.Select(s => s.Args!.ToJsonString()));
	}

	[Fact]
	public void A_refused_cap_reads_original_from_the_backend()
	{
		_vm.OnEvent(StreamEvent(DirectPlay1080));
		_vm.SetQuality(new QualityChoice(4_000_000));
		_vm.OnEvent(StreamEvent(DirectPlay1080.Replace("\"bitrate\": null, \"original\": false", "\"bitrate\": null, \"original\": true", StringComparison.Ordinal)));
		Assert.Equal(QualityChoice.Original, _vm.CurrentQuality);
		Assert.Equal("Original, the file as it is, current", _vm.QualityMenu[1].Name);
	}

	[Fact]
	public void An_older_backend_with_no_stream_event_reads_auto_with_every_cap()
	{
		Assert.Equal(12 + 2, _vm.QualityMenu.Count);
		Assert.True(_vm.QualityMenu[0].IsCurrent);
	}

	// -- Settings: home and away ---------------------------------------------------------------------------------------

	private sealed class MemorySettings : ILocalSettingsService
	{
		public readonly Dictionary<string, JsonNode?> Values = [];
		public int Writes;

		public JsonNode? ReadSettingRaw(string key) => Values.GetValueOrDefault(key);

		public T ReadSetting<T>(string key, T defaultValue) => Values.TryGetValue(key, out var v) && v is not null ? v.Deserialize<T>()! : defaultValue;

		public void SaveSetting<T>(string key, T value)
		{
			Writes++;
			Values[key] = JsonSerializer.SerializeToNode(value);
		}

		public void RemoveSetting(string key) => Values.Remove(key);

		public byte[] GetEntropyBytes() => [];
	}

	[Fact]
	public void Both_defaults_are_auto_and_the_stats_button_off_on_a_new_install()
	{
		var store = new MemorySettings();
		var settings = new Settings.Settings(store);
		Assert.Equal(QualityChoice.Auto, settings.QualityAtHome);
		Assert.Equal(QualityChoice.Auto, settings.QualityAway);
		Assert.False(settings.ShowPlaybackStatsButton);
		var page = new PlayerQualitySettings(settings);
		Assert.Equal("Auto", page.SelectedHome.Name);
		Assert.Equal("Auto", page.SelectedAway.Name);
		Assert.False(page.ShowStatsButton);
		Assert.Equal("""{"home":{"bitrate":null,"original":false},"away":{"bitrate":null,"original":false}}""", PlayerQualitySettings.DefaultsCommand(settings).ToJsonString());
	}

	[Fact]
	public void Nixie_never_moves_a_default_by_itself()
	{
		var store = new MemorySettings();
		var settings = new Settings.Settings(store);
		var page = new PlayerQualitySettings(settings);
		// reading, and the same choice set again, write nothing
		_ = page.SelectedHome;
		_ = page.SelectedAway;
		page.SelectedHome = PlayerQualitySettings.Choices[0];
		page.SelectedAway = PlayerQualitySettings.Choices[0];
		_ = PlayerQualitySettings.DefaultsCommand(settings);
		// the player follows a refused cap to Original for that item only: no setting changes
		_vm.OnEvent(StreamEvent(DirectPlay1080.Replace("\"original\": false", "\"original\": true", StringComparison.Ordinal)));
		Assert.Equal(0, store.Writes);
		Assert.Equal(QualityChoice.Auto, settings.QualityAtHome);
	}

	[Fact]
	public void Home_and_away_are_kept_and_sent_before_each_play()
	{
		var store = new MemorySettings();
		var settings = new Settings.Settings(store);
		var page = new PlayerQualitySettings(settings);
		page.SelectedHome = PlayerQualitySettings.Choices.Single(c => c.Name == "20 Mbps (1080p)");
		page.SelectedAway = PlayerQualitySettings.Choices.Single(c => c.Name == "Original");
		Assert.Equal("20000000", store.Values["QualityAtHome"]!.GetValue<string>());
		Assert.Equal("original", store.Values["QualityAway"]!.GetValue<string>());
		var again = new PlayerQualitySettings(new Settings.Settings(store));
		Assert.Equal("20 Mbps (1080p)", again.SelectedHome.Name);
		Assert.Equal("Original", again.SelectedAway.Name);
		Assert.Equal("""{"home":{"bitrate":20000000,"original":false},"away":{"bitrate":null,"original":true}}""",
			PlayerQualitySettings.DefaultsCommand(new Settings.Settings(store)).ToJsonString());
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("fast")]
	[InlineData("12345")]       // not one of the offered caps
	[InlineData("-8000000")]
	public void A_kept_value_that_is_not_a_choice_reads_auto(string? kept) => Assert.Equal(QualityChoice.Auto, QualityChoice.FromSetting(kept));

	[Fact]
	public void Every_choice_survives_the_round_trip()
	{
		foreach (var choice in PlayerQualitySettings.Choices.Select(c => c.Choice))
		{
			Assert.Equal(choice, QualityChoice.FromSetting(choice.ToSetting()));
		}

		Assert.Equal(14, PlayerQualitySettings.Choices.Count);
		Assert.Equal(["Auto", "Original", "120 Mbps (4K)", "80 Mbps (4K)", "60 Mbps (4K)", "40 Mbps (1080p)", "20 Mbps (1080p)", "10 Mbps (1080p)", "8 Mbps (1080p)", "6 Mbps (720p)", "4 Mbps (720p)", "3 Mbps (480p)", "1.5 Mbps (480p)", "720 kbps (480p)"],
			PlayerQualitySettings.Choices.Select(c => c.Name));
	}

	[Fact]
	public void The_settings_say_a_lower_quality_makes_the_server_convert()
	{
		foreach (var text in new[] { PlayerQualitySettings.HomeDescription, PlayerQualitySettings.AwayDescription })
		{
			Assert.Contains("A lower quality makes the server convert the video, and some servers don't allow it.", text, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void A_mock_settings_store_with_no_answer_sends_auto()
	{
		var settings = new Moq.Mock<ISettings>();
		Assert.Equal("""{"home":{"bitrate":null,"original":false},"away":{"bitrate":null,"original":false}}""", PlayerQualitySettings.DefaultsCommand(settings.Object).ToJsonString());
	}
}
