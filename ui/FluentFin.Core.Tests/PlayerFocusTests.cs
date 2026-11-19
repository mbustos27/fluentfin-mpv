using System.Xml.Linq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.4.0 (the UX review's X8, plan item 4's player parts): the player's Tab order runs left to right with the prompts
/// first while they show, and the keyboard comes back to the item page's Play after the player (Drive-in's Play had no name,
/// so Back from the player left the focus nowhere: DI-M5).
/// </summary>
public sealed class PlayerFocusTests
{
	private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

	private static XDocument Load(params string[] parts) => XDocument.Load(Path.Combine([RepoRoot, "FluentFin", .. parts]));

	[Fact]
	public void ThePrompts_AreFirstInTheTabOrder_AndDrawnOverTheBars()
	{
		var root = Load("Controls", "MpvPlayerControls.xaml").Descendants().Single(e => (string?)e.Attribute(X + "Name") == "Root");
		var order = root.Elements().Select(e => (string?)e.Attribute(X + "Name")).OfType<string>().ToList();

		// XAML order is Tab order: the prompts, then the top bar (Back), then the bottom bar (the seek bar, the transport)
		Assert.True(order.IndexOf("PillRow") < order.IndexOf("TopBar"), string.Join(", ", order));
		Assert.True(order.IndexOf("TopBar") < order.IndexOf("BottomBar"), string.Join(", ", order));
		var pills = root.Elements().Single(e => (string?)e.Attribute(X + "Name") == "PillRow");
		Assert.Equal("1", (string?)pills.Attribute("Canvas.ZIndex"));
		Assert.All(pills.Elements(), p => Assert.Null(p.Attribute("TabIndex")));

		// the bottom bar's controls in reading order: the seek bar, then the transport row from the left
		var bar = root.Elements().Single(e => (string?)e.Attribute(X + "Name") == "BottomBar");
		var named = bar.Descendants().Select(e => (string?)e.Attribute(X + "Name")).OfType<string>()
			.Where(n => n is "SeekBar" or "VolumeButton" or "PreviousButton" or "SkipBackButton" or "PlayPauseButton" or "SkipForwardButton" or "NextButton" or "ChaptersButton" or "SpeedButton" or "TracksButton" or "FullScreenButton")
			.ToList();
		Assert.Equal(["SeekBar", "VolumeButton", "PreviousButton", "SkipBackButton", "PlayPauseButton", "SkipForwardButton", "NextButton", "ChaptersButton", "SpeedButton", "TracksButton", "FullScreenButton"], named);
	}

	[Theory]
	[InlineData("Views", "MoviePage.xaml")]
	[InlineData("Views", "EpisodePage.xaml")]
	[InlineData("Themes", "DriveIn", "Views", "DriveInMoviePage.xaml")]
	[InlineData("Themes", "DriveIn", "Views", "DriveInEpisodePage.xaml")]
	[InlineData("Themes", "DriveIn", "Views", "DriveInSeasonPage.xaml")]
	[InlineData("Themes", "DriveIn", "Views", "DriveInSeriesPage.xaml")]
	[InlineData("Themes", "Cinematic", "Views", "CinematicMoviePage.xaml")]
	[InlineData("Themes", "Cinematic", "Views", "CinematicEpisodePage.xaml")]
	[InlineData("Themes", "Editorial", "Views", "EditorialMoviePage.xaml")]
	[InlineData("Themes", "Editorial", "Views", "EditorialEpisodePage.xaml")]
	public void EachItemPagesPlay_IsNamed_SoTheKeyboardComesBackToIt(params string[] parts)
	{
		var play = Load(parts).Descendants().Single(e => (string?)e.Attribute(X + "Name") == "PlayButton");
		var command = (string?)play.Attribute("Command") ?? (string?)play.Attribute("Click") ?? "";
		Assert.Contains("Play", command, StringComparison.Ordinal);
	}

	private static string RepoRoot { get; } = FindRepoRoot();

	private static string FindRepoRoot()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FluentFin.sln")))
		{
			dir = dir.Parent;
		}

		return dir?.FullName ?? throw new DirectoryNotFoundException("FluentFin.sln not found above " + AppContext.BaseDirectory);
	}
}
