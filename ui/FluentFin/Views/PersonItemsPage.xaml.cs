using System.ComponentModel;
using FluentFin.Controls;
using FluentFin.Core.ViewModels;
using FluentFin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FluentFin.Views;

/// <summary>Nixie 1.5: "With Ava Abara" (<see cref="PersonItemsViewModel"/>), what a person in the search results opens.</summary>
public sealed partial class PersonItemsPage : Page
{
	public PersonItemsViewModel ViewModel { get; } = App.GetService<PersonItemsViewModel>();

	private readonly CardGrid _grid;
	private bool _focusedOnArrival;

	public PersonItemsPage()
	{
		InitializeComponent();

		// the Library's grid model (one Tab stop, arrows both ways, Home/End, the last card remembered, "n of m")
		_grid = new CardGrid(PersonScroller, PersonList, PersonRepeater);
		_grid.ForwardWheel(PersonRoot);
	}

	// 1.5 huntfix (BH-150-m2): the page opened on the top bar's Back (Enter went back); it opens on its first poster, as a
	// library does, by the app's one arrival rule (ArrivalFocus: never after the user moved, nor after R or a theme switch)
	// Nixie 1.7.4: the card the user left the page from, per person: Back (or Forward) puts the keyboard on it again, as a
	// collection does (it went to the first poster)
	private static readonly Dictionary<Guid, Guid> LeftFrom = [];
	private Guid? _returnTo;

	protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
	{
		if (Parameter is { } person && _grid.LastItem is BaseItemViewModel { Id: { } item })
		{
			LeftFrom[person] = item;
		}

		base.OnNavigatingFrom(e);
	}

	private Guid? Parameter => _parameter is Jellyfin.Sdk.Generated.Models.BaseItemDto { Id: { } id } ? id : null;
	private object? _parameter;

	protected override void OnNavigatedTo(NavigationEventArgs e)
	{
		_parameter = e.Parameter;
		_returnTo = e.NavigationMode is NavigationMode.Back or NavigationMode.Forward && Parameter is { } person && LeftFrom.TryGetValue(person, out var left) ? left : null;
		_focusedOnArrival = false;
		ViewModel.Results.PropertyChanged -= OnResultsChanged;
		ViewModel.Results.PropertyChanged += OnResultsChanged;
		base.OnNavigatedTo(e);
		if (ViewModel.Results.IsLoaded)
		{
			OnResultsChanged(ViewModel.Results, new PropertyChangedEventArgs(nameof(LoadState.Status)));
		}
	}

	protected override void OnNavigatedFrom(NavigationEventArgs e)
	{
		ViewModel.Results.PropertyChanged -= OnResultsChanged;
		base.OnNavigatedFrom(e);
	}

	private void OnResultsChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(LoadState.Status) && ViewModel.Results.IsLoaded && ViewModel.Items.Count > 0 && !_focusedOnArrival)
		{
			_focusedOnArrival = true;
			UiDispatch.Enqueue(DispatcherQueue, "the person page's arrival focus", FocusOnArrival, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
		}
	}

	private void FocusOnArrival()
	{
		if (!IsLoaded)
		{
			// loaded before it was drawn (a quick server): once it is
			void OnLoaded(object sender, RoutedEventArgs e)
			{
				Loaded -= OnLoaded;
				UiDispatch.Enqueue(DispatcherQueue, "the person page's arrival focus", FocusOnArrival, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
			}

			Loaded += OnLoaded;
			return;
		}

		// Nixie 1.7.4: Back to the page: the card it was left from
		var back = -1;
		for (var i = 0; _returnTo is { } id && i < ViewModel.Items.Count; i++)
		{
			if (ViewModel.Items[i].Id == id)
			{
				back = i;
				break;
			}
		}

		if (back >= 0)
		{
			ArrivalFocus.TryTake(this, "the card Back returns to", () =>
			{
				UpdateLayout();
				return _grid.FocusIndex(back, FocusState.Keyboard);
			});
			return;
		}

		ArrivalFocus.TryTake(this, "the first poster", () =>
		{
			// the grid lays its cards out first and the card is taken from those (BH-140-M3: a card asked for before the grid
			// had laid any out never let the layout settle)
			UpdateLayout();
			return _grid.FocusIndex(0, FocusState.Keyboard);
		});
	}
	// END 1.5 huntfix
}
