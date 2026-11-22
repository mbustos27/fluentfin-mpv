using FluentFin.Core.Shortcuts;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.6.1 (fix queue): the "/" that opens the search box is not typed into it when its character comes after the focus; a "/"
/// the user types afterwards is.
/// </summary>
public sealed class SlashSwallowTests
{
	private sealed class Clock : TimeProvider
	{
		public long Now { get; set; }

		public override long TimestampFrequency => TimeSpan.TicksPerSecond;

		public override long GetTimestamp() => Now;

		public void Advance(TimeSpan by) => Now += by.Ticks;
	}

	[Theory]
	[InlineData("", 0, 0, "/")]                    // the empty box: "/"
	[InlineData("Signal", 0, 6, "/")]              // a kept search, all selected by the focus: "/" over it
	[InlineData("Signal", 6, 0, "Signal/")]        // the caret at its end
	[InlineData("Signal", 2, 2, "Si/al")]          // over a selection inside
	public void ThePressesOwnSlash_IsSwallowed_Once(string old, int start, int length, string now)
	{
		var slash = new SlashSwallow(new Clock());
		slash.Arm();

		Assert.True(slash.Swallows(old, start, length, now));
		Assert.False(slash.IsArmed);
		Assert.False(slash.Swallows(old, start, length, now)); // the user's next "/" is typed
	}

	[Theory]
	[InlineData("", 0, 0, "S")]                    // the next key's letter: the press made no character (the usual case)
	[InlineData("", 0, 0, "//")]
	[InlineData("Signal", 0, 6, "S")]
	[InlineData("Signal", 6, 0, "/Signal")]        // not where the caret is
	[InlineData("", 0, 0, "")]
	public void AnyOtherChange_IsTyped_AndDisarms(string old, int start, int length, string now)
	{
		var slash = new SlashSwallow(new Clock());
		slash.Arm();

		Assert.False(slash.Swallows(old, start, length, now));
		Assert.False(slash.IsArmed);
		Assert.False(slash.Swallows("", 0, 0, "/"));
	}

	[Fact]
	public void NotArmed_OrDisarmedByAnotherKey_OrTooLate_IsTyped()
	{
		var clock = new Clock();
		var slash = new SlashSwallow(clock);
		Assert.False(slash.Swallows("", 0, 0, "/"));            // Ctrl+F opened it: never armed

		slash.Arm();
		slash.Disarm();                                          // another key came first
		Assert.False(slash.Swallows("", 0, 0, "/"));

		slash.Arm();
		clock.Advance(SlashSwallow.Window + TimeSpan.FromMilliseconds(1));
		Assert.False(slash.IsArmed);
		Assert.False(slash.Swallows("", 0, 0, "/"));            // a "/" typed later is the user's

		slash.Arm();
		clock.Advance(SlashSwallow.Window - TimeSpan.FromMilliseconds(1));
		Assert.True(slash.Swallows("", 0, 0, "/"));
	}

	[Theory]
	[InlineData(null, 0, 0, "/", true)]
	[InlineData("ab", 9, 9, "ab/", true)]                    // a selection past the end counts as the end
	[InlineData("ab", -3, 0, "/ab", true)]
	[InlineData("ab", 0, 0, null, false)]
	public void OddSelections_AreClamped(string? old, int start, int length, string? now, bool swallowed)
	{
		var slash = new SlashSwallow(new Clock());
		slash.Arm();

		Assert.Equal(swallowed, slash.Swallows(old, start, length, now));
	}
}
