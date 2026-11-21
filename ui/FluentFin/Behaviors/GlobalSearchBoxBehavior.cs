using System.Reactive.Linq;
using System.Reactive.Subjects;
using FluentFin.Contracts.Services;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Xaml.Interactivity;
using ReactiveUI;
namespace FluentFin.Behaviors;

public partial class GlobalSearchBoxBehavior : Behavior<AutoSuggestBox>
{
	private IJellyfinClient _jellyfinClient = App.GetService<IJellyfinClient>();
	private IDisposable? _disposable;
	private readonly Subject<string> _textChangedSubject = new();

	// fluentfin-mpv: queries searched at once: choosing "try again" after a failed search (Seaglass 1.1.4: Enter opens the results page)
	private readonly Subject<string> _searchNow = new();

	// Seaglass 1.1.4 (review): the search is dropped (Enter, a page change, a sign-out): no list, and no late answer opening one
	private readonly Subject<System.Reactive.Unit> _drop = new();
	private ViewModels.TitleBarViewModel? _titleBar;

	protected override void OnAttached()
	{
		base.OnAttached();


		AssociatedObject.TextChanged += AssociatedObject_TextChanged;
		AssociatedObject.QuerySubmitted += AssociatedObject_QuerySubmitted;

		// fluentfin-mpv: "no results" and a failed search (with a retry) are shown instead of nothing, and a newer query's
		// answer is never replaced by an older one that came back late (SearchSuggestions)
		// Nixie 1.5 (search): the suggestions ask as the results page does (the longest word again when the whole query finds
		// nothing) and are ranked as it ranks (SearchSuggestions.For)
		_disposable = SearchSuggestions.Pipeline(_textChangedSubject, _searchNow, query => Core.Search.SearchRunner.Suggest(_jellyfinClient, query), TimeSpan.FromMilliseconds(500), _drop)
			.Subscribe(update =>
			{
				AssociatedObject.DispatcherQueue.TryEnqueue(() =>
				{
					AssociatedObject.ItemsSource = update.Suggestions;
					if (update.IsDropped)
					{
						AssociatedObject.IsSuggestionListOpen = false;
					}
				});
			});

		_titleBar = App.GetService<ITitleBarViewModel>() as ViewModels.TitleBarViewModel;
		if (_titleBar is not null)
		{
			_titleBar.SearchReset += OnSearchReset;
		}
	}

	/// <summary>
	/// Seaglass 1.1.4 (interaction audit, item 2): Enter, a click on a row or the search button. A row (chosen with the arrows
	/// or the mouse) acts: an item opens and the box clears, a failed search searches again, "See all" opens the results page;
	/// Enter on the text alone opens the results page for it (SearchSuggestions.OnSubmitted). Moving through the rows with the
	/// arrows only puts the row's name in the box (TextMemberPath): nothing opens until Enter.
	/// </summary>
	private void AssociatedObject_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
	{
		switch (SearchSuggestions.OnSubmitted(args.ChosenSuggestion, args.QueryText))
		{
			case SearchAction.Open open:
				Drop();
				sender.IsSuggestionListOpen = false;
				sender.Text = string.Empty;
				// Nixie 1.4.0 (the trail rule): an item chosen in the box's list lights the search box
				App.GetService<Core.ViewModels.TopNavigation>().ChooseSearch();
				App.Commands.DisplayDto(open.Item);
				break;
			case SearchAction.Retry retry:
				_searchNow.OnNext(retry.Query);
				break;
			case SearchAction.ShowResults results:
				Drop();
				sender.IsSuggestionListOpen = false;
				SearchPageMemory.Forget(results.Query); // Nixie 1.5: a new search starts on All, its groups folded
				App.GetService<INavigationService>().NavigateTo<SearchResultsViewModel>(results.Query);
				break;
		}
	}

	/// <summary>Seaglass 1.1.4 (review): the list goes, and a search still waiting or running never shows.</summary>
	private void Drop()
	{
		_drop.OnNext(System.Reactive.Unit.Default);
		AssociatedObject.IsSuggestionListOpen = false;
		AssociatedObject.ItemsSource = null;
	}

	private void OnSearchReset(object? sender, EventArgs e) => AssociatedObject.DispatcherQueue.TryEnqueue(Drop);

	private void AssociatedObject_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
	{
		if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
		{
			return;
		}

		if (string.IsNullOrEmpty(sender.Text))
		{
			return;
		}

		if (sender.Text is { Length: < 3 })
		{
			return;
		}

		_textChangedSubject.OnNext(sender.Text);
	}

	protected override void OnDetaching()
	{
		_disposable?.Dispose();
		if (_titleBar is not null)
		{
			_titleBar.SearchReset -= OnSearchReset;
		}

		AssociatedObject.TextChanged -= AssociatedObject_TextChanged;
		AssociatedObject.QuerySubmitted -= AssociatedObject_QuerySubmitted;
		base.OnDetaching();
	}
}
