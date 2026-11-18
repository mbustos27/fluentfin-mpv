using FluentFin.Dialogs.ViewModels;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using ReactiveUI;
using Windows.System;

namespace FluentFin.Dialogs.Views;

public sealed partial class EditMetadataDialog : IViewFor<EditMetadataViewModel>
{
	/// <summary>Nixie 1.2.0 (admin): far enough back for any release date (WinUI's default is 100 years ago, and a date picker moves an older date to it).</summary>
	private static readonly DateTimeOffset EarliestYear = new(1800, 1, 1, 0, 0, 0, TimeSpan.Zero);

	public EditMetadataViewModel? ViewModel { get; set; } = App.GetService<EditMetadataViewModel>();

	object? IViewFor.ViewModel { get => ViewModel; set => ViewModel = (EditMetadataViewModel?)value; }

	public EditMetadataDialog()
	{
		InitializeComponent();
		ReleaseDatePicker.MinYear = EarliestYear;
		DateAddedPicker.MinYear = EarliestYear;
		EndDatePicker.MinYear = EarliestYear;
	}

	public static ImageSource GetImage() => new BitmapImage();

	public static bool HasText(string? text) => !string.IsNullOrWhiteSpace(text);

	/// <summary>The genre chip's remove button says which genre it removes.</summary>
	public static string RemoveGenreName(string genre) => $"Remove genre {genre}";

	private void AddGenre_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => AddTypedGenre();

	private void AddGenreTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Key == VirtualKey.Enter)
		{
			e.Handled = true;
			AddTypedGenre();
		}
	}

	private void AddTypedGenre()
	{
		if (!HasText(AddGenreTextBox.Text))
		{
			return;
		}

		ViewModel?.AddGenre(AddGenreTextBox.Text);
		AddGenreTextBox.Text = "";
	}
}
