using System.Text.Json.Nodes;
using FluentFin.Core.Mpv;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.6 player: "Playback stats" (docs/BRIDGE.md §4 <c>get_stats</c>): the rows and their words, the panel asking about once a
/// second while open and never while closed, the I key and Esc, and the Copy text, which never carries the token, the server
/// address's credentials or an <c>ApiKey=</c>.
/// </summary>
// 1.6.1 (the 1.6 review's queue): one test sets SessionInfo's token and address (static) for the Copy check, so this
// class runs with the other tests that set SessionInfo, one at a time, never beside a test that reads them.
[Collection(SessionInfoCollection.Name)]
public sealed class PlaybackStatsTests
{
	private const string Item = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee1";
	private const string Token = "tok-abcdef-0123456789";

	private DateTimeOffset _now = new(2026, 11, 21, 20, 0, 0, TimeSpan.Zero);
	private readonly List<string> _queries = [];
	private readonly MpvControlsViewModel _vm;
	private Func<JsonNode?> _answer = () => JsonNode.Parse(Answer);

	/// <summary>The backend's answer, as test_bridge_quality.py's GetStatsTest gets it.</summary>
	private const string Answer = """
		{"itemId": "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee1", "playMethod": "Transcode", "transcodeReasons": ["ContainerBitrateExceedsLimit"],
		 "container": "mkv", "streamFormat": "hls", "videoCodec": "hevc", "videoProfile": "Main 10", "streamVideoCodec": "h264",
		 "sourceWidth": 1920, "sourceHeight": 1080, "width": 1280, "height": 720, "frameRate": 23.976, "hdr": "HDR10",
		 "hardwareDecoding": "d3d11va", "droppedFrames": 3, "delayedFrames": 4, "audioCodec": "aac", "audioChannels": 2,
		 "audioSampleRate": 48000, "audioTrack": "English - Dolby Digital+ - 5.1", "subtitleFormat": null, "subtitlesOff": true,
		 "streamBitrate": 8000000, "measuredBitrate": 7880000, "bufferSeconds": 12.5, "bufferBytes": 9000000,
		 "networkSpeed": 2500000, "isLocal": false, "mpvVersion": "mpv v0.41.0"}
		""";

	public PlaybackStatsTests()
	{
		_vm = new MpvControlsViewModel((_, _) => Task.CompletedTask, () => _now)
		{
			Query = (command, _) =>
			{
				_queries.Add(command);
				return Task.FromResult(_answer());
			},
		};
		_vm.OnEvent(new BridgeEvent(Bridge.Events.Item, JsonNode.Parse($$"""{"itemId": "{{Item}}", "name": "Film", "type": "Movie"}""")));
	}

	private void Wait(double seconds)
	{
		_now += TimeSpan.FromSeconds(seconds);
		_vm.Tick();
	}

	// -- the rows ----------------------------------------------------------------------------------------------------------

	[Fact]
	public void The_rows_name_each_fact_in_plain_words()
	{
		var rows = PlaybackStats.Rows(PlaybackStats.Parse(JsonNode.Parse(Answer))!);
		Assert.Equal(
		[
			"Playing: Transcoding",
			"Why: the file is over the bitrate limit",
			"Container: MKV → HLS",
			"Video: HEVC Main 10 → H264",
			"Resolution: 1920×1080 → 1280×720",
			"Frame rate: 23.976 fps",
			"HDR: HDR10",
			"Hardware decoding: On (d3d11va)",
			"Frames: 3 dropped · 4 delayed",
			"Audio: AAC · Stereo · 48 kHz",
			"Audio track: English - Dolby Digital+ - 5.1",
			"Subtitles: Off",
			"Server bitrate: 8.0 Mbps",
			"Measured bitrate: 7.9 Mbps",
			"Buffer: 12.5 s · 9.0 MB",
			"Network speed: 20.0 Mbps",
			"Server: Away",
			"mpv: mpv v0.41.0",
		], rows.Select(r => r.Name));
	}

