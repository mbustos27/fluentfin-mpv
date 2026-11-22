using System.ComponentModel;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

// 1.7 upnav
/// <summary>
/// Nixie 1.7 (review of wip/170-upnav, item 3): where the trail's one lookup gets an item that does not carry its ids. An
/// episode or season page's view model asks the server for that item anyway (EpisodeViewModel, SeasonViewModel), so the
/// trail waits for the one it fetched instead of asking a second time; any other page asks the server. The wait ends when
/// the page is left (the token TopNavigation cancels).
/// </summary>
public static class TrailItems
{
	/// <summary>TopNavigation.ItemOf for the app: the shown page's view model's item, else the server's.</summary>
	/// <param name="shownViewModel">The view model of the page the shell shows now.</param>
	/// <param name="server">The server's item by its id (JellyfinClient.GetItem).</param>
	public static Func<Guid, CancellationToken, Task<BaseItemDto?>> FromShownPage(Func<object?> shownViewModel, Func<Guid, Task<BaseItemDto?>> server) =>
		(id, cancel) => shownViewModel() switch
		{
			EpisodeViewModel episode => Fetched(episode, () => episode.Dto, episode.State, id, cancel),
			SeasonViewModel season => Fetched(season, () => season.Dto, season.State, id, cancel),
			_ => server(id).WaitAsync(cancel),
		};

	/// <summary>
	/// The item <paramref name="page"/> fetched: its Dto once it is that item and carries its ids, or once the page's load
	/// ends (that item as the server gave it; null when the load failed or the page shows another item).
	/// </summary>
	public static async Task<BaseItemDto?> Fetched(INotifyPropertyChanged page, Func<BaseItemDto?> dto, LoadState state, Guid id, CancellationToken cancel)
	{
		var done = new TaskCompletionSource<BaseItemDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
		void Check(bool loadEnded)
		{
			var item = dto();
			if (item?.Id == id && (!ItemTrail.NeedsLookup(item) || loadEnded))
			{
				done.TrySetResult(item);
			}
			else if (loadEnded)
			{
				done.TrySetResult(null);
			}
		}

		void OnPage(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == "Dto")
			{
				Check(false);
			}
		}

		// a load that ends after the wait began (a status the page had already, from an item shown before, does not count)
		void OnState(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(LoadState.Status) && state.Status is LoadStatus.Loaded or LoadStatus.Empty or LoadStatus.Failed)
			{
				Check(true);
			}
		}

		page.PropertyChanged += OnPage;
		state.PropertyChanged += OnState;
		try
		{
			using var stop = cancel.Register(() => done.TrySetCanceled(cancel));
			Check(false);
			return await done.Task;
		}
		finally
		{
			page.PropertyChanged -= OnPage;
			state.PropertyChanged -= OnState;
		}
	}
}
// end 1.7 upnav
