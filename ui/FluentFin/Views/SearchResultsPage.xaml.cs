using FluentFin.Core.ViewModels;
using FluentFin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace FluentFin.Views;

/// <summary>Seaglass 1.1.4: the search results page (<see cref="SearchResultsViewModel"/>), opened by Enter in the title bar's search.</summary>
public sealed partial class SearchResultsPage : Page
{
	public SearchResultsViewModel ViewModel { get; } = App.GetService<SearchResultsViewModel>();

	public SearchResultsPage()
	{
		InitializeComponent();
		// Nixie 1.7.5: kept whole by the frame for Back to this very page (its grid as it was, at once); a new visit makes it anew
		// (NavigationService.KeptWhole)
		NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
	}

	// ---- Nixie 1.7.4: Back to the results puts the user where they were ------------------------------------------------------

	// per search: the card the user left from and how far down the results were (Back and Forward put them back; the focus went
	// to the top bar's Back, and the results were searched again)
	private static readonly Dictionary<string, (Guid? Item, double Offset)> LeftAt = [];
	private (Guid? Item, double Offset)? _returnTo;
	private bool _placed;

	// Nixie 1.7.5: the place's next look, held by the page until it ticks (a timer only a local holds can be collected first)
	private Microsoft.UI.Dispatching.DispatcherQueueTimer? _placeTimer;

	protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
	{
		base.OnNavigatedTo(e);
		_placed = false;
		_returnTo = e.NavigationMode is Microsoft.UI.Xaml.Navigation.NavigationMode.Back or Microsoft.UI.Xaml.Navigation.NavigationMode.Forward
			&& (e.Parameter as string)?.Trim() is { } query && LeftAt.TryGetValue(query, out var place) ? place : null;
		ViewModel.Results.PropertyChanged -= OnResultsChanged;
		ViewModel.Results.PropertyChanged += OnResultsChanged;
		// Nixie 1.7.5: results Back shows as they were (the kept view model, loaded already): their place at the normal priority
		_placePriority = _returnTo is not null && ViewModel.Results.IsLoaded ? Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal : Microsoft.UI.Dispatching.DispatcherQueuePriority.Low;
		if (ViewModel.Results.IsLoaded)
		{
			OnResultsChanged(ViewModel.Results, new System.ComponentModel.PropertyChangedEventArgs(nameof(LoadState.Status)));
		}
	}

	protected override void OnNavigatingFrom(Microsoft.UI.Xaml.Navigation.NavigatingCancelEventArgs e)
	{
		var card = XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject focused ? CardOf(focused) : null;
		LeftAt[ViewModel.Query] = (card?.Model?.Id, ResultsScroller.VerticalOffset);
		base.OnNavigatingFrom(e);
	}

	protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
	{
		ViewModel.Results.PropertyChanged -= OnResultsChanged;
		base.OnNavigatedFrom(e);
	}

