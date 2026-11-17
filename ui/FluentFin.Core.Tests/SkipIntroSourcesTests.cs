using System.Text.Json.Nodes;
using FluentFin.Core.Mpv;
using FluentFin.Core.Settings;

namespace FluentFin.Core.Tests;

/// <summary>fluentfin-mpv: where a skip came from, the two Skip intro switches, and forgetting learned intros (docs/BRIDGE.md §4, §5).</summary>
public sealed class SkipIntroSourcesTests : IDisposable
{
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "FluentFinTests", Guid.NewGuid().ToString("N"));

	public SkipIntroSourcesTests() => Directory.CreateDirectory(_dir);

	public void Dispose() => Directory.Delete(_dir, true);

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
	public void TheCommand_IsTheProtocols_AndCarriesOnlyTheTwoSwitches()
	{
		Assert.Equal("set_skip_options", Bridge.Commands.SetSkipOptions);
		var args = BridgeCommands.SkipOptions(onlineLookup: true, learnIntros: false);
		Assert.Equal(["learnIntros", "onlineLookup"], args.Select(x => x.Key).Order());
		Assert.True((bool)args["onlineLookup"]!);
		Assert.False((bool)args["learnIntros"]!);
	}

	[Fact]
	public void Defaults_LookupOff_LearningOn()
	{
		Assert.False(SettingKeys.LookUpIntrosOnline.Default.Value);
		Assert.True(SettingKeys.LearnIntros.Default.Value);
	}

	[Fact]
	public void Forget_DeletesTheFile_AndIsFineWithoutOne()
	{
		var file = Path.Combine(_dir, LearnedIntros.FileName);
		Assert.True(LearnedIntros.Forget(_dir));
		File.WriteAllText(file, "{}");
		Assert.True(LearnedIntros.Forget(_dir));
		Assert.False(File.Exists(file));
		Assert.True(LearnedIntros.Forget(Path.Combine(_dir, "no such folder")));
	}

	[Fact]
	public void TheFile_IsInTheBackendsConfigFolder() =>
		Assert.Equal(Path.Combine(_dir, "mpv-backend"), LearnedIntros.ConfigDirectory(new KnownFolders(_dir)));
}
