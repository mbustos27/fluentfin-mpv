using FluentFin.Core.Mpv;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace FluentFin.Controls;

/// <summary>
/// 1.6 skip: the overlay's skip-rule parts (docs/BRIDGE.md §4, §5): the "Skip this every episode?" card above the prompts,
/// and the "Skip a part every episode" sheet on the bar's skip-parts button (Shift+S). The state is
/// <see cref="MpvControlsViewModel"/>'s (<see cref="MpvControlsViewModel.SkipSheet"/>); this lays it out and passes input on.
/// </summary>
public sealed partial class MpvPlayerControls
{
	private bool _fillingSkipSheet;
	private bool _skipHooks;

	/// <summary>From <see cref="OnControlsChanged"/>: follow the controls' sheet requests and the card's showing.</summary>
	private void HookSkipRules(MpvControlsViewModel? old, MpvControlsViewModel? controls)
	{
		if (old is not null)
		{
			old.SkipSheetRequested -= OnSkipSheetRequested;
			old.PropertyChanged -= OnSkipPropertyChanged;
		}

		if (controls is not null)
		{
			controls.SkipSheetRequested += OnSkipSheetRequested;
			controls.PropertyChanged += OnSkipPropertyChanged;
		}

		if (!_skipHooks)
		{
			_skipHooks = true;
			// the card sits just above the prompts, wherever PlaceSkipButton puts them and however many show
			PillRow.SizeChanged += (_, _) => PlaceSuggestionCard();
			PillRow.RegisterPropertyChangedCallback(MarginProperty, (_, _) => PlaceSuggestionCard());
		}

		PlaceSuggestionCard();
	}

	private void PlaceSuggestionCard()
	{
		var row = PillRow.Margin;
		var above = PillRow.ActualHeight > 0 ? PillRow.ActualHeight + 12 : 0;
		SkipSuggestionCard.Margin = new Thickness(0, 0, row.Right, row.Bottom + above);
	}

	private void OnSkipPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(MpvControlsViewModel.IsSuggestionVisible) && Controls is { IsSuggestionVisible: true } shown)
		{
			// not focused (Space stays play); Narrator hears what it asks and how to answer
			AnnounceSkip($"{shown.SuggestionName} Tab to answer, Escape for not now.", "SkipSuggestion");
			PlaceSuggestionCard();
		}
	}

	private void AnnounceSkip(string text, string id) => Announce(text, id);

	private void OnSuggestionYes(object sender, RoutedEventArgs e) => _ = Controls?.AnswerSuggestion(SkipRuleWords.Yes);

	private void OnSuggestionNotNow(object sender, RoutedEventArgs e) => _ = Controls?.AnswerSuggestion(SkipRuleWords.NotNow);

	private void OnSuggestionNever(object sender, RoutedEventArgs e) => _ = Controls?.AnswerSuggestion(SkipRuleWords.Never);

	// -- the sheet ---------------------------------------------------------------------------------------------------------

	private void OnSkipPartsClick(object sender, RoutedEventArgs e) => Controls?.OpenSkipSheet();

	/// <summary>
	/// Shift+S or the button: the sheet opens on the button, once the bars it sits in show (Shift+S over hidden bars wakes
	/// them in the same call).
	/// </summary>
	private void OnSkipSheetRequested(object? sender, EventArgs e)
	{
		FillSkipSheet();
		if (SkipSheetFlyout.IsOpen)
		{
			return;
		}

		// low: after the layout that shows the bars (Shift+S over hidden bars wakes them in the same call)
		DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
		{
			if (Controls is { ControlsVisible: true } && SkipPartsButton.IsLoaded && SkipPartsButton.Visibility == Visibility.Visible)
			{
				FlyoutBase.ShowAttachedFlyout(SkipPartsButton);
			}
		});
	}

	/// <summary>The sheet's fields from its model (the pre-fill), without the edits echoing back.</summary>
	private void FillSkipSheet()
	{
		if (Controls?.SkipSheet is not { } sheet)
		{
			return;
		}

		_fillingSkipSheet = true;
		try
		{
			SkipSheetStart.Text = sheet.StartText;
			SkipSheetEnd.Text = sheet.EndText;
			SkipSheetWhere.SelectedIndex = sheet.FromEnd ? 1 : 0;
			SkipSheetAction.SelectedIndex = sheet.SkipsByItself ? 1 : 0;
		}
		finally
		{
			_fillingSkipSheet = false;
		}
	}

	private void OnSkipSheetOpened(object? sender, object e)
	{
		Controls?.FlyoutOpened(true);   // 1.6 integration: a flyout (Escape's first rung)
		// the keyboard goes to the sheet's first field (Shift+S left it on the player)
		SkipSheetStart.Focus(FocusState.Programmatic);
		SkipSheetStart.SelectAll();
	}

	private void OnSkipSheetClosed(object? sender, object e) => Controls?.FlyoutOpened(false);

	private void OnSkipSheetTimeChanged(object sender, TextChangedEventArgs e)
	{
		if (_fillingSkipSheet || Controls?.SkipSheet is not { } sheet)
		{
			return;
		}

		sheet.StartText = SkipSheetStart.Text;
		sheet.EndText = SkipSheetEnd.Text;
		sheet.Error = "";
	}

	private void OnSkipSheetChoiceChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_fillingSkipSheet || Controls?.SkipSheet is not { } sheet)
		{
			return;
		}

		sheet.FromEnd = SkipSheetWhere.SelectedIndex == 1;
		sheet.SkipsByItself = SkipSheetAction.SelectedIndex == 1;
	}

	private void OnSkipSheetMarkStart(object sender, RoutedEventArgs e) => MarkSkipSheet(start: true);

	private void OnSkipSheetMarkEnd(object sender, RoutedEventArgs e) => MarkSkipSheet(start: false);

	private void MarkSkipSheet(bool start)
	{
		if (Controls is not { } controls)
		{
			return;
		}

		var seconds = TimeSpan.FromTicks(controls.PositionTicks).TotalSeconds;
		if (start)
		{
			controls.SkipSheet.MarkStart(seconds);
		}
		else
		{
			controls.SkipSheet.MarkEnd(seconds);
		}

		FillSkipSheet();
	}

	private void OnSkipSheetCancel(object sender, RoutedEventArgs e) => SkipSheetFlyout.Hide();

	private async void OnSkipSheetSave(object sender, RoutedEventArgs e) => await SaveSkipSheet();

	private async Task SaveSkipSheet()
	{
		if (Controls is { } controls && await controls.SaveSkipRule())
		{
			SkipSheetFlyout.Hide();
		}
		else if (Controls?.SkipSheet.Error is { Length: > 0 } error)
		{
			AnnounceSkip(error, "SkipSheet");
		}
	}

	/// <summary>Enter in a field saves; Escape is the flyout's own (it closes).</summary>
	private async void OnSkipSheetKeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Key == Windows.System.VirtualKey.Enter && e.OriginalSource is TextBox)
		{
			e.Handled = true;
			await SaveSkipSheet();
		}
	}
}
