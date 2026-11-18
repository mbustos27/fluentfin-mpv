using System.Globalization;
using System.Numerics;
using FluentFin.Converters;
using FluentFin.Core.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FluentFin.Themes.DriveIn.Controls;

/// <summary>Drive-in: the motel sign (see ComingUpSign.xaml): NOW SHOWING, and the Coming up board of Next Up.</summary>
public sealed partial class ComingUpSign : UserControl
{
	public static readonly DependencyProperty ItemsProperty =
		DependencyProperty.Register(nameof(Items), typeof(IReadOnlyList<BaseItemViewModel>), typeof(ComingUpSign), new PropertyMetadata(null, (d, _) => ((ComingUpSign)d).BuildEntries()));

	public static readonly DependencyProperty UpNextCountProperty =
		DependencyProperty.Register(nameof(UpNextCount), typeof(int), typeof(ComingUpSign), new PropertyMetadata(0, (d, _) => ((ComingUpSign)d).UpdateLink()));

	/// <summary>Next Up, in the server's order (the sign shows the first three or four).</summary>
	public IReadOnlyList<BaseItemViewModel>? Items { get => (IReadOnlyList<BaseItemViewModel>?)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }

	/// <summary>How many items "All of Up next" opens.</summary>
	public int UpNextCount { get => (int)GetValue(UpNextCountProperty); set => SetValue(UpNextCountProperty, value); }

	/// <summary>"All of Up next" was clicked.</summary>
	public event EventHandler? AllRequested;

	/// <summary>The arrow was clicked: the screen shows its next feature.</summary>
	public event EventHandler? NextFeatureRequested;

	private DriveInLayout? _layout;
	private bool _chased;
	private SpriteVisual? _glow;

