using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Tests.Mpv;

public sealed class BridgeCommandsTests
{
	private static readonly BridgeIdentity Identity = new(
		"https://jf.example:8920", "server-id", "Home", "0123456789abcdef0123456789abcdef", "alice",
		"secret-token", "device-id", "DESKTOP-1", "FluentFin", "1.0.0.0");

	[Fact]
	public void Connect_CarriesFluentFinsIdentityAndTheEmbeddedWindow()
	{
		var args = BridgeCommands.Connect(Identity, BridgeWindow.Embedded(0x00140A3A));

		var expected = JsonNode.Parse("""
		{
		  "protocol": 1,
		  "server": {"url": "https://jf.example:8920", "id": "server-id", "name": "Home"},
		  "user": {"id": "0123456789abcdef0123456789abcdef", "name": "alice"},
		  "accessToken": "secret-token",
		  "device": {"id": "device-id", "name": "DESKTOP-1"},
		  "client": {"name": "FluentFin", "version": "1.0.0.0"},
		  "window": {"mode": "embedded", "hwnd": 1313338}
		}
		""");
		Assert.True(JsonNode.DeepEquals(expected, args), args.ToJsonString());
	}

	[Fact]
	public void Connect_Prewarm_IsSentOnlyWhenAsked()
	{
		// fluentfin-mpv (startup-perf): docs/BRIDGE.md §4 connect.prewarm
		var prewarmed = BridgeCommands.Connect(Identity, BridgeWindow.Embedded(42), prewarm: true);
		var ordinary = BridgeCommands.Connect(Identity, BridgeWindow.Embedded(42));

		Assert.True((bool?)prewarmed["prewarm"]);
		Assert.False(ordinary.ContainsKey("prewarm"));
		prewarmed.Remove("prewarm");
		Assert.True(JsonNode.DeepEquals(ordinary, prewarmed), "otherwise the same connect");
	}

