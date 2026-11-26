using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

/// <summary>Nixie 1.4.0: a card on the Collection page: the item, and its place in the collection's own order ("3"; "" in another order).</summary>
public sealed record CollectionEntry(BaseItemViewModel Item, int Position, bool ShowsPosition)
{
	public string PositionText => ShowsPosition ? Position.ToString(System.Globalization.CultureInfo.CurrentCulture) : "";

	/// <summary>
	/// 1.6 colpage (LB1): the place as the card's caption shows it ("1" in "1 · Wild Canyons"), not a chip on the art: the
	/// art's top-left square is for counts only ("3" left to watch); "" where the order means nothing.
	/// </summary>
	public string CaptionPrefix => ShowsPosition ? CollectionBrowse.CaptionPrefix(Position) : "";
}

/// <summary>
/// Nixie 1.4.0 (bug hunt M2): a Collection page's view: its Order and Hide watched. R (a new view model for the same page)
/// and Back reset them to the defaults; they are kept here, per user and collection, for the session, and put back when the
/// page comes back (Back, Forward) or is loaded again in place (R, F5), as the library keeps its sort and filters
/// (<see cref="LibraryPlaces"/>). A collection opened anew starts in its own order, every item shown.
/// </summary>
public sealed record CollectionPlace(CollectionOrder Order, bool HideWatched);

public static class CollectionPlaces
{
	private const int Kept = 32;
	private static readonly Lock s_lock = new();
	private static readonly List<(Guid User, Guid Collection, CollectionPlace Place)> s_places = [];

	public static void Remember(Guid user, Guid collection, CollectionPlace place)
	{
		lock (s_lock)
		{
			s_places.RemoveAll(x => x.User == user && x.Collection == collection);
			s_places.Add((user, collection, place));
			if (s_places.Count > Kept)
			{
				s_places.RemoveAt(0);
			}
		}
	}

	public static CollectionPlace? Recall(Guid user, Guid collection)
	{
		lock (s_lock)
		{
			return s_places.LastOrDefault(x => x.User == user && x.Collection == collection).Place;
		}
	}
}

/// <summary>
/// Nixie 1.4.0 (the UX review's plan item 1; X1, X2, CL-M4, CL-B1): the shared Collection page. A box set opens here, in its
/// own order (the server's DisplayOrder, release date when unset), with its art, its title, "6 films · 1979–2016 · 1 of 6
/// watched" and a progress bar, a three-line overview, Play or Resume from the first film not watched (in collection order),
/// Shuffle, the Watched and Favourite toggles, an Order menu and Hide watched; no genre filter, and an A to Z only from
/// <see cref="CollectionBrowse.LettersFrom"/> items. A playlist opens here too, as an ordered list (its own order, 1.3.1's).
/// </summary>
public partial class CollectionViewModel(IJellyfinClient jellyfinClient, GlobalCommands commands) : ObservableObject, INavigationAware, IKeptOnBack
{
	private IReadOnlyList<BaseItemDto> _serverOrder = [];

	/// <summary>Shuffle's dice (tests fix them).</summary>
	public Func<int, int> Pick { get; set; } = Random.Shared.Next;

	public IJellyfinClient JellyfinClient { get; } = jellyfinClient;

	/// <summary>The box set or the playlist.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Title), nameof(Overview), nameof(IsFavorite), nameof(Kind), nameof(OrderLabel), nameof(OrderButtonName))]
	[NotifyPropertyChangedFor(nameof(DirectionWords), nameof(OtherDirectionWords), nameof(DirectionGlyph), nameof(DirectionButtonName), nameof(DirectionToolTip))] // 1.8: a box set's own order (its DisplayOrder) says the direction's words
	public partial BaseItemDto? Collection { get; private set; }

	/// <summary>A playlist: an ordered list (its own order, no years in the summary).</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(OrderLabel), nameof(OrderButtonName), nameof(Kind))]
	[NotifyPropertyChangedFor(nameof(DirectionWords), nameof(OtherDirectionWords), nameof(DirectionGlyph), nameof(DirectionButtonName), nameof(DirectionToolTip))] // 1.8
	public partial bool IsPlaylist { get; private set; }

