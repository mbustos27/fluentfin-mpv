using System.Text.Json.Nodes;
using FluentFin.Core.Mpv;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.2.0 (UX review, prioritised fix 5): the Next pill shows only near the end of the item: from the credits when
/// a segment marks them, else the last 30 to 40 s (Jellyfin web's lead), and Previous follows the queue and the <c>next</c>
/// event. In 1.1.0 it showed with the bars from the first seconds.
/// </summary>
public sealed class PlayerNextPromptTests
{
	private const string Item = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee1";
	private const string NextItem = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee2";

	private DateTimeOffset _now = new(2026, 11, 17, 21, 0, 0, TimeSpan.Zero);
	private readonly List<(string Command, JsonObject? Args)> _sent = [];
	private readonly MpvControlsViewModel _vm;

	public PlayerNextPromptTests()
	{
		_vm = new MpvControlsViewModel((command, args) =>
		{
			_sent.Add((command, args?.DeepClone() as JsonObject));
			return Task.CompletedTask;
		}, () => _now);
	}

	private List<string> Commands() => [.. _sent.Select(x => x.Command)];

	private static long Ticks(double seconds) => TimeSpan.FromSeconds(seconds).Ticks;

	private static long Minutes(double minutes) => TimeSpan.FromMinutes(minutes).Ticks;

	private void Wait(double seconds)
	{
		_now += TimeSpan.FromSeconds(seconds);
		_vm.Tick();
	}

	/// <summary>An episode of <paramref name="durationSeconds"/> playing at <paramref name="positionSeconds"/>, with a next one.</summary>
	private void PlayingEpisode(double positionSeconds, double durationSeconds = 1320, bool hasNext = true, string type = "Episode", int queueIndex = 0)
	{
		_vm.OnEvent(new BridgeEvent(Bridge.Events.Item, JsonNode.Parse($$"""
			{"itemId": "{{Item}}", "name": "Pilot", "type": "{{type}}", "seriesName": "Spike Show", "seasonNumber": 1, "episodeNumber": 1, "runTimeTicks": {{Ticks(durationSeconds)}}}
			""")));
		_vm.OnState(new BridgeState
		{
			State = Bridge.States.Playing,
			ItemId = Item,
			DurationTicks = Ticks(durationSeconds),
			Queue = new BridgeQueue { ItemIds = [Item, NextItem], Index = queueIndex },
		});
		_vm.OnEvent(new BridgeEvent(Bridge.Events.Next, JsonNode.Parse(hasNext
			? $$"""{"itemId": "{{Item}}", "nextItemId": "{{NextItem}}", "autoPlay": true}"""
			: $$"""{"itemId": "{{Item}}", "autoPlay": false}""")));
		_vm.OnPosition(new BridgePosition(Ticks(positionSeconds), Ticks(durationSeconds)));
	}

	private void Credits(double startSeconds, double endSeconds, bool offered = false) =>
		_vm.OnEvent(new BridgeEvent(Bridge.Events.Segments, JsonNode.Parse($$"""
			{"itemId": "{{Item}}",
			 "segments": [{"type": "Intro", "startTicks": {{Ticks(30)}}, "endTicks": {{Ticks(90)}}, "action": "ask"},
			              {"type": "Outro", "startTicks": {{Ticks(startSeconds)}}, "endTicks": {{Ticks(endSeconds)}}, "action": "ask", "source": "chapters"}],
			 "current": {{(offered ? "1" : "null")}}}
			""")));

	/// <summary>Plays on for <paramref name="seconds"/>: the heartbeat in <paramref name="steps"/>, each followed by the backend's position report.</summary>
	private void Wait(double seconds, int steps)
	{
		for (var i = 0; i < steps; i++)
		{
			Wait(seconds / steps);
			_vm.OnPosition(new BridgePosition(_vm.PositionTicks, _vm.DurationTicks));
		}
	}

	// -- the countdown (1.2.0, the interaction audit, item 7) -------------------------------------------------------------

	[Fact]
	public void Over_the_credits_the_prompt_counts_down_ten_seconds_then_plays_the_next_once()
	{
		PlayingEpisode(1229);
		Credits(1230, 1320);
		Assert.False(_vm.IsCountingDown);
		Wait(1);
		Assert.True(_vm.IsNextPromptVisible);
		Assert.True(_vm.IsCountingDown);
		Assert.Equal(10, _vm.NextCountdown);
		Assert.Equal("Next episode in 10", _vm.NextPromptText);
		Wait(4, 16);
		Assert.Equal(6, _vm.NextCountdown);
		Assert.InRange(_vm.NextCountdownProgress, 0.39, 0.41);
		Assert.DoesNotContain(Bridge.Commands.Next, Commands());
		Wait(6, 24);
		Wait(3, 12);
		Assert.Equal([Bridge.Commands.Next], Commands().Where(x => x == Bridge.Commands.Next));
	}

