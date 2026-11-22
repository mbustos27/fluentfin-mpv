using System.Text.Json.Nodes;
using FluentFin.Core.Mpv;
using FluentFin.Core.Settings;

namespace FluentFin.Core.Tests;

/// <summary>
/// fluentfin-mpv: where a skip came from and the Skip intro switches (docs/BRIDGE.md §4, §5). 1.6 skip: forgetting learned
/// intros is the skip rules list's (SkipRulesTests): the backend keeps them in skip-rules.json now.
/// </summary>
public sealed class SkipIntroSourcesTests
{
	[Fact]
	public void Segments_CarryTheirSource_AndOldBackendsMeanServer()
	{
		var e = new BridgeEvent(Bridge.Events.Segments, JsonNode.Parse("""
		{"itemId": "a", "segments": [
		  {"type": "Intro", "startTicks": 1, "endTicks": 2, "action": "ask", "source": "chapters"},
		  {"type": "Outro", "startTicks": 3, "endTicks": 4, "action": "ask"}], "current": 0}
		"""));

		var segments = e.DataAs<BridgeSegments>()!;

		Assert.Equal("chapters", segments.Segments![0].Source);
		Assert.Equal("from chapters", segments.Segments[0].SourceNote);
		Assert.Equal("server", segments.Segments[1].Source);
		Assert.Null(segments.Segments[1].SourceNote);
	}

	[Theory]
	[InlineData("online", "from an online source")]
	[InlineData("learned", "from your earlier skips")]
	[InlineData("something new", null)]
	public void TheNote_IsQuiet(string source, string? note) => Assert.Equal(note, new BridgeSegment("Intro", 1, 2, "ask", source).SourceNote);

	[Fact]
	public void TheCommand_IsTheProtocols_AndCarriesOnlyTheSkipSettings()
	{
		// 1.6 skip: learnSkips (sent as learnIntros too, for an older backend), the ads and previews, the rules' default
		Assert.Equal("set_skip_options", Bridge.Commands.SetSkipOptions);
		var args = BridgeSkipCommands.SkipOptions(onlineLookup: true, learnSkips: false, "always", "ask");
		Assert.Equal(["learnIntros", "learnSkips", "onlineLookup", "ruleAction", "segmentActions"], args.Select(x => x.Key).Order());
		Assert.True((bool)args["onlineLookup"]!);
		Assert.False((bool)args["learnIntros"]!);
		Assert.False((bool)args["learnSkips"]!);
		Assert.Equal("always", (string)args["segmentActions"]!["Commercial"]!);
		Assert.Equal("always", (string)args["segmentActions"]!["Preview"]!);
		Assert.Equal("ask", (string)args["ruleAction"]!);
	}

	[Fact]
	public void Defaults_LookupOff_LearningOn()
	{
		Assert.False(SettingKeys.LookUpIntrosOnline.Default.Value);
		Assert.True(SettingKeys.LearnIntros.Default.Value);
	}
}
