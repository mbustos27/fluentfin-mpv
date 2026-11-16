using System.Reactive.Linq;
using System.Reactive.Subjects;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Xaml.Interactivity;
using ReactiveUI;
namespace FluentFin.Behaviors;

public partial class GlobalSearchBoxBehavior : Behavior<AutoSuggestBox>
{
	private IJellyfinClient _jellyfinClient = App.GetService<IJellyfinClient>();
	private IDisposable? _disposable;
	private readonly Subject<string> _textChangedSubject = new();

	// fluentfin-mpv: queries searched at once: Enter, or choosing "try again" after a failed search
	private readonly Subject<string> _searchNow = new();

	protected override void OnAttached()
	{
		base.OnAttached();


		AssociatedObject.TextChanged += AssociatedObject_TextChanged;
		AssociatedObject.SuggestionChosen += AssociatedObject_SuggestionChosen;
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

	/// <summary>fluentfin-mpv: Enter searches now; choosing a failed search's row searches again.</summary>
	private void AssociatedObject_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
	{
		switch (args.ChosenSuggestion)
		{
			case SearchMessage { CanRetry: true } failed:
				_searchNow.OnNext(failed.Query);
				break;
			case null when !string.IsNullOrWhiteSpace(args.QueryText):
				_searchNow.OnNext(args.QueryText);
				break;
		}
	}

	private void AssociatedObject_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
	{
		if (args.SelectedItem is not BaseItemDto dto)
		{
			return;
		}

		sender.Text = string.Empty;

		App.Commands.DisplayDto(dto);
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
		AssociatedObject.SuggestionChosen -= AssociatedObject_SuggestionChosen;
		AssociatedObject.QuerySubmitted -= AssociatedObject_QuerySubmitted;
		base.OnDetaching();
	}
}
