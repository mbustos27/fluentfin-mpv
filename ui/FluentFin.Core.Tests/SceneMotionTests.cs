using FluentFin.Core.Theming;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.2.0: when the dusk scene moves (SceneMotion decides; AmbientBackdropView only carries out its steps). An endlessly moving
/// backdrop redraws the whole window every frame (measured: about 0.6 of a core idle), so only the full scene moves, only for a
/// while, and the strip never.
/// </summary>
public sealed class SceneMotionTests
{
	[Fact]
	public void The_full_scene_starts_moving_then_settles_and_stays_still()
	{
		var motion = new SceneMotion();
		Assert.Equal(SceneMotionStep.Start, motion.Update(DuskSceneKind.Full, motionAllowed: true));
		Assert.Equal(SceneMotionStep.None, motion.Update(DuskSceneKind.Full, true));
		Assert.Equal(SceneMotionStep.Ease, motion.Settle());
		Assert.False(motion.Moving);

		// every later update (a mode change, a layout pass) leaves it still: the motion never comes back by itself
		for (var i = 0; i < 5; i++)
		{
			Assert.Equal(SceneMotionStep.None, motion.Update(DuskSceneKind.Full, true));
		}

		Assert.Equal(SceneMotionStep.None, motion.Settle());
	}

	[Theory]
	[InlineData(DuskSceneKind.Strip)]
	[InlineData(DuskSceneKind.None)]
	public void The_strip_and_no_scene_never_move(DuskSceneKind scene)
	{
		var motion = new SceneMotion();
		Assert.Equal(SceneMotionStep.None, motion.Update(scene, true));
		motion.Rebuilt();
		motion.BackInFront();
		Assert.Equal(SceneMotionStep.None, motion.Update(scene, true));
		Assert.False(motion.Moving);
	}

	[Fact]
	public void Nothing_moves_where_motion_is_not_allowed_and_losing_it_stops_the_loops()
	{
		var motion = new SceneMotion();
		Assert.Equal(SceneMotionStep.None, motion.Update(DuskSceneKind.Full, motionAllowed: false));
		Assert.Equal(SceneMotionStep.Start, motion.Update(DuskSceneKind.Full, true));
		Assert.Equal(SceneMotionStep.Stop, motion.Update(DuskSceneKind.Full, false)); // Reduced, or the window went behind
		Assert.Equal(SceneMotionStep.Start, motion.Update(DuskSceneKind.Full, true)); // not settled yet: it may go on
		Assert.Equal(SceneMotionStep.Stop, motion.Update(DuskSceneKind.Strip, true)); // the scene became the strip
	}

	[Fact]
	public void A_new_scene_or_the_window_coming_back_gives_one_more_while()
	{
		var motion = new SceneMotion();
		motion.Update(DuskSceneKind.Full, true);
		motion.Settle();
		motion.BackInFront();
		Assert.Equal(SceneMotionStep.Start, motion.Update(DuskSceneKind.Full, true));
		motion.Settle();
		motion.Rebuilt();
		Assert.Equal(SceneMotionStep.Start, motion.Update(DuskSceneKind.Full, true));
		motion.Stopped();
		Assert.False(motion.Moving);
		Assert.InRange(SceneMotion.Duration, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
	}
}
