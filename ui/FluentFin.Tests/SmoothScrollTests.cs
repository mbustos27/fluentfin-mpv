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

	/// <summary>
	/// Nixie 1.9 (the scroll stream; the owner: "scrolling the collections page takes a longgg time"): a quick spin over a page
	/// whose glide, asked again before it ends, starts from where the page is (the Collections page's ScrollView): each notch
	/// counts from where the page is going, so 27 notches of 96 px reach the end of a page 1,992 px past its view. Counted from
	/// where the page was (ScrollView.ScrollBy), the same spin got 314 px.
	/// </summary>
	[Fact]
	public void AQuickSpin_CountsEveryNotch_FromWhereThePageIsGoing()
	{
		var state = new SmoothScroll.Pending();
		double offset = 0, max = 1992;
		for (var notch = 0; notch < 27; notch++)
		{
			state.Begin(SmoothScroll.NextFrom(state, offset) + 96, max, offset);
			offset += (state.Target - offset) * 0.05; // the glide's first 40 ms, then the next notch starts it again
		}

		Assert.Equal(max, state.Target);
		Assert.True(state.Moving);

		// the glide ends (ScrollCompleted): the next notch counts from where the page is
		state.Moving = false;
		Assert.Equal(1500, SmoothScroll.NextFrom(state, 1500));
	}
}