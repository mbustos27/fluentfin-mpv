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

	protected override void OnAttached()
	{
		base.OnAttached();


		AssociatedObject.TextChanged += AssociatedObject_TextChanged;
		AssociatedObject.QuerySubmitted += AssociatedObject_QuerySubmitted;

		// fluentfin-mpv: "no results" and a failed search (with a retry) are shown instead of nothing, and a newer query's
		// answer is never replaced by an older one that came back late (SearchSuggestions)
		_disposable = SearchSuggestions.Pipeline(_textChangedSubject, _searchNow, _jellyfinClient.Search, TimeSpan.FromMilliseconds(500))
			.Subscribe(update =>
			{
				AssociatedObject.DispatcherQueue.TryEnqueue(() =>
				{
					AssociatedObject.ItemsSource = update.Suggestions;
				});
			});
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
				sender.IsSuggestionListOpen = false;
				sender.Text = string.Empty;
				App.Commands.DisplayDto(open.Item);
				break;
			case SearchAction.Retry retry:
				_searchNow.OnNext(retry.Query);
				break;
			case SearchAction.ShowResults results:
				sender.IsSuggestionListOpen = false;
				App.GetService<INavigationService>().NavigateTo<SearchResultsViewModel>(results.Query);
				break;
		}
	}

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
		AssociatedObject.TextChanged -= AssociatedObject_TextChanged;
		AssociatedObject.QuerySubmitted -= AssociatedObject_QuerySubmitted;
		base.OnDetaching();
	}
}
