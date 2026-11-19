using FluentFin.Core.Mpv;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Tests;

/// <summary>Nixie 1.4.0 (the UX review's E-M10, plan item 14): the shared bar's Chapters menu, in every theme.</summary>
public sealed class PlayerChaptersTests
{
	private static long Seconds(double s) => TimeSpan.FromSeconds(s).Ticks;

	[Fact]
	public void TheChapters_InOrder_WithTheirTimes_TheOnePlayingMarked()
	{
		ChapterInfo[] chapters =
		[
			new() { Name = "The Harbor", StartPositionTicks = Seconds(300) },
			new() { Name = "Opening", StartPositionTicks = 0 },
			new() { Name = "  ", StartPositionTicks = Seconds(3725) },
			new() { Name = "No start" },
		];

		var menu = PlayerChapters.From(chapters, Seconds(320));

		Assert.Equal(["0:00  Opening", "5:00  The Harbor", "1:02:05  Chapter 3"], menu.Select(c => c.Label));
		Assert.Equal([false, true, false], menu.Select(c => c.IsCurrent));
		Assert.Equal(Seconds(300), menu[1].StartTicks);
		Assert.Equal("Go to The Harbor, 5:00, playing now", menu[1].SpokenName);
		Assert.Equal("Go to Opening, 0:00", menu[0].SpokenName);
	}

	[Fact]
	public void NoChapters_TheButtonSaysSo()
	{
		Assert.Empty(PlayerChapters.From(null, 0));
		Assert.Equal("No chapters", PlayerChapters.ToolTip(0));
		Assert.Equal("Chapters, none", PlayerChapters.ButtonName(0));
		Assert.Equal("Chapters", PlayerChapters.ToolTip(6));
		Assert.Equal("Chapters, 6", PlayerChapters.ButtonName(6));
	}
}
