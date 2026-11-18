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

	/// <summary>
	/// Seaglass 1.1.3: a smooth wheel or a touchpad that turns back drops the part of a notch it had made the other way; in
	/// 1.1.2 the first part of the reversal only undid it (+100 then -120 changed nothing).
	/// </summary>
	[Fact]
	public void A_wheel_that_turns_back_drops_the_part_of_a_notch_left_over()
	{
		Playing(); // volume 80
		_vm.Wheel(100);
		Assert.Equal(80, _vm.Volume);
		_vm.Wheel(-MpvControlsViewModel.WheelNotch);
		Assert.Equal(75, _vm.Volume);

		_vm.Wheel(-100);
		_vm.Wheel(30);
		_vm.Wheel(90);
		Assert.Equal(80, _vm.Volume);
	}

	/// <summary>Seaglass 1.1.3: part of a notch from a while ago does not make the next small turn a whole notch.</summary>
	[Fact]
	public void A_part_of_a_notch_is_forgotten_after_a_pause()
	{
		Playing(); // volume 80
		_vm.Wheel(100);
		_now += MpvControlsViewModel.WheelRemainderFor;
		_vm.Wheel(40);
		Assert.Equal(80, _vm.Volume);
		Assert.Empty(Commands());

		// within the pause it still adds up
		_vm.Wheel(40);
		_now += TimeSpan.FromSeconds(1);
		_vm.Wheel(40);
		Assert.Equal(85, _vm.Volume);
	}

	/// <summary>
	/// Seaglass 1.1.3, the "jumpy" wheel: a fast spin gives one step per notch, one command per notch, and the level the
	/// indicator's fill is sent to never goes back, whatever the backend echoes on the way (each notch's own state arriving
	/// late, the last one after the settle time) and also from a mute.
	/// </summary>
	[Fact]
	public void A_fast_spin_moves_the_fill_one_step_per_notch_and_never_back()
	{
		Playing(); // volume 80
		var fractions = new List<double>();
		_vm.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(MpvControlsViewModel.VolumeFraction))
			{
				fractions.Add(_vm.VolumeFraction);
			}
		};

		var echoes = new Queue<int>();
		for (var notch = 0; notch < 12; notch++)
		{
			_vm.Wheel(-MpvControlsViewModel.WheelNotch);
			echoes.Enqueue(_vm.Volume);
			_now += TimeSpan.FromMilliseconds(30);
			if (notch >= 2)
			{
				// the backend's state for the notch two before this one
				_vm.OnState(new BridgeState { State = Bridge.States.Playing, Volume = echoes.Dequeue() });
			}
		}

		// the last echoes come after the settle time: they are the final level
		_now += MpvControlsViewModel.AudioSettle;
		while (echoes.Count > 1)
		{
			echoes.Dequeue();
		}

		_vm.OnState(new BridgeState { State = Bridge.States.Playing, Volume = echoes.Dequeue() });
		Assert.Equal(20, _vm.Volume);
		Assert.Equal([.. Enumerable.Range(1, 12).Select(n => Math.Round((80 - 5 * n) / 100.0, 6))], fractions.Select(f => Math.Round(f, 6)));
		Assert.Equal([.. Enumerable.Range(1, 12).Select(n => 80 - 5 * n)], _sent.Where(x => x.Command == Bridge.Commands.SetVolume).Select(x => (int)x.Args!["volume"]!));

		// from a mute, up: nothing, then each level once, never below the one before
		_ = _vm.ToggleMute();
		fractions.Clear();
		_now += TimeSpan.FromSeconds(3);
		for (var notch = 0; notch < 3; notch++)
		{
			_vm.Wheel(MpvControlsViewModel.WheelNotch);
		}

		for (var i = 1; i < fractions.Count; i++)
		{
			Assert.True(fractions[i] >= fractions[i - 1], $"step {i}: {fractions[i - 1]} -> {fractions[i]}");
		}

		Assert.Equal(0.35, fractions[^1], 3);
	}

	private int Toggles() => _sent.Count(x => x.Command == Bridge.Commands.TogglePause);

	private List<string> PlayCommands() => [.. _sent.Select(x => x.Command).Where(x => x is Bridge.Commands.TogglePause or Bridge.Commands.Pause or Bridge.Commands.Unpause)];

	/// <summary>1.2.0 (the interaction audit, item 8): a click on the video plays or pauses at once (1.1.3 waited the double-click time).</summary>
	[Fact]
	public void A_click_on_the_video_plays_or_pauses_at_once()
	{
		Playing();
		_vm.DoubleClickTime = TimeSpan.FromMilliseconds(400);
		_ = _vm.VideoClicked();
		Assert.Equal(1, Toggles());
		Assert.True(_vm.IsPaused);
		Assert.True(_vm.ControlsVisible, "the controls show at once");
		_now += TimeSpan.FromSeconds(2);
		_ = _vm.VideoClicked();
		Assert.Equal(2, Toggles());
		Assert.False(_vm.IsPaused);
	}

	/// <summary>
	/// 1.2.0: a double click is full screen, and the play state goes back to what it was before its first click, with an
	/// absolute command (a state the backend sent before the click's toggle landed cannot turn it the wrong way).
	/// </summary>
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void A_double_click_is_full_screen_and_puts_the_play_state_back(bool pausedBefore)
	{
		Playing(state: pausedBefore ? Bridge.States.Paused : Bridge.States.Playing);
		var fullScreen = new List<bool>();
		_vm.SetFullScreen = fullScreen.Add;
		_ = _vm.VideoClicked();
		Assert.Equal(!pausedBefore, _vm.IsPaused);
		// the backend's state from before the toggle landed
		_vm.OnState(new BridgeState { State = pausedBefore ? Bridge.States.Paused : Bridge.States.Playing, Volume = 80 });
		_now += TimeSpan.FromMilliseconds(250);
		_vm.VideoDoubleClicked();

		Assert.Equal([true], fullScreen);
		Assert.Equal(pausedBefore, _vm.IsPaused);
		Assert.Equal([Bridge.Commands.TogglePause, pausedBefore ? Bridge.Commands.Pause : Bridge.Commands.Unpause], PlayCommands());
	}

	/// <summary>A double click long after the click, or after another play or pause in between, puts nothing back.</summary>
	[Theory]
	[InlineData("late")]
	[InlineData("space")]
	[InlineData("button")]
	[InlineData("new item")]
	[InlineData("page left")]
	public void A_double_click_puts_nothing_back_after_another_play_or_pause(string what)
	{
		Playing();
		_vm.SetFullScreen = _ => { };
		_ = _vm.VideoClicked();
		switch (what)
		{
			case "late": _now += TimeSpan.FromSeconds(2); break;
			case "space": _vm.HandleKey(MpvControlsViewModel.Keys.Space); break;
			case "button": _ = _vm.TogglePause(); break;
			case "new item": _vm.OnItem(new BridgeItem("next", "Next", "Episode")); break;
			case "page left": _vm.CancelClick(); break;
		}

		var before = PlayCommands().Count;
		_vm.VideoDoubleClicked();
		Assert.Equal(before, PlayCommands().Count);
	}

	[Fact]
	public void The_volume_keys_and_mute_show_the_indicator_too_and_it_fills_to_the_volume()
	{
		Playing();
		Wait(4);
		Assert.False(_vm.VolumeIndicatorVisible);
		_vm.HandleKey(MpvControlsViewModel.Keys.Down);
		Assert.True(_vm.VolumeIndicatorVisible, "the volume key shows it");
		Assert.Equal(0.75, _vm.VolumeFraction, 3);
		Wait(2);
		Assert.False(_vm.VolumeIndicatorVisible);
		_ = _vm.ToggleMute();
		Assert.True(_vm.VolumeIndicatorVisible, "mute shows it");
		Assert.Equal("Muted", _vm.VolumeText);
		Assert.Equal(0, _vm.VolumeFraction);
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
			Wait(1.3);
			Assert.True(_vm.VolumeIndicatorVisible);
			Wait(0.3);
			Assert.False(_vm.VolumeIndicatorVisible, $"round {round}: gone 1.5 s after the last notch");
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

	[Fact]
	public void Controls_stay_while_the_keyboard_is_on_them_and_hide_once_it_left()
	{
		Playing();
		_vm.NotifyActivity();
		_vm.KeyboardInControls = true;
		for (var round = 0; round < 3; round++)
		{
			Wait(10);
			Assert.True(_vm.ControlsVisible, $"round {round}: Tab is on a button of the bar");
			Assert.Equal("the keyboard is on them", _vm.HeldBy);
		}

		_vm.KeyboardInControls = false;
		_vm.NotifyActivity();
		Wait(2.9);
		Assert.True(_vm.ControlsVisible);
		Wait(0.2);
		Assert.False(_vm.ControlsVisible, "gone 3 s after the keyboard left them");
	}

	/// <summary>A volume above 100 (an mpv.conf's <c>volume=130</c>): the protocol sets 0-100, so a step up keeps it, never lowers it.</summary>
	[Fact]
	public void A_step_up_never_lowers_a_volume_above_100()
	{
		_vm.OnState(new BridgeState { State = Bridge.States.Playing, Volume = 130 });
		for (var round = 0; round < 3; round++)
		{
			_vm.Wheel(MpvControlsViewModel.WheelNotch);
			Assert.Equal(130, _vm.Volume);
			Assert.True(_vm.HandleKey(MpvControlsViewModel.Keys.Up));
			Assert.Equal(130, _vm.Volume);
		}

		Assert.DoesNotContain(_sent, x => x.Command == Bridge.Commands.SetVolume);

		// down still lowers it (into the range FluentFin sets)
		_vm.Wheel(-MpvControlsViewModel.WheelNotch);
		Assert.Equal(100, _vm.Volume);
		Assert.Equal(100, (int)_sent.Single(x => x.Command == Bridge.Commands.SetVolume).Args!["volume"]!);
	}

	[Fact]
	public void A_step_up_above_100_while_muted_only_unmutes()
	{
		_vm.OnState(new BridgeState { State = Bridge.States.Playing, Volume = 130, Muted = true });
		_vm.Wheel(MpvControlsViewModel.WheelNotch);
		Assert.False(_vm.IsMuted);
		Assert.Equal(130, _vm.Volume);
		Assert.Equal([Bridge.Commands.SetMute], Commands());
		Assert.False((bool)_sent[0].Args!["muted"]!);
	}

	/// <summary>
	/// Wheel notches while muted: the first unmutes and its volume waits behind the unmute; a later notch must not be
	/// overtaken by the first one's older volume (the last <c>set_volume</c> is the newest).
	/// </summary>
	[Fact]
	public async Task Wheel_notches_while_muted_send_the_newest_volume_last()
	{
		var answers = new List<TaskCompletionSource>();
		var sent = new List<(string Command, JsonObject? Args)>();
		var vm = new MpvControlsViewModel((command, args) =>
		{
			sent.Add((command, args?.DeepClone() as JsonObject));
			var answer = new TaskCompletionSource();
			answers.Add(answer);
			return answer.Task;
		}, () => _now);
		vm.OnState(new BridgeState { State = Bridge.States.Playing, Volume = 80, Muted = true });

		var first = vm.Wheel(MpvControlsViewModel.WheelNotch);
		_ = vm.Wheel(MpvControlsViewModel.WheelNotch);
		_ = vm.Wheel(MpvControlsViewModel.WheelNotch);
		Assert.Equal(95, vm.Volume);

		// the backend answers each command as it comes
		for (var i = 0; i < 40; i++)
		{
			foreach (var answer in answers.ToArray())
			{
				answer.TrySetResult();
			}

			await Task.Delay(10);
		}

		await first;
		var volumes = sent.Where(x => x.Command == Bridge.Commands.SetVolume).Select(x => (int)x.Args!["volume"]!).ToList();
		Assert.Equal(Bridge.Commands.SetMute, sent[0].Command);
		Assert.NotEmpty(volumes);
		Assert.Equal(95, volumes[^1]);
		Assert.Equal(Bridge.Commands.SetVolume, sent[^1].Command);
	}

	// S (skip the segment on offer): MpvControlsSkipTests
}
