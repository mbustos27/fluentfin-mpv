using FluentFin.Controls;
using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace FluentFin.Themes.Editorial.Controls;

/// <summary>Editorial's shelf with details in place (see EditorialShelf.xaml).</summary>
public sealed partial class EditorialShelf : UserControl, IEditorialDetailsHost
{
	public static readonly DependencyProperty TitleProperty =
		DependencyProperty.Register(nameof(Title), typeof(string), typeof(EditorialShelf), new PropertyMetadata("", (d, e) => ((EditorialShelf)d).OnTitleChanged()));

	public static readonly DependencyProperty CaptionProperty =
		DependencyProperty.Register(nameof(Caption), typeof(string), typeof(EditorialShelf), new PropertyMetadata("", (d, e) => ((EditorialShelf)d).CaptionText.Text = e.NewValue as string ?? ""));

	public static readonly DependencyProperty ItemsSourceProperty =
		DependencyProperty.Register(nameof(ItemsSource), typeof(object), typeof(EditorialShelf), new PropertyMetadata(null, (d, e) => ((EditorialShelf)d).OnItemsChanged()));

	public static readonly DependencyProperty IsNarrowProperty =
		DependencyProperty.Register(nameof(IsNarrow), typeof(bool), typeof(EditorialShelf), new PropertyMetadata(false, (d, e) => ((EditorialShelf)d).UpdateTemplate()));

	public static readonly DependencyProperty HasSeeAllProperty =
		DependencyProperty.Register(nameof(HasSeeAll), typeof(bool), typeof(EditorialShelf), new PropertyMetadata(false, (d, e) => ((EditorialShelf)d).UpdateSeeAll()));

	private const double RingRoom = 10;

	// one shelf's details open at a time on a page: opening another closes them
	private static WeakReference<EditorialShelf>? s_open;

	private ScrollViewer? _scroller;
	private BaseItemViewModel? _openItem;
	private WeakReference<EditorialPoster>? _openPoster;

	public EditorialShelf()
	{
		InitializeComponent();
		UpdateTemplate();
		Row.Loaded += (_, _) => AdoptRow();
		Row.AddHandler(PreviewKeyDownEvent, new KeyEventHandler(Row_PreviewKeyDown), true);
		Details.CloseRequested += (_, _) => CloseDetails(returnFocus: true);
		Details.UpRequested += (_, _) => FocusOpenPoster();
		Unloaded += (_, _) =>
		{
			if (s_open?.TryGetTarget(out var open) == true && ReferenceEquals(open, this))
			{
				s_open = null;
			}
		};
	}

	/// <summary>The shelf's name: its heading, and the row's list name for Narrator.</summary>
	public string Title
	{
		get => (string)GetValue(TitleProperty);
		set => SetValue(TitleProperty, value);
	}

	public string Caption
	{
		get => (string)GetValue(CaptionProperty);
		set => SetValue(CaptionProperty, value);
	}

	public object? ItemsSource
	{
		get => GetValue(ItemsSourceProperty);
		set => SetValue(ItemsSourceProperty, value);
	}

	/// <summary>The narrow window's posters (122 px; 144 otherwise).</summary>
	public bool IsNarrow
	{
		get => (bool)GetValue(IsNarrowProperty);
		set => SetValue(IsNarrowProperty, value);
	}

	public bool HasSeeAll
	{
		get => (bool)GetValue(HasSeeAllProperty);
		set => SetValue(HasSeeAllProperty, value);
	}

	public event EventHandler? SeeAll;

	/// <summary>The shared row inside (its keyboard model; Home's Back restore uses it).</summary>
	internal ShelfRow ShelfRow => Row;