	public string Title => CollectionNames.Display(Collection); // 1.6 collections: a box set's tidy name (a playlist's as it is)

	public string Overview => Collection?.Overview ?? "";

	/// <summary>"Collection" or "Playlist": the eyebrow over the title, and the list's description.</summary>
	public string Kind => CollectionBrowse.KindOf(Collection);

	/// <summary>The order the set keeps: an album its tracks', a box set its DisplayOrder.</summary>
	private string? OwnDisplayOrder => Collection?.Type is BaseItemDto_Type.MusicAlbum or BaseItemDto_Type.PhotoAlbum ? CollectionBrowse.TrackOrder : Collection?.DisplayOrder;

	/// <summary>The cards as shown: in the order picked, without the watched ones under Hide watched.</summary>
	[ObservableProperty]
	public partial IReadOnlyList<CollectionEntry> Entries { get; private set; } = [];

	/// <summary>Over the films: "5 of 6 films" when Hide watched hides some; else "" (1.6 colpage, C2: the summary says "6 films").</summary>
	[ObservableProperty]
	public partial string ShownText { get; private set; } = "";

	/// <summary>1.6 colpage (C1, C5): what the theme's header stage shows (the art, the marquee, the contents list).</summary>
	[ObservableProperty]
	public partial CollectionHeaderFacts Header { get; private set; } = CollectionHeaderFacts.Empty;

	// 1.6 collections: a Nixie collection's eyebrow ("Made by Nixie · Franchise"), why line, and whether its order means
	// something (a franchise, trending; not a decade): the collections stream's ICollectionSource sets them in its Load hook
	public string HeaderEyebrow { get; set; } = "";

	public string HeaderWhy { get; set; } = "";

	public bool? PositionsOverride { get; set; }
	// end 1.6 collections

	/// <summary>"6 films · 1979–2016 · 1 of 6 watched".</summary>
	[ObservableProperty]
	public partial string Summary { get; private set; } = "";

