using System.Text.Json.Nodes;
using FluentFin.Core.Mpv;

namespace FluentFin.Core.Tests;

/// <summary>
/// fluentfin-mpv: FluentFin's own player controls over mpv (MpvControlsViewModel). The clock only moves when a test moves
/// it, and properties that a timer or the heartbeat could re-run are asserted over several steps (no walking).
/// </summary>
public sealed class MpvControlsViewModelTests
{
	private DateTimeOffset _now = new(2026, 11, 15, 20, 0, 0, TimeSpan.Zero);
	private readonly List<(string Command, JsonObject? Args)> _sent = [];
	private readonly MpvControlsViewModel _vm;

	public MpvControlsViewModelTests()
	{
		_vm = new MpvControlsViewModel((command, args) =>
		{
			_sent.Add((command, args?.DeepClone() as JsonObject));
			return Task.CompletedTask;
		}, () => _now);
	}

	private void Wait(double seconds)
	{
		_now += TimeSpan.FromSeconds(seconds);
		_vm.Tick();
	}

	private static long Ticks(double seconds) => TimeSpan.FromSeconds(seconds).Ticks;

	private void Playing(double positionSeconds = 0, double durationSeconds = 600, int index = 0, int count = 1, string state = Bridge.States.Playing)
	{
		_vm.OnState(new BridgeState
		{
			State = state,
			Volume = 80,
			DurationTicks = Ticks(durationSeconds),
			Queue = new BridgeQueue { ItemIds = [.. Enumerable.Range(0, count).Select(n => n.ToString())], Index = index },
		});
		_vm.OnPosition(new BridgePosition(Ticks(positionSeconds), Ticks(durationSeconds)));
	}

	private List<string> Commands() => [.. _sent.Select(x => x.Command)];

	[Fact]
	public void Controls_hide_after_three_seconds_without_input_and_come_back_on_activity()
	{
		Playing();
		_vm.NotifyActivity();
		for (var round = 0; round < 3; round++)
		{
			Wait(2.9);
			Assert.True(_vm.ControlsVisible, $"round {round}: still within 3 s");
			Assert.False(_vm.CursorHidden);
			Wait(0.2);
			Assert.False(_vm.ControlsVisible, $"round {round}: hidden after 3 s");
			Assert.True(_vm.CursorHidden, "the pointer hides with them");
			Wait(10);
			Assert.False(_vm.ControlsVisible, "and stay hidden");
			_vm.NotifyActivity();
			Assert.True(_vm.ControlsVisible);
			Assert.False(_vm.CursorHidden);
		}
	}

	[Fact]
	public void Controls_stay_while_paused_a_menu_is_open_or_the_pointer_is_on_them()
	{
		Playing(state: Bridge.States.Paused);
		_vm.NotifyActivity();
		Wait(30);
		Assert.True(_vm.ControlsVisible, "paused");
		Assert.Equal("paused", _vm.HeldBy);

		Playing();
		_vm.MenuOpened(true);
		Wait(30);
		Assert.True(_vm.ControlsVisible, "a menu is open");
		_vm.MenuOpened(false);
		Wait(2);
		Assert.True(_vm.ControlsVisible, "closing the menu counts as input");
		Wait(1.5);
		Assert.False(_vm.ControlsVisible);

		_vm.NotifyActivity();
		_vm.PointerOverControls = true;
		Wait(30);
		Assert.True(_vm.ControlsVisible, "the pointer is on them");
		_vm.PointerOverControls = false;
		Wait(0.1);
		Assert.False(_vm.ControlsVisible, "off them, 3 s since the last input have passed");
	}

