using System;
using FluentFin.Core.Journal;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace FluentFin.Plugins.Journal.Controls;

/// <summary>
/// fluentfin-mpv: five stars in half-star steps, value 0-10 (0 = not rated; <see cref="StarRating"/> does the arithmetic).
/// Pointer: hover previews, click sets, clicking the current value clears. Keys: arrows by half a star, Home clears, End
/// is five stars, Delete clears. WinUI's RatingControl takes whole stars only, hence this one.
/// </summary>
public sealed partial class HalfStarRating : UserControl
{
	public static readonly DependencyProperty ValueProperty =
		DependencyProperty.Register(nameof(Value), typeof(int), typeof(HalfStarRating), new PropertyMetadata(0, (d, _) => ((HalfStarRating)d).Render()));

	public static readonly DependencyProperty IsReadOnlyProperty =
		DependencyProperty.Register(nameof(IsReadOnly), typeof(bool), typeof(HalfStarRating), new PropertyMetadata(false, (d, _) => ((HalfStarRating)d).OnReadOnlyChanged()));

	public static readonly DependencyProperty StarSizeProperty =
		DependencyProperty.Register(nameof(StarSize), typeof(double), typeof(HalfStarRating), new PropertyMetadata(20.0, (d, _) => ((HalfStarRating)d).Resize()));

	/// <summary>Filled stars: a warm gold that reads on the dark item pages and on the light and dark Diary.</summary>
	private static readonly SolidColorBrush Gold = new(Color.FromArgb(0xFF, 0xF2, 0xB7, 0x05));

	private const string OutlineGlyph = "";
	private const string FullGlyph = "";
	private const string HalfGlyph = "";

	private readonly StackPanel _stars = new() { Orientation = Orientation.Horizontal, Background = new SolidColorBrush(Colors.Transparent) };
	private readonly FontIcon[] _outlines = new FontIcon[StarRating.Stars];
	private readonly FontIcon[] _fills = new FontIcon[StarRating.Stars];
	private int? _hover;

	public HalfStarRating()
	{
		for (var i = 0; i < StarRating.Stars; i++)
		{
			_outlines[i] = new FontIcon { Glyph = OutlineGlyph, Opacity = 0.7 };
			_fills[i] = new FontIcon { Glyph = FullGlyph, Foreground = Gold };
			var cell = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
			cell.Children.Add(_outlines[i]);
			cell.Children.Add(_fills[i]);
			_stars.Children.Add(cell);
		}

		Content = _stars;
		AutomationProperties.SetName(this, "Your rating");
		_stars.PointerMoved += OnPointerMoved;
		_stars.PointerExited += OnPointerExited;
		_stars.Tapped += OnTapped;
		KeyDown += OnKeyDown;
		Resize();
		OnReadOnlyChanged();
	}

	/// <summary>0-10, half-stars.</summary>
	public int Value
	{
		get => (int)GetValue(ValueProperty);
		set => SetValue(ValueProperty, StarRating.Normalize(value));
	}

	public bool IsReadOnly
	{
		get => (bool)GetValue(IsReadOnlyProperty);
		set => SetValue(IsReadOnlyProperty, value);
	}

	public double StarSize
	{
		get => (double)GetValue(StarSizeProperty);
		set => SetValue(StarSizeProperty, value);
	}

	/// <summary>The user changed the value (not raised when <see cref="Value"/> is set from code).</summary>
	public event EventHandler<int>? ValueChanged;

	private void Resize()
	{
		// equal cells and no gaps, so the pointer maps straight to half stars
		foreach (var cell in _stars.Children)
		{
			((FrameworkElement)cell).Width = StarSize * 1.25;
		}

		foreach (var icon in _outlines)
		{
			icon.FontSize = StarSize;
		}

		foreach (var icon in _fills)
		{
			icon.FontSize = StarSize;
		}
	}

	private void OnReadOnlyChanged()
	{
		IsTabStop = !IsReadOnly;
		UseSystemFocusVisuals = true;
		_hover = null;
		Render();
	}

	private void Render()
	{
		var fills = StarRating.Fills(_hover ?? Value);
		for (var i = 0; i < fills.Length; i++)
		{
			_fills[i].Visibility = fills[i] == StarFill.Empty ? Visibility.Collapsed : Visibility.Visible;
			_fills[i].Glyph = fills[i] == StarFill.Half ? HalfGlyph : FullGlyph;
		}

		var text = StarRating.Describe(Value);
		ToolTipService.SetToolTip(this, IsReadOnly && Value == 0 ? null : text);
		AutomationProperties.SetItemStatus(this, text);
		if (FrameworkElementAutomationPeer.FromElement(this) is { } peer)
		{
			peer.RaiseAutomationEvent(AutomationEvents.PropertyChanged);
		}
	}

	private void SetByUser(int value)
	{
		value = StarRating.Normalize(value);
		if (value == Value)
		{
			return;
		}

		Value = value;
		ValueChanged?.Invoke(this, value);
	}

	private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
	{
		if (IsReadOnly)
		{
			return;
		}

		_hover = StarRating.FromPointer(e.GetCurrentPoint(_stars).Position.X, _stars.ActualWidth);
		Render();
	}

	private void OnPointerExited(object sender, PointerRoutedEventArgs e)
	{
		_hover = null;
		Render();
	}

	private void OnTapped(object sender, TappedRoutedEventArgs e)
	{
		if (IsReadOnly)
		{
			return;
		}

		e.Handled = true;
		_hover = null;
		Focus(FocusState.Pointer);
		SetByUser(StarRating.Click(Value, StarRating.FromPointer(e.GetPosition(_stars).X, _stars.ActualWidth)));
	}

	private void OnKeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (IsReadOnly)
		{
			return;
		}

		int? value = e.Key switch
		{
			VirtualKey.Left or VirtualKey.Down => StarRating.Step(Value, -1),
			VirtualKey.Right or VirtualKey.Up => StarRating.Step(Value, 1),
			VirtualKey.Home or VirtualKey.Delete or VirtualKey.Back => 0,
			VirtualKey.End => StarRating.Max,
			_ => null,
		};

		if (value is { } v)
		{
			e.Handled = true;
			SetByUser(v);
		}
	}

	protected override AutomationPeer OnCreateAutomationPeer() => new HalfStarRatingPeer(this);

	/// <summary>A slider to screen readers: 0-5 stars in steps of a half.</summary>
	private sealed partial class HalfStarRatingPeer(HalfStarRating owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
	{
		protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;

		protected override string GetClassNameCore() => nameof(HalfStarRating);

		protected override object GetPatternCore(PatternInterface patternInterface) =>
			patternInterface == PatternInterface.RangeValue ? this : base.GetPatternCore(patternInterface);

		public bool IsReadOnly => owner.IsReadOnly;

		public double LargeChange => 1;

		public double SmallChange => 0.5;

		public double Maximum => StarRating.Stars;

		public double Minimum => 0;

		public double Value => owner.Value / 2.0;

		public void SetValue(double value) => owner.SetByUser((int)Math.Round(value * 2, MidpointRounding.AwayFromZero));
	}
}
