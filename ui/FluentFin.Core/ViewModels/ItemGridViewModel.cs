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
}
