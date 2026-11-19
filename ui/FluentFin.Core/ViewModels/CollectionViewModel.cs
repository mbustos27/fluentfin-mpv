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
		Update();

		if (_serverOrder.Count == 0)
		{
			var (title, message) = CollectionBrowse.EmptyText(Kind, LoadMessages.ServerName());
			return LoadOutcome.Empty(title, message);
		}

		return LoadOutcome.Loaded;
	}

	private Task<BaseItemDtoQueryResult?> ItemsOf(BaseItemDto dto) =>
		dto.Type == BaseItemDto_Type.Playlist && dto.Id is { } id ? JellyfinClient.GetPlaylistItems(id) : JellyfinClient.GetItems(dto);

	partial void OnOrderChanged(CollectionOrder value) => Update();

	partial void OnHideWatchedChanged(bool value) => Update();

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
	public Task Play() => Next.Item is { } item ? commands.Play(item, null, streamsPicked: false) : Task.CompletedTask;

	/// <summary>Shuffle: a film not watched yet, at random (any film when all are watched).</summary>
	[RelayCommand]
	public Task Shuffle() => CollectionBrowse.Shuffle(_serverOrder, Pick) is { } item ? commands.Play(item, null, streamsPicked: false) : Task.CompletedTask;

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