	[Fact]
	public void Over_the_credits_Skip_credits_gives_way_to_the_Next_prompt()
	{
		PlayingEpisode(1231);
		Credits(1230, 1320, offered: true);
		Assert.True(_vm.IsNextPromptVisible);
		Assert.False(_vm.IsSkipVisible, "one button over the credits");

		// the last episode: no Next prompt, so Skip credits stays
		PlayingEpisode(1231, hasNext: false);
		Credits(1230, 1320, offered: true);
		Assert.False(_vm.IsNextPromptVisible);
		Assert.True(_vm.IsSkipVisible);
	}

	[Fact]
	public void Without_credits_the_countdown_is_the_last_ten_seconds_and_the_backend_plays_the_next()
	{
		PlayingEpisode(1295);
		Assert.True(_vm.IsNextPromptVisible, "25 s before the end");
		Assert.False(_vm.IsCountingDown, "the episode's own ending is not cut short");
		Wait(15, 60);
		Assert.True(_vm.IsCountingDown);
		Assert.Equal(10, _vm.NextCountdown);
		Wait(11, 44);
		Assert.Equal(0, _vm.NextCountdown);
		Assert.DoesNotContain(Bridge.Commands.Next, Commands());
	}

	[Fact]
	public void With_auto_play_off_the_prompt_is_a_plain_Next_episode()
	{
		_vm.AutoPlay = false;
		PlayingEpisode(1229);
		_vm.OnEvent(new BridgeEvent(Bridge.Events.Next, JsonNode.Parse($$"""{"itemId": "{{Item}}", "nextItemId": "{{NextItem}}", "autoPlay": false}""")));
		Credits(1230, 1320);
		Wait(20, 80);
		Assert.True(_vm.IsNextPromptVisible);
		Assert.False(_vm.IsCountingDown);
		Assert.Equal("Next episode", _vm.NextPromptText);
		Assert.DoesNotContain(Bridge.Commands.Next, Commands());
	}

	[Fact]
	public void Cancel_stops_the_countdown_and_the_backends_auto_play_until_another_item()
	{
		PlayingEpisode(1231);
		Credits(1230, 1320);
		Assert.True(_vm.IsCountingDown);

		_ = _vm.CancelNextCountdown();
		Assert.False(_vm.IsCountingDown);
		Assert.True(_vm.IsNextPromptVisible, "Next episode stays, to play it by hand");
		Wait(20, 80);
		Assert.DoesNotContain(Bridge.Commands.Next, Commands());
		Assert.Equal([false], _sent.Where(x => x.Command == Bridge.Commands.SetAutoPlay).Select(x => (bool)x.Args!["autoPlay"]!));

		// another item: the user's choice again
		PlayingEpisode(10);
		Assert.Equal([false, true], _sent.Where(x => x.Command == Bridge.Commands.SetAutoPlay).Select(x => (bool)x.Args!["autoPlay"]!));
	}

	/// <summary>
	/// Seen off-screen: paused at the credits for a few seconds, then played, the next episode started at once. Play read the
	/// position after the state flipped and counted the pause as played; and the countdown's own next goes only when the
	/// backend's last report (not a guess) is at its end.
	/// </summary>
	[Fact]
	public void A_play_after_a_long_pause_does_not_count_the_pause_as_played()
	{
		PlayingEpisode(1231);
		Credits(1230, 1320);
		_ = _vm.TogglePause();
		_now += TimeSpan.FromSeconds(30);
		_vm.Tick();
		_ = _vm.TogglePause();
		_vm.Tick();

		Assert.InRange(_vm.PositionTicks, Ticks(1230.9), Ticks(1231.1));
		Assert.Equal(9, _vm.NextCountdown);
		Assert.DoesNotContain(Bridge.Commands.Next, Commands());
	}

	[Fact]
	public void The_countdowns_next_waits_for_the_backends_report()
	{
		PlayingEpisode(1231);
		Credits(1230, 1320);
		// the time moves on between reports, but no report comes: no next on a guess
		for (var i = 0; i < 80; i++)
		{
			Wait(0.25);
		}

		Assert.Equal(0, _vm.NextCountdown);
		Assert.DoesNotContain(Bridge.Commands.Next, Commands());
		_vm.OnPosition(new BridgePosition(Ticks(1240.2), Ticks(1320)));
		_vm.Tick();
		Assert.Contains(Bridge.Commands.Next, Commands());
	}

	[Fact]
	public void Paused_the_countdown_waits()
	{
		PlayingEpisode(1231);
		Credits(1230, 1320);
		_vm.OnState(new BridgeState { State = Bridge.States.Paused, ItemId = Item, DurationTicks = Ticks(1320) });
		var left = _vm.NextCountdown;
		Wait(30, 120);
		Assert.Equal(left, _vm.NextCountdown);
		Assert.DoesNotContain(Bridge.Commands.Next, Commands());
	}

	[Fact]
	public void Countdown_span_is_the_credits_ten_seconds_or_the_last_ten()
	{
		Assert.Equal((Ticks(1230), Ticks(1240)), MpvControlsViewModel.CountdownSpan(Ticks(1320), [Ticks(1230)]));
		Assert.Equal((Ticks(1310), Ticks(1320)), MpvControlsViewModel.CountdownSpan(Ticks(1320)));
		// credits shorter than the countdown: to the end, where the backend plays the next
		Assert.Equal((Ticks(1315), Ticks(1320)), MpvControlsViewModel.CountdownSpan(Ticks(1320), [Ticks(1315)]));
	}

