using FluentFin.Controls;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.4.0 (the UX review's X12 and C-m10): every theme's season list marks "Up next" on the episode its Play starts, and
/// Narrator hears it.
/// </summary>
public sealed class UpNextMarkTests
{
	[Fact]
	public void TheListsEpisode_IsMarked_OthersAndNoneAreNot()
	{
		var up = Guid.NewGuid();

		Assert.True(UpNextMark.Marks(up, up));
		Assert.False(UpNextMark.Marks(up, Guid.NewGuid()));
		// every episode watched: Play starts again from the first, nothing is up next
		Assert.False(UpNextMark.Marks(null, up));
		Assert.False(UpNextMark.Marks(up, null));
	}

	[Fact]
	public void NarratorHearsIt_AfterTheRowsOwnName()
	{
		Assert.Equal("S1 E4 After the Engine, up next", UpNextMark.Spoken("S1 E4 After the Engine", upNext: true));
		Assert.Equal("S1 E4 After the Engine", UpNextMark.Spoken("S1 E4 After the Engine", upNext: false));
		Assert.Equal("Up next", UpNextMark.Label);
	}
}
