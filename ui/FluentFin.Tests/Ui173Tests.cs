using System.Xml.Linq;
using FluentFin.Themes.DriveIn.Controls;
using FluentFin.Views;
using Microsoft.Extensions.DependencyInjection;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.7.3, stream C (UI): the Collections page's arrival focus under load and its reading line's space (BH-171-m4),
/// Drive-in's Up next board, and the Libraries page's art crop. The harness checks what the user sees of each
/// (tools\ui-harness\offscreen\then-173-rows.ps1, then-173-drivein.ps1, then-160-collections.ps1).
/// </summary>
public sealed class Ui173Tests
{
	private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

	private static XDocument Xaml(params string[] parts) => XDocument.Load(AppVersionTests.FindUp(Path.Combine(["ui", "FluentFin", .. parts])));

	private static string Code(params string[] parts) => File.ReadAllText(AppVersionTests.FindUp(Path.Combine(["ui", "FluentFin", .. parts])));

	// ---- 1.7.3 fix round: the page's wiring of the two rules below, which only the harness drove (stream C's review) -------

	[Fact]
	public void ArrivalFocus_TheRetryIsWiredToTheRule_ByLoadedAndByATimerThatCannotEndTheApp()
	{
		var code = Code("Views", "CollectionsPage.xaml.cs");
		var take = code[code.IndexOf("private void TakeArrivalFocus(int tries)", StringComparison.Ordinal)..code.IndexOf("private int EndArrivalTries()", StringComparison.Ordinal)];
		Assert.Contains("NextArrivalTry(IsLoaded, ArrivalFocus.Shown(this), tries)", take);
		Assert.Contains("next is ArrivalRetry.Loaded or ArrivalRetry.LoadedOrTimer", take);
		Assert.Contains("Loaded += Once;", take);
		Assert.Contains("next is ArrivalRetry.Timer or ArrivalRetry.LoadedOrTimer", take);
		Assert.Contains("TakeArrivalFocus(tries - 1)", take);
		Assert.Contains("TakeArrivalFocus(ArrivalTries)", take); // a fresh count once it is in the window
		// the close crash's class: the retry's timer tick through UiDispatch.Tick (nothing once closing, never a throw)
		Assert.Contains("timer.Tick += UiDispatch.Tick(", take);
	}

	[Theory]
	// the close crash (0xc000027b, a stowed E_UNEXPECTED, 3 to 9 s to exit): what ran in the window's teardown on the player
	// page, read XAML there and threw out of a dispatcher callback. The harness proves the close (then-173-close-nompv.ps1);
	// these keep each guard where it was put
	[InlineData("Themes/DriveIn/Controls/DriveInPlayer.cs", "timer.Tick += UiDispatch.Tick(\"the Drive-in player's dressing\"")]
	[InlineData("Mpv/MpvVideoHost.cs", "private void OnResync(DispatcherQueueTimer sender, object args)\n\t{\n\t\t// 1.7.3 fix round (the close crash)")]
	[InlineData("Controls/MpvPlayerControls.xaml.cs", "private void OnTick(DispatcherQueueTimer sender, object args)\n\t{\n\t\t// 1.7.3 fix round (the close crash)")]
	[InlineData("Views/VideoPlayerPage.xaml.cs", "private void OnUnloaded(object sender, RoutedEventArgs e)\n\t{\n\t\t// 1.7.3 fix round (the close crash)")]
	[InlineData("Views/TitleBarControl.xaml.cs", "private void Relayout()\n\t{\n\t\tif (App.IsClosing)")]
	[InlineData("Controls/TrailerTheater.cs", "if (App.IsClosing || App.MainWindow.AppWindow is null)")]
	public void TheClose_NothingOfThePlayerPageReadsXamlInTheWindowsTeardown(string file, string guard)
	{
		var code = Code([.. file.Split('/')]).Replace("\r\n", "\n", StringComparison.Ordinal);
		Assert.Contains(guard, code);
	}