	public ComingUpSign()
	{
		InitializeComponent();
		AutomationProperties.SetName(Arrow, "Now showing: next feature");
		Loaded += (_, _) =>
		{
			ApplyLayout(_layout);
			if (!_chased)
			{
				_chased = true;
				DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Board.Bulbs?.Chase());
			}
		};
		ArrowText.SizeChanged += (_, _) => UpdateGlow();
		Unloaded += (_, _) => { _glow?.Dispose(); _glow = null; };
	}

	/// <summary>The keyboard on the first entry (Home's "N", Next Up).</summary>
	public bool FocusFirst() => Entries.Children.OfType<Button>().FirstOrDefault() is { } first && first.Focus(FocusState.Keyboard);

	/// <summary>Places the arrow and the board from the lot's layout (the control sits at the arrow's top left).</summary>
	public void ApplyLayout(DriveInLayout? layout)
	{
		_layout = layout;
		if (layout is null || !layout.HasSign)
		{
			Visibility = Visibility.Collapsed;
			return;
		}

		Visibility = Visibility.Visible;
		var arrow = layout.Arrow;
		var board = layout.Board;
		Width = Math.Max(arrow.Right, board.Right) - Math.Min(arrow.X, board.X);
		Height = board.Bottom - arrow.Y;
		Root.Width = Width;
		Root.Height = Height;
		Arrow.Width = arrow.Width;
		Arrow.Height = arrow.Height;
		Canvas.SetLeft(Board, board.X - arrow.X);
		Canvas.SetTop(Board, board.Y - arrow.Y);
		Board.Width = board.Width;
		Board.Height = board.Height;
		Board.Padding = layout.Wide ? new Thickness(20, 34, 20, 30) : new Thickness(16, 30, 16, 24);
		Heading.FontSize = layout.Wide ? 26 : 20;
		Heading.Margin = new Thickness(0, 0, 0, layout.Wide ? 12 : 8);
		ArrowText.FontSize = layout.Wide ? 42 : 30;
		ArrowWords.Margin = new Thickness(arrow.Height / 2, 0, 0, 0);

		var tube = Tube(arrow.Width, arrow.Height);
		ArrowFace.Data = Geo(tube);
		ArrowHalo.Data = Geo(tube);
		ArrowGlow.Data = Geo(tube);
		ArrowCore.Data = Geo(tube);
		BuildEntries();
	}

	/// <summary>The arrow's outline: a sign pointing left at the screen (the concept's tube path).</summary>
	public static string Tube(double w, double h) => string.Create(CultureInfo.InvariantCulture,
		$"M{h / 2:0.#},2 L{w - 6:0.#},2 Q{w - 2:0.#},2 {w - 2:0.#},6 L{w - 2:0.#},{h - 6:0.#} Q{w - 2:0.#},{h - 2:0.#} {w - 6:0.#},{h - 2:0.#} L{h / 2:0.#},{h - 2:0.#} L4,{h / 2:0.#} Z");

	private static Geometry Geo(string data) => (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data);

	private void UpdateLink()
	{
		AllLink.Content = DriveInHome.AllUpNextText(UpNextCount);
		AllLink.Visibility = UpNextCount > 0 ? Visibility.Visible : Visibility.Collapsed;
	}

	private void BuildEntries()
	{
		Entries.Children.Clear();
		var wide = _layout?.Wide ?? true;
		var items = (Items ?? []).Take(DriveInHome.SignEntries(wide)).ToList();
		for (var i = 0; i < items.Count; i++)
		{
			var entry = Entry(items[i].Dto, wide);
			AutomationProperties.SetPositionInSet(entry, i + 1);
			AutomationProperties.SetSizeOfSet(entry, items.Count);
			Entries.Children.Add(entry);
		}

		UpdateLink();
	}

	private static Button Entry(Jellyfin.Sdk.Generated.Models.BaseItemDto dto, bool wide)
	{
		var grid = new Grid { ColumnSpacing = 14 };
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

		if (wide)
		{
			var thumb = new Grid { Width = 104, Height = 58, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromArgb(255, 0x2A, 0x10, 0x18)) };
			thumb.Children.Add(new Image { Source = ItemArtworkConverters.Thumb(dto, 208), Stretch = Stretch.UniformToFill });
			thumb.Children.Add(new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), BorderBrush = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)) });
			AutomationProperties.SetAccessibilityView(thumb, AccessibilityView.Raw);
			grid.Children.Add(thumb);
		}

		var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 5 };
		Grid.SetColumn(words, 1);
		words.Children.Add(new TextBlock
		{
			Text = DriveInHome.EntryTitle(dto),
			FontSize = wide ? 17 : 15,
			FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
			TextTrimming = TextTrimming.CharacterEllipsis,
			TextWrapping = TextWrapping.NoWrap,
		});
		var second = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
		var code = DriveInText.NixieCode(dto);
		if (code.Length > 0)
		{
			second.Children.Add(new NixieReadout { Text = code, DigitHeight = 22, Spoken = Core.ItemLabels.EpisodeCode(dto) });
		}

		second.Children.Add(new TextBlock
		{
			Text = DriveInHome.EntryDetail(dto),
			FontSize = 14,
			VerticalAlignment = VerticalAlignment.Center,
			Foreground = (Brush)Application.Current.Resources["DriveInBoardTextSecondaryBrush"],
			TextTrimming = TextTrimming.CharacterEllipsis,
			TextWrapping = TextWrapping.NoWrap,
		});
		words.Children.Add(second);
		grid.Children.Add(words);

		if (wide)
		{
			// what a click does, drawn (the whole entry is the button): the button system's 32 px square
			var play = new Border
			{
				Width = 32,
				Height = 32,
				CornerRadius = new CornerRadius(8),
				BorderThickness = new Thickness(1),
				BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
				Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
				VerticalAlignment = VerticalAlignment.Center,
				Child = new FontIcon { Glyph = "", FontSize = 14 },
			};
			AutomationProperties.SetAccessibilityView(play, AccessibilityView.Raw);
			Grid.SetColumn(play, 2);
			grid.Children.Add(play);
		}

		var button = new Button
		{
			Content = grid,
			Style = (Style)Application.Current.Resources["DriveInEntryButtonStyle"],
			Padding = wide ? new Thickness(12, 9, 12, 9) : new Thickness(10, 7, 10, 7),
			Tag = dto,
		};
		var name = DriveInHome.ComingUpName(dto);
		AutomationProperties.SetName(button, name);
		ToolTipService.SetToolTip(button, name);
		button.ContextFlyout = new JellyfinFlyoutConverter { IncludePlay = true }.Convert(dto, typeof(object), null!, "") as FlyoutBase;
		button.Click += (_, _) => _ = App.Commands.PlayDto(dto);
		return button;
	}

	private void AllLink_Click(object sender, RoutedEventArgs e) => AllRequested?.Invoke(this, EventArgs.Empty);

	private void Arrow_Tapped(object sender, TappedRoutedEventArgs e) => NextFeatureRequested?.Invoke(this, EventArgs.Empty);

	/// <summary>The NOW SHOWING letters' one glow: a drop shadow of their own shape in coral (none in high contrast).</summary>
	private void UpdateGlow()
	{
		if (BulbFrame.HighContrast() || ArrowText.ActualWidth <= 0)
		{
			ElementCompositionPreview.SetElementChildVisual(ArrowGlowHost, null);
			return;
		}

		var compositor = ElementCompositionPreview.GetElementVisual(ArrowGlowHost).Compositor;
		var shadow = compositor.CreateDropShadow();
		shadow.Mask = ArrowText.GetAlphaMask();
		shadow.Color = Color.FromArgb(255, 0xEC, 0x6A, 0x79);
		shadow.BlurRadius = (float)(ArrowText.FontSize * 0.42);
		shadow.Opacity = 0.95f;
		shadow.Offset = Vector3.Zero;
		_glow?.Dispose();
		_glow = compositor.CreateSpriteVisual();
		_glow.Shadow = shadow;
		// the host is the letters' own box: the glow covers it, whatever moves the letters
		_glow.RelativeSizeAdjustment = Vector2.One;
		ElementCompositionPreview.SetElementChildVisual(ArrowGlowHost, _glow);
	}
}
