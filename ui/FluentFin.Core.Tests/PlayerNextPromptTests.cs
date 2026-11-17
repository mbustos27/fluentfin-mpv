using System.Text.Json.Nodes;
using FluentFin.Core.Mpv;

namespace FluentFin.Core.Tests;

/// <summary>
/// Seaglass 1.1.4 (UX review, prioritised fix 5): the Next pill shows only near the end of the item: from the credits when
/// a segment marks them, else the last 30 to 40 s (Jellyfin web's lead), and Previous follows the queue and the <c>next</c>
/// event. In 1.1.0 it showed with the bars from the first seconds.
/// </summary>
public sealed class PlayerNextPromptTests
{
	private const string Item = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee1";
	private const string NextItem = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee2";

	private DateTimeOffset _now = new(2026, 11, 17, 21, 0, 0, TimeSpan.Zero);
	private readonly MpvControlsViewModel _vm;

	public PlayerNextPromptTests()
	{
		_vm = new MpvControlsViewModel((_, _) => Task.CompletedTask, () => _now);
	}

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

	private void Credits(double startSeconds, double endSeconds) =>
		_vm.OnEvent(new BridgeEvent(Bridge.Events.Segments, JsonNode.Parse($$"""
			{"itemId": "{{Item}}",
			 "segments": [{"type": "Intro", "startTicks": {{Ticks(30)}}, "endTicks": {{Ticks(90)}}, "action": "ask"},
			              {"type": "Outro", "startTicks": {{Ticks(startSeconds)}}, "endTicks": {{Ticks(endSeconds)}}, "action": "ask", "source": "chapters"}],
			 "current": null}
			""")));

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
