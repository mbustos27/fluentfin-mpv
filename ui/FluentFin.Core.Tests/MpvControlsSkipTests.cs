using System.Text.Json.Nodes;
using FluentFin.Core.Mpv;

namespace FluentFin.Core.Tests;

/// <summary>
/// fluentfin-mpv: Skip intro / Skip credits and Next episode in FluentFin's own player controls, from the backend's
/// <c>segments</c> and <c>next</c> events (docs/BRIDGE.md §4 skip_segment, §5). The clock only moves when a test moves it.
/// </summary>
public sealed class MpvControlsSkipTests
{
	private const string Item = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee1";
	private const string NextItem = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee2";

	private DateTimeOffset _now = new(2026, 11, 16, 20, 0, 0, TimeSpan.Zero);
	private readonly List<(string Command, JsonObject? Args)> _sent = [];
	private readonly MpvControlsViewModel _vm;

	public MpvControlsSkipTests()
	{
		_vm = new MpvControlsViewModel((command, args) =>
		{
			_sent.Add((command, args?.DeepClone() as JsonObject));
			return Task.CompletedTask;
		}, () => _now);
	}

	private static long Ticks(double seconds) => TimeSpan.FromSeconds(seconds).Ticks;

	private List<string> Commands() => [.. _sent.Select(x => x.Command)];

	private void Wait(double seconds)
	{
		_now += TimeSpan.FromSeconds(seconds);
		_vm.Tick();
	}

	/// <summary>An episode playing at <paramref name="positionSeconds"/>, as the backend starts one: item, then state and position.</summary>
	private void PlayingEpisode(double positionSeconds = 5, string itemId = Item, int queueIndex = 0, int queueCount = 2)
	{
		_vm.OnEvent(new BridgeEvent(Bridge.Events.Item, JsonNode.Parse($$"""
			{"itemId": "{{itemId}}", "name": "Pilot", "type": "Episode", "seriesName": "Spike Show", "seasonNumber": 1, "episodeNumber": 1, "runTimeTicks": {{Ticks(600)}}}
			""")));
		_vm.OnState(new BridgeState
		{
			State = Bridge.States.Playing,
			ItemId = itemId,
			DurationTicks = Ticks(600),
			Queue = new BridgeQueue { ItemIds = [.. Enumerable.Range(0, queueCount).Select(n => n == 0 ? Item : NextItem)], Index = queueIndex },
		});
		_vm.OnPosition(new BridgePosition(Ticks(positionSeconds), Ticks(600)));
	}

	/// <summary>The backend's <c>segments</c> event, as it writes it: an intro 2-20 s (ask), credits 590-600 s (ask).</summary>
	private void Segments(int? current, string itemId = Item, string introAction = "ask") =>
		_vm.OnEvent(new BridgeEvent(Bridge.Events.Segments, JsonNode.Parse($$"""
			{"itemId": "{{itemId}}",
			 "segments": [{"type": "Intro", "startTicks": {{Ticks(2)}}, "endTicks": {{Ticks(20)}}, "action": "{{introAction}}"},
			              {"type": "Outro", "startTicks": {{Ticks(590)}}, "endTicks": {{Ticks(600)}}, "action": "ask"}],
			 "current": {{(current is int c ? c.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null")}}}
			""")));

	private void Next(string? nextItemId, string itemId = Item) =>
		_vm.OnEvent(new BridgeEvent(Bridge.Events.Next, JsonNode.Parse($$"""
			{"itemId": "{{itemId}}", {{(nextItemId is null ? "" : $"\"nextItemId\": \"{nextItemId}\", ")}}"autoPlay": {{(nextItemId is null ? "false" : "true")}}}
			""")));

	// -- the Skip button ---------------------------------------------------------------------------------------------------