	private void OnResultsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(LoadState.Status) && ViewModel.Results.IsLoaded && _returnTo is { } place && !_placed)
		{
			_placed = true;
			Services.UiDispatch.Enqueue(DispatcherQueue, "the search results' place", () => PutBack(place), _placePriority);
		}
	}

	// Nixie 1.7.5: the place's passes: on the low priority they waited for the cards' pictures, and Back's card took the
	// keyboard 2.4 s after the results were shown under load (gate2, Classic); Back's kept results go at the normal one
	private Microsoft.UI.Dispatching.DispatcherQueuePriority _placePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority.Low;

	// the results as far down as they were (once), then the card left from takes the keyboard (by the app's one arrival rule);
	// the scroll lands a layout or two later, and the cards there are made then: the card is looked for again a few times (the
	// scroll asked once: asked again before it had landed, Drive-in's results showed nothing at all)
	private void PutBack((Guid? Item, double Offset) place, int tries = 10, bool scrolled = false)
	{
		if (!IsLoaded)
		{
			void OnLoaded(object sender, RoutedEventArgs e)
			{
				Loaded -= OnLoaded;
				Services.UiDispatch.Enqueue(DispatcherQueue, "the search results' place", () => PutBack(place, tries, scrolled), _placePriority);
			}

			Loaded += OnLoaded;
			return;
		}

		if (!scrolled)
		{
			scrolled = true;
			UpdateLayout();
			ResultsScroller.ScrollTo(0, place.Offset, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
			NavigationService.PlaceStep(this, $"scroll asked ({(int)place.Offset} px)"); // 1.7.5
			NavigationService.StepWhenAt(this, ResultsScroller, place.Offset);
		}

		if (place.Item is not { } id || !Services.ArrivalFocus.Shown(this))
		{
			return;
		}

		if (CardFor(id) is { } card)
		{
			NavigationService.PlaceStep(this, $"card made (try {11 - tries})"); // 1.7.5
			Services.ArrivalFocus.TryTake(this, "the card Back returns to", () => card.Focus(FocusState.Programmatic));
			NavigationService.PlacePutBack(this, "place"); // 1.7.5 (rule 10)
			return;
		}

		if (tries > 1)
		{
			_placeTimer?.Stop();
			var timer = _placeTimer = DispatcherQueue.CreateTimer();
			timer.Interval = TimeSpan.FromMilliseconds(100);
			timer.IsRepeating = false;
			// (1.7.5: UiDispatch.Tick as written, so Ui173Tests' scan of every tick finds its guard)
			timer.Tick += UiDispatch.Tick("the search results' place, again", () => PutBack(place, tries - 1, scrolled));
			timer.Start();
		}
	}

	/// <summary>
	/// The card the results show for <paramref name="id"/>, as its row's repeater has it made now (the top result's, else its
	/// group's); null while it is not made. Never a card a repeater keeps aside to reuse (it still shows an older item).
	/// </summary>
	private Controls.BaseItemDtoCard? CardFor(Guid id)
	{
		if (ViewModel.TopResult?.Id == id && Descendants<ItemsRepeater>(TopResultRow).FirstOrDefault()?.TryGetElement(0) is { } top)
		{
			return CardIn(top);
		}

		for (var g = 0; g < ViewModel.CardGroups.Count; g++)
		{
			var items = ViewModel.CardGroups[g].Items;
			for (var i = 0; i < items.Count; i++)
			{
				if (items[i].Id == id)
				{
					return ResultGroups.ContainerFromIndex(g) is DependencyObject row
						&& Descendants<ItemsRepeater>(row).FirstOrDefault()?.TryGetElement(i) is { } element ? CardIn(element) : null;
				}
			}
		}

		return null;
	}

	private static Controls.BaseItemDtoCard? CardIn(UIElement element) =>
		element as Controls.BaseItemDtoCard ?? Descendants<Controls.BaseItemDtoCard>(element).FirstOrDefault();

	private static Controls.BaseItemDtoCard? CardOf(DependencyObject? element)
	{
		for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (node is Controls.BaseItemDtoCard card)
			{
				return card;
			}
		}

		return null;
	}

	/// <summary>
	/// Nixie 1.5: Left and Right (Home, End) move the keyboard between the chips; Space or Enter lights the one it is on. The
	/// arrows stop at either end (no wrap), and anything else goes on to the page.
	/// </summary>
	private void KindChips_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Key is not (VirtualKey.Left or VirtualKey.Right or VirtualKey.Home or VirtualKey.End))
		{
			return;
		}

		var chips = Descendants<RadioButton>(KindChips).ToList();
		var at = chips.FindIndex(c => c.FocusState != FocusState.Unfocused);
		if (chips.Count == 0 || at < 0)
		{
			return;
		}

		var to = e.Key switch
		{
			VirtualKey.Left => Math.Max(0, at - 1),
			VirtualKey.Right => Math.Min(chips.Count - 1, at + 1),
			VirtualKey.Home => 0,
			_ => chips.Count - 1,
		};
		e.Handled = true;
		if (to != at)
		{
			chips[to].Focus(FocusState.Keyboard);
		}
	}

	private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
	{
		for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
		{
			var child = VisualTreeHelper.GetChild(root, i);
			if (child is T match)
			{
				yield return match;
			}

			foreach (var inner in Descendants<T>(child))
			{
				yield return inner;
			}
		}
	}

	/// <summary>Nixie 1.5: a line of the top result's details shows only with words in it.</summary>
	public Microsoft.UI.Xaml.Visibility Shows(string? text) => string.IsNullOrEmpty(text) ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

	/// <summary>Nixie 1.5: the top result as its row's one card (none when there is no top result).</summary>
	public IReadOnlyList<BaseItemViewModel> One(BaseItemViewModel? item) => item is null ? [] : [item];
}