	[Fact]
	public void A_direct_play_with_nothing_known_still_has_every_row()
	{
		var rows = PlaybackStats.Rows(new BridgeStats(PlayMethod: "DirectPlay", SubtitleFormat: "subrip"));
		Assert.DoesNotContain(rows, r => r.Label == "Why");
		Assert.Equal("Direct play", rows[0].Value);
		Assert.Equal("Off", rows.Single(r => r.Label == "Hardware decoding").Value);
		Assert.Equal("? dropped · ? delayed", rows.Single(r => r.Label == "Frames").Value);
		// the container once when mpv reads it as the file's (a direct play), with what mpv reads when not (a transcode)
		Assert.Equal("MKV", PlaybackStats.Rows(new BridgeStats(Container: "mkv", StreamFormat: "mkv")).Single(r => r.Label == "Container").Value);
		Assert.Equal("H264 High", PlaybackStats.Rows(new BridgeStats(VideoCodec: "h264", VideoProfile: "High", StreamVideoCodec: "h264")).Single(r => r.Label == "Video").Value);
		Assert.Equal("SUBRIP", rows.Single(r => r.Label == "Subtitles").Value);
		Assert.Equal("At home", rows.Single(r => r.Label == "Server").Value);
		Assert.All(rows.Where(r => r.Label is "Container" or "Video" or "Resolution" or "Frame rate" or "HDR" or "mpv" or "Buffer" or "Network speed"),
			r => Assert.Equal(PlaybackStats.Unknown, r.Value));
	}

	[Theory]
	[InlineData(1, "Mono")]
	[InlineData(2, "Stereo")]
	[InlineData(6, "5.1")]
	[InlineData(8, "7.1")]
	[InlineData(3, "3 channels")]
	public void Channels_read_as_people_say_them(int channels, string text) => Assert.Equal(text, PlaybackStats.Channels(channels));

	[Fact]
	public void Not_an_answer_is_not_stats() => Assert.Null(PlaybackStats.Parse(JsonNode.Parse("[1, 2]")));

	// -- asking: once a second while open, never while closed ----------------------------------------------------------------

	[Fact]
	public void Closed_it_reads_nothing_however_long_it_plays()
	{
		for (var i = 0; i < 120; i++)
		{
			Wait(0.25);
		}

		Assert.Empty(_queries);
		Assert.Equal(0, _vm.StatsQuestions);
		// opened and closed again: nothing more after the close
		_vm.ToggleStats();
		_vm.ToggleStats();
		var asked = _queries.Count;
		for (var i = 0; i < 40; i++)
		{
			Wait(0.25);
		}

		Assert.Equal(asked, _queries.Count);
		Assert.False(_vm.IsStatsOpen);
	}

	[Fact]
	public void Open_it_asks_at_once_then_about_once_a_second()
	{
		_vm.ToggleStats();
		Assert.True(_vm.IsStatsOpen);
		Assert.Equal(["get_stats"], _queries);
		Assert.Equal("Playing: Transcoding", _vm.StatsRows[0].Name);
		for (var i = 0; i < 3; i++)
		{
			Wait(0.25);
		}

		Assert.Single(_queries);
		Wait(0.25);
		Assert.Equal(2, _queries.Count);
		for (var i = 0; i < 16; i++)
		{
			Wait(0.25);
		}

		Assert.Equal(6, _queries.Count);
	}

	[Fact]
	public void Open_but_minimized_it_asks_nothing_until_the_window_is_back()
	{
		// 1.6.1 (the 1.6 review's queue): the open panel asked mpv once a second with the window minimized
		var minimized = false;
		_vm.WindowMinimized = () => minimized;
		_vm.ToggleStats();
		Assert.Single(_queries);
		minimized = true;
		for (var i = 0; i < 40; i++)
		{
			Wait(0.25);
		}

		Assert.Single(_queries);
		Assert.True(_vm.IsStatsOpen, "still open for when the window is back");
		minimized = false;
		Wait(0.25);
		Assert.Equal(2, _queries.Count);
		Assert.Equal("Playing: Transcoding", _vm.StatsRows[0].Name);
	}

	[Fact]
	public void One_question_at_a_time_and_a_late_answer_after_the_close_is_dropped()
	{
		var pending = new TaskCompletionSource<JsonNode?>();
		var vm = new MpvControlsViewModel((_, _) => Task.CompletedTask, () => _now)
		{
			Query = (command, _) =>
			{
				_queries.Add(command);
				return pending.Task;
			},
		};
		vm.ToggleStats();
		for (var i = 0; i < 12; i++)
		{
			_now += TimeSpan.FromSeconds(0.5);
			vm.Tick();
		}

		Assert.Single(_queries);
		vm.ToggleStats();
		pending.SetResult(JsonNode.Parse(Answer));
		Assert.Same(PlaybackStats.Waiting, vm.StatsRows);
	}

