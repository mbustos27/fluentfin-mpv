using FluentFin.Controls;
using Windows.Foundation;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.4.0 (the UX review's item 15): the loading placeholders are compared with the grid's first cells, and the log says
/// what differs (the review measured 5 tiles a row against 6, and 42 px high).
/// </summary>
public sealed class LibraryProbeTests
{
	private static List<Rect> Row(int count, double width, double height, double gap, double x = 48, double y = 200) =>
		[.. Enumerable.Range(0, count).Select(i => new Rect(x + i * (width + gap), y, width, height))];

	[Fact]
	public void TheSameCells_AreTheSame_WithinRounding()
	{
		var grid = Row(6, 220, 390, 16);
		var placeholders = grid.Select(r => new Rect(r.X + 1, r.Y - 1, r.Width + 1.5, r.Height)).ToList();
		Assert.Empty(LibraryProbe.Differences(placeholders, grid));
	}

	[Fact]
	public void FiveARowAgainstSix_Differs()
	{
		var differences = LibraryProbe.Differences(Row(5, 268, 390, 16), Row(6, 220, 390, 16));
		Assert.NotEmpty(differences);
		Assert.StartsWith("cell 0: placeholder 48,200 268x390, grid 48,200 220x390", differences[0]);
	}

	[Fact]
	public void PlaceholdersTooLow_Differ()
	{
		Assert.Equal(6, LibraryProbe.Differences(Row(6, 220, 42, 16), Row(6, 220, 390, 16)).Count);
		Assert.Equal(6, LibraryProbe.Differences(Row(6, 220, 390, 16, y: 260), Row(6, 220, 390, 16)).Count);
	}
}
