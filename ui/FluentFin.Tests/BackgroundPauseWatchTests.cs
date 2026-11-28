using FluentFin.Core.SmartCollections;
using FluentFin.Services;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.9 (stream text; the review of wip/190-text, item 8): the window's minimise pauses background work by Windows' own
/// word (IsIconic), at any change of the window. The off-screen check (tools\ui-harness\offscreen\then-190-minimise.ps1)
/// minimises the real window; this checks the seam it goes through.
/// </summary>
public sealed class BackgroundPauseWatchTests
{
	[Fact]
	public void AnyChangeOfTheWindow_PausesWhileWindowsSaysItIsMinimised_AndGoesOnWhenItIsnt()
	{
		var gate = new PauseGate();
		var iconic = true;

		BackgroundPauseWatch.WindowChanged(gate, () => iconic);
		Assert.True(gate.Paused);
		Assert.Equal(BackgroundPauseWatch.Minimised, gate.Why);

		BackgroundPauseWatch.WindowChanged(gate, () => iconic); // said again: still one reason
		Assert.Equal(BackgroundPauseWatch.Minimised, gate.Why);

		iconic = false;
		BackgroundPauseWatch.WindowChanged(gate, () => iconic);
		Assert.False(gate.Paused);
		Assert.True(gate.WhileGoing(CancellationToken.None).IsCompleted);
	}

	[Fact]
	public void AMinimise_KeepsOtherReasons_AndRestoringLeavesThemHolding()
	{
		var gate = new PauseGate();
		gate.Set(BackgroundPauseWatch.Standby, true);

		BackgroundPauseWatch.WindowChanged(gate, () => true);
		Assert.Equal("minimised, standby", gate.Why);
		BackgroundPauseWatch.WindowChanged(gate, () => false);

		Assert.True(gate.Paused); // standby still holds
		Assert.Equal(BackgroundPauseWatch.Standby, gate.Why);
	}
}
