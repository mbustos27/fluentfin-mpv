using System.Text.Json.Nodes;
using FluentFin.Core.Mpv;

namespace FluentFin.Core.Tests;

/// <summary>
/// Seaglass 1.1.4: the playback speed and the subtitle delay of the player's controls: their values, the commands sent
/// (<c>set_speed</c>, <c>set_subtitle_delay</c>, docs/BRIDGE.md §4), when they go back to normal, and their keys.
/// </summary>
public sealed class PlayerSpeedAndDelayTests
{
	private const string Item = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee1";
	private const string NextItem = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee2";

	private DateTimeOffset _now = new(2026, 11, 17, 20, 0, 0, TimeSpan.Zero);
	private readonly List<(string Command, JsonObject? Args)> _sent = [];
	private readonly MpvControlsViewModel _vm;

	public PlayerSpeedAndDelayTests()
	{
		_vm = new MpvControlsViewModel((command, args) =>
		{
			_sent.Add((command, args?.DeepClone() as JsonObject));
			return Task.CompletedTask;
		}, () => _now);
	}

	private static long Ticks(double seconds) => TimeSpan.FromSeconds(seconds).Ticks;

	private double[] Sent(string command, string key) => [.. _sent.Where(x => x.Command == command).Select(x => (double)x.Args![key]!)];

	private void Wait(double seconds)
	{
		_now += TimeSpan.FromSeconds(seconds);
		_vm.Tick();
	}

	private void StartItem(string itemId) => _vm.OnEvent(new BridgeEvent(Bridge.Events.Item, JsonNode.Parse($$"""
		{"itemId": "{{itemId}}", "name": "Pilot", "type": "Episode", "seriesName": "Spike Show", "seasonNumber": 1, "episodeNumber": 1, "runTimeTicks": {{Ticks(600)}}}
		""")));

	private void Playing(double positionSeconds)
	{
		_vm.OnState(new BridgeState { State = Bridge.States.Playing, ItemId = Item, DurationTicks = Ticks(600) });
		_vm.OnPosition(new BridgePosition(Ticks(positionSeconds), Ticks(600)));
	}

	// -- the values ------------------------------------------------------------------------------------------------------

	[Theory]
	[InlineData(1.0, 1.0)]
	[InlineData(0.1, 0.5)]
	[InlineData(9.0, 2.0)]
	[InlineData(1.3, 1.25)]
	[InlineData(1.4, 1.5)]
	[InlineData(double.NaN, 1.0)]
	[InlineData(double.PositiveInfinity, 1.0)]
	public void Speeds_are_the_offered_steps(double asked, double expected) => Assert.Equal(expected, PlaybackSpeeds.Clamp(asked));

	[Fact]
	public void Speeds_go_up_and_down_a_step_and_stop_at_the_ends()
	{
		Assert.Equal([0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0], PlaybackSpeeds.Steps);
		Assert.Equal(1.25, PlaybackSpeeds.Faster(1.0));
		Assert.Equal(0.75, PlaybackSpeeds.Slower(1.0));
		Assert.Equal(2.0, PlaybackSpeeds.Faster(2.0));
		Assert.Equal(0.5, PlaybackSpeeds.Slower(0.5));
		Assert.Equal("1.5×", PlaybackSpeeds.Text(1.5));
		Assert.Equal("0.75×", PlaybackSpeeds.Text(0.75));
		Assert.Equal("2×", PlaybackSpeeds.Text(2));
		Assert.Equal("Normal", PlaybackSpeeds.Label(1));
		Assert.Equal("1.25×", PlaybackSpeeds.Label(1.25));
	}

	[Theory]
	[InlineData(0.0, 0.0, "0.0 s", "none")]
	[InlineData(0.1, 0.1, "+0.1 s", "0.1 s later")]
	[InlineData(-0.30000000000000004, -0.3, "−0.3 s", "0.3 s earlier")]
	[InlineData(0.25, 0.3, "+0.3 s", "0.3 s later")]
	[InlineData(45, 30, "+30.0 s", "30.0 s later")]
	[InlineData(-45, -30, "−30.0 s", "30.0 s earlier")]
	[InlineData(double.NaN, 0, "0.0 s", "none")]
	public void Delays_are_tenths_within_thirty_seconds(double asked, double expected, string text, string words)
	{
		Assert.Equal(expected, SubtitleDelays.Clamp(asked));
		Assert.Equal(text, SubtitleDelays.Text(asked));
		Assert.Equal(words, SubtitleDelays.Describe(asked));
	}