	[Fact]
	public void The_panels_rows_stay_the_same_elements_while_their_values_change()
	{
		_vm.ToggleStats();
		var views = _vm.StatsRowViews.ToList();
		Assert.Equal("Playing: Transcoding", views[0].Name);
		var changed = new List<string?>();
		views.Single(v => v.Label == "Measured bitrate").PropertyChanged += (_, e) => changed.Add(e.PropertyName);
		_answer = () => JsonNode.Parse(Answer.Replace("7880000", "5000000", StringComparison.Ordinal));
		Wait(1);
		Assert.Equal(views, _vm.StatsRowViews.ToList());          // the same objects: nothing rebuilt
		Assert.Equal("Measured bitrate: 5.0 Mbps", views.Single(v => v.Label == "Measured bitrate").Name);
		Assert.Contains(nameof(PlaybackStatsRowView.Name), changed);
		// other rows (a direct play has no "Why"): the list is rebuilt once
		_answer = () => JsonNode.Parse(Answer.Replace("\"Transcode\"", "\"DirectPlay\"", StringComparison.Ordinal));
		Wait(1);
		Assert.DoesNotContain(_vm.StatsRowViews, v => v.Label == "Why");
		Assert.Equal("Playing: Direct play", _vm.StatsRowViews[0].Name);
	}

	[Fact]
	public void An_older_backend_says_the_stats_are_not_available()
	{
		_answer = () => throw new BridgeCommandException("get_stats", "unknown_cmd", null);
		_vm.ToggleStats();
		Assert.Same(PlaybackStats.NotAvailable, _vm.StatsRows);
		// a miss after rows came keeps them (between two items)
		_answer = () => JsonNode.Parse(Answer);
		Wait(1);
		_answer = () => throw new BridgeCommandException("get_stats", "no_playback", null);
		Wait(1);
		Assert.Equal("Playing: Transcoding", _vm.StatsRows[0].Name);
	}

	// -- the ways out --------------------------------------------------------------------------------------------------------

	[Fact]
	public void I_toggles_it_and_holding_I_does_not_flicker()
	{
		Assert.Equal(MpvControlsAction.TogglePlaybackStats, MpvControlsViewModel.ActionFor(0x49, false, false, false));
		Assert.Null(MpvControlsViewModel.ActionFor(0x49, true, false, false));
		Assert.Null(MpvControlsViewModel.ActionFor(0x49, false, true, false));
		Assert.Null(MpvControlsViewModel.ActionFor(0x49, false, false, true));
		Assert.False(PlayerKeyRouter.Repeats(MpvControlsAction.TogglePlaybackStats));
		Assert.Equal(PlayerKeyRoute.Swallow, PlayerKeyRouter.Route(0x49, false, false, false, repeat: true, videoShown: true, PlayerFocus.Player).Route);
		Assert.Equal(PlayerKeyRoute.Element, PlayerKeyRouter.Route(0x49, false, false, false, repeat: false, videoShown: true, PlayerFocus.TextInput).Route);
		Assert.Equal(PlayerKeyRoute.Element, PlayerKeyRouter.Route(0x49, false, false, false, repeat: false, videoShown: true, PlayerFocus.Modal).Route);
		Assert.Equal(PlayerKeyRoute.Element, PlayerKeyRouter.Route(0x49, false, false, false, repeat: false, videoShown: false, PlayerFocus.Player).Route);
		foreach (var focus in new[] { PlayerFocus.Player, PlayerFocus.Button, PlayerFocus.SeekBar, PlayerFocus.List })
		{
			Assert.Equal(new PlayerKeyDecision(PlayerKeyRoute.Player, MpvControlsAction.TogglePlaybackStats),
				PlayerKeyRouter.Route(0x49, false, false, false, repeat: false, videoShown: true, focus));
		}

		_vm.RunKey(MpvControlsAction.TogglePlaybackStats);
		Assert.True(_vm.IsStatsOpen);
		Assert.Equal("Hide playback stats", _vm.StatsButtonName);
		_vm.HandleKey(0x49);
		Assert.False(_vm.IsStatsOpen);
		Assert.Equal("Show playback stats", _vm.StatsButtonName);
	}

	[Fact]
	public void I_clashes_with_no_other_key()
	{
		Assert.Null(Shortcuts.AppShortcuts.For(0x49, control: false, shift: false, alt: false, inTextInput: false));
		Assert.Equal(1, Enumerable.Range(0, 256).Count(key => MpvControlsViewModel.ActionFor(key, false, false, false) == MpvControlsAction.TogglePlaybackStats));
		// the skip stream's new key (wip/160-skip) is Shift+S; I is not it, and S stays Skip
		Assert.Equal(MpvControlsAction.SkipSegment, MpvControlsViewModel.ActionFor(0x53, false, false, false));
	}

