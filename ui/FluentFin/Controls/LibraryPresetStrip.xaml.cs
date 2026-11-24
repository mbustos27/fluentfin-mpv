using System.ComponentModel;
using CommunityToolkit.WinUI;
using FluentFin.Core.ViewModels;
using FluentFin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace FluentFin.Controls;

/// <summary>1.6 colpage (LB2, LB3, LB5): the library's presets strip (see LibraryPresetStrip.xaml).</summary>
public sealed partial class LibraryPresetStrip : UserControl
{
	/// <summary>The theme keys: the presets' style (a ToggleButton style), and whether their words are in capitals (Drive-in's board).</summary>
	public const string ChipStyleKey = "LibraryPresetChipStyle", UppercaseKey = "LibraryPresetUppercase";

	[GeneratedDependencyProperty]
	public partial LibraryViewModel? ViewModel { get; set; }

	private readonly List<ToggleButton> _buttons = [];
	private LibraryViewModel? _watched;

	public LibraryPresetStrip()
	{
		InitializeComponent();
		// Nixie 1.7.4 (the rows stream): the rows' pointer behaviour (EdgeScroller: arrows at its ends on hover, the wheel once
		// the page can't move, a drag), as for every horizontal row
		Content = null;
		Content = EdgeScroller.Wrap(Scroller, Strip, "Presets").Host;
		// Left, Right, Home and End along the strip, here rather than by the scroller (which would scroll instead of moving)
		Strip.AddHandler(KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler(Strip_KeyDown), true);
		Loaded += (_, _) => Watch(ViewModel);
		Unloaded += (_, _) => Watch(null);
	}

	/// <summary>The preset buttons, for a page that moves the keyboard to them (Up from the grid's first row).</summary>
	public IReadOnlyList<ToggleButton> Buttons => _buttons;

	partial void OnViewModelChanged(LibraryViewModel? newValue)
	{
		if (IsLoaded)
		{
			Watch(newValue);
		}
	}

	private void Watch(LibraryViewModel? vm)
	{
		if (_watched is not null)
		{
			_watched.PropertyChanged -= OnViewModelPropertyChanged;
		}

		_watched = vm;
		if (vm is not null)
		{
			vm.PropertyChanged += OnViewModelPropertyChanged;
		}

		Show();
	}

	private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(LibraryViewModel.Presets))
		{
			Show();
		}
	}

	/// <summary>The buttons as the model says now: made again only when the presets themselves change, else lit or not in place (the keyboard stays on its button).</summary>
	private void Show()
	{
		var states = _watched?.Presets ?? [];
		if (_buttons.Count != states.Count || _buttons.Zip(states).Any(x => ((LibraryPresetState)x.First.Tag).Id != x.Second.Id))
		{
			Strip.Children.Clear();
			_buttons.Clear();
			var style = ThemeResources.Find(ChipStyleKey) as Style ?? (Style)Application.Current.Resources["NixieChipToggleButtonStyle"];
			foreach (var state in states)
			{
				var button = new ToggleButton { Style = style, Tag = state };
				button.Click += Button_Click;
				// the keyboard's preset in view, with a little of its neighbour (the strip scrolls sideways)
				button.GotFocus += (sender, _) => ((UIElement)sender).StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
				_buttons.Add(button);
				Strip.Children.Add(button);
			}
		}

		var upper = ThemeResources.Find(UppercaseKey) is true;
		for (var i = 0; i < _buttons.Count; i++)
		{
			var state = states[i];
			var button = _buttons[i];
			button.Tag = state;
			button.Content = upper ? state.Label.ToUpper(System.Globalization.CultureInfo.CurrentCulture) : state.Label;
			button.IsChecked = state.IsOn;
			AutomationProperties.SetName(button, state.Name);
			AutomationProperties.SetAutomationId(button, $"Preset-{state.Id}");
			AutomationProperties.SetPositionInSet(button, i + 1);
			AutomationProperties.SetSizeOfSet(button, _buttons.Count);
			ToolTipService.SetToolTip(button, state.Name);
		}
	}

	private void Strip_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
	{
		var at = _buttons.FindIndex(b => b.FocusState != FocusState.Unfocused);
		if (at < 0 || TabKeys.Next(e.Key, at, _buttons.Count) is not { } next)
		{
			return;
		}

		e.Handled = true;
		_buttons[next].Focus(FocusState.Keyboard);
	}

	private void Button_Click(object sender, RoutedEventArgs e)
	{
		if (sender is ToggleButton { Tag: LibraryPresetState state } && _watched is { } vm)
		{
			vm.TogglePreset(state.Preset);
			Show(); // All clicked while lit stays lit: the button shows the model, not its own toggle
		}
	}
}
