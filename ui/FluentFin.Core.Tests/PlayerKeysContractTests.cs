using System.Xml.Linq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.3.1: themes restyle and re-lay out the player, never change what a key does. The shared overlay
/// (MpvPlayerControls.xaml) keeps the named parts the key router, the after-click step and the off-screen key matrix rely on,
/// its controls take no focus from a click, and each theme's player styles set only looks and layout (no focus, visibility
/// or hit-testing of their own). Fails when a theme or an edit drops or renames one of those parts.
/// </summary>
public sealed class PlayerKeysContractTests
{
	private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

	/// <summary>
	/// The parts by name: the bars (the router's "in the bars", auto-hide), the seek bar (the router's SeekBar kind), and every
	/// control the matrix clicks or reads (then-keys-matrix.ps1).
	/// </summary>
	public static readonly string[] Parts =
	[
		"Root", "VideoPanel", "TopBar", "BottomBar", "PillRow", "BackButton", "SeekBar", "ElapsedText", "RemainingText", "VolumeButton",
		"PreviousButton", "SkipBackButton", "PlayPauseButton", "SkipForwardButton", "NextButton", "SpeedButton", "TracksButton",
		"FullScreenButton", "VolumeFlyout", "TracksMenu", "SpeedMenu", "MuteButton", "VolumeSlider",
	];

	/// <summary>The controls of the bars and the pills: none takes the focus from a click (the keyboard stays on the player).</summary>
	public static readonly string[] ClickedWithoutFocus =
	[
		"BackButton", "SeekBar", "VolumeButton", "PreviousButton", "SkipBackButton", "PlayPauseButton", "SkipForwardButton", "NextButton",
		"SpeedButton", "TracksButton", "FullScreenButton", "CancelNextButton", "NextPill", "SkipSegmentButton",
	];

	/// <summary>What a theme's PlayerTopBarStyle and PlayerBottomBarStyle may set: looks and layout, and the themes' own dressing.</summary>
	public static readonly string[] AllowedSetters =
	[
		"Padding", "Margin", "Background", "BorderBrush", "BorderThickness", "CornerRadius", "HorizontalAlignment", "VerticalAlignment",
		"Width", "MaxWidth", "MinWidth", "Height", "MinHeight", "MaxHeight", "Opacity",
		"dc:DriveInPlayer.Console", "dc:DriveInPlayer.Clock", "cc:CinematicPlayer.TopTabs",
	];

	private static XDocument Load(params string[] parts) => XDocument.Load(Path.Combine([RepoRoot, "FluentFin", .. parts]));

	[Fact]
	public void TheOverlay_KeepsEveryPartByName()
	{
		var names = Load("Controls", "MpvPlayerControls.xaml").Descendants().Select(e => (string?)e.Attribute(X + "Name")).OfType<string>().ToHashSet();
		Assert.All(Parts, part => Assert.Contains(part, names));
	}

	[Fact]
	public void TheBarsControls_TakeNoFocusFromAClick_AndStayTabStops()
	{
		var doc = Load("Controls", "MpvPlayerControls.xaml");
		foreach (var name in ClickedWithoutFocus)
		{
			var element = doc.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);
			Assert.Equal("False", (string?)element.Attribute("AllowFocusOnInteraction"));
			Assert.Null(element.Attribute("IsTabStop"));
		}
	}

	[Fact]
	public void ThePagesRouter_IsAtTheWindow_NotOnThePlayersGrid()
	{
		var page = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin", "Views", "VideoPlayerPage.xaml"));
		Assert.DoesNotContain("PreviewKeyDown=", page);
		var code = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin", "Views", "VideoPlayerPage.xaml.cs"));
		Assert.Contains("MpvPlayerKeys.OnPreviewKeyDown(MpvControlsView", code);
		Assert.Contains("root.AddHandler(PreviewKeyDownEvent, _rootPreviewKeyDown, handledEventsToo: true)", code);
	}

	[Theory]
	[InlineData("Classic")]
	[InlineData("DriveIn")]
	[InlineData("Cinematic")]
	[InlineData("Editorial")]
	public void EachThemesPlayerStyles_SetOnlyLooksAndLayout(string theme)
	{
		var file = Path.Combine(RepoRoot, "FluentFin", "Themes", theme, theme + ".xaml");
		if (!File.Exists(file))
		{
			// Classic is today's look: no dictionary of player styles (the overlay's own)
			Assert.Equal("Classic", theme);
			return;
		}

		var styles = XDocument.Load(file).Descendants().Where(e => e.Name.LocalName == "Style"
			&& (string?)e.Attribute(X + "Key") is "PlayerTopBarStyle" or "PlayerBottomBarStyle").ToList();
		foreach (var style in styles)
		{
			Assert.Equal("Grid", (string?)style.Attribute("TargetType"));
			foreach (var setter in style.Elements().Where(e => e.Name.LocalName == "Setter"))
			{
				var property = (string?)setter.Attribute("Property");
				Assert.True(property is not null && AllowedSetters.Contains(property), $"{theme}'s {style.Attribute(X + "Key")!.Value} sets {property}");
			}
		}
	}

	[Fact]
	public void DriveInsConsole_DressesOnlyPartsThatExist()
	{
		var code = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin", "Themes", "DriveIn", "Controls", "DriveInPlayer.cs"));
		var names = Load("Controls", "MpvPlayerControls.xaml").Descendants().Select(e => (string?)e.Attribute(X + "Name")).OfType<string>().ToHashSet();
		var parts = System.Text.RegularExpressions.Regex.Match(code, "Parts = \\[(?<list>[^\\]]*)\\]").Groups["list"].Value
			.Split(',').Select(p => p.Trim().Trim('"')).Where(p => p.Length > 0).ToList();
		Assert.NotEmpty(parts);
		Assert.All(parts, part => Assert.Contains(part, names));
		// it never takes focus or hides a control from the keyboard
		Assert.DoesNotContain("IsTabStop", code);
		Assert.DoesNotContain("AllowFocusOnInteraction", code);
		Assert.DoesNotContain(".Focus(", code);
	}

	[Fact]
	public void CinematicsPlayerTabs_AreAListToTheRouter_AndTakeNoFocusFromAClick()
	{
		var panel = Load("Themes", "Cinematic", "Controls", "CinematicPlayerPanel.xaml");
		var strip = panel.Descendants().Single(e => e.Name.LocalName == "CinematicTabStrip");
		Assert.Equal("False", (string?)strip.Attribute("TabsTakeFocusOnClick"));
		var code = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin", "Themes", "Cinematic", "Controls", "CinematicPlayerPanel.xaml.cs"));
		Assert.Contains("MpvPlayerKeys.SetOwnsArrows(this, true)", code);
		Assert.Contains("ClosePanelsRequested += OnClosePanelsRequested", code);
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
