namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.6.1 (bug hunt BH-160-m2, and the keyboard after a save): the Collection page offers Save as playlist only where
/// it can save (never on a collection of shows only), and the keyboard never stays on Save once it turns itself off: a
/// disabled button gets no keys (then-160-collections: Back did nothing after a save).
/// </summary>
public sealed class CollectionPageSaveTests
{
	private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([FlyleafEngineTests.RepoRoot, "FluentFin", .. parts])).Replace("\r\n", "\n");

	[Fact]
	public void SaveAsPlaylist_IsShownOnlyWhereItCanSave()
	{
		var xaml = Source("Views", "CollectionPage.xaml");
		var button = xaml[xaml.IndexOf("x:Name=\"SaveAsPlaylistButton\"", StringComparison.Ordinal)..];
		button = button[..button.IndexOf('>')];

		Assert.Contains("Visibility=\"{x:Bind ViewModel.OffersSaveAsPlaylist, Mode=OneWay}\"", button);
	}

	[Fact]
	public void TheKeyboard_LeavesSaveWhenItTurnsOff_ForOpenOnceSaved_OrBackToSave()
	{
		var page = Source("Views", "CollectionPage.xaml.cs");

		Assert.Contains("SaveAsPlaylistButton.IsEnabledChanged += (_, e) => OnSaveEnabledChanged((bool)e.NewValue);", page);
		var turned = page[page.IndexOf("private void OnSaveEnabledChanged(bool enabled)", StringComparison.Ordinal)..];
		turned = turned[..turned.IndexOf("\n\t}\n", StringComparison.Ordinal)];
		Assert.Contains("if (!enabled && SaveAsPlaylistButton.FocusState != FocusState.Unfocused)", turned);
		Assert.Contains("FavoriteToggle.Focus(_saveHadKeyboard.Value);", turned);
		Assert.Contains("SaveAsPlaylistButton.Focus(how);", turned);
		// saved: on to Open, unless the user went elsewhere while it saved
		var saved = page[page.IndexOf("e.PropertyName == nameof(CollectionViewModel.SavedPlaylist)", StringComparison.Ordinal)..];
		saved = saved[..saved.IndexOf("Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);", StringComparison.Ordinal)];
		Assert.Contains("if (KeyboardWaitsOnFavourite)", saved);
		Assert.Contains("OpenPlaylistLink.Focus(how);", saved);
	}
}
