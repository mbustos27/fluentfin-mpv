using System;
using System.ComponentModel;
using FluentFin.Core;
using FluentFin.Core.Journal;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Plugins.Journal.Controls;

/// <summary>
/// fluentfin-mpv: "Your rating" on the item pages: the journal's half-star rating of <see cref="Item"/>. Hidden for items the
/// journal does not rate (movies, for now) and when the journal is not there (no sign-in, or it could not be opened).
/// Seaglass 1.1.4: it sits in the action row of the item pages' hero (ItemHero.xaml), which draws the pill around it.
/// </summary>
public sealed partial class JournalRatingPanel : UserControl
{
	/// <summary>
	/// Seaglass 1.1.4: the stars' size. <see cref="HalfStarRating"/> gives each star a cell 1.25 times as wide, so 20 makes a
	/// 25 px target per star (WCAG 2.5.8: at least 24); 18 made 22.5.
	/// </summary>
	public const double StarSize = 20;

	/// <summary>Seaglass 1.1.4: the stars' target height (WCAG 2.5.8: at least 24 px; the stars alone were 18 high).</summary>
	public const double TargetHeight = 24;

	public static readonly DependencyProperty ItemProperty =
		DependencyProperty.Register(nameof(Item), typeof(object), typeof(JournalRatingPanel), new PropertyMetadata(null, (d, _) => ((JournalRatingPanel)d).Load()));

	private readonly JournalRatingViewModel? _viewModel;
	private readonly HalfStarRating _stars = new() { StarSize = JournalRatingPanel.StarSize, MinHeight = JournalRatingPanel.TargetHeight, VerticalAlignment = VerticalAlignment.Center };

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
		// Seaglass 1.1.4: no card of its own any more; the hero's action row draws the pill
		Content = panel;

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