	[Theory]
	[InlineData(22, 30)]
	[InlineData(39.9, 30)]
	[InlineData(40, 35)]
	[InlineData(50, 40)]
	[InlineData(120, 40)]
	public void Lead_is_Jellyfin_webs_by_length(double minutes, double seconds) =>
		Assert.Equal(TimeSpan.FromSeconds(seconds), MpvControlsViewModel.NextPromptLead(Minutes(minutes)));

	[Fact]
	public void PromptAt_is_the_credits_in_the_second_half_else_the_lead_before_the_end()
	{
		var length = Minutes(22);
		Assert.Equal(length - Ticks(30), MpvControlsViewModel.NextPromptAt(length));
		Assert.Equal(Ticks(1250), MpvControlsViewModel.NextPromptAt(length, [Ticks(1250)]));
		// an "Outro" in the first half is not the end credits; the earliest in the second half is
		Assert.Equal(length - Ticks(30), MpvControlsViewModel.NextPromptAt(length, [Ticks(300)]));
		Assert.Equal(Ticks(1200), MpvControlsViewModel.NextPromptAt(length, [Ticks(300), Ticks(1270), Ticks(1200)]));
		// a short item: at most its last quarter
		Assert.Equal(Ticks(75), MpvControlsViewModel.NextPromptAt(Ticks(100)));
		// the length not known yet: never
		Assert.Equal(long.MaxValue, MpvControlsViewModel.NextPromptAt(0, [Ticks(10)]));
	}

	[Fact]
	public void Pill_is_away_for_most_of_the_episode_and_comes_for_the_last_thirty_seconds()
	{
		PlayingEpisode(2);
		for (var i = 0; i < 4; i++)
		{
			Wait(1);
			Assert.False(_vm.IsNextPromptVisible, "early in the episode, whatever the bars do");
		}

		_vm.OnPosition(new BridgePosition(Ticks(1289), Ticks(1320)));
		Assert.False(_vm.IsNextPromptVisible, "31 s before the end");
		Wait(1.5);
		Assert.True(_vm.IsNextPromptVisible, "30 s before the end, between two position events");

		// it stays while the bars hide, and it holds the window shown
		Wait(4);
		Assert.False(_vm.ControlsVisible);
		Assert.True(_vm.IsNextPromptVisible);
		Assert.True(_vm.AnythingVisible);

		// back before the lead: gone again
		_ = _vm.SeekBy(-60);
		Assert.False(_vm.IsNextPromptVisible);
	}

	[Fact]
	public void Pill_comes_with_the_credits_when_a_segment_marks_them()
	{
		PlayingEpisode(1100);
		Credits(1230, 1320);
		Assert.False(_vm.IsNextPromptVisible);
		_vm.OnPosition(new BridgePosition(Ticks(1230), Ticks(1320)));
		Assert.True(_vm.IsNextPromptVisible, "the credits start 90 s before the end");
	}

	[Fact]
	public void Credits_of_the_last_item_do_not_hold_over_to_the_next()
	{
		PlayingEpisode(1100);
		Credits(1100, 1320);
		Assert.True(_vm.IsNextPromptVisible);

		// the next item starts: its own segments come later, none so far
		PlayingEpisode(1100);
		Assert.False(_vm.IsNextPromptVisible);
	}

	[Fact]
	public void No_pill_without_a_next_item_or_for_a_film_alone()
	{
		PlayingEpisode(1310, hasNext: false);
		Assert.False(_vm.IsNextPromptVisible, "the last episode");

		var film = new MpvControlsViewModel((_, _) => Task.CompletedTask, () => _now);
		film.OnEvent(new BridgeEvent(Bridge.Events.Item, JsonNode.Parse("""{"itemId": "m", "name": "A film", "type": "Movie", "runTimeTicks": 72000000000}""")));
		film.OnState(new BridgeState { State = Bridge.States.Playing, ItemId = "m", DurationTicks = 72000000000, Queue = new BridgeQueue { ItemIds = ["m"], Index = 0 } });
		film.OnPosition(new BridgePosition(71900000000, 72000000000));
		Assert.False(film.IsNextPromptVisible);
	}

	[Fact]
	public void Previous_follows_the_queue_and_the_next_event()
	{
		PlayingEpisode(10, queueIndex: 0);
		Assert.False(_vm.CanPrevious, "the first of the queue");

		// a remote play of one episode: the backend fills the queue with earlier episodes, and says so
		_vm.OnEvent(new BridgeEvent(Bridge.Events.Next, JsonNode.Parse($$"""
			{"itemId": "{{Item}}", "nextItemId": "{{NextItem}}", "previousItemId": "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee0", "autoPlay": true}
			""")));
		Assert.True(_vm.CanPrevious);

		PlayingEpisode(10, queueIndex: 1);
		Assert.True(_vm.CanPrevious, "the second of the queue");
	}
}
