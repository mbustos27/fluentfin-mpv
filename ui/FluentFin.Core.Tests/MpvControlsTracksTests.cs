using System.Text.Json.Nodes;
using FluentFin.Core.Mpv;

namespace FluentFin.Core.Tests;

/// <summary>fluentfin-mpv player controls: the audio and subtitle menus of FluentFin's controls (MpvControlsViewModel).</summary>
public sealed class MpvControlsTracksTests
{
	private readonly List<(string Command, JsonObject? Args)> _sent = [];
	private readonly MpvControlsViewModel _vm;

	public MpvControlsTracksTests()
	{
		_vm = new MpvControlsViewModel((command, args) =>
		{
			_sent.Add((command, args?.DeepClone() as JsonObject));
			return Task.CompletedTask;
		}, () => new DateTimeOffset(2026, 11, 15, 20, 0, 0, TimeSpan.Zero));
	}

	private static readonly BridgeTrack English51 = new(1, "eng", "English - Dolby Digital - 5.1 - Default", Codec: "ac3", Channels: 6, IsDefault: true);
	private static readonly BridgeTrack Commentary = new(2, "eng", "English - AAC - Stereo", StreamTitle: "Commentary", Codec: "aac", Channels: 2);
	private static readonly BridgeTrack SubsEnglish = new(3, "eng", "English - SUBRIP", Codec: "subrip");
	private static readonly BridgeTrack SubsSdh = new(4, "eng", "English - SUBRIP", StreamTitle: "SDH", Codec: "subrip", IsHearingImpaired: true);
	private static readonly BridgeTrack SubsForcedExternal = new(5, "spa", null, Codec: "ass", IsForced: true, IsExternal: true);

	private void Tracks(IReadOnlyList<BridgeTrack> audio, IReadOnlyList<BridgeTrack> subtitles) =>
		_vm.OnEvent(new BridgeEvent(Bridge.Events.Tracks, System.Text.Json.JsonSerializer.SerializeToNode(new BridgeTracks(audio, subtitles), Bridge.JsonOptions)));

	private void Playing(int? audio, int? subtitle) => _vm.OnState(new BridgeState { State = Bridge.States.Playing, AudioStreamIndex = audio, SubtitleStreamIndex = subtitle });

	[Fact]
	public void Menus_ListTheTracks_WithTheOnesPlayingSelected_AndSubtitlesOff()
	{
		Tracks([English51, Commentary], [SubsEnglish, SubsSdh]);
		Playing(audio: 1, subtitle: -1);

		Assert.Equal([1, 2], _vm.AudioChoices.Select(x => x.Index));
		Assert.Equal([true, false], _vm.AudioChoices.Select(x => x.IsSelected));
		Assert.Equal([-1, 3, 4], _vm.SubtitleChoices.Select(x => x.Index));
		Assert.Equal("Off", _vm.SubtitleChoices[0].Label);
		Assert.True(_vm.SubtitleChoices[0].IsSelected, "subtitles off: Off is the one checked");
		Assert.True(_vm.HasAudioChoice);
		Assert.True(_vm.HasSubtitleChoice);
	}

	[Fact]
	public void Labels_SayWhatTheTrackIs_WithoutRepeatingTheServersTitle()
	{
		Assert.Equal("English - Dolby Digital - 5.1 - Default", MpvControlsViewModel.AudioLabel(English51));
		Assert.Equal("English - AAC - Stereo (Commentary)", MpvControlsViewModel.AudioLabel(Commentary));
		Assert.Equal("English - SUBRIP (SDH)", MpvControlsViewModel.SubtitleLabel(SubsSdh));
		Assert.Equal("Spanish · Forced · External", MpvControlsViewModel.SubtitleLabel(SubsForcedExternal));
		Assert.Equal("English - SUBRIP · SDH", MpvControlsViewModel.SubtitleLabel(SubsSdh with { StreamTitle = null }));
		Assert.Equal("Track 7", MpvControlsViewModel.AudioLabel(new BridgeTrack(7)));
		Assert.Equal("xyz", MpvControlsViewModel.LanguageName("xyz"));
	}

	[Fact]
	public void PickingATrack_SendsItsJellyfinIndex_Once()
	{
		Tracks([English51, Commentary], [SubsEnglish]);
		Playing(audio: 1, subtitle: 3);

		_ = _vm.SetAudio(2);
		_ = _vm.SetAudio(2);
		_ = _vm.SetSubtitle(-1);
		_ = _vm.SetSubtitle(-1);

		Assert.Equal([("set_audio", 2), ("set_subtitle", -1)], _sent.Select(x => (x.Command, (int)x.Args!["index"]!)));
		Assert.True(_vm.AudioChoices.Single(x => x.Index == 2).IsSelected, "the pick shows at once");
		Assert.True(_vm.SubtitleChoices.Single(x => x.Index == -1).IsSelected);
	}

	[Fact]
	public void PickingWhatPlays_SendsNothing()
	{
		Tracks([English51, Commentary], [SubsEnglish]);
		Playing(audio: 1, subtitle: null);

		_ = _vm.SetAudio(1);
		_ = _vm.SetSubtitle(-1);

		Assert.Empty(_sent);
	}

	[Fact]
	public void OneAudioTrack_AndNoSubtitles_OfferNoMenus()
	{
		Tracks([English51], []);
		Playing(audio: 1, subtitle: null);

		Assert.False(_vm.HasAudioChoice);
		Assert.False(_vm.HasSubtitleChoice);
		Assert.Empty(_vm.SubtitleChoices);
	}

	[Fact]
	public void ANewItem_ForgetsTheLastOnesTracks_UntilItsOwnArrive()
	{
		Tracks([English51, Commentary], [SubsEnglish]);
		Playing(audio: 1, subtitle: 3);

		_vm.OnItem(new BridgeItem("next", "Next", "Episode"));

		Assert.Empty(_vm.AudioChoices);
		Assert.Empty(_vm.SubtitleChoices);
		Tracks([Commentary], [SubsSdh]);
		Assert.Equal([2], _vm.AudioChoices.Select(x => x.Index));
	}

	[Fact]
	public void TheBackendsChoice_MovesTheCheck()
	{
		Tracks([English51, Commentary], [SubsEnglish, SubsSdh]);
		Playing(audio: 1, subtitle: 3);
		Playing(audio: 2, subtitle: 4);

		Assert.True(_vm.AudioChoices.Single(x => x.IsSelected).Index == 2);
		Assert.True(_vm.SubtitleChoices.Single(x => x.IsSelected).Index == 4);
	}
}
