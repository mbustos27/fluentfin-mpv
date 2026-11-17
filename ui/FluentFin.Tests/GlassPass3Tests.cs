using FluentFin.Controls;
using FluentFin.Services;
using Microsoft.UI.Input;

namespace FluentFin.Tests;

/// <summary>Seaglass pass 3: what the page transition, the grain and the panel highlight do in each glass mode.</summary>
public sealed class GlassPass3Tests
{
	[Theory]
	[InlineData(GlassModeKind.Full, true, false, PageTransitionKind.Settle)]
	[InlineData(GlassModeKind.Reduced, true, false, PageTransitionKind.Fade)]
	[InlineData(GlassModeKind.Solid, true, false, PageTransitionKind.None)]
	[InlineData(GlassModeKind.Full, false, false, PageTransitionKind.None)]
	[InlineData(GlassModeKind.Full, true, true, PageTransitionKind.None)]
	public void Page_transition_kind_follows_the_mode_animations_and_the_player(GlassModeKind mode, bool animations, bool player, PageTransitionKind expected) =>
		Assert.Equal(expected, PageTransition.KindFor(mode, animations, player));

	[Fact]
	public void Grain_is_strongest_in_full_halved_in_reduced_and_absent_in_solid()
	{
		Assert.Equal(GrainOverlay.FullOpacity, GrainOverlay.OpacityFor(GlassModeKind.Full));
		Assert.Equal(GrainOverlay.ReducedOpacity, GrainOverlay.OpacityFor(GlassModeKind.Reduced));
		Assert.Equal(0, GrainOverlay.OpacityFor(GlassModeKind.Solid));
		Assert.InRange(GrainOverlay.FullOpacity, 0.03, 0.05);
	}

	[Fact]
	public void Grain_tiles_cover_the_surface()
	{
		Assert.Equal((1, 1), GrainOverlay.TilesFor(256, 256));
		Assert.Equal((2, 1), GrainOverlay.TilesFor(257, 10));
		Assert.Equal((8, 4), GrainOverlay.TilesFor(1900, 900));
		Assert.Equal((0, 0), GrainOverlay.TilesFor(0, -5));
	}

	[Fact]
	public void The_pointer_highlight_is_for_mouse_and_pen_in_full_motion_only()
	{
		Assert.True(PanelSheen.Applies(PointerDeviceType.Mouse, true));
		Assert.True(PanelSheen.Applies(PointerDeviceType.Pen, true));
		Assert.False(PanelSheen.Applies(PointerDeviceType.Touch, true));
		Assert.False(PanelSheen.Applies(PointerDeviceType.Mouse, false));
	}
}
