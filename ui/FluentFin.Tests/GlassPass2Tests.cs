using FluentFin.Controls;
using FluentFin.Services;

namespace FluentFin.Tests;

/// <summary>Seaglass pass 2: the window backdrop's choice (the poster tilt went in 1.1.4: 1.1.3 UX audit, interaction item 11).</summary>
public sealed class GlassPass2Tests
{
	[Theory]
	[InlineData(false, GlassModeKind.Full, true)]
	[InlineData(false, GlassModeKind.Reduced, true)]
	[InlineData(false, GlassModeKind.Solid, false)]
	[InlineData(true, GlassModeKind.Full, false)]
	public void WindowBackdrop_IsDesktopAcrylicOnlyWithoutArtwork_AndNeverInSolid(bool hasArtwork, GlassModeKind mode, bool acrylic) =>
		Assert.Equal(acrylic, AmbientBackdropView.WantsDesktopAcrylic(hasArtwork ? new Uri("http://x/a.jpg") : null, mode));

	[Fact]
	public void Drift_IsSubtle_AndKeepsItsEdgesCovered()
	{
		Assert.InRange(AmbientBackdropView.DriftTo - AmbientBackdropView.DriftFrom, 0.01f, 0.15f);
		Assert.True(AmbientBackdropView.DriftDuration >= TimeSpan.FromSeconds(30));
		// the sway is 2% of the width each way; the smallest zoom hides 3% on each side
		Assert.True((AmbientBackdropView.DriftFrom - 1) / 2 > 0.02f);
	}
}