	[Fact]
	public void Position_moves_on_between_reports_while_playing_and_not_while_paused()
	{
		Playing(positionSeconds: 100);
		Wait(0.25);
		Wait(0.25);
		Assert.Equal(Ticks(100.5), _vm.PositionTicks);
		Assert.Equal("1:40", _vm.ElapsedText);
		Assert.Equal("-8:19", _vm.RemainingText);

		// a report resets the base; it never runs ahead of it
		_vm.OnPosition(new BridgePosition(Ticks(100.4), Ticks(600)));
		Assert.Equal(Ticks(100.4), _vm.PositionTicks);

		Playing(positionSeconds: 200, state: Bridge.States.Paused);
		for (var i = 0; i < 5; i++)
		{
			Wait(1);
			Assert.Equal(Ticks(200), _vm.PositionTicks);
		}

		// never past the end
		Playing(positionSeconds: 599.9);
		Wait(5);
		Assert.Equal(Ticks(600), _vm.PositionTicks);
	}

	[Fact]
	public void A_seek_shows_where_it_goes_until_the_backend_gets_there()
	{
		Playing(positionSeconds: 100);
		_vm.SeekBy(30);
		Assert.Equal((Bridge.Commands.SeekRelative, 30.0), (_sent[^1].Command, (double)_sent[^1].Args!["seconds"]!));
		Assert.Equal(Ticks(130), _vm.PositionTicks);

		// reports from before the seek do not pull the bar back
		_vm.OnPosition(new BridgePosition(Ticks(100.5), Ticks(600)));
		Wait(0.5);
		Assert.Equal(Ticks(130), _vm.PositionTicks);

		// the backend arrives: the bar follows the backend again
		_vm.OnPosition(new BridgePosition(Ticks(130.2), Ticks(600)));
		Assert.Equal(Ticks(130.2), _vm.PositionTicks);

		// a seek the backend never reports gives up after SeekSettle
		_vm.SeekBy(-10);
		Wait(MpvControlsViewModel.SeekSettle.TotalSeconds + 0.1);
		Assert.NotEqual(Ticks(120.2), _vm.PositionTicks);
	}

	[Fact]
	public void Dragging_the_seek_bar_sends_one_seek_when_let_go()
	{
		Playing(positionSeconds: 10);
		_vm.BeginScrub();
		foreach (var seconds in new[] { 20, 40, 80, 120 })
		{
			_vm.Scrub(Ticks(seconds));
			_vm.OnPosition(new BridgePosition(Ticks(11), Ticks(600)));
			Wait(0.25);
			Assert.Equal(Ticks(seconds), _vm.PositionTicks);
		}

		Assert.DoesNotContain(Bridge.Commands.Seek, Commands());
		Wait(10);
		Assert.True(_vm.ControlsVisible, "never hidden while seeking");
		_vm.SeekTo(Ticks(120));
		Assert.Equal([Bridge.Commands.Seek], Commands());
		Assert.Equal(Ticks(120), (long)_sent[^1].Args!["positionTicks"]!);
		_vm.SeekTo(Ticks(10_000));
		Assert.Equal(Ticks(600), (long)_sent[^1].Args!["positionTicks"]!);
	}

	[Fact]
	public void Subtitles_are_lifted_only_while_the_bar_shows_and_each_change_is_sent_once()
	{
		Playing();
		_vm.SendControls();
		_vm.SetBottomBarFraction(0.18);
		_vm.SetBottomBarFraction(0.18);
		for (var round = 0; round < 3; round++)
		{
			Wait(3.1);
			_vm.NotifyActivity();
		}

		var insets = _sent.Where(x => x.Command == Bridge.Commands.SetControls).Select(x => ((string)x.Args!["controls"]!, (double)x.Args!["subtitleInset"]!)).ToList();
		// first what the page says when it starts (the bar not measured yet), then every show and hide, once each
		Assert.Equal([("ui", 0.0), ("ui", 0.18), ("ui", 0.0), ("ui", 0.18), ("ui", 0.0), ("ui", 0.18), ("ui", 0.0), ("ui", 0.18)], insets);
	}

