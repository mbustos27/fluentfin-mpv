using FluentFin.Controls;

namespace FluentFin.Tests;

public class SmoothScrollTests
{
	[Fact]
	public void AtTheClampNothingMovesAndTheNextByStartsFromTheView()
	{
		var state = new SmoothScroll.Pending();
		Assert.True(state.Begin(900, 1000, 0));       // a real move: the view is going to 900
		Assert.True(state.Moving);
		state.Moving = false;                          // it arrives
		Assert.False(state.Begin(5000, 1000, 1000));  // already at the clamp: ChangeView would change nothing
		Assert.False(state.Moving);                    // so it must not wait for a ViewChanged that never comes
	}

	[Fact]
	public void TargetIsClamped()
	{
		var state = new SmoothScroll.Pending();
		state.Begin(-40, 1000, 300);
		Assert.Equal(0, state.Target);
		state.Begin(5000, 1000, 300);
		Assert.Equal(1000, state.Target);
	}
}