	[Fact]
	public void Skip_shows_while_the_backend_offers_a_segment_and_goes_with_the_offer()
	{
		PlayingEpisode();
		Segments(current: null);
		Assert.False(_vm.IsSkipVisible, "listed, but nothing on offer yet");

		Segments(current: 0);
		Assert.True(_vm.IsSkipVisible);
		Assert.Equal("Skip intro", _vm.SkipText);

		Segments(current: null);
		Assert.False(_vm.IsSkipVisible, "the offer is over");

		Segments(current: 1);
		Assert.True(_vm.IsSkipVisible);
		Assert.Equal("Skip credits", _vm.SkipText);
		Assert.Empty(_sent);
	}

	[Theory]
	[InlineData("Intro", "Skip intro")]
	[InlineData("Outro", "Skip credits")]
	[InlineData("Commercial", "Skip commercial")]
	[InlineData("Preview", "Skip preview")]
	[InlineData("Recap", "Skip recap")]
	[InlineData("SomethingNewer", "Skip intro")]
	public void Skip_text_is_the_segment_types_in_sentence_case(string type, string text)
	{
		Assert.Equal(text, MpvControlsViewModel.SkipTextFor(type));
	}

	[Fact]
	public void Skip_stays_for_the_whole_offer_when_the_bars_hide()
	{
		PlayingEpisode();
		Segments(current: 0);
		_vm.NotifyActivity();

		for (var round = 0; round < 3; round++)
		{
			Wait(10);
			Assert.False(_vm.ControlsVisible, $"round {round}: the bars hid");
			Assert.True(_vm.IsSkipVisible, $"round {round}: the Skip button stays");
			Assert.True(_vm.AnythingVisible, "something of the controls' is on screen");
		}
	}

	[Fact]
	public async Task Skip_button_sends_skip_segment_once_hides_and_shows_the_segments_end()
	{
		PlayingEpisode(positionSeconds: 5);
		Segments(current: 0);

		await _vm.SkipSegment();

		Assert.Equal([Bridge.Commands.SkipSegment], Commands());
		Assert.Null(_sent[0].Args);
		Assert.False(_vm.IsSkipVisible, "gone at once, before the backend's next sample");
		Assert.Equal("0:20", _vm.ElapsedText);

		// a second press, and the backend still offering the same segment (its sample before the seek): nothing more
		await _vm.SkipSegment();
		Segments(current: 0);
		Assert.False(_vm.IsSkipVisible);
		Assert.Single(_sent);

		// the offer ended; back in the intro later (a seek back): offered again
		Segments(current: null);
		Segments(current: 0);
		Assert.True(_vm.IsSkipVisible);
		await _vm.SkipSegment();
		Assert.Equal(2, _sent.Count);
	}

	/// <summary>
	/// Skipped, then a quick seek back into the intro: the backend's offer may never have left the intro (its sampling missed
	/// the end), so no segments event says so. The seek itself offers it again; a seek elsewhere does not.
	/// </summary>
	[Fact]
	public async Task Skip_is_offered_again_after_a_seek_back_into_the_skipped_segment()
	{
		PlayingEpisode(positionSeconds: 5);
		Segments(current: 0);
		await _vm.SkipSegment();
		Assert.False(_vm.IsSkipVisible);

		await _vm.SeekBy(MpvControlsViewModel.SmallSeekSeconds);
		Assert.False(_vm.IsSkipVisible, "a seek on, past the intro: still skipped");

		await _vm.SeekTo(Ticks(8));
		Assert.True(_vm.IsSkipVisible, "back in the intro with no change of offer from the backend");
		Assert.True(_vm.HandleKey(MpvControlsViewModel.Keys.S));
		Assert.Equal(2, Commands().Count(c => c == Bridge.Commands.SkipSegment));
		Assert.False(_vm.IsSkipVisible);

		await _vm.SeekBy(-LargeBack);
		Assert.True(_vm.IsSkipVisible, "a seek back by keys, into the intro, too");
	}

	private const int LargeBack = 10;

	[Fact]
	public async Task Skip_with_nothing_on_offer_sends_nothing()
	{
		PlayingEpisode();
		Segments(current: null);

		await _vm.SkipSegment();

		Assert.Empty(_sent);
	}

