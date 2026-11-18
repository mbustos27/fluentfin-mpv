namespace FluentFin.Core.Theming;

/// <summary>What the backdrop does with the dusk scene's motion now.</summary>
public enum SceneMotionStep
{
	/// <summary>Nothing changes.</summary>
	None,
	/// <summary>Start the loops (the stars' glint, the sun's breath, the hills' sway).</summary>
	Start,
	/// <summary>Stop them at once (the scene went, motion is no longer allowed, the window went to the background).</summary>
	Stop,
	/// <summary>The scene's time is up: ease each part to its rest pose, then nothing moves.</summary>
	Ease,
}

/// <summary>
/// 1.2.0: when the dusk scene moves. Any continuous motion makes the compositor redraw the whole window, acrylic included, every
/// frame (measured: an endlessly swaying hill strip cost about 0.6 of a core on an idle page). So only the full scene moves,
/// only where motion is allowed, and only for <see cref="Duration"/> after it appears or the window comes back; the strip never
/// moves. AmbientBackdropView carries out the steps and decides nothing itself.
/// </summary>
public sealed class SceneMotion
{
	/// <summary>How long the full scene moves before it settles.</summary>
	public static readonly TimeSpan Duration = TimeSpan.FromSeconds(20);

	/// <summary>Whether the loops are running.</summary>
	public bool Moving { get; private set; }

	/// <summary>Whether this scene has had its time and stays still.</summary>
	public bool Settled { get; private set; }

	/// <summary>The step for the scene shown and whether motion is allowed now (Full mode, animations on, the window in front).</summary>
	public SceneMotionStep Update(DuskSceneKind scene, bool motionAllowed)
	{
		var want = motionAllowed && scene == DuskSceneKind.Full && !Settled;
		if (want == Moving)
		{
			return SceneMotionStep.None;
		}

		Moving = want;
		return want ? SceneMotionStep.Start : SceneMotionStep.Stop;
	}

	/// <summary>The time is up (the settle timer): ease to rest if moving; this scene then stays still.</summary>
	public SceneMotionStep Settle()
	{
		if (!Moving)
		{
			return SceneMotionStep.None;
		}

		Moving = false;
		Settled = true;
		return SceneMotionStep.Ease;
	}

	/// <summary>A new scene was built (another kind, another theme): it may move again for a while.</summary>
	public void Rebuilt()
	{
		Moving = false;
		Settled = false;
	}

	/// <summary>The window came back to the front: the full scene may move again for a while.</summary>
	public void BackInFront() => Settled = false;

	/// <summary>The view stopped the loops itself (it was unloaded).</summary>
	public void Stopped() => Moving = false;
}
