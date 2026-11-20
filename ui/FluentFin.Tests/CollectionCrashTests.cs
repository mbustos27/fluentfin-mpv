using FluentFin.Services;
using FluentFin.Views;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.4.0 (bug hunt M3: the app ended, 0xc000027b in CoreMessagingXP, about a second after Back from a film to a
/// Collection page; reproduced off-screen by then-140-backstress.ps1 on 25817a3: a "Layout cycle detected" as the page came
/// back with the keyboard's card past the first, then the stowed exception): the card is taken from those the grid laid out,
/// work queued on the UI thread cannot end the app, and what threw is in the log before anything else can happen.
/// </summary>
public sealed class CollectionCrashTests
{
	private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([FlyleafEngineTests.RepoRoot, "FluentFin", .. parts])).Replace("\r\n", "\n");

	[Fact]
	public void TheCardBackReturnsTo_IsTakenFromTheLaidOutGrid_AndAFailureIsLogged()
	{
		var page = Source("Views", "CollectionPage.xaml.cs");
		var focus = page[page.IndexOf("private bool FocusCard(int index)", StringComparison.Ordinal)..];
		focus = focus[..focus.IndexOf("internal static bool CanFocusCard", StringComparison.Ordinal)];
		// laid out first, the card the grid made taken; GetOrCreateElement only for one it has not made
		Assert.True(focus.IndexOf("UpdateLayout();", StringComparison.Ordinal) < focus.IndexOf("GridRepeater.TryGetElement(index) ?? GridRepeater.GetOrCreateElement(index)", StringComparison.Ordinal));
		Assert.Contains("catch (Exception ex)", focus);
		Assert.Contains("UiDispatch.Failed(", focus);

		// the queued work goes through the guard; nothing is queued bare
		Assert.DoesNotContain("DispatcherQueue.TryEnqueue(", page);
		Assert.Contains("UiDispatch.Enqueue(DispatcherQueue, \"the Collection page's arrival focus\", FocusOnArrival", page);

		// the grid's spacing is set after the repeater's layout pass, not inside its ElementPrepared
		var prepared = page[page.IndexOf("GridRepeater.ElementPrepared +=", StringComparison.Ordinal)..];
		prepared = prepared[..prepared.IndexOf("PageContent.SizeChanged +=", StringComparison.Ordinal)];
		Assert.True(prepared.IndexOf("UiDispatch.Enqueue(", StringComparison.Ordinal) < prepared.IndexOf("LayoutGrid();", StringComparison.Ordinal));

		// the page follows its view model only while shown (bug hunt m8)
		var constructor = page[page.IndexOf("public CollectionPage()", StringComparison.Ordinal)..page.IndexOf("protected override void OnNavigatedTo", StringComparison.Ordinal)];
		Assert.DoesNotContain("PropertyChanged +=", constructor);
		var from = page[page.IndexOf("protected override void OnNavigatedFrom", StringComparison.Ordinal)..];
		Assert.Contains("ViewModel.State.PropertyChanged -= OnStateChanged;", from[..from.IndexOf("base.OnNavigatedFrom", StringComparison.Ordinal)]);
	}

	[Theory]
	[InlineData(2, 6, 6, true, true)]
	[InlineData(5, 6, 6, true, true)]
	[InlineData(2, 6, 6, false, false)]   // the page not drawn (left, or not yet in)
	[InlineData(6, 6, 6, true, false)]    // past the entries
	[InlineData(-1, 6, 6, true, false)]
	[InlineData(3, 6, 2, true, false)]    // the grid holding fewer (its source not yet the new entries)
	[InlineData(0, 6, -1, true, false)]   // no source at all
	public void ACard_IsAskedOfTheGrid_OnlyWhenItHasIt(int index, int entries, int inGrid, bool drawn, bool asks) =>
		Assert.Equal(asks, CollectionPage.CanFocusCard(index, entries, inGrid, drawn));

	[Fact]
	public void QueuedWork_ThatThrows_IsLogged_AndTheAppGoesOn()
	{
		var before = UiDispatch.Failures;
		var after = false;
		UiDispatch.Run("a test's callback", () => throw new System.Runtime.InteropServices.COMException("Layout cycle detected.", unchecked((int)0x80004005)));
		after = true;
		Assert.True(after);
		Assert.Equal(before + 1, UiDispatch.Failures);

		var ran = false;
		UiDispatch.Run("a test's other callback", () => ran = true);
		Assert.True(ran);
		Assert.Equal(before + 1, UiDispatch.Failures);

		// nothing to queue on (the window closed): nothing done, nothing thrown
		Assert.False(UiDispatch.Enqueue(null, "nothing", () => throw new InvalidOperationException()));
	}

	[Fact]
	public void TheTrail_IsTheUiThreadsExceptions_FromTheAppsOwnCode()
	{
		const string ours = "   at FluentFin.Views.CollectionPage.FocusCard(Int32 index)\n   at WinRT.Something()";
		Assert.True(FirstChanceTrail.Worth(new System.Runtime.InteropServices.COMException("x"), onUiThread: true, ours));
		Assert.False(FirstChanceTrail.Worth(new System.Runtime.InteropServices.COMException("x"), onUiThread: false, ours));
		Assert.False(FirstChanceTrail.Worth(new OperationCanceledException(), onUiThread: true, ours));
		Assert.False(FirstChanceTrail.Worth(new InvalidOperationException(), onUiThread: true, "   at System.Net.Http.HttpClient.Send()"));

		var app = Source("App.xaml.cs");
		Assert.Contains("Services.FirstChanceTrail.Start();", app);
	}

	/// <summary>The arrival paths queued on the UI thread go through the guard too (the libraries' first tile and poster).</summary>
	[Fact]
	public void TheOtherArrivalPaths_AreGuarded()
	{
		Assert.DoesNotContain("DispatcherQueue.TryEnqueue(", Source("Views", "LibrariesLandingPage.xaml.cs"));
		var host = Source("Controls", "LibraryGridHost.cs");
		var requested = host[host.IndexOf("private void FocusRequested()", StringComparison.Ordinal)..];
		requested = requested[..requested.IndexOf("private void Scroller_ScrollCompleted", StringComparison.Ordinal)];
		Assert.Contains("UiDispatch.Enqueue(_page.DispatcherQueue,", requested);
		Assert.DoesNotContain(".TryEnqueue(", requested);
	}
}