	[Fact]
	public void A_state_sent_before_a_volume_change_landed_does_not_put_the_old_volume_back()
	{
		Playing();
		_ = _vm.Wheel(-MpvControlsViewModel.WheelNotch);
		_ = _vm.Wheel(-MpvControlsViewModel.WheelNotch);
		Assert.Equal(70, _vm.Volume);

		// the backend's echo of the first notch, sent before the second landed (measured in tools\ui-harness\fluentfin-controls)
		_vm.OnState(new BridgeState { State = Bridge.States.Playing, Volume = 75 });
		Assert.Equal(70, _vm.Volume);
		Assert.Equal("70 %", _vm.VolumeText);

		Wait(2);
		_vm.OnState(new BridgeState { State = Bridge.States.Playing, Volume = 70 });
		Assert.Equal(70, _vm.Volume);

		// later, a change made elsewhere (a remote control) is shown
		Wait(2);
		_vm.OnState(new BridgeState { State = Bridge.States.Playing, Volume = 40, Muted = true });
		Assert.Equal(40, _vm.Volume);
		Assert.True(_vm.IsMuted);
	}

	[Fact]
	public void The_wheel_and_the_keys_use_the_volume_step_and_the_wheel_can_be_turned_off()
	{
		Playing();
		_vm.Step = 10;
		_ = _vm.Wheel(-MpvControlsViewModel.WheelNotch);
		Assert.Equal(70, _vm.Volume);
		_vm.HandleKey(MpvControlsViewModel.Keys.Up);
		Assert.Equal(80, _vm.Volume);

		_vm.WheelChangesVolume = false;
		Wait(5);
		var sent = _sent.Count;
		_ = _vm.Wheel(-MpvControlsViewModel.WheelNotch);
		Assert.Equal(80, _vm.Volume);
		Assert.Equal(sent, _sent.Count);
		Assert.False(_vm.VolumeIndicatorVisible, "nothing shows either");

		_vm.Step = 0;
		Assert.Equal(1, _vm.Step);
		_vm.Step = 400;
		Assert.Equal(20, _vm.Step);
	}

	[Fact]
	public void Nothing_is_sent_about_the_bar_before_the_page_announces_its_controls()
	{
		// the view measures its bar as soon as it is laid out, before any backend runs
		_vm.SetBottomBarFraction(0.18);
		Wait(3.1);
		_vm.NotifyActivity();
		Assert.DoesNotContain(_sent, x => x.Command == Bridge.Commands.SetControls);

		_vm.SendControls();
		Wait(3.1);
		var insets = _sent.Where(x => x.Command == Bridge.Commands.SetControls).Select(x => (double)x.Args!["subtitleInset"]!).ToList();
		Assert.Equal([0.18, 0.0], insets);
	}

	[Fact]
	public void Keys_map_like_FluentFins_player_and_the_web_conventions()
	{
		var k = MpvControlsViewModel.Keys.K;
		Assert.Equal(MpvControlsAction.TogglePause, MpvControlsViewModel.ActionFor(MpvControlsViewModel.Keys.Space, false, false, false));
		Assert.Equal(MpvControlsAction.TogglePause, MpvControlsViewModel.ActionFor(k, false, false, false));
		Assert.Equal(MpvControlsAction.SeekBack, MpvControlsViewModel.ActionFor(MpvControlsViewModel.Keys.Left, false, false, false));
		Assert.Equal(MpvControlsAction.SeekForwardLong, MpvControlsViewModel.ActionFor(MpvControlsViewModel.Keys.Right, true, false, false));
		Assert.Equal(MpvControlsAction.VolumeUp, MpvControlsViewModel.ActionFor(MpvControlsViewModel.Keys.Up, false, false, false));
		Assert.Equal(MpvControlsAction.ToggleMute, MpvControlsViewModel.ActionFor(MpvControlsViewModel.Keys.M, false, false, false));
		Assert.Equal(MpvControlsAction.ToggleFullScreen, MpvControlsViewModel.ActionFor(MpvControlsViewModel.Keys.F, false, false, false));
		Assert.Equal(MpvControlsAction.Next, MpvControlsViewModel.ActionFor(MpvControlsViewModel.Keys.N, false, true, false));
		Assert.Null(MpvControlsViewModel.ActionFor(MpvControlsViewModel.Keys.N, false, false, false));
		Assert.Null(MpvControlsViewModel.ActionFor(MpvControlsViewModel.Keys.Left, false, false, true));
		Assert.Null(MpvControlsViewModel.ActionFor(MpvControlsViewModel.Keys.F, true, false, false));
		Assert.Null(MpvControlsViewModel.ActionFor(0x41, false, false, false));

		Playing(positionSeconds: 50);
		Assert.True(_vm.HandleKey(MpvControlsViewModel.Keys.Space));
		Assert.True(_vm.HandleKey(MpvControlsViewModel.Keys.Left));
		Assert.True(_vm.HandleKey(MpvControlsViewModel.Keys.Down));
		Assert.False(_vm.HandleKey(0x41));
		Assert.Equal([Bridge.Commands.TogglePause, Bridge.Commands.SeekRelative, Bridge.Commands.SetVolume], Commands());
		Assert.Equal(75, (int)_sent[^1].Args!["volume"]!);
	}

