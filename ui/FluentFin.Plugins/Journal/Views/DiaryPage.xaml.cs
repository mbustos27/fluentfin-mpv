using System;
using System.Threading.Tasks;
using FluentFin.Core;
using FluentFin.Core.Journal;
using FluentFin.Plugins.Journal.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;

namespace FluentFin.Plugins.Journal.Views;

/// <summary>
/// fluentfin-mpv: the Diary page (docs/JOURNAL.md). The view model is in FluentFin.Core (tested there); this page only adds
/// what needs WinUI: the edit and delete dialogs, the save picker, images.
/// </summary>
public sealed partial class DiaryPage : Page, IJournalDialogs
{
	public DiaryViewModel ViewModel { get; } = Locator.GetService<DiaryViewModel>();

	public DiaryPage()
	{
		InitializeComponent();
		ViewModel.Dialogs = this;
	}

	public static BitmapImage? Image(string? url) =>
		Uri.TryCreate(url, UriKind.Absolute, out var uri) ? new BitmapImage(uri) { DecodePixelHeight = 180 } : null;

	private void OnItemClick(object sender, ItemClickEventArgs e) => ViewModel.OpenCommand.Execute(e.ClickedItem as DiaryEntryViewModel);

	private void OnEditClick(object sender, RoutedEventArgs e) => ViewModel.EditCommand.Execute((sender as FrameworkElement)?.Tag as DiaryEntryViewModel);

	private void OnDeleteClick(object sender, RoutedEventArgs e) => ViewModel.DeleteCommand.Execute((sender as FrameworkElement)?.Tag as DiaryEntryViewModel);

	public async Task<DiaryEditResult?> EditAsync(DiaryEntryViewModel entry)
	{
		var local = entry.Local;
		var date = new CalendarDatePicker { Header = "Watched on", Date = local, PlaceholderText = "Date unknown", MinWidth = 200 };
		var time = new TimePicker { Header = "At", Time = local?.TimeOfDay ?? new TimeSpan(20, 0, 0) };
		var rating = new HalfStarRating { Value = entry.Rating, StarSize = 24 };
		var rewatch = new CheckBox { Content = "Rewatch", IsChecked = entry.Rewatch };
		var review = new TextBox
		{
			Header = "Review",
			Text = entry.Review ?? "",
			AcceptsReturn = true,
			TextWrapping = TextWrapping.Wrap,
			Height = 120,
			PlaceholderText = "Only on this PC",
		};
		var spoiler = new CheckBox { Content = "Contains spoilers", IsChecked = entry.Entry.Spoiler };

		var when = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
		when.Children.Add(date);
		when.Children.Add(time);
		var stars = new StackPanel { Spacing = 4 };
		stars.Children.Add(new TextBlock { Text = "Your rating of the episode" });
		stars.Children.Add(rating);
		var form = new StackPanel { Spacing = 16, MinWidth = 420 };
		form.Children.Add(when);
		if (entry.IsApproximate)
		{
			form.Children.Add(new TextBlock { Text = entry.DateNote, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
		}

		form.Children.Add(stars);
		form.Children.Add(rewatch);
		form.Children.Add(review);
		form.Children.Add(spoiler);

		var dialog = new ContentDialog
		{
			XamlRoot = XamlRoot,
			Title = $"{entry.SeriesName} · {entry.EpisodeCode}",
			Content = new ScrollViewer { Content = form },
			PrimaryButtonText = "Save",
			CloseButtonText = "Cancel",
			DefaultButton = ContentDialogButton.Primary,
		};

		if (await dialog.ShowAsync() != ContentDialogResult.Primary)
		{
			return null;
		}

		// an untouched date keeps the stored instant (and where it came from); a changed one is the user's, in local time
		DateTimeOffset? watchedAt = entry.Entry.WatchedAt;
		if (date.Date is { } picked)
		{
			// the time picker shows minutes: seconds the user cannot see are no change
			static long Minutes(TimeSpan t) => (long)t.TotalMinutes;
			var changed = local is not { } before || picked.Date != before.Date || Minutes(time.Time) != Minutes(before.TimeOfDay);
			if (changed)
			{
				var wall = picked.Date + time.Time;
				watchedAt = new DateTimeOffset(wall, ViewModel.TimeZone.GetUtcOffset(wall));
			}
		}
		else
		{
			watchedAt = null;
		}

		return new DiaryEditResult(new DiaryEntryEdit(watchedAt, rewatch.IsChecked == true, review.Text, spoiler.IsChecked == true), rating.Value);
	}

	public async Task<bool> ConfirmDeleteAsync(DiaryEntryViewModel entry)
	{
		var dialog = new ContentDialog
		{
			XamlRoot = XamlRoot,
			Title = "Delete this diary entry?",
			Content = new TextBlock
			{
				Text = $"{entry.SeriesName} {entry.EpisodeCode}, {entry.DateText}. Jellyfin still has it as watched; a sync does not bring the entry back.",
				TextWrapping = TextWrapping.Wrap,
			},
			PrimaryButtonText = "Delete",
			CloseButtonText = "Cancel",
			DefaultButton = ContentDialogButton.Close,
		};

		return await dialog.ShowAsync() == ContentDialogResult.Primary;
	}

	public async Task<string?> PickExportFileAsync(string suggestedName, string extension)
	{
		var picker = new FileSavePicker { SuggestedFileName = suggestedName, SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
		picker.FileTypeChoices.Add(extension == ".csv" ? "CSV (spreadsheet)" : "JSON", [extension]);
		var window = Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
		WinRT.Interop.InitializeWithWindow.Initialize(picker, window);
		var file = await picker.PickSaveFileAsync();
		return file?.Path;
	}
}