	[Fact]
	public void Esc_closes_the_stats_first_then_the_page_has_it()
	{
		_vm.ToggleStats();
		Assert.True(_vm.ClosePanels());
		Assert.False(_vm.IsStatsOpen);
		Assert.False(_vm.ClosePanels(), "nothing open: Esc goes on to the page (full screen)");
		var asked = _queries.Count;
		Wait(3);
		Assert.Equal(asked, _queries.Count);
	}

	[Fact]
	public void The_button_is_off_by_default_and_the_key_works_without_it()
	{
		Assert.False(_vm.ShowStatsButton);
		_vm.HandleKey(0x49);
		Assert.True(_vm.IsStatsOpen);
	}

	[Fact]
	public void It_stays_open_while_the_bars_hide_and_across_items()
	{
		_vm.ToggleStats();
		Wait(MpvControlsViewModel.HideAfter.TotalSeconds + 1);
		Assert.False(_vm.ControlsVisible);
		Assert.True(_vm.IsStatsOpen);
		_vm.OnEvent(new BridgeEvent(Bridge.Events.Item, JsonNode.Parse("""{"itemId": "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee2", "name": "Next", "type": "Movie"}""")));
		var asked = _queries.Count;
		Wait(1);
		Assert.True(_vm.IsStatsOpen);
		Assert.Equal(asked + 1, _queries.Count);
	}

	// -- Copy ------------------------------------------------------------------------------------------------------------

	[Fact]
	public void Copy_never_carries_the_token_the_servers_credentials_or_an_api_key()
	{
		const string server = "https://alice:s3cretPass@jf.example:8920/jellyfin";
		var poison = $"{Token} ApiKey={Token} api_key={Token} https://alice:s3cretPass@jf.example:8920/Videos/x/stream?static=true&ApiKey={Token} X-Emby-Token: {Token} Token=\"{Token}\" {Uri.EscapeDataString(Token)}";
		var stats = new BridgeStats(
			ItemId: poison, PlayMethod: "Transcode", TranscodeReasons: [poison], Container: poison, StreamFormat: poison, VideoCodec: poison,
			VideoProfile: poison, Hdr: poison, HardwareDecoding: poison, AudioCodec: poison, AudioTrack: poison, SubtitleFormat: poison,
			MpvVersion: poison);
		var rows = PlaybackStats.Rows(stats);
		Assert.Contains(rows, r => r.Value.Contains(Token, StringComparison.Ordinal));     // the poison reached the rows
		var text = PlaybackStats.CopyText(rows, "1.6.0", Token, server);
		Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
		Assert.DoesNotContain(Uri.EscapeDataString(Token), text, StringComparison.Ordinal);
		Assert.DoesNotContain("s3cretPass", text, StringComparison.Ordinal);
		Assert.DoesNotContain("alice:", text, StringComparison.Ordinal);
		Assert.DoesNotContain("ApiKey=t", text, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("api_key=t", text, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("static=true", text, StringComparison.Ordinal);
		Assert.StartsWith("Nixie playback stats 1.6.0\n", text, StringComparison.Ordinal);
		Assert.Contains("\nPlaying: Transcoding\n", text, StringComparison.Ordinal);
	}

	[Fact]
	public void Copy_is_the_rows_as_plain_text()
	{
		var rows = PlaybackStats.Rows(PlaybackStats.Parse(JsonNode.Parse(Answer))!);
		var text = PlaybackStats.CopyText(rows, "1.6.0");
		Assert.Equal(rows.Count + 1, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
		Assert.Contains("Resolution: 1920×1080 → 1280×720\n", text, StringComparison.Ordinal);
		// a short or empty token is not a secret worth blanking words for
		Assert.Equal(text, PlaybackStats.CopyText(rows, "1.6.0", "", null));
	}

	[Fact]
	public void The_view_models_copy_uses_the_sessions_token()
	{
		var before = (SessionInfo.AccessToken, SessionInfo.BaseUrl);
		try
		{
			SessionInfo.AccessToken = Token;
			SessionInfo.BaseUrl = "https://bob:hunter22@jf.example";
			_answer = () => JsonNode.Parse(Answer.Replace("mpv v0.41.0", $"mpv {Token} https://bob:hunter22@jf.example/x?api_key={Token}", StringComparison.Ordinal));
			_vm.ToggleStats();
			var text = _vm.StatsCopyText();
			Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
			Assert.DoesNotContain("hunter22", text, StringComparison.Ordinal);
		}
		finally
		{
			(SessionInfo.AccessToken, SessionInfo.BaseUrl) = before;
		}
	}
}
