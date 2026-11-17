using FluentFin.Core.Mpv;

namespace FluentFin.Core.Tests;

/// <summary>Seaglass full screen: one switch at a time, the last request wins (FullScreenSwitch).</summary>
public sealed class FullScreenSwitchTests
{
	private bool _full;
	private readonly List<bool> _applied = [];
	private readonly FullScreenSwitch _switch;

	public FullScreenSwitchTests()
	{
		_switch = new FullScreenSwitch(() => _full, fullScreen =>
		{
			_applied.Add(fullScreen);
			_full = fullScreen;
		});
	}

	[Fact]
	public void ARequestSwitchesTheWindowAndWaitsForItToSettle()
	{
		Assert.Equal(FullScreenSwitch.Outcome.Applied, _switch.Request(true));
		Assert.True(_switch.IsSwitching);
		Assert.Equal([true], _applied);

		_switch.Settled();
		Assert.False(_switch.IsSwitching);
	}

	[Fact]
	public void AskingForTheStateTheWindowIsInChangesNothing()
	{
		Assert.Equal(FullScreenSwitch.Outcome.Unchanged, _switch.Request(false));
		Assert.Empty(_applied);
		Assert.False(_switch.IsSwitching);
	}

	[Fact]
	public void TheKeyPressedTwiceQuicklyDoesNotStartASecondSwitchOnTheFirst()
	{
		_switch.Request(true);
		Assert.Equal(FullScreenSwitch.Outcome.Queued, _switch.Request(false));
		Assert.Equal([true], _applied);

		// ... and runs after it, once it has settled
		_switch.Settled();
		Assert.Equal([true, false], _applied);
		Assert.True(_switch.IsSwitching);
		_switch.Settled();
		Assert.False(_switch.IsSwitching);
	}

	[Fact]
	public void TheLastRequestWinsAndAnEvenNumberOfPressesEndsWhereItStarted()
	{
		_switch.Request(true);
		_switch.Request(false);
		_switch.Request(true);
		_switch.Request(false);
		Assert.False(_switch.Target);

		_switch.Settled();
		Assert.Equal([true, false], _applied);
		_switch.Settled();
		Assert.False(_full);
		Assert.False(_switch.IsSwitching);
	}

	[Fact]
	public void ARequestForWhatTheRunningSwitchMakesIsNotRepeated()
	{
		_switch.Request(true);
		_switch.Request(true);
		_switch.Settled();
		Assert.Equal([true], _applied);
		Assert.False(_switch.IsSwitching);
	}

	[Fact]
	public void SettlingTwiceOrWithNothingRunningDoesNothing()
	{
		_switch.Settled();
		_switch.Request(true);
		_switch.Settled();
		_switch.Settled();
		Assert.Equal([true], _applied);
	}

	[Fact]
	public void ASwitchThatThrowsLeavesTheNextRequestFree()
	{
		var failing = true;
		var sw = new FullScreenSwitch(() => false, _ =>
		{
			if (failing)
			{
				throw new InvalidOperationException("no presenter");
			}
		});

		Assert.Throws<InvalidOperationException>(() => sw.Request(true));
		Assert.False(sw.IsSwitching);
		failing = false;
		Assert.Equal(FullScreenSwitch.Outcome.Applied, sw.Request(true));
	}

	[Fact]
	public void TargetIsTheLastRequestedStateWhileASwitchRuns()
	{
		_switch.Request(true);
		Assert.True(_switch.Target);
		_switch.Request(false);
		Assert.False(_switch.Target);
	}
}
