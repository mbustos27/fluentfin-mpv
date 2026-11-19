using System.ComponentModel;
using CommunityToolkit.WinUI;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace FluentFin.Controls;

/// <summary>A letter of the A to Z was pressed: the jump, and how (a key: the card gets the keyboard focus; a click: the program's).</summary>
public sealed record LetterJumpedEventArgs(LetterJump Jump, FocusState FocusState);

/// <summary>
/// Nixie 1.4.0: the A to Z, or the sort's two directions, in the row under a library's toolbar (see LetterBar.xaml). Each
/// theme gives the letters its own look (<see cref="LetterStyle"/>, the tile size); the shape, the names and the keys are this
/// control's.
/// </summary>
public sealed partial class LetterBar : UserControl
{
	[GeneratedDependencyProperty]
	public partial LibraryViewModel? ViewModel { get; set; }

	/// <summary>The theme's style for a letter (a Button).</summary>
	[GeneratedDependencyProperty]
	public partial Style? LetterStyle { get; set; }

	/// <summary>The theme's style for the two directions (ToggleButtons).</summary>
	[GeneratedDependencyProperty]
	public partial Style? DirectionStyle { get; set; }

	/// <summary>A letter's widest (it narrows to keep the 27 on one row, down to <see cref="MinTileWidth"/>).</summary>
	[GeneratedDependencyProperty(DefaultValue = 32d)]
	public partial double TileWidth { get; set; }

	[GeneratedDependencyProperty(DefaultValue = 24d)]
	public partial double MinTileWidth { get; set; }

	/// <summary>A letter's height: at least 32 (WCAG 2.5.8), and the row's.</summary>
	[GeneratedDependencyProperty(DefaultValue = 32d)]
	public partial double TileHeight { get; set; }

	[GeneratedDependencyProperty(DefaultValue = 2d)]
	public partial double TileSpacing { get; set; }

	/// <summary>The readout's ink ("M · 324 of 1,000"), where the row sits on a theme's art (Drive-in's sky).</summary>
	[GeneratedDependencyProperty]
	public partial Microsoft.UI.Xaml.Media.Brush? ReadoutBrush { get; set; }

	partial void OnReadoutBrushChanged(Microsoft.UI.Xaml.Media.Brush? newValue)
	{
		if (newValue is not null)
		{
			PositionLine.Foreground = newValue;
		}
	}

	/// <summary>A letter was pressed (the page gives the card the focus the way the letter was pressed).</summary>
	public event EventHandler<LetterJumpedEventArgs>? Jumped;

	public LetterBar()
	{
		InitializeComponent();
		Visibility = Visibility.Collapsed;
		LetterRepeater.ElementPrepared += (_, e) => Prepare(e.Element);
		RowGrid.SizeChanged += (_, _) => Fit();
		PositionLine.SizeChanged += (_, _) => Fit();
	}

	/// <summary>The letters as drawn (the page's tests and the harness find them by name).</summary>
	internal ItemsRepeater Repeater => LetterRepeater;

	private LibraryViewModel? _watched;

	partial void OnViewModelChanged(LibraryViewModel? newValue)
	{
		if (_watched is not null)
		{
			_watched.PropertyChanged -= ViewModel_PropertyChanged;
		}

		_watched = newValue;
		if (newValue is not null)
		{
			newValue.PropertyChanged += ViewModel_PropertyChanged;
		}

		Bindings.Update();
		ShowMode();
	}

	partial void OnLetterStyleChanged(Style? newValue) => Restyle();

	partial void OnDirectionStyleChanged(Style? newValue)
	{
		AscendingButton.Style = newValue ?? (Style)Resources["LetterBarDirectionStyle"];
		DescendingButton.Style = AscendingButton.Style;
	}

	partial void OnTileWidthChanged(double newValue) => Fit();

	partial void OnTileHeightChanged(double newValue) => Restyle();

	partial void OnTileSpacingChanged(double newValue)
	{
		LetterLayout.Spacing = newValue;
		Fit();
	}