	[Fact]
	public void Commands_carry_a_clamped_value()
	{
		Assert.Equal("""{"speed":1.5}""", BridgeCommands.Speed(1.4).ToJsonString());
		Assert.Equal("""{"speed":2}""", BridgeCommands.Speed(7).ToJsonString());
		Assert.Equal("""{"seconds":-0.3}""", BridgeCommands.SubtitleDelay(-0.30000000000000004).ToJsonString());
		Assert.Equal("""{"seconds":30}""", BridgeCommands.SubtitleDelay(99).ToJsonString());
		Assert.Equal("set_speed", Bridge.Commands.SetSpeed);
		Assert.Equal("set_subtitle_delay", Bridge.Commands.SetSubtitleDelay);
	}

	// -- the speed -------------------------------------------------------------------------------------------------------

	[Fact]
	public async Task Speed_is_sent_on_a_step_and_the_same_speed_again_sends_nothing()
	{
		await _vm.SetSpeed(1.4);
		await _vm.SetSpeed(1.5);
		await _vm.SetSpeed(2);

		Assert.Equal([1.5, 2.0], Sent(Bridge.Commands.SetSpeed, "speed"));
		Assert.Equal(2.0, _vm.Speed);
		Assert.Equal("2×", _vm.SpeedText);
		Assert.True(_vm.IsSpeedChanged);
		Assert.False(_vm.ToastVisible, "the menu shows its own value");

		await _vm.SetSpeed(1);
		Assert.False(_vm.IsSpeedChanged);
	}

	[Fact]
	public void Speed_keys_step_through_the_speeds_and_Backspace_is_normal()
	{
		Assert.True(_vm.HandleKey(MpvControlsViewModel.Keys.CloseBracket));
		Assert.True(_vm.HandleKey(MpvControlsViewModel.Keys.CloseBracket));
		Assert.True(_vm.ToastVisible);
		Assert.Equal("Speed 1.5×", _vm.ToastText);
		Assert.True(_vm.HandleKey(MpvControlsViewModel.Keys.Back));
		Assert.Equal("Normal speed", _vm.ToastText);
		for (var i = 0; i < 4; i++)
		{
			_vm.HandleKey(MpvControlsViewModel.Keys.OpenBracket);
		}

		// up, up, normal, then down to the slowest and no further
		Assert.Equal([1.25, 1.5, 1.0, 0.75, 0.5], Sent(Bridge.Commands.SetSpeed, "speed"));
		Assert.Equal(0.5, _vm.Speed);
		Assert.Equal("Speed 0.5×", _vm.ToastText);
	}

	[Fact]
	public async Task Position_moves_on_at_the_speed()
	{
		Playing(100);
		Wait(2);
		Assert.Equal(Ticks(102), _vm.PositionTicks);

		await _vm.SetSpeed(2);
		Wait(2);
		Assert.Equal(Ticks(106), _vm.PositionTicks);

		await _vm.SetSpeed(0.5);
		Wait(2);
		Assert.Equal(Ticks(107), _vm.PositionTicks);
	}

	[Fact]
	public async Task Speed_stays_for_the_next_item_and_takeover_sends_this_pages_values()
	{
		StartItem(Item);
		await _vm.SetSpeed(1.25);
		StartItem(NextItem);
		Assert.Equal(1.25, _vm.Speed);
		Assert.Equal([1.25], Sent(Bridge.Commands.SetSpeed, "speed"));

		// a new page's controls start normal and say so, whatever mpv kept from the page before
		_sent.Clear();
		var page = new MpvControlsViewModel((c, a) => { _sent.Add((c, a?.DeepClone() as JsonObject)); return Task.CompletedTask; }, () => _now);
		await page.SendControls();
		Assert.Equal([1.0], Sent(Bridge.Commands.SetSpeed, "speed"));
		Assert.Equal([0.0], Sent(Bridge.Commands.SetSubtitleDelay, "seconds"));
	}

	// -- the subtitle delay ----------------------------------------------------------------------------------------------