	private void OnTitleChanged()
	{
		TitleText.Text = Title;
		Row.Header = Title;
		Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SeeAllButton, $"See all {Title}");
	}

	private void UpdateSeeAll() => SeeAllButton.Visibility = HasSeeAll ? Visibility.Visible : Visibility.Collapsed;

	private void UpdateTemplate() => Row.ItemTemplate = (DataTemplate)Resources[IsNarrow ? "NarrowPoster" : "WidePoster"];

	private void OnItemsChanged()
	{
		CloseDetails(returnFocus: false);
		Row.ItemsSource = ItemsSource;
	}

	/// <summary>
	/// The shared row's own header gives way to this one. Nixie 1.7.4: the row's arrows are the shared row's, at its ends (this
	/// header's pair went: one behaviour in every theme).
	/// </summary>
	private void AdoptRow()
	{
		if (_scroller is not null || Row.Content is not Grid root || root.Children.Count < 2)
		{
			return;
		}

		root.RowSpacing = 0;
		root.Children[0].Visibility = Visibility.Collapsed;
		_scroller = Row.RowScroller;
		// room inside the row's clip for a poster's focus ring and glow; the row moves out by as much, so the posters stay aligned
		Row.Margin = new Thickness(-RingRoom, -RingRoom, -RingRoom, 0);
		_scroller.Padding = new Thickness(RingRoom, RingRoom, RingRoom, 0);
	}

	private void SeeAllButton_Click(object sender, RoutedEventArgs e) => SeeAll?.Invoke(this, EventArgs.Empty);

	// ---- details in place ----------------------------------------------------------------------------------------------

	public bool IsOpen(BaseItemViewModel item) => _openItem is { } open && ReferenceEquals(open, item);

	public void ToggleDetails(BaseItemViewModel item, EditorialPoster poster)
	{
		if (IsOpen(item))
		{
			CloseDetails(returnFocus: poster.FocusState is FocusState.Keyboard);
			return;
		}

		if (s_open?.TryGetTarget(out var other) == true && !ReferenceEquals(other, this))
		{
			other.CloseDetails(returnFocus: false);
		}

		if (_openPoster?.TryGetTarget(out var previous) == true)
		{
			previous.IsOpen = false;
		}

		var byKeyboard = poster.FocusState is FocusState.Keyboard;
		_openItem = item;
		_openPoster = new WeakReference<EditorialPoster>(poster);
		poster.IsOpen = true;
		s_open = new WeakReference<EditorialShelf>(this);
		Details.Item = item;
		Details.Visibility = Visibility.Visible;
		Details.UpdateLayout();
		Details.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = SafeMotion.Allowed, VerticalAlignmentRatio = 0.85 });
		if (byKeyboard)
		{
			Details.FocusPlay();
		}
	}

	/// <summary>Takes the details away; <paramref name="returnFocus"/>: the keyboard back to the poster they belong to.</summary>
	public void CloseDetails(bool returnFocus)
	{
		if (_openItem is not { } item)
		{
			return;
		}

		_openItem = null;
		if (_openPoster?.TryGetTarget(out var poster) == true)
		{
			poster.IsOpen = false;
		}

		_openPoster = null;
		Details.Visibility = Visibility.Collapsed;
		Details.Item = null;
		if (returnFocus)
		{
			Row.FocusItem(x => ReferenceEquals(x, item), FocusState.Keyboard);
		}
	}

	private void FocusOpenPoster()
	{
		if (_openItem is { } item)
		{
			Row.FocusItem(x => ReferenceEquals(x, item), FocusState.Keyboard);
		}
	}

	/// <summary>Down from a poster of this row while its details are open: into them (the shared row would skip to the next row).</summary>
	private void Row_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Handled || _openItem is null || e.Key is not (VirtualKey.Down or VirtualKey.GamepadDPadDown) || e.OriginalSource is not EditorialPoster)
		{
			return;
		}

		e.Handled = Details.FocusPlay();
	}

	/// <summary>The page's shelves (Home's Back restore and the keys N and C look for rows on the page).</summary>
	internal static IEnumerable<EditorialShelf> Within(DependencyObject root)
	{
		for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
		{
			var child = VisualTreeHelper.GetChild(root, i);
			if (child is EditorialShelf shelf)
			{
				yield return shelf;
			}
			else
			{
				foreach (var inner in Within(child))
				{
					yield return inner;
				}
			}
		}
	}
}
