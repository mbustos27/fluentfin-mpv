using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using ReactiveUI;

namespace FluentFin.Core.ViewModels;

public partial class HomeViewModel(IJellyfinClient jellyfinClient,
								   IObservable<IOutboundWebSocketMessage> webSocketMessages,
								   GlobalCommands commands) : ObservableObject, INavigationAware
{
	private readonly CompositeDisposable _disposable = [];


	[ObservableProperty]
	public partial ObservableCollection<BaseItemViewModel> ContinueItems { get; set; } = [];

	[ObservableProperty]
	public partial ObservableCollection<BaseItemViewModel> NextUpItems { get; set; } = [];

	[ObservableProperty]
	public partial bool HasNextUpItems { get; set; }

	[ObservableProperty]
	public partial bool HasContinueItems { get; set; }

	/// <summary>True until every part of the page has its data.</summary>
	[ObservableProperty]
	public partial bool IsLoading { get; set; }

	// fluentfin-mpv: progressive home. The page no longer waits for everything behind one shimmer: Continue Watching,
	// Next Up and each "Recently added" row (one per view, in the server's order, from the views sign-in already read)
	// show a placeholder until their own request answers, and all those requests go out together.
	[ObservableProperty]
	public partial bool IsContinueLoading { get; set; } = true;

	[ObservableProperty]
	public partial bool IsNextUpLoading { get; set; } = true;

	public ObservableCollection<NamedQueryResult> RecentItems { get; } = [];

	public IJellyfinClient JellyfinClient { get; } = jellyfinClient;

	public Task OnNavigatedFrom()
	{
		_disposable.Dispose();
		return Task.CompletedTask;
	}

	public async Task OnNavigatedTo(object parameter)
	{
		IsLoading = true;

		// fluentfin-mpv: listen from the start, since rows can be used (mark watched...) while others still load
		ListenToUserDataChanges();

		await Task.WhenAll(UpdateContinueItems(), UpdateNextUpItems(), UpdateRecentItems());

		IsLoading = false;
	}

	private void SetContinueItems(IReadOnlyList<BaseItemDto> items)
	{
		ContinueItems = [.. items.Select(BaseItemViewModel.FromDto)];
		ContinueItems.CollectionChanged += (_, _) => HasContinueItems = ContinueItems.Count > 0;
		HasContinueItems = ContinueItems.Count > 0;
	}

	private void SetNextUpItems(IReadOnlyList<BaseItemDto> items)
	{
		NextUpItems = [.. items.Select(BaseItemViewModel.FromDto)];
		HasNextUpItems = NextUpItems.Count > 0;
	}

	private void ListenToUserDataChanges()
	{
		webSocketMessages
			.Select(x => x as UserDataChangedMessage)
			.WhereNotNull()
			.ObserveOn(RxSchedulers.MainThreadScheduler)
			.Subscribe(async msg =>
			{
				foreach (var item in msg.Data?.UserDataList ?? [])
				{
					if (item.ItemId is not { } guid)
					{
						continue;
					}

					ProcessContinueWatchingItemChanged(item, guid);
					await ProcessNextUpItemChanged(item, guid);
					ProcessRecentItemChanged(item, guid);
				}
			})
			.DisposeWith(_disposable);
	}

	// fluentfin-mpv: a failed request leaves the section empty (its placeholder goes)
	private async Task UpdateContinueItems()
	{
		try
		{
			if (await JellyfinClient.GetContinueWatching() is { } response)
			{
				SetContinueItems(response.Items ?? []);
			}
		}
		finally
		{
			IsContinueLoading = false;
		}
	}

	private async Task UpdateNextUpItems()
	{
		try
		{
			if (await JellyfinClient.GetNextUp() is { } response)
			{
				SetNextUpItems(response.Items ?? []);
			}
		}
		finally
		{
			IsNextUpLoading = false;
		}
	}

	private async Task UpdateRecentItems()
	{
		// fluentfin-mpv: a placeholder row per view at once, in the server's order; each fills in, or goes, as its
		// request answers
		var views = (await JellyfinClient.GetUserViews()).Where(x => x.CollectionType is not BaseItemDto_CollectionType.Music).ToList();
		var rows = views.Select(view => new NamedQueryResult(view)).ToList();
		foreach (var row in rows)
		{
			RecentItems.Add(row);
		}

		await Task.WhenAll(rows.Select(UpdateRecentRow));
	}

	private async Task UpdateRecentRow(NamedQueryResult row)
	{
		try
		{
			var items = await JellyfinClient.GetLatestItems(row.Library);

			if (items is null or { Count: 0 })
			{
				RecentItems.Remove(row);
				return;
			}

			row.Items = [.. items.Select(BaseItemViewModel.FromDto)];
		}
		finally
		{
			row.IsLoading = false;
		}
	}

	private void ProcessRecentItemChanged(UserItemDataDto userData, Guid guid)
	{
		foreach (var library in RecentItems)
		{
			if (library.Items.FirstOrDefault(x => x.Id == guid) is not BaseItemViewModel { UserData: not null } dto)
			{
				continue;
			}

			dto.UserData.IsFavorite = userData.IsFavorite;
			dto.UserData.Played = userData.Played;
			dto.UserData.PlayedPercentage = userData.PlayedPercentage;
			dto.UserData.UnplayedItemCount = userData.UnplayedItemCount;
		}
		;
	}

	private void ProcessContinueWatchingItemChanged(UserItemDataDto userData, Guid guid)
	{
		if (ContinueItems.FirstOrDefault(x => x.Id == guid) is not { } item)
		{
			return;
		}

		if (userData.PlaybackPositionTicks is null or 0 ||
			userData.Played == true)
		{
			ContinueItems.Remove(item);
		}
		else
		{
			item.UserData!.PlayedPercentage = userData.PlayedPercentage;
		}
	}

	private async Task ProcessNextUpItemChanged(UserItemDataDto userData, Guid guid)
	{
		if (NextUpItems.FirstOrDefault(x => x.Id == guid) is not { } item)
		{
			return;
		}

		if (userData.Played == true)
		{
			await UpdateNextUpItems();
		}
	}

	[RelayCommand]
	private async Task Continue() => await PlayFirstItem(ContinueItems);

	[RelayCommand]
	private async Task NextUp() => await PlayFirstItem(NextUpItems);

	private Task PlayFirstItem(ObservableCollection<BaseItemViewModel> items)
	{
		if (items.Count == 0)
		{
			return Task.CompletedTask;
		}

		return commands.PlayDto(items.First().Dto);
	}
}

// fluentfin-mpv: was a record (Library, Items); a row now exists, showing a placeholder, before its items are in
public partial class NamedQueryResult(BaseItemDto library) : ObservableObject
{
	public BaseItemDto Library { get; } = library;

	[ObservableProperty]
	public partial ObservableCollection<BaseItemViewModel> Items { get; set; } = [];

	[ObservableProperty]
	public partial bool IsLoading { get; set; } = true;
}