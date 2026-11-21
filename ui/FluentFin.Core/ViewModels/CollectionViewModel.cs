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
public partial class CollectionViewModel(IJellyfinClient jellyfinClient, GlobalCommands commands) : ObservableObject, INavigationAware
{
	private IReadOnlyList<BaseItemDto> _serverOrder = [];

	/// <summary>Shuffle's dice (tests fix them).</summary>
	public Func<int, int> Pick { get; set; } = Random.Shared.Next;

	public IJellyfinClient JellyfinClient { get; } = jellyfinClient;

	/// <summary>The box set or the playlist.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Title), nameof(Overview), nameof(IsFavorite), nameof(Kind), nameof(OrderLabel), nameof(OrderButtonName))]
	public partial BaseItemDto? Collection { get; private set; }

	/// <summary>A playlist: an ordered list (its own order, no years in the summary).</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(OrderLabel), nameof(OrderButtonName), nameof(Kind))]
	public partial bool IsPlaylist { get; private set; }

	public string Title => Collection?.Name ?? "";

	public string Overview => Collection?.Overview ?? "";

	/// <summary>"Collection" or "Playlist": the eyebrow over the title, and the list's description.</summary>
	public string Kind => CollectionBrowse.KindOf(Collection);

	/// <summary>The order the set keeps: an album its tracks', a box set its DisplayOrder.</summary>
	private string? OwnDisplayOrder => Collection?.Type is BaseItemDto_Type.MusicAlbum or BaseItemDto_Type.PhotoAlbum ? CollectionBrowse.TrackOrder : Collection?.DisplayOrder;

	/// <summary>The cards as shown: in the order picked, without the watched ones under Hide watched.</summary>
	[ObservableProperty]
	public partial IReadOnlyList<CollectionEntry> Entries { get; private set; } = [];

	/// <summary>Over the films: "6 films", or "5 of 6 films" under Hide watched.</summary>
	[ObservableProperty]
	public partial string ShownText { get; private set; } = "";

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
	public partial CollectionOrder Order { get; set; } = CollectionOrder.Collection;

	/// <summary>"Order: Collection".</summary>
	public string OrderLabel => Order == CollectionOrder.Collection ? $"Order: {Kind}" : CollectionBrowse.OrderLabel(Order, IsPlaylist);

	public string OrderButtonName => $"{OrderLabel}, change the order";

	[ObservableProperty]
	public partial bool HideWatched { get; set; }

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

	public async Task OnNavigatedTo(object parameter)
	{
		var given = parameter as BaseItemDto;
		var id = given?.Id ?? (parameter as Guid?);
		if (id is null)
		{
			return;
		}

		Collection = given;
		IsPlaylist = given?.Type == BaseItemDto_Type.Playlist;
		await State.RunAsync(() => Load(id.Value, given));
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
		if (KeepsPlace?.Invoke() == true && known.Id is { } set && CollectionPlaces.Recall(JellyfinClient.UserId, set) is { } place)
		{
			// Back or R: the order and Hide watched as the user left them (each change shows the cards again: Update below once)
			_placeKnown = false;
			Order = place.Order;
			HideWatched = place.HideWatched;
		}

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

	private void Update()
	{
		var own = CollectionBrowse.Ordered(_serverOrder, CollectionOrder.Collection, IsPlaylist, OwnDisplayOrder);
		var places = own.Select((item, i) => (item, i)).ToDictionary(x => x.item, x => x.i + 1, ReferenceEqualityComparer.Instance);
		var shown = CollectionBrowse.Ordered(_serverOrder, Order, IsPlaylist, OwnDisplayOrder)
			.Where(x => !HideWatched || !CollectionBrowse.IsWatched(x)).ToList();

		Entries = [.. shown.Select(x => new CollectionEntry(BaseItemViewModel.FromDto(x), places[x], Order == CollectionOrder.Collection))];
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
