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

	[ObservableProperty]
	public partial bool IsLoading { get; set; }

	public ObservableCollection<NamedQueryResult> RecentItems { get; } = [];

	/// <summary>fluentfin-mpv: Home failed, is empty, or only partly loaded: said on the page, with Retry.</summary>
	public LoadState State { get; } = new("Home");

	public IJellyfinClient JellyfinClient { get; } = jellyfinClient;

	public Task OnNavigatedFrom()
	{
		_disposable.Dispose();
		return Task.CompletedTask;
	}

	public async Task OnNavigatedTo(object parameter)
	{
		await State.RunAsync(LoadSections);

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

	/// <summary>
	/// fluentfin-mpv: loads every section (again, for Retry) and says how it went: nothing could be loaded, the server has
	/// nothing to show, or some sections failed (docs/research/ux-review.md H6). The client answers a failed request with
	/// null; "Recently added" cannot tell a failure from no libraries, so it only counts as content.
	/// </summary>
	private async Task<LoadOutcome> LoadSections()
	{
		IsLoading = true;
		try
		{
			RecentItems.Clear();
			var continueLoaded = UpdateContinueItems();
			var nextUpLoaded = UpdateNextUpItems();
			await Task.WhenAll(continueLoaded, nextUpLoaded, UpdateRecentItems());

			var failed = !continueLoaded.Result || !nextUpLoaded.Result;
			var nothing = ContinueItems.Count == 0 && NextUpItems.Count == 0 && RecentItems.Count == 0;
			return (failed, nothing) switch
			{
				(true, true) => LoadMessages.CouldNotLoad("Home"),
				(true, false) => LoadOutcome.Partly("Some of Home couldn't be loaded", LoadMessages.CheckServer()),
				(false, true) => LoadOutcome.Empty("Nothing to watch yet",
					$"Movies and shows on {LoadMessages.ServerName()} appear here once its libraries have some. Libraries shows what is there now."),
				_ => LoadOutcome.Loaded,
			};
		}
		finally
		{
			IsLoading = false;
		}
	}

	// fluentfin-mpv: a section's flag follows its list, also when a Retry replaces the list
	partial void OnContinueItemsChanged(ObservableCollection<BaseItemViewModel> oldValue, ObservableCollection<BaseItemViewModel> newValue)
	{
		if (oldValue is not null)
		{
			oldValue.CollectionChanged -= OnContinueItemsCollectionChanged;
		}
		newValue.CollectionChanged += OnContinueItemsCollectionChanged;
		HasContinueItems = newValue.Count > 0;
	}

	partial void OnNextUpItemsChanged(ObservableCollection<BaseItemViewModel> oldValue, ObservableCollection<BaseItemViewModel> newValue)
	{
		if (oldValue is not null)
		{
			oldValue.CollectionChanged -= OnNextUpItemsCollectionChanged;
		}
		newValue.CollectionChanged += OnNextUpItemsCollectionChanged;
		HasNextUpItems = newValue.Count > 0;
	}

	private void OnContinueItemsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => HasContinueItems = ContinueItems.Count > 0;

	private void OnNextUpItemsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => HasNextUpItems = NextUpItems.Count > 0;

	/// <summary>False when the server did not answer (fluentfin-mpv: so the page can say so).</summary>
	private async Task<bool> UpdateContinueItems()
	{
		var response = await JellyfinClient.GetContinueWatching();

		if (response is null)
		{
			return false;
		}

		ContinueItems = [.. response.Items?.Select(BaseItemViewModel.FromDto) ?? []];
		return true;
	}

	/// <summary>False when the server did not answer (fluentfin-mpv: so the page can say so).</summary>
	private async Task<bool> UpdateNextUpItems()
	{
		var response = await JellyfinClient.GetNextUp();

		if (response is null)
		{
			return false;
		}

		NextUpItems = [.. response.Items?.Select(BaseItemViewModel.FromDto) ?? []];
		return true;
	}

	private async Task UpdateRecentItems()
	{
		await foreach (var list in JellyfinClient.GetRecentItemsFromUserLibraries())
		{
			RecentItems.Add(new(list.Library, [.. list.Items.Select(BaseItemViewModel.FromDto)]));
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

public record NamedQueryResult(BaseItemDto Library, ObservableCollection<BaseItemViewModel> Items);