	[Fact]
	public async Task Delay_moves_in_tenths_and_resets()
	{
		await _vm.StepSubtitleDelay(1);
		await _vm.StepSubtitleDelay(1);
		await _vm.StepSubtitleDelay(1);
		Assert.Equal(0.3, _vm.SubtitleDelay);
		Assert.Equal("+0.3 s", _vm.SubtitleDelayText);
		await _vm.StepSubtitleDelay(-5);
		Assert.Equal("−0.2 s", _vm.SubtitleDelayText);
		await _vm.SetSubtitleDelay(0);
		await _vm.SetSubtitleDelay(0);

		Assert.Equal([0.1, 0.2, 0.3, -0.2, 0.0], Sent(Bridge.Commands.SetSubtitleDelay, "seconds"));
	}

	[Fact]
	public void Delay_keys_are_Z_earlier_and_X_later_and_say_so()
	{
		Assert.True(_vm.HandleKey(MpvControlsViewModel.Keys.X));
		Assert.Equal("Subtitles 0.1 s later", _vm.ToastText);
		Assert.True(_vm.HandleKey(MpvControlsViewModel.Keys.Z));
		Assert.Equal("Subtitles: no delay", _vm.ToastText);
		Assert.True(_vm.HandleKey(MpvControlsViewModel.Keys.Z));
		Assert.Equal("Subtitles 0.1 s earlier", _vm.ToastText);

		Assert.Equal([0.1, 0.0, -0.1], Sent(Bridge.Commands.SetSubtitleDelay, "seconds"));
		Assert.True(_vm.ToastVisible);
		Wait(1.4);
		Assert.True(_vm.ToastVisible);
		Wait(0.2);
		Assert.False(_vm.ToastVisible, "gone 1.5 s after the last change");
	}

	[Fact]
	public async Task Delay_is_the_items_own()
	{
		StartItem(Item);
		await _vm.SetSubtitleDelay(0.4);

		// the same item again (a restart for a track change) keeps it
		StartItem(Item.ToUpperInvariant());
		Assert.Equal(0.4, _vm.SubtitleDelay);

		StartItem(NextItem);
		Assert.Equal(0, _vm.SubtitleDelay);
		Assert.Equal([0.4, 0.0], Sent(Bridge.Commands.SetSubtitleDelay, "seconds"));

		// nothing to reset: nothing sent
		StartItem(Item);
		Assert.Equal([0.4, 0.0], Sent(Bridge.Commands.SetSubtitleDelay, "seconds"));
	}

	[Fact]
	public void The_volume_and_the_speed_indicator_take_each_others_place()
	{
		_vm.HandleKey(MpvControlsViewModel.Keys.CloseBracket);
		Assert.True(_vm.ToastVisible);
		Assert.True(_vm.AnythingVisible);
		_vm.HandleKey(0x26); // Up: the volume
		Assert.False(_vm.ToastVisible);
		Assert.True(_vm.VolumeIndicatorVisible);
		_vm.HandleKey(MpvControlsViewModel.Keys.X);
		Assert.True(_vm.ToastVisible);
		Assert.False(_vm.VolumeIndicatorVisible);
	}

	// -- the keys --------------------------------------------------------------------------------------------------------

	[Theory]
	[InlineData(MpvControlsViewModel.Keys.OpenBracket, MpvControlsAction.SpeedDown)]
	[InlineData(MpvControlsViewModel.Keys.CloseBracket, MpvControlsAction.SpeedUp)]
	[InlineData(MpvControlsViewModel.Keys.Back, MpvControlsAction.SpeedNormal)]
	[InlineData(MpvControlsViewModel.Keys.Z, MpvControlsAction.SubtitleEarlier)]
	[InlineData(MpvControlsViewModel.Keys.X, MpvControlsAction.SubtitleLater)]
	public void Keys_are_plain_presses_only(int key, MpvControlsAction action)
	{
		Assert.Equal(action, MpvControlsViewModel.ActionFor(key, false, false, false));
		Assert.Null(MpvControlsViewModel.ActionFor(key, true, false, false));
		Assert.Null(MpvControlsViewModel.ActionFor(key, false, true, false));
		Assert.Null(MpvControlsViewModel.ActionFor(key, false, false, true));
		Assert.Null(FluentFin.Core.Shortcuts.AppShortcuts.For(key, false, false, false, inTextInput: false));
	}
}
