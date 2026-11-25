using System.Globalization;
using System.Xml.Linq;
using FluentFin.Controls;
using FluentFin.Services;
using FluentFin.Themes.DriveIn.Controls;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.7.5, stream fx3 (UI fixes and the Drive-in crash's trail): Drive-in's item board under the top bar, the close
/// guards of Home's heroes, the crash trail, the rows' keyboard and Settings' Check now. The harness checks what the user
/// sees of each (tools\ui-harness\offscreen\then-175-fx3.ps1).
/// </summary>
public sealed class Ui175Tests
{
	private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
	private static readonly XNamespace Dc = "using:FluentFin.Themes.DriveIn.Controls";

	private static XDocument Xaml(params string[] parts) => XDocument.Load(AppVersionTests.FindUp(Path.Combine(["ui", "FluentFin", .. parts])));

	// ---- Drive-in: the item board's lights below the top bar (1.7 stream U's find, at 1100 x 900) ---------------------------

	[Theory]
	[InlineData(1100)]
	[InlineData(1280)]
	[InlineData(1399)]
	[InlineData(1400)]
	[InlineData(1920)]
	[InlineData(2560)]
	public void DriveInItemBoard_ItsTopRowOfLights_ComesOutBelowTheTopBar_AtEveryWidth(double width)
	{
		// the item pages run under the bar (TopBar.ExtendsUnder); the board's lights are drawn by the letter board's template
		var bulbs = Xaml("Themes", "DriveIn", "DriveIn.xaml").Descendants(Dc + "BulbFrame")
			.Single(b => (string?)b.Attribute(X + "Name") == "Bulbs");
		var offset = double.Parse((string)bulbs.Attribute("Offset")!, CultureInfo.InvariantCulture);
		var radius = double.Parse((string)bulbs.Attribute("Radius")!, CultureInfo.InvariantCulture);
		// the lamp as drawn: BulbFrame's warm ring (1.5 r at 0.6), round the core (r); the faint halo (2.2 r at 0.2) may touch it
		var lampTop = ItemStage.BoardMargin(width).Top + offset - 1.5 * radius;

		Assert.True(lampTop >= TopBar.Height, $"at {width} px the board's top lights start at {lampTop}, under the {TopBar.Height} px bar");
		Assert.Equal(ItemStage.MarginFor(width), ItemStage.BoardMargin(width).Left);
		Assert.Equal(ItemStage.MarginFor(width), ItemStage.BoardMargin(width).Right);
	}

	[Fact]
	public void DriveInItemBoard_TheStageLaysItOutByTheRule()
	{
		var code = File.ReadAllText(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "Themes", "DriveIn", "Controls", "ItemStage.cs")));
		Assert.Contains("_board.Margin = BoardMargin(width);", code);
	}

	// ---- BH-175-M1: a row hears a kept view model's items only while it is loaded --------------------------------------

	/// <summary>A row's stand-in: its handler is its own method, as ShelfRow's Source_CollectionChanged is.</summary>
	private sealed class RowStandIn
	{
		public RowStandIn() => Watch = new LoadedSourceWatch(OnItems);

		public LoadedSourceWatch Watch { get; }

		public int Heard { get; private set; }

		private void OnItems(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Heard++;
	}

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	private static WeakReference ARowThatShowedThenLeft(System.Collections.ObjectModel.ObservableCollection<int> kept)
	{
		var row = new RowStandIn();
		row.Watch.SourceChanged(kept, loaded: false); // x:Bind gives the items before the row is in the window
		row.Watch.Loaded(kept);                       // shown
		row.Watch.Unloaded();                         // Back: the page leaves, the view model (and its items) is kept
		return new WeakReference(row);
	}

	[Fact]
	public void ShelfRow_ARowThatLeft_IsNotKeptAliveByTheKeptItems()
	{
		var kept = new System.Collections.ObjectModel.ObservableCollection<int> { 1, 2, 3 };
		var rows = Enumerable.Range(0, 5).Select(_ => ARowThatShowedThenLeft(kept)).ToList();
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();

		Assert.All(rows, r => Assert.False(r.IsAlive)); // 1.7.5's RC: each one alive, with its page (10 to 13 MB a Back)
		GC.KeepAlive(kept);
	}

	[Fact]
	public void ShelfRow_AKeptPageShownAgain_StillHearsItsItems_AndOnlyWhileLoaded()
	{
		var kept = new System.Collections.ObjectModel.ObservableCollection<int>();
		var row = new RowStandIn();
		row.Watch.SourceChanged(kept, loaded: false);
		Assert.False(row.Watch.Listening);           // not before it is in the window
		row.Watch.Loaded(kept);
		kept.Add(1);
		Assert.Equal(1, row.Heard);
		row.Watch.Unloaded();
		kept.Add(2);
		Assert.Equal(1, row.Heard);                  // gone: not heard
		row.Watch.Loaded(kept);                      // the kept page shown again (Back)
		row.Watch.Loaded(kept);                      // a second Loaded never hears it twice
		kept.Add(3);
		Assert.Equal(2, row.Heard);
		var other = new System.Collections.ObjectModel.ObservableCollection<int>();
		row.Watch.SourceChanged(other, loaded: true); // new items while shown: the old ones let go
		kept.Add(4);
		other.Add(1);
		Assert.Equal(3, row.Heard);
	}

	[Fact]
	public void ShelfRow_HearsItsItemsThroughTheLoadedHold()
	{
		var code = Code("Controls", "ShelfRow.xaml.cs");
		Assert.Contains("Services.WhileLoaded.Hold(this, () => _watch.Loaded(IsLoaded ? ItemsSource : null), _watch.Unloaded);", code);
		Assert.Contains("_watch.SourceChanged(ItemsSource, IsLoaded);", code);
		Assert.DoesNotContain("CollectionChanged += Source_CollectionChanged", code);
	}

	// ---- 1.7.5 review: no guard on a row's card against the UI Automation scroll's move (the coordinator's call) -------

	[Fact]
	public void ShelfRow_HasNoFocusGuard_NarratorsSetFocusGoesAsAsked()
	{
		// the guard (86902122) also refused Narrator's SetFocus to the page's first stop, which nothing could tell from the scroll's
		// move; a refused Narrator user can't navigate, so the scroll's move stays (KNOWN-ISSUES) and no row cancels a focus move
		var code = Code("Controls", "ShelfRow.xaml.cs");
		Assert.DoesNotContain("LosingFocus +=", code);
		Assert.DoesNotContain("TryCancel()", code);
	}

	// ---- the crash trail: what led to it (1.7.3's Drive-in library crash: a stowed E_UNEXPECTED, nothing in the log) -------

	private static string Code(params string[] parts) => File.ReadAllText(AppVersionTests.FindUp(Path.Combine(["ui", "FluentFin", .. parts]))).Replace("\r\n", "\n", StringComparison.Ordinal);

	[Fact]
	public void UiTrail_KeepsTheLastFew_NewestFirst_ARepeatCountedOnce()
	{
		lock (UiTrailLock)
		{
			UiTrail.Clear();
			Assert.Equal("nothing yet", UiTrail.Last());
			UiTrail.NotePage(typeof(Ui175Tests));
			for (var i = 0; i < 12; i++)
			{
				UiTrail.Note("wheel");
			}

			UiTrail.Note("pressed PageDown");
			var last = UiTrail.Last();
			Assert.StartsWith("pressed PageDown ", last, StringComparison.Ordinal);
			Assert.Contains("; wheel x12 ", last, StringComparison.Ordinal);
			Assert.EndsWith("s ago", last, StringComparison.Ordinal);
			Assert.Contains("page Ui175Tests", last, StringComparison.Ordinal);

			for (var i = 0; i < 20; i++)
			{
				UiTrail.Note($"key K{i}");
			}

			last = UiTrail.Last();
			Assert.Equal(UiTrail.Kept, last.Split("; ").Length);
			Assert.StartsWith("key K19 ", last, StringComparison.Ordinal);
			Assert.DoesNotContain("wheel", last, StringComparison.Ordinal);
			UiTrail.Clear();
		}
	}

	private static readonly object UiTrailLock = new();

	// ---- the trail's privacy (the 1.7.5 review's Blocker: every key was noted, a password's last characters with them) ---

	[Theory]
	[InlineData(Windows.System.VirtualKey.A)]
	[InlineData(Windows.System.VirtualKey.Z)]
	[InlineData(Windows.System.VirtualKey.Number7)]
	[InlineData(Windows.System.VirtualKey.NumberPad7)]
	[InlineData(Windows.System.VirtualKey.Multiply)]
	[InlineData(Windows.System.VirtualKey.Decimal)]
	[InlineData(Windows.System.VirtualKey.Space)]
	[InlineData((Windows.System.VirtualKey)186)] // ;: and the other symbol keys (OEM)
	[InlineData((Windows.System.VirtualKey)222)]
	[InlineData((Windows.System.VirtualKey)229)] // the IME's ProcessKey
	[InlineData(Windows.System.VirtualKey.F5)]
	[InlineData(Windows.System.VirtualKey.Shift)]
	public void UiTrail_NeverNotesACharacterKey(Windows.System.VirtualKey key)
	{
		Assert.Null(UiTrail.KeyName(key, inTextEntry: false));
		Assert.Equal(UiTrail.Typing, UiTrail.KeyName(key, inTextEntry: true));
	}

	[Theory]
	[InlineData(Windows.System.VirtualKey.Up)]
	[InlineData(Windows.System.VirtualKey.Down)]
	[InlineData(Windows.System.VirtualKey.Left)]
	[InlineData(Windows.System.VirtualKey.Right)]
	[InlineData(Windows.System.VirtualKey.Tab)]
	[InlineData(Windows.System.VirtualKey.Enter)]
	[InlineData(Windows.System.VirtualKey.Escape)]
	[InlineData(Windows.System.VirtualKey.Back)]
	[InlineData(Windows.System.VirtualKey.GoBack)]
	[InlineData(Windows.System.VirtualKey.Home)]
	[InlineData(Windows.System.VirtualKey.End)]
	[InlineData(Windows.System.VirtualKey.PageUp)]
	[InlineData(Windows.System.VirtualKey.PageDown)]
	[InlineData(Windows.System.VirtualKey.Application)]
	[InlineData(Windows.System.VirtualKey.GamepadA)]
	[InlineData(Windows.System.VirtualKey.GamepadDPadDown)]
	[InlineData(Windows.System.VirtualKey.GamepadMenu)]
	public void UiTrail_NotesTheKeysThatMoveAround_ByName_AndInATextBoxOnlyTyping(Windows.System.VirtualKey key)
	{
		Assert.Equal($"pressed {key}", UiTrail.KeyName(key, inTextEntry: false));
		Assert.Equal(UiTrail.Typing, UiTrail.KeyName(key, inTextEntry: true)); // Enter in the password box too
	}

	[Fact]
	public void UiTrail_Typing_IsNeverCounted()
	{
		lock (UiTrailLock)
		{
			UiTrail.Clear();
			for (var i = 0; i < 9; i++)
			{
				UiTrail.Note(UiTrail.Typing);
			}

			var last = UiTrail.Last();
			Assert.StartsWith("typing ", last, StringComparison.Ordinal);
			Assert.DoesNotContain(" x", last, StringComparison.Ordinal); // a count would say how long a password is
			UiTrail.Clear();
		}
	}

	[Fact]
	public void UiTrail_TheKeyHandler_GoesThroughTheRule_AndTextEntryIsEveryEditBox()
	{
		var code = Code("Services", "UiDispatch.cs");
		var attach = code[code.IndexOf("public static void Attach(Microsoft.UI.Xaml.UIElement root)", StringComparison.Ordinal)..];
		attach = attach[..attach.IndexOf("\n\t}\n", StringComparison.Ordinal)];
		Assert.Contains("KeyName(e.Key, InTextEntry(e.OriginalSource as Microsoft.UI.Xaml.DependencyObject))", attach);
		Assert.DoesNotContain("{e.Key}", attach);
		var entry = code[code.IndexOf("internal static bool InTextEntry(", StringComparison.Ordinal)..];
		entry = entry[..entry.IndexOf("return false;", StringComparison.Ordinal)];
		foreach (var box in new[] { "TextBox", "PasswordBox", "AutoSuggestBox", "RichEditBox", "NumberBox" })
		{
			Assert.Contains($"Microsoft.UI.Xaml.Controls.{box}", entry);
		}

		// what else the trail says is the code's own: an element's type and x:Name, a page's type (never its text or a title)
		var describe = code[code.IndexOf("internal static string Describe(object? element)", StringComparison.Ordinal)..];
		describe = describe[..describe.IndexOf("};", StringComparison.Ordinal)];
		Assert.DoesNotContain("Text", describe);
		Assert.DoesNotContain("AutomationProperties", describe);
		Assert.Contains("public static void NotePage(Type? page) => Note($\"page {page?.Name ?? \"none\"}\");", code);
	}

	[Fact]
	public void CrashTrail_SaysTheType_TheHResult_XamlsOwnMessage_TheStack_AndTheTrail()
	{
		lock (UiTrailLock)
		{
			UiTrail.Clear();
			UiTrail.Note("wheel");
			Exception thrown;
			try
			{
				throw new System.Runtime.InteropServices.COMException("Catastrophic failure", unchecked((int)0x8000FFFF));
			}
			catch (Exception ex)
			{
				thrown = ex;
			}

			var line = CrashTrail.Describe("XAML's unhandled exception", thrown, "Layout cycle detected.  Layout could not complete.");
			Assert.StartsWith("[crash-trail] XAML's unhandled exception: System.Runtime.InteropServices.COMException (0x8000FFFF): Catastrophic failure", line, StringComparison.Ordinal);
			Assert.Contains("\nXAML says: Layout cycle detected.", line, StringComparison.Ordinal);
			Assert.Contains("\nstack: ", line, StringComparison.Ordinal);
			Assert.Contains(nameof(CrashTrail_SaysTheType_TheHResult_XamlsOwnMessage_TheStack_AndTheTrail), line, StringComparison.Ordinal);
			Assert.Contains("\nlast in the window: wheel ", line, StringComparison.Ordinal);

			// XAML's message said once when it is the exception's own; none from native code says so
			var same = CrashTrail.Describe("XAML's unhandled exception", new InvalidOperationException("same"), "same");
			Assert.DoesNotContain("XAML says", same, StringComparison.Ordinal);
			Assert.Contains("stack: (none: thrown outside managed code)", same, StringComparison.Ordinal);
			Assert.Contains("no exception", CrashTrail.Describe("the process's unhandled exception", null, null), StringComparison.Ordinal);
			UiTrail.Clear();
		}
	}

	[Fact]
	public void CrashTrail_IsWired_WhereTheFirstChanceTrailMissesIt()
	{
		var app = Code("App.xaml.cs");
		var xaml = app[app.IndexOf("private void App_UnhandledException(", StringComparison.Ordinal)..];
		xaml = xaml[..xaml.IndexOf("\n\t}\n", StringComparison.Ordinal)];
		Assert.Contains("Services.CrashTrail.Log(\"XAML's unhandled exception\", e.Exception, e.Message);", xaml);
		Assert.Contains("e.Handled = true;", xaml); // what happens next is as before
		Assert.Contains("Services.CrashTrail.Log(\"the process's unhandled exception\", e.ExceptionObject as Exception);", app);
		Assert.Contains("Services.CrashTrail.ListenForUnhandledErrors(GetService<ILogger<App>>());", app);
		Assert.Contains("Services.UiTrail.Attach(MainWindow.Content);", app);
		Assert.Contains(".Navigated += (_, e) => Services.UiTrail.NotePage(e.SourcePageType);", app);

		var dispatch = Code("Services", "UiDispatch.cs");
		// the first-chance lines carry the trail too
		Assert.Contains("last in the window: {Trail}", dispatch);
		Assert.Contains("exception.Message, stack, UiTrail.Last());", dispatch);
		// an unhandled error is logged and the app goes on: Propagate marks it handled (the 1.7.5 review: a rethrow didn't end it)
		var detected = dispatch[dispatch.IndexOf("private static void OnUnhandledErrorDetected(", StringComparison.Ordinal)..];
		detected = detected[..detected.IndexOf("\n\t}\n", StringComparison.Ordinal)];
		Assert.Contains("e.UnhandledError.Propagate();", detected);
		Assert.Contains("Log(\"an unhandled error (UnhandledErrorDetected)\", ex);\n\t\t}", detected);
		Assert.DoesNotContain("throw;", detected);
	}
}