	/// <summary>How much is watched, 0 to 100 (the progress bar).</summary>
	[ObservableProperty]
	public partial double Progress { get; private set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(PlayLabel), nameof(PlayName), nameof(CanPlay))]
	public partial CollectionNext Next { get; private set; }

	public string PlayLabel => CollectionBrowse.PlayLabel(Next);

	public string PlayName => CollectionBrowse.PlayName(Next, _serverOrder);

	public bool CanPlay => Next.Item is not null;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(OrderLabel), nameof(OrderButtonName))]
	[NotifyPropertyChangedFor(nameof(DirectionWords), nameof(OtherDirectionWords), nameof(DirectionGlyph), nameof(DirectionButtonName), nameof(DirectionToolTip))] // 1.8: each order has its direction's words
	public partial CollectionOrder Order { get; set; } = CollectionOrder.Collection;

	/// <summary>"Order: Collection".</summary>
	public string OrderLabel => Order == CollectionOrder.Collection ? $"Order: {Kind}" : CollectionBrowse.OrderLabel(Order, IsPlaylist);

	public string OrderButtonName => $"{OrderLabel}, change the order";

	[ObservableProperty]
	public partial bool HideWatched { get; set; }

	// ---- 1.8 (the reverse toggle; the owner, 2026-11-23: "we should be able to reverse the direction of the sort in collections")

	/// <summary>
	/// 1.8: the cards run the other way: whichever order is chosen, mirrored (<see cref="SortDirections.Apply"/>). Kept like
	/// Order and Hide watched: Back and Forward keep this view model (<see cref="IKeptOnBack"/>), R puts it back
	/// (<see cref="SortDirections.Recall"/>); a collection opened anew runs as its order does.
	/// </summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(DirectionWords), nameof(OtherDirectionWords), nameof(DirectionGlyph), nameof(DirectionButtonName), nameof(DirectionToolTip))]
	public partial bool Reversed { get; set; }

	/// <summary>What the order chosen runs by (its direction's words): A to Z, oldest first, newest first, first to last.</summary>
	public OrderRuns Runs => SortDirections.Runs(Order, IsPlaylist, OwnDisplayOrder);

	/// <summary>The direction button's words: "Oldest first", "Newest first", "A to Z", "Z to A", "First to last".</summary>
	public string DirectionWords => SortDirections.Words(Runs, Reversed);

	/// <summary>The other direction's words: the button holds both in one cell, so it keeps its width when it flips (rule 7).</summary>
	public string OtherDirectionWords => SortDirections.OtherWords(Runs, Reversed);

	public string DirectionGlyph => SortDirections.Glyph(Runs, Reversed);

	/// <summary>Narrator: the direction, then what a press does: "Newest first, switch to oldest first".</summary>
	public string DirectionButtonName => SortDirections.ButtonName(Runs, Reversed);

	public string DirectionToolTip => SortDirections.ToolTip(Runs, Reversed);

	/// <summary>The direction button: the other way.</summary>
	[RelayCommand]
	public void ToggleDirection() => Reversed = !Reversed;

	partial void OnReversedChanged(bool value)
	{
		Update();
		if (_placeKnown && DirectionView is { } view)
		{
			SortDirections.Remember(JellyfinClient.UserId, view, value); // the user's flip only (never a page's default: CollectionPlaces' rule)
		}
	}

	// the view the direction is kept under: a Made by Nixie collection by its own id, a server's set or playlist by its id
	private string? DirectionView => Nixie is { } nixie ? SortDirections.ForNixie(nixie.Collection.Id) : Collection?.Id is { } id ? SortDirections.ForCollection(id) : null;

	// R (a new view model, the page saying it reloads in place) puts the direction back
	private void RecallDirection(bool keeps)
	{
		if (keeps && DirectionView is { } view && SortDirections.Recall(JellyfinClient.UserId, view) is { } reversed)
		{
			Reversed = reversed;
		}
	}
	// end 1.8

	/// <summary>Every item is watched (the Watched toggle shows it on).</summary>
	[ObservableProperty]
	public partial bool IsWatched { get; private set; }

	public bool IsFavorite => Collection?.UserData?.IsFavorite == true;

	/// <summary>Hide watched hid everything: "All 6 films are watched", with Show watched.</summary>
	[ObservableProperty]
	public partial string AllHiddenText { get; private set; } = "";

	/// <summary>The A to Z, from <see cref="CollectionBrowse.LettersFrom"/> items sorted by name; else none.</summary>
	[ObservableProperty]
	public partial IReadOnlyList<LetterJump> Letters { get; private set; } = [];

	/// <summary>The card the page should focus (an A to Z jump), or -1; set again for each jump.</summary>
	[ObservableProperty]
	public partial int FocusRequest { get; set; } = -1;

	/// <summary>Loading, empty and failed, worded, with Retry.</summary>
	public LoadState State { get; } = new("this collection");

	/// <summary>
	/// Nixie 1.4.0 (bug hunt M2): whether this visit comes back to the collection (Back, Forward) or loads it again in place
	/// (R, F5), so its Order and Hide watched are put back (<see cref="CollectionPlaces"/>); set by the page, asked once the
	/// server has answered. Unset or false: a collection opened anew, in its own order.
	/// </summary>
	public Func<bool>? KeepsPlace { get; set; }

	// the view is kept from the first time it is shown on (not the defaults a new view model starts with)
	private bool _placeKnown;

	public Task OnNavigatedFrom() => Task.CompletedTask;

	// ---- Nixie 1.7.4: Back shows the collection as it was (IKeptOnBack; NavigationService keeps it) -------------------------

	/// <summary>Kept for Back once its films are in (a set left while it loads, or one that failed, loads again).</summary>
	public bool CanKeep => State.Status is LoadStatus.Loaded or LoadStatus.Empty && Collection is not null;

	/// <summary>
	/// Back or Forward shows the collection as it was: its cards, its Order and Hide watched. An A to Z jump of the visit that
	/// left is not made again. When something may have changed since (a film marked watched on its page, a play), the set and its
	/// films are asked again behind the cards (<see cref="RefreshBehind"/>).
	/// </summary>
	public async Task Returned(object parameter, bool stale)
	{
		FocusRequest = -1;
		if (!stale)
		{
			return;
		}

		// 1.8.1 (BH-175-q2, the review): written only while this refresh is the newest (an older one answering last overwrote it)
		if (Nixie is { } nixie)
		{
			await State.RefreshBehindAsync(async newest =>
			{
				if (await nixie.LoadItems(JellyfinClient) is not { } items)
				{
					return LoadMessages.CouldNotLoad("this collection");
				}

				if (!newest())
				{
					return LoadOutcome.Loaded; // (not shown: a newer refresh shows its own)
				}

				_serverOrder = [.. items];
				Update(keepCards: true);
				return _serverOrder.Count == 0 ? EmptyOutcome(nixie.Collection) : LoadOutcome.Loaded;
			});
		}
		else if (Collection is { Id: { } id } shown)
		{
			await State.RefreshBehindAsync(newest => RefreshBehind(id, shown, newest));
		}
	}

	/// <summary>
	/// Nixie 1.7.4: the set and its films asked again together (one round trip), behind the cards Back showed: the same films in the
	/// same places keep their cards (a watch state or a favourite changed shows on its card: <see cref="KeptCards"/>), so the
	/// focus and the scroll stay; the header, the summary, the progress and Play follow; Hide watched hides a film marked meanwhile.
	/// </summary>
	private async Task<LoadOutcome> RefreshBehind(Guid id, BaseItemDto shown, Func<bool> newest)
	{
		var fresh = JellyfinClient.GetItem(id);
		var items = await ItemsOf(shown);
		var known = await fresh;
		if (known is null || items?.Items is not { } list)
		{
			return LoadMessages.CouldNotLoad($"this {Kind.ToLowerInvariant()}");
		}

		if (!newest())
		{
			return LoadOutcome.Loaded; // 1.8.1 (BH-175-q2): a newer refresh shows its own
		}

		Collection = known;
		_serverOrder = [.. list];
		Update(keepCards: true);
		if (_serverOrder.Count == 0)
		{
			var (title, message) = CollectionBrowse.EmptyText(Kind, LoadMessages.ServerName());
			return LoadOutcome.Empty(title, message);
		}

		return LoadOutcome.Loaded;
	}

	public async Task OnNavigatedTo(object parameter)
	{
		// 1.6 collections: a Nixie collection loads through its source (CollectionViewModel.Nixie.cs)
		if (parameter is NixieCollection nixie)
		{
			await OpenNixie(nixie);
			return;
		}

		var given = parameter as BaseItemDto;
		var id = given?.Id ?? (parameter as Guid?);
		if (id is null)
		{
			return;
		}

		Collection = given;
		IsPlaylist = given?.Type == BaseItemDto_Type.Playlist;
		await State.RunAsync(() => Load(id.Value, given));
		await AfterServerLoad(); // 1.6 collections: "Also in your library" on an admin's box set
	}

	private async Task<LoadOutcome> Load(Guid id, BaseItemDto? given)
	{
		// the set itself (its overview, its DisplayOrder, its user data) and its items, asked together when the card said what it is
		var fresh = JellyfinClient.GetItem(id);
		var early = given is { Type: not null } ? ItemsOf(given) : null;
		var known = await fresh ?? given;
		if (known is null)
		{
			return LoadMessages.CouldNotLoad("this collection");
		}

		Collection = known;
		IsPlaylist = known.Type == BaseItemDto_Type.Playlist;
		var what = $"this {Kind.ToLowerInvariant()}";

		var result = early is not null && known.Type == given!.Type ? await early : await ItemsOf(known);
		if (result is null or { Items: null })
		{
			return LoadMessages.CouldNotLoad(what);
		}

		_serverOrder = [.. result.Items];
		var keeps = KeepsPlace?.Invoke() == true;
		_placeKnown = false; // (each change shows the cards again: Update below once; nothing is remembered from here)
		if (keeps && known.Id is { } set && CollectionPlaces.Recall(JellyfinClient.UserId, set) is { } place)
		{
			// Back or R: the order and Hide watched as the user left them
			Order = place.Order;
			HideWatched = place.HideWatched;
		}

		RecallDirection(keeps); // 1.8: and the direction

		_placeKnown = true;
		Update();
		// (not remembered here: a collection opened anew would put its defaults over the place an earlier visit left, and Back
		// to that visit lost its Order; the place is the user's changes, OnOrderChanged and OnHideWatchedChanged)

		if (_serverOrder.Count == 0)
		{
			var (title, message) = CollectionBrowse.EmptyText(Kind, LoadMessages.ServerName());
			return LoadOutcome.Empty(title, message);
		}

		return LoadOutcome.Loaded;
	}

	private Task<BaseItemDtoQueryResult?> ItemsOf(BaseItemDto dto) =>
		dto.Type == BaseItemDto_Type.Playlist && dto.Id is { } id ? JellyfinClient.GetPlaylistItems(id) : JellyfinClient.GetItems(dto);

	partial void OnOrderChanged(CollectionOrder value)
	{
		Update();
		Remember();
	}

	partial void OnHideWatchedChanged(bool value)
	{
		Update();
		Remember();
	}

	/// <summary>Nixie 1.4.0 (bug hunt M2): the view as it is now, for Back and R (<see cref="CollectionPlaces"/>).</summary>
	private void Remember()
	{
		if (_placeKnown && Collection?.Id is { } set)
		{
			CollectionPlaces.Remember(JellyfinClient.UserId, set, new CollectionPlace(Order, HideWatched));
		}
	}

	/// <param name="keepCards">Nixie 1.7.4: a refresh behind the cards Back showed: the same films in the same places keep their cards.</param>
	private void Update(bool keepCards = false)
	{
		var own = CollectionBrowse.Ordered(_serverOrder, CollectionOrder.Collection, IsPlaylist, OwnDisplayOrder);
		var places = own.Select((item, i) => (item, i)).ToDictionary(x => x.item, x => x.i + 1, ReferenceEqualityComparer.Instance);
		var shown = SortDirections.Apply(CollectionBrowse.Ordered(_serverOrder, Order, IsPlaylist, OwnDisplayOrder), Reversed, SortDirections.Undated(Order, IsPlaylist, OwnDisplayOrder)) // 1.8: either way, undated last
			.Where(x => !HideWatched || !CollectionBrowse.IsWatched(x)).ToList();

		// 1.6 colpage (LB1): places only where the order means something (a franchise's release order, a playlist, an album)
		var numbered = Order == CollectionOrder.Collection && (PositionsOverride ?? CollectionBrowse.PositionsMeanSomething(Order, IsPlaylist, OwnDisplayOrder));
		var samePlaces = Entries.Count == shown.Count && Entries.Zip(shown).All(x => x.First.Position == places[x.Second] && x.First.ShowsPosition == numbered);
		if (!(keepCards && samePlaces && KeptCards.Refresh([.. Entries.Select(x => x.Item)], shown)))
		{
			Entries = [.. shown.Select(x => new CollectionEntry(BaseItemViewModel.FromDto(x), places[x], numbered))];
		}
		Header = CollectionBrowse.Header(Collection, own, Kind, PositionsOverride ?? CollectionBrowse.PositionsMeanSomething(CollectionOrder.Collection, IsPlaylist, OwnDisplayOrder), HeaderEyebrow, HeaderWhy);
		Summary = CollectionBrowse.Summary(_serverOrder, IsPlaylist);
		ShownText = CollectionBrowse.ShownText(shown.Count, _serverOrder);
		Progress = Math.Round(CollectionBrowse.Progress(_serverOrder) * 100, 1);
		Next = CollectionBrowse.Next(own);
		OnPropertyChanged(nameof(PlayName));
		IsWatched = _serverOrder.Count > 0 && _serverOrder.All(CollectionBrowse.IsWatched);
		AllHiddenText = HideWatched && shown.Count == 0 && _serverOrder.Count > 0 ? CollectionBrowse.AllWatchedText(_serverOrder) : "";
		Letters = Order == CollectionOrder.Name && shown.Count >= CollectionBrowse.LettersFrom
			? LibraryBrowse.Letters(shown.Select(CollectionBrowse.SortKey))
			: [];
	}

	/// <summary>Play or Resume: the first film not watched, in collection order.</summary>
	[RelayCommand]
	public Task Play()
	{
		if (Next.Item is not { } item)
		{
			return Task.CompletedTask;
		}

		// 1.5 qacheck: a playlist plays as itself, in its own order from that item (an episode in it brought its series)
		if (IsPlaylist)
		{
			var queue = CollectionBrowse.Ordered(_serverOrder, CollectionOrder.Collection, IsPlaylist, OwnDisplayOrder).Where(CollectionBrowse.IsPlayable).ToList();
			return commands.PlayInOrder(queue, queue.IndexOf(item), fromBeginning: Next.Again);
		}

		return commands.Play(item, null, streamsPicked: false);
	}

	/// <summary>Shuffle: a film not watched yet, at random (any film when all are watched).</summary>
	[RelayCommand]
	public Task Shuffle()
	{
		if (CollectionBrowse.Shuffle(_serverOrder, Pick) is not { } item)
		{
			return Task.CompletedTask;
		}

		// 1.5 qacheck: a playlist's Shuffle plays the playlist, the item picked first and the rest in a random order (an
		// episode picked brought its series)
		if (IsPlaylist)
		{
			var rest = _serverOrder.Where(x => CollectionBrowse.IsPlayable(x) && !ReferenceEquals(x, item)).ToList();
			var queue = new List<BaseItemDto> { item };
			while (rest.Count > 0)
			{
				var at = Math.Clamp(Pick(rest.Count), 0, rest.Count - 1);
				queue.Add(rest[at]);
				rest.RemoveAt(at);
			}

			return commands.PlayInOrder(queue, 0);
		}

		return commands.Play(item, null, streamsPicked: false);
	}

	[RelayCommand]
	public void SetOrder(CollectionOrder order) => Order = order;

	/// <summary>Hide watched off again (the "all watched" message's button).</summary>
	[RelayCommand]
	public void ShowWatched() => HideWatched = false;

	/// <summary>The whole set watched or not: the server marks every item, and the cards are read again.</summary>
	public async Task SetWatched(bool played)
	{
		if (await SetNixieWatched(played)) // 1.6 collections: a Nixie collection marks its titles
		{
			return;
		}

		if (Collection is not { Id: not null } collection)
		{
			return;
		}

		await commands.SetWatched(collection, played);
		var result = await ItemsOf(collection);
		if (result?.Items is { } items)
		{
			_serverOrder = [.. items];
		}

		Update();
	}

	public async Task SetFavorite(bool favorite)
	{
		if (await SetNixieFavorite(favorite)) // 1.6 collections: a Nixie collection's Favourite is kept on this PC
		{
			return;
		}

		if (Collection is { } collection)
		{
			await commands.SetFavorite(collection, favorite);
			OnPropertyChanged(nameof(IsFavorite));
		}
	}

	/// <summary>A to Z: the first title under that letter gets the focus.</summary>
	[RelayCommand]
	public void JumpTo(LetterJump? jump)
	{
		if (jump is null)
		{
			return;
		}

		if (FocusRequest == jump.Index)
		{
			OnPropertyChanged(nameof(FocusRequest));
		}
		else
		{
			FocusRequest = jump.Index;
		}
	}
}
