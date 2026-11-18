using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;

namespace FluentFin.Core.ViewModels;

/// <summary>What "See all" on a row opens: the row's items as they are (a title and the cards).</summary>
public sealed record ItemGridRequest(string Title, IReadOnlyList<BaseItemViewModel> Items);

/// <summary>
/// Seaglass scrolling: the vertical grid page behind a row's "See all" where the row has no library of its own (Continue
/// Watching, Next Up). It shows the items the row had, in the row's order.
/// </summary>
public partial class ItemGridViewModel(IJellyfinClient jellyfinClient) : ObservableObject, INavigationAware
{
	public IJellyfinClient JellyfinClient { get; } = jellyfinClient;

	[ObservableProperty]
	public partial string Title { get; set; } = "";

	public ObservableCollection<BaseItemViewModel> Items { get; } = [];

	public Task OnNavigatedTo(object parameter)
	{
		if (parameter is ItemGridRequest request)
		{
			Title = request.Title;
			Items.Clear();
			foreach (var item in request.Items)
			{
				Items.Add(item);
			}
		}

		return Task.CompletedTask;
	}

	public Task OnNavigatedFrom() => Task.CompletedTask;

	// ---- Seaglass 1.1.4 (interaction audit, item 5): Back to the grid puts the user where they were ------------------

	private static readonly Lock s_lock = new();
	private static readonly Dictionary<(Guid User, string Title), (Guid? Item, double Offset)> s_places = [];

	/// <summary>The user leaves the grid for an item: the card they were on and how far down the grid was, kept for Back.</summary>
	public void LeftAt(BaseItemViewModel? item, double offset)
	{
		lock (s_lock)
		{
			s_places[(JellyfinClient.UserId, Title)] = (item?.Id, offset);
		}
	}

	/// <summary>Back: the card to focus (its index here, or -1 when it is no longer in the grid) and how far down to scroll; null when this grid was never left.</summary>
	public (int Index, double Offset)? ReturnPlace()
	{
		lock (s_lock)
		{
			if (!s_places.TryGetValue((JellyfinClient.UserId, Title), out var place))
			{
				return null;
			}

			var index = place.Item is { } id ? Items.ToList().FindIndex(x => x.Id == id) : -1;
			return (index, place.Offset);
		}
	}
}