	[Fact]
	public void Connect_WithoutIdentity_SendsOnlyProtocolAndWindow()
	{
		var args = BridgeCommands.Connect(null, BridgeWindow.Embedded(42));

		Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"protocol":1,"window":{"mode":"embedded","hwnd":42}}"""), args), args.ToJsonString());
	}

	[Fact]
	public void EmbeddedWindow_HandleIsSentAsAnUnsigned32BitNumber()
	{
		// a handle with bit 31 set comes back sign-extended in a 64-bit nint; mpv must never see a negative --wid
		var window = BridgeWindow.Embedded(unchecked((nint)(int)0x80001234));

		Assert.Equal(0x80001234L, window.Hwnd);
	}

	[Fact]
	public void Identity_ToStringNeverShowsTheToken()
	{
		Assert.DoesNotContain("secret-token", Identity.ToString());
	}

	[Fact]
	public void Identity_ToStringNeverShowsCredentialsOrAQueryInTheServerUrl()
	{
		var text = (Identity with { ServerUrl = "https://alice:pa55word@jf.example:8920/jf?api_key=k3y" }).ToString();

		Assert.Contains("https://jf.example:8920", text);
		Assert.DoesNotContain("pa55word", text);
		Assert.DoesNotContain("k3y", text);
	}

	[Fact]
	public void Play_FromAPlaylistWithThePagesSelection()
	{
		var ids = new[] { Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222") };
		var request = BridgePlayRequest.FromPlaylist(ids, 1, 6000000000, new PlaybackSelection(3, PlaybackSelection.NoSubtitles));

		var args = BridgeCommands.Play(request);

		var expected = JsonNode.Parse("""
		{
		  "itemIds": ["11111111111111111111111111111111", "22222222222222222222222222222222"],
		  "startIndex": 1,
		  "startPositionTicks": 6000000000,
		  "audioStreamIndex": 3,
		  "subtitleStreamIndex": -1
		}
		""");
		Assert.True(JsonNode.DeepEquals(expected, args), args.ToJsonString());
	}

	[Fact]
	public void Play_WithoutASelection_LeavesTheTrackChoiceToTheBackend()
	{
		var args = BridgeCommands.Play(BridgePlayRequest.FromPlaylist([Guid.Empty], 0, 0, null));

		Assert.False(args.ContainsKey("audioStreamIndex"));
		Assert.False(args.ContainsKey("subtitleStreamIndex"));
		Assert.False(args.ContainsKey("mediaSourceId"));
		Assert.Equal(0, (int?)args["startIndex"]);
	}

	[Fact]
	public void Play_NeedsAnItem()
	{
		Assert.Throws<ArgumentException>(() => BridgeCommands.Play(new BridgePlayRequest([])));
	}

	[Theory]
	[InlineData(PlaystateRequest_Command.Pause, "pause")]
	[InlineData(PlaystateRequest_Command.Unpause, "unpause")]
	[InlineData(PlaystateRequest_Command.PlayPause, "toggle_pause")]
	[InlineData(PlaystateRequest_Command.Stop, "stop")]
	[InlineData(PlaystateRequest_Command.NextTrack, "next")]
	[InlineData(PlaystateRequest_Command.PreviousTrack, "previous")]
	public void RemotePlaystate_BecomesTheBridgeCommand(PlaystateRequest_Command command, string expected)
	{
		var translated = MpvRemoteControl.Translate(new PlaystateMessage { Data = new PlaystateRequest { Command = command } }, null);

		Assert.Equal(expected, translated?.Name);
	}

	[Fact]
	public void RemoteSeek_IsAnAbsoluteSeekInTicks()
	{
		var translated = MpvRemoteControl.Translate(new PlaystateRequest { Command = PlaystateRequest_Command.Seek, SeekPositionTicks = 123456789 });

		Assert.Equal("seek", translated?.Name);
		Assert.Equal(123456789L, (long?)translated!.Args!["positionTicks"]);
	}

	[Fact]
	public void RemoteVolumeAndMute_UseTheArgumentsAndTheLastState()
	{
		var state = new BridgeState { Volume = 50, Muted = true };

		var set = MpvRemoteControl.Translate(General(GeneralCommand_Name.SetVolume, ("Volume", "35")), state);
		var up = MpvRemoteControl.Translate(General(GeneralCommand_Name.VolumeUp), state);
		var toggle = MpvRemoteControl.Translate(General(GeneralCommand_Name.ToggleMute), state);
		var subtitle = MpvRemoteControl.Translate(General(GeneralCommand_Name.SetSubtitleStreamIndex, ("Index", "-1")), state);
		var display = MpvRemoteControl.Translate(General(GeneralCommand_Name.DisplayMessage, ("Text", "hi")), state);

		Assert.Equal(("set_volume", 35), (set!.Name, (int)set.Args!["volume"]!));
		Assert.Equal(("set_volume", 55), (up!.Name, (int)up.Args!["volume"]!));
		Assert.Equal(("set_mute", false), (toggle!.Name, (bool)toggle.Args!["muted"]!));
		Assert.Equal(("set_subtitle", -1), (subtitle!.Name, (int)subtitle.Args!["index"]!));
		Assert.Null(display);
	}

	[Fact]
	public void RemotePlayNow_BecomesAPlayRequest()
	{
		var first = Guid.NewGuid();
		var second = Guid.NewGuid();
		var request = MpvRemoteControl.ToPlayRequest(new PlayRequest
		{
			PlayCommand = PlayRequest_PlayCommand.PlayNow,
			ItemIds = [first, null, second],
			StartIndex = 1,
			StartPositionTicks = 50,
			MediaSourceId = "source",
			AudioStreamIndex = 2,
			SubtitleStreamIndex = 4,
		});

		Assert.NotNull(request);
		Assert.Equal([first.ToString("N"), second.ToString("N")], request.ItemIds);
		Assert.Equal((1, 50L, "source", (int?)2, (int?)4), (request.StartIndex, request.StartPositionTicks, request.MediaSourceId, request.AudioStreamIndex, request.SubtitleStreamIndex));
	}

	[Theory]
	[InlineData(PlayRequest_PlayCommand.PlayNext)]
	[InlineData(PlayRequest_PlayCommand.PlayLast)]
	public void RemoteQueueAdditions_AreNotPlayRequests(PlayRequest_PlayCommand command)
	{
		Assert.Null(MpvRemoteControl.ToPlayRequest(new PlayRequest { PlayCommand = command, ItemIds = [Guid.NewGuid()] }));
	}

	private static GeneralCommandMessage General(GeneralCommand_Name name, params (string Key, object Value)[] arguments)
	{
		var args = new GeneralCommand_Arguments();
		foreach (var (key, value) in arguments)
		{
			args.AdditionalData[key] = value;
		}

		return new GeneralCommandMessage { Data = new GeneralCommand { Name = name, Arguments = args } };
	}
}
