using FluentFin.Converters;
using FluentFin.Core.ViewModels;
using FluentFin.Services;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FluentFin.Themes.DriveIn.Controls;

/// <summary>
/// Drive-in: the top of an item page. The item's backdrop fills the stage, faded into the ground at the left and the
/// bottom, so it shows to the right of the opaque marquee board (<see cref="ItemBoard"/>); the credits ("Starring",
/// "Created by") sit on the art at the lower right. The page is opaque: nothing of the window's soft artwork behind it.
/// </summary>
[ContentProperty(Name = nameof(PrimaryActions))]
public sealed partial class ItemStage : Grid
{
	public static readonly DependencyProperty DtoProperty =
		DependencyProperty.Register(nameof(Dto), typeof(object), typeof(ItemStage), new PropertyMetadata(null, (d, _) => ((ItemStage)d).Apply()));

	public static readonly DependencyProperty PrimaryActionsProperty =
		DependencyProperty.Register(nameof(PrimaryActions), typeof(object), typeof(ItemStage), new PropertyMetadata(null, (d, e) => ((ItemStage)d)._board.PrimaryActions = e.NewValue));

	public static readonly DependencyProperty LeftOfProperty =
		DependencyProperty.Register(nameof(LeftOf), typeof(object), typeof(ItemStage), new PropertyMetadata(null, (d, e) => ((ItemStage)d)._board.LeftOf = e.NewValue as BaseItemDto));

	public BaseItemDto? Dto { get => GetValue(DtoProperty) as BaseItemDto; set => SetValue(DtoProperty, value); }

	public object? PrimaryActions { get => GetValue(PrimaryActionsProperty); set => SetValue(PrimaryActionsProperty, value); }

	public BaseItemDto? LeftOf { get => GetValue(LeftOfProperty) as BaseItemDto; set => SetValue(LeftOfProperty, value); }

	/// <summary>The board (for the page: its Play has the keyboard on arrival).</summary>
	public ItemBoard Board => _board;

	private readonly Grid _art = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
	private readonly Image _backdrop = new() { Stretch = Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Center };
	private readonly Border _sideScrim = new() { IsHitTestVisible = false };
	private readonly Border _bottomScrim = new() { IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Bottom, Height = 140 };
	private readonly ItemBoard _board = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
	private readonly Border _creditsPlate = new()
	{
		HorizontalAlignment = HorizontalAlignment.Right,
		VerticalAlignment = VerticalAlignment.Bottom,
		CornerRadius = new CornerRadius(8),
		Padding = new Thickness(14, 8, 14, 8),
	};
	private readonly TextBlock _credits = new()
	{
		TextAlignment = TextAlignment.Right,
		FontSize = 14,
		LineHeight = 24,
		TextWrapping = TextWrapping.Wrap,
		MaxWidth = 520,
	};