	[Fact]
	public void A_key_shows_the_controls()
	{
		Playing();
		Wait(5);
		Assert.False(_vm.ControlsVisible);
		_vm.HandleKey(MpvControlsViewModel.Keys.Right);
		Assert.True(_vm.ControlsVisible);
	}

	[Fact]
	public void Play_pause_is_shown_at_once_and_corrected_by_the_backend()
	{
		Playing();
		_vm.TogglePause();
		Assert.True(_vm.IsPaused);
		Playing(state: Bridge.States.Playing);
		Assert.False(_vm.IsPaused, "the backend says what is true");
	}

	[Fact]
	public void Next_and_previous_follow_the_queue()
	{
		Playing(index: 0, count: 3);
		Assert.True(_vm.CanNext);
		Assert.False(_vm.CanPrevious);
		_vm.Previous();
		Assert.Empty(Commands());
		Playing(index: 2, count: 3);
		Assert.False(_vm.CanNext);
		Assert.True(_vm.CanPrevious);
		_vm.Next();
		_vm.Previous();
		Assert.Equal([Bridge.Commands.Previous], Commands());
	}

	[Fact]
	public async Task Volume_dragging_sends_one_at_a_time_and_the_latest_last()
	{
		var answers = new List<TaskCompletionSource>();
		var sent = new List<int>();
		var vm = new MpvControlsViewModel((command, args) =>
		{
			if (command != Bridge.Commands.SetVolume)
			{
				return Task.CompletedTask;
			}

			sent.Add((int)args!["volume"]!);
			var answer = new TaskCompletionSource();
			answers.Add(answer);
			return answer.Task;
		}, () => _now);

		var first = vm.SetVolume(10);
		_ = vm.SetVolume(20);
		_ = vm.SetVolume(30);
		_ = vm.SetVolume(40);
		Assert.Equal([10], sent);
		answers[0].SetResult();
		for (var i = 0; i < 100 && sent.Count < 2; i++)
		{
			await Task.Delay(10);
		}

		Assert.Equal([10, 40], sent);
		answers[1].SetResult();
		await first;
		Assert.Equal([10, 40], sent);
		Assert.Equal(40, vm.Volume);
	}

	[Fact]
	public void Moving_the_volume_unmutes()
	{
		Playing();
		_vm.ToggleMute();
		Assert.True(_vm.IsMuted);
		_vm.SetVolume(30);
		Assert.False(_vm.IsMuted);
		Assert.Equal([Bridge.Commands.SetMute, Bridge.Commands.SetMute, Bridge.Commands.SetVolume], Commands());
		Assert.False((bool)_sent[1].Args!["muted"]!);
	}

	[Fact]
	public void The_wheel_changes_the_volume_in_steps_of_five_within_0_to_100_and_never_seeks()
	{
		Playing(); // volume 80
		var volumes = new List<int>();
		for (var notch = 0; notch < 6; notch++)
		{
			_vm.Wheel(MpvControlsViewModel.WheelNotch);
			volumes.Add(_vm.Volume);
		}

		Assert.Equal([85, 90, 95, 100, 100, 100], volumes);
		Assert.Equal([85, 90, 95, 100], _sent.Where(x => x.Command == Bridge.Commands.SetVolume).Select(x => (int)x.Args!["volume"]!));

		_vm.Wheel(-3 * MpvControlsViewModel.WheelNotch);
		Assert.Equal(85, _vm.Volume);
		for (var notch = 0; notch < 25; notch++)
		{
			_vm.Wheel(-MpvControlsViewModel.WheelNotch);
		}

		Assert.Equal(0, _vm.Volume);
		Assert.Equal(0, (int)_sent.Last(x => x.Command == Bridge.Commands.SetVolume).Args!["volume"]!);
		Assert.DoesNotContain(_sent, x => x.Command is Bridge.Commands.Seek or Bridge.Commands.SeekRelative);
	}

