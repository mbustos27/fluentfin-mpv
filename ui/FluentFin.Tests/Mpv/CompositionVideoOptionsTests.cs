using FluentFin.Mpv;

namespace FluentFin.Tests.Mpv;

/// <summary>
/// fluentfin-mpv player controls: what FluentFin's in-process mpv is started and told with (pure: no libmpv is loaded).
/// The backend attaches to this mpv and cannot give it construction options (docs/BRIDGE.md §4, attached).
/// </summary>
public sealed class CompositionVideoOptionsTests
{
	[Fact]
	public void TheCore_IsAComposition_Output_WithNoInputOrOscOfItsOwn_OnItsPipe()
	{
		var options = MpvInProcessCore.Options("fluentfin-mpv-1-00", @"C:\cfg").ToDictionary(x => x.Name, x => x.Value);

		Assert.Equal("gpu-next", options["vo"]);
		Assert.Equal("d3d11", options["gpu-context"]);
		Assert.Equal("composition", options["d3d11-output-mode"]);
		Assert.Equal(@"\\.\pipe\fluentfin-mpv-1-00", options["input-ipc-server"]);
		Assert.Equal("no", options["osc"]);
		Assert.Equal("no", options["input-default-bindings"]);
		Assert.Equal("no", options["input-vo-keyboard"]);
		Assert.Equal("yes", options["idle"]);
		Assert.Equal("yes", options["force-window"]);
		Assert.Equal("yes", options["config"]);
		Assert.Equal(@"C:\cfg", options["config-dir"]);

		var noConfig = MpvInProcessCore.Options("p", null).ToDictionary(x => x.Name, x => x.Value);
		Assert.Equal("no", noConfig["config"]);
		Assert.False(noConfig.ContainsKey("config-dir"));
	}

	[Fact]
	public void PipeNames_AreUnique_AndWhatTheProtocolAllows()
	{
		var a = MpvInProcessCore.NewPipeName();
		var b = MpvInProcessCore.NewPipeName();

		Assert.NotEqual(a, b);
		Assert.Matches("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", a);
	}

	[Fact]
	public void TheDisplay_DecidesHdrAndTheRate()
	{
		var hdr = MpvCompositionVideo.DisplayOptions(hdr: true, peakNits: 1015.4, refreshHz: 119.88).ToDictionary(x => x.Name, x => x.Value);
		Assert.Equal("yes", hdr["target-colorspace-hint"]);
		Assert.Equal("1015", hdr["target-peak"]);
		Assert.Equal("119.88", hdr["display-fps-override"]);

		var sdr = MpvCompositionVideo.DisplayOptions(hdr: false, peakNits: 400, refreshHz: 0).ToDictionary(x => x.Name, x => x.Value);
		Assert.Equal("no", sdr["target-colorspace-hint"]);
		Assert.Equal("auto", sdr["target-peak"]);
		Assert.False(sdr.ContainsKey("display-fps-override"), "no rate known: mpv's own guess");

		// an HDR display that reports no usable peak: mpv's default
		Assert.Equal("auto", MpvCompositionVideo.DisplayOptions(true, 0, 60).Single(x => x.Name == "target-peak").Value);
	}

	[Fact]
	public void TheLineMpvAlwaysLogsInCompositionMode_IsNotAWarningOfOurs()
	{
		Assert.True(MpvInProcessCore.IsExpected("Failed to query swap chain's output information"));
		Assert.False(MpvInProcessCore.IsExpected("Failed initializing any suitable GPU context!"));
		Assert.False(MpvInProcessCore.IsExpected(null));
	}
}