	private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		switch (e.PropertyName)
		{
			case nameof(LibraryViewModel.LetterRow) or nameof(LibraryViewModel.SortBy) or nameof(LibraryViewModel.Order) or nameof(LibraryViewModel.InPlaylistOrder):
				ShowMode();
				break;
			case nameof(LibraryViewModel.LetterTiles):
				DispatcherQueue.TryEnqueue(Fit);
				break;
		}
	}

	/// <summary>A resting letter is silent to Narrator (it is not a stop either: it is disabled).</summary>
	public static AccessibilityView ViewFor(bool hasTitles) => hasTitles ? AccessibilityView.Content : AccessibilityView.Raw;

	public static double MarkOpacity(bool current) => current ? 1 : 0;

	public static Visibility PositionVisibility(string? text, int total) =>
		!string.IsNullOrEmpty(text) && total >= LibraryBrowse.LettersFrom ? Visibility.Visible : Visibility.Collapsed;

	/// <summary>Letters, the directions, or (nothing to show) no row; the row's height kept for both.</summary>
	private void ShowMode()
	{
		var mode = ViewModel?.LetterRow ?? LetterRowMode.None;
		Visibility = mode == LetterRowMode.None ? Visibility.Collapsed : Visibility.Visible;
		RowGrid.MinHeight = Math.Max(TileHeight, 32);
		LetterList.Visibility = mode == LetterRowMode.Letters ? Visibility.Visible : Visibility.Collapsed;
		Directions.Visibility = mode == LetterRowMode.Directions ? Visibility.Visible : Visibility.Collapsed;
		if (ViewModel is { } vm && mode == LetterRowMode.Directions)
		{
			SetDirection(AscendingButton, vm, SortOrder.Ascending);
			SetDirection(DescendingButton, vm, SortOrder.Descending);
		}
	}

	private static void SetDirection(ToggleButton button, LibraryViewModel vm, SortOrder order)
	{
		var words = LibraryBrowse.DirectionWords(vm.SortBy, order);
		words = char.ToUpperInvariant(words[0]) + words[1..];
		button.Content = words;
		button.Tag = order;
		button.IsChecked = !vm.InPlaylistOrder && vm.Order == order;
		AutomationProperties.SetName(button, words);
	}

	private void Direction_Click(object sender, RoutedEventArgs e)
	{
		if (sender is ToggleButton { Tag: SortOrder order } && ViewModel is { } vm)
		{
			vm.UpdateSortOrder(order);
		}

		ShowMode(); // a click on the one already on leaves it on
	}

	private void Letter_Click(object sender, RoutedEventArgs e)
	{
		if (sender is Button { Tag: LetterTile { Jump: { } jump } } button && ViewModel is { } vm)
		{
			Jumped?.Invoke(this, new LetterJumpedEventArgs(jump, button.FocusState is FocusState.Keyboard ? FocusState.Keyboard : FocusState.Programmatic));
			vm.JumpToCommand.Execute(jump);
		}
	}

	private void Restyle()
	{
		RowGrid.MinHeight = Math.Max(TileHeight, 32);
		for (var i = 0; i < (ViewModel?.LetterTiles.Count ?? 0); i++)
		{
			if (LetterRepeater.TryGetElement(i) is { } element)
			{
				Prepare(element);
			}
		}
	}

	private double _width = 32;

	private void Prepare(UIElement element)
	{
		if (element is not Button button)
		{
			return;
		}

		button.Style = LetterStyle ?? (Style)Resources["LetterBarButtonStyle"];
		button.Width = _width;
		button.Height = Math.Max(TileHeight, 32);
		if (button.Tag is LetterTile tile)
		{
			// the place among the letters that have titles ("3 of 24"); a resting letter has none
			AutomationProperties.SetPositionInSet(button, tile.Position);
			AutomationProperties.SetSizeOfSet(button, tile.Size);
		}
	}

	/// <summary>The letters narrow (down to <see cref="MinTileWidth"/>) to stay on one row beside the readout.</summary>
	private void Fit()
	{
		var count = ViewModel?.LetterTiles.Count ?? 0;
		if (count == 0)
		{
			return;
		}

		var readout = PositionLine.Visibility == Visibility.Visible ? PositionLine.ActualWidth + RowGrid.ColumnSpacing : 0;
		var room = RowGrid.ActualWidth - readout;
		var width = room <= 0 ? TileWidth : Math.Floor((room - TileSpacing * (count - 1)) / count);
		width = Math.Clamp(width, MinTileWidth, TileWidth);
		if (Math.Abs(width - _width) < 0.5)
		{
			return;
		}

		_width = width;
		Restyle();
	}

	/// <summary>The keyboard into the A to Z: the letter in view, else the first that has titles.</summary>
	public bool FocusCurrent(FocusState state)
	{
		var tiles = ViewModel?.LetterTiles ?? [];
		var index = -1;
		for (var i = 0; i < tiles.Count && index < 0; i++)
		{
			if (tiles[i].IsCurrent && tiles[i].HasTitles)
			{
				index = i;
			}
		}

		if (index < 0)
		{
			for (var i = 0; i < tiles.Count && index < 0; i++)
			{
				if (tiles[i].HasTitles)
				{
					index = i;
				}
			}
		}

		return index >= 0 && LetterRepeater.GetOrCreateElement(index) is Control letter && letter.Focus(state);
	}
}
