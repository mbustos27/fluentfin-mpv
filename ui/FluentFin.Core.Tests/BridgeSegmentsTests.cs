using System.Text.Json;
using System.Text.Json.Nodes;
using FluentFin.Core.Mpv;

namespace FluentFin.Core.Tests;

/// <summary>
/// fluentfin-mpv: the <c>segments</c> and <c>next</c> events and the <c>skip_segment</c> command (docs/BRIDGE.md §4, §5), read
/// the way the backend writes them (backend/tests/test_bridge_protocol.py, SegmentsAndNextTest, builds the same payloads).
/// </summary>
public sealed class BridgeSegmentsTests
{
	private static BridgeEvent Event(string name, string json) => new(name, JsonNode.Parse(json));

	[Fact]
	public void Names_AreTheProtocols()
	{
		Assert.Equal("skip_segment", Bridge.Commands.SkipSegment);
		Assert.Equal("segments", Bridge.Events.Segments);
		Assert.Equal("next", Bridge.Events.Next);
		Assert.Equal(("ask", "always"), (Bridge.SegmentActions.Ask, Bridge.SegmentActions.Always));
	}

	[Fact]
	public void Segments_TheOneOnOfferIsCurrentsEntry()
	{
		var e = Event(Bridge.Events.Segments, """
		{"itemId": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
		 "segments": [{"type": "Intro", "startTicks": 100000000, "endTicks": 705000000, "action": "ask"},
		              {"type": "Outro", "startTicks": 13000000000, "endTicks": 13800000000, "action": "always"}],
		 "current": 0, "someFieldFromLater": true}
		""");

		var segments = e.DataAs<BridgeSegments>()!;

		Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", segments.ItemId);
		Assert.Equal(2, segments.Segments!.Count);
		Assert.Equal(new BridgeSegment("Intro", 100_000_000, 705_000_000, Bridge.SegmentActions.Ask), segments.Offered);
		Assert.Equal("Skip Intro", segments.Offered!.SkipLabel);
		Assert.Equal(TimeSpan.FromSeconds(70.5), TimeSpan.FromTicks(segments.Offered.EndTicks));
		Assert.Equal(Bridge.SegmentActions.Always, segments.Segments[1].Action);
	}

	[Theory]
	[InlineData("""{"itemId": "a", "segments": [], "current": null}""")]
	[InlineData("""{"itemId": "a", "segments": [{"type": "Intro", "startTicks": 1, "endTicks": 2, "action": "ask"}], "current": null}""")]
	[InlineData("""{"itemId": "a", "segments": [{"type": "Intro", "startTicks": 1, "endTicks": 2, "action": "ask"}], "current": 1}""")]
	[InlineData("""{"itemId": "a", "segments": [{"type": "Intro", "startTicks": 1, "endTicks": 2, "action": "ask"}], "current": -1}""")]
	[InlineData("""{"itemId": "a", "current": 0}""")]
	public void Segments_WithNothingOnOffer_OfferNothing(string json)
	{
		Assert.Null(Event(Bridge.Events.Segments, json).DataAs<BridgeSegments>()!.Offered);
	}

	[Theory]
	[InlineData("Intro", "Skip Intro")]
	[InlineData("Outro", "Skip Credits")]
	[InlineData("Commercial", "Skip Commercial")]
	[InlineData("Preview", "Skip Preview")]
	[InlineData("Recap", "Skip Recap")]
	[InlineData("SomethingNewer", "Skip Intro")]
	public void Segments_SkipLabels_AreTheBackendsWords(string type, string label)
	{
		// backend: media.segment_labels, the text of its own Skip button
		Assert.Equal(label, new BridgeSegment(type, 0, 1, Bridge.SegmentActions.Ask).SkipLabel);
	}

	[Fact]
	public void Segments_SerializedBack_CarryOnlyTheProtocolsFields()
	{
		var node = JsonSerializer.SerializeToNode(new BridgeSegments("a", [new BridgeSegment("Intro", 1, 2, "ask")], 0), Bridge.JsonOptions)!;

		Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
			{"itemId": "a", "segments": [{"type": "Intro", "startTicks": 1, "endTicks": 2, "action": "ask", "source": "server"}], "current": 0}
			"""), node), node.ToJsonString());
	}

	[Fact]
	public void Next_ReadAsTheBackendWritesIt()
	{
		var middle = Event(Bridge.Events.Next, """
		{"itemId": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "nextItemId": "cccccccccccccccccccccccccccccccc",
		 "previousItemId": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "autoPlay": true}
		""").DataAs<BridgeNext>();
		var last = Event(Bridge.Events.Next, """
		{"itemId": "cccccccccccccccccccccccccccccccc", "previousItemId": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "autoPlay": false}
		""").DataAs<BridgeNext>();

		Assert.Equal(new BridgeNext("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "cccccccccccccccccccccccccccccccc", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", AutoPlay: true), middle);
		Assert.Equal(new BridgeNext("cccccccccccccccccccccccccccccccc", null, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", AutoPlay: false), last);
	}

	[Fact]
	public void SkipSegment_ResultSaysWhetherAnythingWasOnOffer()
	{
		Assert.True(JsonNode.Parse("""{"skipped": true}""").Deserialize<BridgeSkipResult>(Bridge.JsonOptions)!.Skipped);
		Assert.False(JsonNode.Parse("""{"skipped": false}""").Deserialize<BridgeSkipResult>(Bridge.JsonOptions)!.Skipped);
	}
}
