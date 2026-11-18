using FluentFin.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Controls;

/// <summary>Nixie 1.3.0: the layout theme picker's tiles (ThemePicker.xaml) over a <see cref="ThemePickerViewModel"/>.</summary>
public sealed partial class ThemePicker : UserControl
{
	public ThemePicker()
	{
		InitializeComponent();
	}

	public static readonly DependencyProperty ViewModelProperty =
		DependencyProperty.Register(nameof(ViewModel), typeof(ThemePickerViewModel), typeof(ThemePicker), new PropertyMetadata(null, OnViewModelChanged));

	public ThemePickerViewModel? ViewModel
	{
		get => (ThemePickerViewModel?)GetValue(ViewModelProperty);
		set => SetValue(ViewModelProperty, value);
	}

	private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		var picker = (ThemePicker)d;
		if (e.OldValue is ThemePickerViewModel old)
		{
			foreach (var option in old.Options)
			{
				option.PropertyChanged -= picker.OnOptionChanged;
			}
		}

		if (e.NewValue is ThemePickerViewModel vm)
		{
			foreach (var option in vm.Options)
			{
				option.PropertyChanged += picker.OnOptionChanged;
			}
		}

		picker.Bindings.Update();
	}

	/// <summary>Moves the keyboard to the tile of the selected theme (the "Choose a look" step opens on it).</summary>
	public void FocusSelected()
	{
		if (Options.SelectedItem is { } item && Options.ContainerFromItem(item) is GridViewItem tile)
		{
			tile.Focus(FocusState.Keyboard);
		}
		else
		{
			Options.Focus(FocusState.Keyboard);
		}
	}

	/// <summary>The name Narrator reads for an option's tile.</summary>
	public static string TileName(ThemeOption option) => option.IsActive ? $"{option.AutomationName} In use." : option.AutomationName;

	private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
	{
		if (args.Item is ThemeOption option)
		{
			AutomationProperties.SetName(args.ItemContainer, TileName(option));
		}
	}

	private void OnOptionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (sender is ThemeOption option && e.PropertyName == nameof(ThemeOption.IsActive) && Options.ContainerFromItem(option) is GridViewItem tile)
		{
			AutomationProperties.SetName(tile, TileName(option));
		}
	}
}