	[Fact]
	public void S_key_skips_the_segment_on_offer()
	{
		Assert.Equal(MpvControlsAction.SkipSegment, MpvControlsViewModel.ActionFor(MpvControlsViewModel.Keys.S, control: false, shift: false, alt: false));
		Assert.Null(MpvControlsViewModel.ActionFor(MpvControlsViewModel.Keys.S, control: true, shift: false, alt: false));

		PlayingEpisode();
		Segments(current: null);
		Assert.True(_vm.HandleKey(MpvControlsViewModel.Keys.S), "S is the player's key");
		Assert.Empty(_sent);

		Segments(current: 0);
		Assert.True(_vm.HandleKey(MpvControlsViewModel.Keys.S));
		Assert.Equal([Bridge.Commands.SkipSegment], Commands());
		Assert.False(_vm.IsSkipVisible);
	}

	[Fact]
	public void Skip_setting_off_hides_the_button_but_S_still_skips()
	{
		_vm.ShowSkipPrompt = false;
		PlayingEpisode();
		Segments(current: 0);

		Assert.False(_vm.IsSkipVisible);

		_vm.HandleKey(MpvControlsViewModel.Keys.S);
		Assert.Equal([Bridge.Commands.SkipSegment], Commands());
	}

	[Fact]
	public async Task Skip_offer_of_another_item_is_not_shown_and_a_new_item_clears_the_offer()
	{
		PlayingEpisode();
		Segments(current: 0, itemId: NextItem);
		Assert.False(_vm.IsSkipVisible, "another item's offer");

		Segments(current: 0);
		Assert.True(_vm.IsSkipVisible);

		// Next: the new item starts; its own segments event follows
		PlayingEpisode(positionSeconds: 0, itemId: NextItem, queueIndex: 1);
		Assert.False(_vm.IsSkipVisible, "the last item's offer went with it");
		await _vm.SkipSegment();
		Assert.Empty(_sent);
	}

	/// <summary>
	/// Reviewer: the credits' offer of an item that ended stayed over the next item's opening (the backend drops it at
	/// <c>ended</c> with no segments event, and the controls do not get <c>ended</c>). A state of the next item drops it; the
	/// next item's own offer, which the backend writes before that tick's state, stays.
	/// </summary>
	[Fact]
	public async Task Skip_offer_goes_with_its_item_when_the_next_one_opens_or_playback_stops()
	{
		// the queue's last item: with a next one, its Next prompt takes Skip credits' place (1.2.0, PlayerNextPromptTests)
		PlayingEpisode(positionSeconds: 595, queueCount: 1);
		Segments(current: 1);
		Assert.True(_vm.IsSkipVisible);
		Assert.Equal("Skip credits", _vm.SkipText);

		// eof: ended (not passed to the controls), then the next item opens; no segments event in between
		_vm.OnState(new BridgeState { State = Bridge.States.Opening, ItemId = NextItem, Queue = new BridgeQueue { ItemIds = [Item, NextItem], Index = 1 } });
		Assert.False(_vm.IsSkipVisible, "the last item's credits are not offered over the next one");
		await _vm.SkipSegment();
		Assert.Empty(_sent);

		// the next item: item, its intro from 0:00 on offer at once, then the tick's state (still opening)
		_vm.OnEvent(new BridgeEvent(Bridge.Events.Item, JsonNode.Parse($$"""{"itemId": "{{NextItem}}", "name": "Second", "type": "Episode"}""")));
		_vm.OnEvent(new BridgeEvent(Bridge.Events.Segments, JsonNode.Parse($$"""
			{"itemId": "{{NextItem}}", "segments": [{"type": "Intro", "startTicks": 0, "endTicks": {{Ticks(30)}}, "action": "ask"}], "current": 0}
			""")));
		_vm.OnState(new BridgeState { State = Bridge.States.Opening, ItemId = NextItem, Queue = new BridgeQueue { ItemIds = [Item, NextItem], Index = 1 } });
		Assert.True(_vm.IsSkipVisible, "the opening item's own offer stays");
		Assert.Equal("Skip intro", _vm.SkipText);

		// stop: idle
		_vm.OnState(new BridgeState { State = Bridge.States.Idle });
		Assert.False(_vm.IsSkipVisible, "nothing plays: nothing to skip");
	}

