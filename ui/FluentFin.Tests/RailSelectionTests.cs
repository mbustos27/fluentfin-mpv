using FluentFin.ViewModels;

namespace FluentFin.Tests;

/// <summary>
/// Seaglass 1.1.3 (review): Settings stayed marked on the rail after Back from it to an item page (a page off the rail never
/// changed the mark). Now a page off the rail unmarks a footer item and keeps a section (Home, Libraries) marked.
/// </summary>
public sealed class RailSelectionTests
{
	private static bool Footer(string item) => item == "Settings";

	[Fact]
	public void BackFromSettings_ToAPageOffTheRail_UnmarksSettings()
	{
		Assert.Null(RailSelection.Next("Settings", null, Footer));
	}

	[Theory]
	[InlineData("Home")]
	[InlineData("Libraries")]
	public void APageOffTheRail_KeepsItsSectionMarked(string section)
	{
		Assert.Equal(section, RailSelection.Next(section, null, Footer));
	}

	[Theory]
	[InlineData(null, "Settings")]
	[InlineData("Settings", "Home")]
	[InlineData("Home", "Libraries")]
	public void ARailDestination_MarksItself(string? current, string target)
	{
		Assert.Equal(target, RailSelection.Next(current, target, Footer));
	}

	[Fact]
	public void NothingMarked_StaysUnmarked()
	{
		Assert.Null(RailSelection.Next<string>(null, null, Footer));
	}
}