	[Fact]
	public void A_smooth_wheel_adds_up_to_a_notch()
	{
		Playing();
		_vm.Wheel(40);
		_vm.Wheel(40);
		Assert.Equal(80, _vm.Volume);
		Assert.Empty(Commands());
		_vm.Wheel(40);
		Assert.Equal(85, _vm.Volume);
		_vm.Wheel(-60);
		_vm.Wheel(-60);
		Assert.Equal(80, _vm.Volume);
	}

	[Fact]
	public void The_wheel_shows_a_brief_indicator_even_with_the_bars_hidden_and_does_not_wake_them()
	{
		Playing();
		Wait(4);
		Assert.False(_vm.ControlsVisible);
		Assert.False(_vm.AnythingVisible);
		for (var round = 0; round < 3; round++)
		{
			_vm.Wheel(MpvControlsViewModel.WheelNotch);
			Assert.True(_vm.VolumeIndicatorVisible);
			Assert.True(_vm.AnythingVisible, "the window the controls draw in shows for it");
			Assert.False(_vm.ControlsVisible, "the bars stay hidden");
			Wait(1.0);
			Assert.True(_vm.VolumeIndicatorVisible);
			Wait(0.3);
			Assert.False(_vm.VolumeIndicatorVisible, $"round {round}: gone 1.2 s after the last notch");
			Assert.False(_vm.AnythingVisible);
		}

		Assert.Equal("95 %", _vm.VolumeText);
		_vm.ToggleMute();
		Assert.Equal("Muted", _vm.VolumeText);
	}

	[Theory]
	[InlineData("Episode", "Pilot", "Show", 6, 4, "Show", "S6E4 · Pilot")]
	[InlineData("Episode", "Show", "Show", 6, 4, "Show", "S6E4")]
	[InlineData("Episode", "", "Show", 6, 4, "Show", "S6E4")]
	[InlineData("Episode", "Pilot", "Show", null, 4, "Show", "E4 · Pilot")]
	[InlineData("Episode", "Pilot", "Show", null, null, "Show", "Pilot")]
	[InlineData("Episode", "Pilot", null, 1, 1, "Pilot", "")]
	[InlineData("Movie", "A Film", null, null, null, "A Film", "")]
	public void Title_line(string type, string name, string? series, int? season, int? episode, string title, string subtitle)
	{
		Assert.Equal((title, subtitle), MpvControlsViewModel.FormatTitle(new BridgeItem("1", name, type, series, season, episode)));
	}

	[Fact]
	public void Times()
	{
		Assert.Equal("0:00", MpvControlsViewModel.FormatTime(TimeSpan.Zero));
		Assert.Equal("4:05", MpvControlsViewModel.FormatTime(TimeSpan.FromSeconds(245)));
		Assert.Equal("1:02:03", MpvControlsViewModel.FormatTime(TimeSpan.FromSeconds(3723)));
		Assert.Equal("0:00", MpvControlsViewModel.FormatTime(TimeSpan.FromSeconds(-3)));
	}

	[Fact]
	public void A_new_item_starts_its_bar_at_zero_and_shows_the_controls()
	{
		Playing(positionSeconds: 500);
		Wait(5);
		_vm.OnItem(new BridgeItem("2", "Next one", "Episode", "Show", 1, 2, Ticks(1200)));
		Assert.Equal(0, _vm.PositionTicks);
		Assert.Equal(Ticks(1200), _vm.DurationTicks);
		Assert.True(_vm.ControlsVisible);
		Assert.Equal(("Show", "S1E2 · Next one"), (_vm.Title, _vm.Subtitle));
	}
}
