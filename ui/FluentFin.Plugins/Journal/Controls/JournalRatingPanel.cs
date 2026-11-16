using System;
using System.ComponentModel;
using FluentFin.Core;
using FluentFin.Core.Journal;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Plugins.Journal.Controls;

/// <summary>
/// fluentfin-mpv: "Your rating" on the episode and series pages (one element each in EpisodePage.xaml and SeriesPage.xaml):
/// the journal's half-star rating of <see cref="Item"/>. Hidden for items the journal does not rate (movies, for now) and
/// when the journal is not there (no sign-in, or it could not be opened).
/// </summary>
public sealed partial class JournalRatingPanel : UserControl
{
	public static readonly DependencyProperty ItemProperty =
		DependencyProperty.Register(nameof(Item), typeof(object), typeof(JournalRatingPanel), new PropertyMetadata(null, (d, _) => ((JournalRatingPanel)d).Load()));

	private readonly JournalRatingViewModel? _viewModel;
	private readonly HalfStarRating _stars = new() { StarSize = 18, VerticalAlignment = VerticalAlignment.Center };

	public JournalRatingPanel()
	{
		Visibility = Visibility.Collapsed;
		try
		{
			_viewModel = Locator.GetService<JournalRatingViewModel>();
		}
		catch (ArgumentException)
		{
			// no journal registered (a designer, a test host): the panel stays hidden
			return;
		}

		var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
		panel.Children.Add(new TextBlock { Text = "Your rating", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 });
		panel.Children.Add(_stars);
		// Seaglass: the rating sits in one glass card
		Content = new Border
		{
			Child = panel,
			Padding = new Thickness(18, 10, 18, 10),
			HorizontalAlignment = HorizontalAlignment.Left,
			Style = Application.Current.Resources.TryGetValue("GlassPanelStyle", out var style) ? style as Style : null,
		};

		_viewModel.PropertyChanged += OnViewModelChanged;
		_stars.ValueChanged += (_, value) => _viewModel.Value = value;
	}

	public BaseItemDto? Item
	{
		get => GetValue(ItemProperty) as BaseItemDto;
		set => SetValue(ItemProperty, value);
	}

	private async void Load()
	{
		if (_viewModel is null)
		{
			return;
		}

		try
		{
			await _viewModel.LoadAsync(Item);
		}
		catch (Exception)
		{
			// LoadAsync handles the journal's own failures; anything else must not reach the page
			Visibility = Visibility.Collapsed;
		}
	}

	private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
	{
		switch (e.PropertyName)
		{
			case nameof(JournalRatingViewModel.Value):
				_stars.Value = _viewModel!.Value;
				break;
			case nameof(JournalRatingViewModel.IsAvailable):
				Visibility = _viewModel!.IsAvailable ? Visibility.Visible : Visibility.Collapsed;
				break;
		}
	}
}