	// -- Next episode -----------------------------------------------------------------------------------------------------

	[Fact]
	public async Task Next_event_enables_the_Next_button_and_names_the_next_item()
	{
		var asked = new List<string>();
		_vm.NameItem = id =>
		{
			asked.Add(id);
			return Task.FromResult<string?>("S1 E2 · Second");
		};
		_vm.OnEvent(new BridgeEvent(Bridge.Events.Item, JsonNode.Parse($$"""{"itemId": "{{Item}}", "name": "Pilot", "type": "Episode"}""")));
		Assert.False(_vm.CanNext, "nothing known yet");

		Next(NextItem);

		Assert.True(_vm.CanNext, "the next event alone enables it");
		Assert.True(_vm.ShowsNext);
		Assert.Equal([NextItem], asked);
		Assert.Equal("Next: S1 E2 · Second", _vm.NextToolTip);

		await _vm.Next();
		Assert.Equal([Bridge.Commands.Next], Commands());
	}

	[Fact]
	public async Task Next_event_without_a_next_item_disables_the_button_whatever_the_queue_says()
	{
		PlayingEpisode(queueIndex: 0, queueCount: 2);
		Assert.True(_vm.CanNext, "the state's queue, before the next event");

		Next(null);

		Assert.False(_vm.CanNext);
		Assert.True(_vm.ShowsNext, "an episode keeps the button, disabled");
		Assert.Equal("No next episode", _vm.NextToolTip);

		// a later state does not enable it again: the next event decides for this item
		_vm.OnState(new BridgeState { State = Bridge.States.Playing, ItemId = Item, Queue = new BridgeQueue { ItemIds = [Item, NextItem], Index = 0 } });
		Assert.False(_vm.CanNext);
		await _vm.Next();
		Assert.Empty(_sent);
	}

	[Fact]
	public void Next_name_that_arrives_after_the_next_item_changed_is_dropped()
	{
		var answers = new Dictionary<string, TaskCompletionSource<string?>>();
		_vm.NameItem = id => (answers[id] = new TaskCompletionSource<string?>()).Task;
		PlayingEpisode();

		Next(NextItem);
		Next("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee3");
		answers[NextItem].SetResult("S1 E2 · Second");
		Assert.Equal("Next episode", _vm.NextToolTip);

		answers["eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee3"].SetResult("S1E3 · Third");
		Assert.Equal("Next: S1E3 · Third", _vm.NextToolTip);
	}

	[Theory]
	[InlineData("fails")]
	[InlineData(null)]
	[InlineData("  ")]
	public void Next_name_lookup_that_fails_or_finds_no_name_leaves_the_plain_tooltip(string? answer)
	{
		_vm.NameItem = _ => answer == "fails" ? Task.FromException<string?>(new HttpRequestException("offline")) : Task.FromResult(answer);
		PlayingEpisode();

		Next(NextItem);

		Assert.True(_vm.CanNext);
		Assert.Equal("Next episode", _vm.NextToolTip);
	}

	[Fact]
	public void Next_button_is_not_there_for_a_film_alone()
	{
		_vm.OnEvent(new BridgeEvent(Bridge.Events.Item, JsonNode.Parse("""{"itemId": "m", "name": "A film", "type": "Movie"}""")));
		_vm.OnState(new BridgeState { State = Bridge.States.Playing, ItemId = "m", Queue = new BridgeQueue { ItemIds = ["m"], Index = 0 } });
		Next(null, itemId: "m");

		Assert.False(_vm.ShowsNext);
		Assert.False(_vm.CanNext);
	}

	[Fact]
	public void Next_title_is_the_episodes_line_or_the_name()
	{
		Assert.Equal("S1 E2 · Second", MpvControlsViewModel.NextTitle(new BridgeItem(NextItem, "Second", "Episode", "Spike Show", 1, 2)));
		Assert.Equal("A film", MpvControlsViewModel.NextTitle(new BridgeItem("m", "A film", "Movie")));
	}
}