	public ItemStage()
	{
		AutomationProperties.SetAccessibilityView(_backdrop, AccessibilityView.Raw);
		AutomationProperties.SetAccessibilityView(_sideScrim, AccessibilityView.Raw);
		AutomationProperties.SetAccessibilityView(_bottomScrim, AccessibilityView.Raw);
		_credits.RequestedTheme = ElementTheme.Dark;
		// the art, its own scrims inside it, sized to the board (the stage is as tall as the board, never the picture)
		_art.Children.Add(_backdrop);
		_art.Children.Add(_sideScrim);
		_art.Children.Add(_bottomScrim);
		Children.Add(_art);
		// the credits on the art keep 4.5:1 whatever the picture: a dark plate under them (a sign's card, not glass)
		_creditsPlate.Child = _credits;
		Children.Add(_creditsPlate);
		Children.Add(_board);
		Loaded += (_, _) =>
		{
			AmbientBackdrop.Set(null);
			ApplyScrims();
			ApplyWidth(ActualWidth);
		};
		ActualThemeChanged += (_, _) => ApplyScrims();
		SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);
		_board.SizeChanged += (_, _) => _art.Height = _board.ActualHeight + _board.Margin.Top + _board.Margin.Bottom;
	}

	/// <summary>The page's side margin at a width: 40 at 1100, 96 at 1920 (the concept's), as the lot's.</summary>
	public static double MarginFor(double width) => DriveInLayout.MarginFor(width);

	private void ApplyWidth(double width)
	{
		if (width <= 0)
		{
			return;
		}

		var margin = MarginFor(width);
		var compact = width < 1400;
		_board.Compact = compact;
		// the board takes about three fifths of a narrow window, so the art still shows beside it
		_board.Width = Math.Min(compact ? Math.Max(560, Math.Round(width * 0.6)) : 820, width - 2 * margin);
		_board.Margin = new Thickness(margin, compact ? 20 : 40, margin, 28);
		_creditsPlate.Margin = new Thickness(0, 0, margin, 36);
		_creditsPlate.Visibility = _credits.Inlines.Count == 0 ? Visibility.Collapsed : width - _board.Width - 2 * margin > 320 ? Visibility.Visible : Visibility.Collapsed;
		_art.Width = Math.Max(0, width * (compact ? 1 : 0.64));
		if (_backdrop.Source is null && Dto is { } dto)
		{
			_backdrop.Source = ItemArtworkConverters.Backdrop(dto, 1920);
		}
	}

	private void Apply()
	{
		var dto = Dto;
		_board.Dto = dto;
		_backdrop.Source = ItemArtworkConverters.Backdrop(dto, 1920);
		_credits.Inlines.Clear();
		void Line(string label, string names)
		{
			if (names.Length == 0)
			{
				return;
			}

			if (_credits.Inlines.Count > 0)
			{
				_credits.Inlines.Add(new LineBreak());
			}

			_credits.Inlines.Add(new Run { Text = label + "  ", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
			_credits.Inlines.Add(new Run { Text = names });
		}

		Line("Starring", DriveInItems.Starring(dto));
		var (made, by) = DriveInItems.MadeBy(dto);
		Line(made, by);
		ApplyWidth(ActualWidth);
	}

	/// <summary>The scrims fade the art into the page's ground (its colour in the theme), so the board's edge and the page meet.</summary>
	private void ApplyScrims()
	{
		var ground = BulbFrame.HighContrast() ? Color.FromArgb(255, 0, 0, 0) : DriveInScene.Ground(ActualTheme == ElementTheme.Light);
		Color A(double alpha) => Color.FromArgb((byte)Math.Round(alpha * 255), ground.R, ground.G, ground.B);
		var side = new LinearGradientBrush { StartPoint = new(0, 0.5), EndPoint = new(1, 0.5) };
		side.GradientStops.Add(new GradientStop { Offset = 0, Color = A(1) });
		side.GradientStops.Add(new GradientStop { Offset = 0.22, Color = A(0.7) });
		side.GradientStops.Add(new GradientStop { Offset = 0.5, Color = A(0.18) });
		side.GradientStops.Add(new GradientStop { Offset = 1, Color = A(0.05) });
		_sideScrim.Background = side;
		var bottom = new LinearGradientBrush { StartPoint = new(0.5, 0), EndPoint = new(0.5, 1) };
		bottom.GradientStops.Add(new GradientStop { Offset = 0, Color = A(0) });
		bottom.GradientStops.Add(new GradientStop { Offset = 1, Color = A(1) });
		_bottomScrim.Background = bottom;
		_credits.Foreground = BulbFrame.HighContrast() ? (Brush)Application.Current.Resources["DriveInPageTextBrush"] : new SolidColorBrush(Color.FromArgb(255, 0xF8, 0xEF, 0xF0));
		_creditsPlate.Background = BulbFrame.HighContrast() ? null : new SolidColorBrush(Color.FromArgb(0xD9, 0x10, 0x04, 0x09));
		_art.Visibility = BulbFrame.HighContrast() ? Visibility.Collapsed : Visibility.Visible;
	}
}
