using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using FluentFin.Controls;
using Microsoft.UI.Xaml;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.8.1, stream mem (BH-175-M2): a film page or a Collection page come back to by Back cost 6 to 15 MB that never came
/// back. Back keeps a page's view model and its items (PageKeep); each card the new page made listened to its kept item through
/// x:Bind's generated tracker, and the tracker held two of the card's own buttons strongly ("MarkWatchedButton.IsChecked" in
/// the card's XAML), so every card that ever showed a kept item stayed alive with its whole tree. The harness measures what the
/// user sees of it, the memory (tools\ui-harness\offscreen\then-175-leak.ps1, LK_CASE=film and collection, in the gate).
/// </summary>
public sealed class Mem181Tests
{
	private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

	/// <summary>The x:Bind trackers the XAML compiler made for <paramref name="owner"/> (nested in its Bindings classes).</summary>
	private static IEnumerable<Type> TrackersOf(Type owner) =>
		owner.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)
			.SelectMany(t => t.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public).Append(t))
			.Where(t => t.Name.EndsWith("_BindingsTracking", StringComparison.Ordinal));

	private static bool IsElement(Type type) => typeof(DependencyObject).IsAssignableFrom(type);

	// what a tracker listens to and may outlive the element: a view model, an item, a list (a DependencyObject is the element's own)
	private static bool IsSource(Type type) =>
		!IsElement(type) && (typeof(INotifyPropertyChanged).IsAssignableFrom(type) || typeof(INotifyCollectionChanged).IsAssignableFrom(type));

	[Fact]
	public void Card_ItsBindingsTracker_ListensToItsItem_ButHoldsNoElement()
	{
		var trackers = TrackersOf(typeof(BaseItemDtoCard)).ToList();
		Assert.NotEmpty(trackers); // the card's XAML has OneWay x:Binds on its item: the tracker exists

		var fields = trackers.SelectMany(t => t.GetFields(Fields)).ToList();
		Assert.Contains(fields, f => IsSource(f.FieldType)); // it listens to the item (Model), which Back keeps
		// 1.8.0: cache_MarkWatchedButton and cache_AddToFavoriteButton (ToggleButton): a kept item held every card that showed it
		Assert.DoesNotContain(fields, f => IsElement(f.FieldType));
	}

	[Fact]
	public void Card_TheTogglesNames_ReadTheCardsOwnProperties_NotTheToggles()
	{
		var xaml = File.ReadAllText(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "Controls", "BaseItemDtoCard.xaml")));
		Assert.DoesNotContain("MarkWatchedButton.IsChecked", xaml);
		Assert.DoesNotContain("AddToFavoriteButton.IsChecked", xaml);
		Assert.Contains("ItemLabels.WatchedAction(Model.Dto, WatchedShown)", xaml);
		Assert.Contains("ItemLabels.FavoriteAction(Model.Dto, FavoriteShown)", xaml);
		Assert.Contains("ItemRowConverters.FavoriteGlyph(FavoriteShown)", xaml);

		var code = File.ReadAllText(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "Controls", "BaseItemDtoCard.xaml.cs")));
		// what the toggles show, mirrored as they change (a click flips a toggle before the server hears it: its name follows)
		Assert.Contains("MarkWatchedButton.RegisterPropertyChangedCallback(ToggleButton.IsCheckedProperty, (_, _) => WatchedShown = MarkWatchedButton.IsChecked);", code);
		Assert.Contains("AddToFavoriteButton.RegisterPropertyChangedCallback(ToggleButton.IsCheckedProperty, (_, _) => FavoriteShown = AddToFavoriteButton.IsChecked);", code);
	}

	/// <summary>
	/// The second path (1.8.1): Windows' UI Automation keeps a peer it was given after its element has left (Narrator, any
	/// assistive tool, the harness's driver), so a peer must not hold its element itself: a primary constructor's <c>owner</c>
	/// used in the peer's members is kept in a hidden field. The card's peer held every Collection page's 24 cards (12 MB a
	/// visit) while a UI Automation client was looking. A peer reads its element through <c>Owner</c>.
	/// </summary>
	[Fact]
	public void NoAutomationPeer_HoldsItsElementInAField()
	{
		var peers = Assemblies.SelectMany(a => a.GetTypes()).Where(t => typeof(Microsoft.UI.Xaml.Automation.Peers.AutomationPeer).IsAssignableFrom(t)).ToList();
		Assert.Contains(typeof(BaseItemDtoCardAutomationPeer), peers);
		Assert.Contains(peers, t => t.Name == "HalfStarRatingPeer"); // the plugins' (the journal's rating, on every item page)
		var holding = peers.SelectMany(t => t.GetFields(Fields | BindingFlags.DeclaredOnly).Where(f => IsElement(f.FieldType)).Select(f => $"{t.Name}.{f.Name} ({f.FieldType.Name})")).ToList();
		Assert.True(holding.Count == 0, "peers that hold their element: " + string.Join("; ", holding));
	}

	/// <summary>
	/// The class of the bug: an x:Bind tracker that listens to a view model and holds one of its control's elements keeps that
	/// element (and its control) alive as long as the view model, whatever page it was on. Each element held here was checked,
	/// by its control and its field: its view model is made with the control, or the control lives as long (the reason given).
	/// A new one fails until it is checked the same way, or fixed as the card was; one checked that is gone fails too, so the
	/// list says what the code does.
	/// </summary>
	[Fact]
	public void NoBindingsTracker_ThatListensToAViewModel_HoldsAnElement_ButTheOnesChecked()
	{
		var checkedOnes = new Dictionary<string, string>
		{
			["SelectServerPage.cache_TxtUrl"] = "SelectServerViewModel is transient: made with the page, gone with it",
			["EditMetadataDialog.cache_AddGenreTextBox"] = "its view model is made with the dialog (AddDialog)",
			["TransportControls.cache_CCSelectionButton"] = "its Playlist and Trickplay are its player view model's own, made with it",
			["TransportControls.cache_AudioSelectionButton"] = "as cache_CCSelectionButton",
			["TransportControls.cache_Subtitles"] = "as cache_CCSelectionButton",
			["TitleBarControl.cache_BarChrome"] = "one top bar for the app's life (MainWindow.xaml, in the static App.MainWindow), as long as its singleton TitleBarViewModel: nothing to let go",
			["DiaryPage.cache_GroupedEntries"] = "DiaryViewModel is transient (JournalPlugin) and not kept on Back (no IKeptOnBack): made with the page, gone with it",
			["JellyseerDashboardPage.cache_UrlTextBox"] = "JellyseerDashboardViewModel is transient (JellyseerPlugin) and not kept on Back: made with the page, gone with it",
		};
		var held = new List<string>();
		foreach (var owner in Assemblies.SelectMany(a => a.GetTypes()).Where(t => !t.IsNested))
		{
			foreach (var tracker in TrackersOf(owner))
			{
				var fields = tracker.GetFields(Fields);
				if (fields.Any(f => IsSource(f.FieldType)))
				{
					held.AddRange(fields.Where(f => IsElement(f.FieldType)).Select(f => $"{owner.Name}.{f.Name}"));
				}
			}
		}

		var notChecked = held.Where(x => !checkedOnes.ContainsKey(x)).ToList();
		Assert.True(notChecked.Count == 0, "x:Bind trackers that hold an element while they listen to a view model: " + string.Join("; ", notChecked));
		var gone = checkedOnes.Keys.Where(x => !held.Contains(x)).ToList();
		Assert.True(gone.Count == 0, "checked ones the code no longer has (take them out of the list): " + string.Join("; ", gone));
	}

	/// <summary>
	/// The review's find (in 1.8.0, since 1.6): a Collection page's cards 2 to 24 said no place to Narrator. The page set it on the
	/// card as its repeater prepared the entry, before the theme's PosterTemplate had made the card. The entry holds it now
	/// (CardPlace), and the card's peer (the shared card's, Editorial's poster's) reads it. The harness reads what Narrator
	/// hears: then-181-place.ps1, the gate's collection-place.
	/// </summary>
	[Fact]
	public void CollectionCards_ThePlaceIsTheEntrys_AndTheirPeersReadIt()
	{
		var page = File.ReadAllText(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "Views", "CollectionPage.xaml.cs")));
		Assert.Contains("private void SetPlace(UIElement element, int index) => Controls.CardPlace.Set(element, index, ViewModel.Entries.Count);", page);
		Assert.DoesNotContain("AutomationProperties.SetPositionInSet(card", page);

		const BindingFlags Declared = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
		foreach (var peer in new[] { typeof(BaseItemDtoCardAutomationPeer), typeof(FluentFin.Themes.Editorial.Controls.EditorialPosterAutomationPeer) })
		{
			Assert.NotNull(peer.GetMethod("GetPositionInSetCore", Declared));
			Assert.NotNull(peer.GetMethod("GetSizeOfSetCore", Declared));
		}

		// a card's own place (a row's) wins; with none, and nothing that holds it, none
		Assert.Equal(3, CardPlace.Position(3, null));
		Assert.Equal(12, CardPlace.Size(12, null));
		Assert.Equal(-1, CardPlace.Position(-1, null));
	}

	/// <summary>The app's assembly and the plugins' (the journal's pages and rating, Jellyseerr's), whose XAML x:Bind compiles too.</summary>
	private static readonly Assembly[] Assemblies = [typeof(App).Assembly, typeof(FluentFin.Plugins.Journal.Views.DiaryPage).Assembly];
}