	[Fact]
	public void TheClose_EveryTimerTickInTheApp_DoesNothingOnceClosing()
	{
		// the re-review of the fix round: Drive-in Home's hero (OutdoorScreen, the default theme's) was missed. Every Tick handler
		// of a DispatcherQueueTimer or DispatcherTimer in the app goes through UiDispatch.Tick or checks App.IsClosing first
		var root = Path.GetDirectoryName(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "App.xaml.cs")))!;
		var sites = 0;
		var unguarded = new List<string>();
		foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
			&& !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
		{
			var lines = File.ReadAllLines(file);
			for (var i = 0; i < lines.Length; i++)
			{
				var at = lines[i].IndexOf(".Tick += ", StringComparison.Ordinal);
				if (at < 0 || lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
				{
					continue;
				}

				sites++;
				var handler = lines[i][(at + ".Tick += ".Length)..].Trim();
				string body;
				if (handler.StartsWith("UiDispatch.Tick(", StringComparison.Ordinal))
				{
					continue;
				}
				else if (handler.StartsWith('('))
				{
					body = string.Join("\n", lines.Skip(i).Take(8));
				}
				else
				{
					var name = handler.TrimEnd(';');
					var method = Array.FindIndex(lines, l => l.Contains($" void {name}(", StringComparison.Ordinal));
					body = method < 0 ? "" : string.Join("\n", lines.Skip(method).Take(8));
				}

				if (!body.Contains("App.IsClosing", StringComparison.Ordinal))
				{
					unguarded.Add($"{Path.GetFileName(file)}:{i + 1}");
				}
			}
		}

		Assert.True(sites >= 28, $"{sites} Tick handlers found");
		Assert.Empty(unguarded);
	}

	[Fact]
	public void TheLog_IsOnlyNixiesMaskedFile_NotTheWindowsEventLog()
	{
		// the re-review of the fix round (privacy): the host's default builder added the Event Log provider beside Serilog's file,
		// so every Warning and Error went to the Application log too, without the token masking
		using var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
			.ConfigureServices((_, s) => s.AddLogging(FluentFin.Services.NixieLogging.OnlyNixiesLog))
			.Build();
		var providers = host.Services.GetServices<Microsoft.Extensions.Logging.ILoggerProvider>().Select(p => p.GetType().Name).ToList();

		Assert.DoesNotContain(providers, n => n.Contains("EventLog", StringComparison.Ordinal));
		Assert.DoesNotContain(providers, n => n.Contains("Console", StringComparison.Ordinal) || n.Contains("Debug", StringComparison.Ordinal) || n.Contains("EventSource", StringComparison.Ordinal));
		Assert.Single(providers, n => n.Contains("Serilog", StringComparison.Ordinal));
		Assert.Contains("services.AddLogging(NixieLogging.OnlyNixiesLog);", Code("App.xaml.cs"));
	}

	[Fact]
	public void TheClose_ATickOfUiDispatch_DoesNothingOnceClosing_AndNeverThrows()
	{
		var code = Code("Services", "UiDispatch.cs").Replace("\r\n", "\n", StringComparison.Ordinal);
		var tick = code[code.IndexOf("public static Windows.Foundation.TypedEventHandler<DispatcherQueueTimer, object> Tick(", StringComparison.Ordinal)..];
		tick = tick[..tick.IndexOf("\t};", StringComparison.Ordinal)];
		Assert.Contains("if (App.IsClosing)", tick);
		Assert.Contains("Run(what, tick);", tick); // Run catches and logs
		// the crash's trail: once closing, every exception is logged, by a logger taken at the start
		Assert.Contains("(s_log ?? App.GetService<ILogger<App>>())", code);
	}

	[Fact]
	public void ReadingLine_ThePanelFollowsTheViewModel_ThroughTheRule()
	{
		var code = Code("Views", "CollectionsPage.xaml.cs");
		Assert.Contains("ViewModel.PropertyChanged += OnViewModelChanged;", code);
		Assert.Contains("e.PropertyName is nameof(CollectionsViewModel.ShowsReadingLine)", code);
		var show = code[code.IndexOf("private void ShowReadingLine()", StringComparison.Ordinal)..];
		Assert.Contains("ReadingLineFor(ViewModel.ShowsReadingLine, _lineShown)", show);
		Assert.Contains("ReadingPanel.Visibility = state == ReadingLineState.Gone ? Visibility.Collapsed : Visibility.Visible;", show);
	}

	private static XElement Named(XDocument doc, string name) => doc.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

	// ---- the Collections page's arrival focus ---------------------------------------------------------------------------

	[Fact]
	public void ArrivalFocus_APageNotInTheWindowButStillShown_TriesAgain_WhenLoadedOrOnTheTimer()
	{
		// 1 of 7 Drive-in runs under load: the page had been loaded, wasn't at the try, and 1.7.1 gave up (the keyboard on Back)
		Assert.Equal(CollectionsPage.ArrivalRetry.LoadedOrTimer, CollectionsPage.NextArrivalTry(loaded: false, shown: true, tries: 12));
		Assert.Equal(CollectionsPage.ArrivalRetry.LoadedOrTimer, CollectionsPage.NextArrivalTry(loaded: false, shown: true, tries: 2));
		// its tries used up while it waited: still when it is in the window (then with a fresh count)
		Assert.Equal(CollectionsPage.ArrivalRetry.Loaded, CollectionsPage.NextArrivalTry(loaded: false, shown: true, tries: 1));
	}

	[Fact]
	public void ArrivalFocus_InTheWindow_TheTimerWhileTriesLast_AndAPageThatLeft_GivesUp()
	{
		Assert.Equal(CollectionsPage.ArrivalRetry.Timer, CollectionsPage.NextArrivalTry(loaded: true, shown: true, tries: 12));
		Assert.Equal(CollectionsPage.ArrivalRetry.Timer, CollectionsPage.NextArrivalTry(loaded: true, shown: true, tries: 2));
		Assert.Equal(CollectionsPage.ArrivalRetry.None, CollectionsPage.NextArrivalTry(loaded: true, shown: true, tries: 1));
		foreach (var loaded in new[] { true, false })
		{
			Assert.Equal(CollectionsPage.ArrivalRetry.None, CollectionsPage.NextArrivalTry(loaded, shown: false, tries: 12));
		}
	}

	// ---- the reading line keeps its space (BH-171-m4) ------------------------------------------------------------------

	[Fact]
	public void ReadingLine_WhenItGoes_ItsSpaceStaysForTheVisit_AndAVisitWithoutItHasNone()
	{
		Assert.Equal(CollectionsPage.ReadingLineState.Shown, CollectionsPage.ReadingLineFor(shows: true, shownThisVisit: false));
		Assert.Equal(CollectionsPage.ReadingLineState.Shown, CollectionsPage.ReadingLineFor(shows: true, shownThisVisit: true));
		// the rows under the keyboard and the pointer don't move up 40 px when the read is done
		Assert.Equal(CollectionsPage.ReadingLineState.SpaceKept, CollectionsPage.ReadingLineFor(shows: false, shownThisVisit: true));
		Assert.Equal(CollectionsPage.ReadingLineState.Gone, CollectionsPage.ReadingLineFor(shows: false, shownThisVisit: false));
	}

	[Fact]
	public void ReadingLine_ThePanelIsThePages_TheLineInsideFollowsTheViewModel()
	{
		var page = Xaml("Views", "CollectionsPage.xaml");
		var panel = Named(page, "ReadingPanel");
		// the panel: shown and kept by the page (ShowReadingLine), never collapsed by a binding, at least the ring's height
		Assert.Equal("Collapsed", (string?)panel.Attribute("Visibility"));
		Assert.Equal("20", (string?)panel.Attribute("MinHeight"));
		// the line inside: gone with the read (nothing left for Narrator), its ring only while reading
		var line = Named(page, "ReadingLine");
		Assert.Same(panel, line.Parent);
		Assert.Equal("{x:Bind ViewModel.ShowsReadingLine, Mode=OneWay}", (string?)line.Attribute("Visibility"));
		Assert.Contains(line.Descendants(), e => e.Name.LocalName == "TextBlock" && (string?)e.Attribute("Text") == "{x:Bind ViewModel.ReadingText, Mode=OneWay}");
	}

	// ---- Drive-in's Up next board -------------------------------------------------------------------------------------

	[Fact]
	public void UpNextBoard_TheLinkCountsTheSignsList_AnEmptyBoardSaysSo_OnceNextUpIsIn()
	{
		Assert.Equal(("All of Up next (1)", (string?)null), ComingUpSign.BoardText(1, known: true));
		Assert.Equal(("All of Up next (9)", (string?)null), ComingUpSign.BoardText(9, known: true));
		Assert.Equal(("All of Up next (2)", (string?)null), ComingUpSign.BoardText(2, known: false));
		// Next Up empty (Continue Watching full or not): no link counting what the board doesn't list
		Assert.Equal(((string?)null, ComingUpSign.NothingUpNext), ComingUpSign.BoardText(0, known: true));
		// not known yet (on its way, or Home had a problem): nothing said
		Assert.Equal(((string?)null, (string?)null), ComingUpSign.BoardText(0, known: false));
		Assert.Equal("Nothing up next", ComingUpSign.NothingUpNext);
	}

	[Fact]
	public void UpNextBoard_SaysNothingUpNext_OnlyWhenNextUpAnsweredAndHomeHadNoProblem()
	{
		Assert.True(ComingUpSign.KnowsNextUp(nextUpLoading: false, Core.ViewModels.LoadStatus.Loaded, homeHasProblem: false));
		Assert.True(ComingUpSign.KnowsNextUp(nextUpLoading: false, Core.ViewModels.LoadStatus.Empty, homeHasProblem: false));
		// still asking, or Home still loading
		Assert.False(ComingUpSign.KnowsNextUp(nextUpLoading: true, Core.ViewModels.LoadStatus.Loaded, homeHasProblem: false));
		Assert.False(ComingUpSign.KnowsNextUp(nextUpLoading: false, Core.ViewModels.LoadStatus.Loading, homeHasProblem: false));
		// a Next Up request that failed leaves the list empty too: Home says "Some of Home couldn't be loaded", the board
		// doesn't claim there is nothing up next (rule 11: say what Nixie couldn't do, not a false empty)
		Assert.False(ComingUpSign.KnowsNextUp(nextUpLoading: false, Core.ViewModels.LoadStatus.Loaded, homeHasProblem: true));
		Assert.False(ComingUpSign.KnowsNextUp(nextUpLoading: false, Core.ViewModels.LoadStatus.Failed, homeHasProblem: false));
	}

	[Fact]
	public void UpNextBoard_TheSignHasNoCountOfItsOwn_HomeGivesItOneList()
	{
		// the count is the sign's own list's (Items), never a number Home adds up beside it
		Assert.Null(typeof(ComingUpSign).GetProperty("UpNextCount"));
		var code = File.ReadAllText(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "Themes", "DriveIn", "Views", "DriveInHomePage.xaml.cs")));
		Assert.Contains("Sign.Items = ViewModel.DriveInUpNext();", code);
		Assert.Contains("Sign.IsNextUpKnown = ComingUpSign.KnowsNextUp(ViewModel.IsNextUpLoading, ViewModel.State.Status, ViewModel.State.HasProblem);", code);
		var sign = Xaml("Themes", "DriveIn", "Controls", "ComingUpSign.xaml");
		// "Nothing up next" where the entries go
		Assert.Equal((string?)Named(sign, "Entries").Attribute("Grid.Row"), (string?)Named(sign, "EmptyText").Attribute("Grid.Row"));
	}

	// ---- the Libraries page's art: cropped round its middle ------------------------------------------------------------

	[Fact]
	public void LibraryTiles_ArtOfAnotherShape_IsCroppedRoundItsMiddle_NotToItsTopStrip()
	{
		var images = Xaml("Views", "LibrariesLandingPage.xaml").Descendants().Where(e => e.Name.LocalName == "LoadingImage").ToList();
		Assert.Equal(2, images.Count); // the media libraries' tile and the collections' and playlists'
		Assert.All(images, image =>
		{
			Assert.Equal("UniformToFill", (string?)image.Attribute("Stretch"));
			// a logo on a dark card (square, or taller than 16:9) showed only the card's top strip under Stretch
			Assert.Equal("Center", (string?)image.Attribute("VerticalAlignment"));
			// across it stays Stretch: the loading and failed plate (its glyph on grey) still fills the tile
			Assert.Null(image.Attribute("HorizontalAlignment"));
		});

		// the mechanism: the image inside LoadingImage's template takes the control's alignment
		var style = Xaml("Styles", "ImageEx.xaml");
		var inner = style.Descendants().Single(e => e.Name.LocalName == "Image" && (string?)e.Attribute("Name") == "Image");
		Assert.Equal("{TemplateBinding VerticalAlignment}", (string?)inner.Attribute("VerticalAlignment"));
	}
}
