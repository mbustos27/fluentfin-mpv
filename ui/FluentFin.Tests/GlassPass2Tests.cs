using System.Numerics;
using FluentFin.Controls;
using FluentFin.Services;

namespace FluentFin.Tests;

/// <summary>Seaglass pass 2: the poster tilt's maths and the window backdrop's choice.</summary>
public sealed class GlassPass2Tests
{
	[Fact]
	public void Tilt_IsZeroAtTheCentre_AndAtMostTheMaximumAtACorner()
	{
		Assert.Equal(0f, PosterTilt.Rotation(100, 150, 200, 300).Degrees);
		var corner = PosterTilt.Rotation(200, 300, 200, 300);
		Assert.InRange(corner.Degrees, 0.1f, PosterTilt.MaxDegrees);
		Assert.InRange(PosterTilt.Rotation(5000, -5000, 200, 300).Degrees, 0f, PosterTilt.MaxDegrees);
		Assert.Equal(0f, PosterTilt.Rotation(10, 10, 0, 0).Degrees);
	}

	[Fact]
	public void Tilt_TurnsAboutYForThePointerRight_AndAboutMinusXForThePointerLow()
	{
		var right = PosterTilt.Rotation(200, 150, 200, 300);
		Assert.True(right.Axis.Y > 0.99f && Math.Abs(right.Axis.X) < 0.01f);
		var low = PosterTilt.Rotation(100, 300, 200, 300);
		Assert.True(low.Axis.X < -0.99f && Math.Abs(low.Axis.Y) < 0.01f);
	}

	[Fact]
	public void Perspective_LeavesFlatContentAlone_AndMakesDepthShrink()
	{
		var m = PosterTilt.Perspective(200, 300);
		var flat = Vector4.Transform(new Vector4(30, 40, 0, 1), m);
		Assert.Equal(1f, flat.W, 4);
		Assert.Equal(30f, flat.X, 3);
		var far = Vector4.Transform(new Vector4(30, 40, -50, 1), m);
		Assert.True(far.W > 1f, "a point pushed away must be drawn smaller (w above 1)");
	}

	/// <summary>1.1.4: the dusk scene covers the window wherever there is no artwork, so desktop acrylic is never worth its cost.</summary>
	[Theory]
	[InlineData(false, GlassModeKind.Full)]
	[InlineData(false, GlassModeKind.Reduced)]
	[InlineData(false, GlassModeKind.Solid)]
	[InlineData(true, GlassModeKind.Full)]
	public void WindowBackdrop_IsNeverDesktopAcrylic(bool hasArtwork, GlassModeKind mode) =>
		Assert.False(AmbientBackdropView.WantsDesktopAcrylic(hasArtwork ? new Uri("http://x/a.jpg") : null, mode));

	[Fact]
	public void Drift_IsSubtle_AndKeepsItsEdgesCovered()
	{
		Assert.InRange(AmbientBackdropView.DriftTo - AmbientBackdropView.DriftFrom, 0.01f, 0.15f);
		Assert.True(AmbientBackdropView.DriftDuration >= TimeSpan.FromSeconds(30));
		// the sway is 2% of the width each way; the smallest zoom hides 3% on each side
		Assert.True((AmbientBackdropView.DriftFrom - 1) / 2 > 0.02f);
	}
}
