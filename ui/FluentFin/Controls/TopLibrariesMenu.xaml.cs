using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace FluentFin.Controls;

// 1.6 topbar

/// <summary>
/// Nixie 1.6: "Libraries ▾"'s menu (TopLibrariesMenu.xaml): All libraries and every media library, the current one checked,
/// each with its star. Choosing an entry opens it (<see cref="Chosen"/>); a star adds the library to the top bar or removes it
/// (TopNavigation.ToggleFavourite), and the menu stays open. The bar's row follows once it closes (TopTabStrip).
/// </summary>
public sealed partial class TopLibrariesMenu : UserControl
{
	private readonly TopNavigation _navigation;
	private readonly TopTab _all;
	private readonly List<TopLibraryRow> _rows;

	public TopLibrariesMenu(TopNavigation navigation, TopTab libraries)
	{
		InitializeComponent();
		_navigation = navigation;
		_all = libraries.Menu.Count > 0 ? libraries.Menu[0] : libraries;
		var lit = navigation.Lit;
		AllEntry.IsCurrent = lit is { } m && _all.Mark == m;
		AllCheck.Visibility = AllEntry.IsCurrent ? Visibility.Visible : Visibility.Collapsed;
		_rows = [.. libraries.Menu.Skip(1).Select(t => new TopLibraryRow(this, t, lit is { } l && t.Mark == l, navigation.IsFavourite(t.LibraryId)))];
		Rows.ItemsSource = _rows;
		Separator.Visibility = _rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
		MenuPanel.KeyDown += OnPanelKeyDown;
	}

	/// <summary>
	/// The menu's keys (TopMenuKeys): Up and Down a row (round, as a menu), Home and End the first and last, Left and Right
	/// between a library and its star. The menu moves the keyboard itself: Windows' own arrow navigation skipped a row.
	/// </summary>
	private void OnPanelKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
	{
		if (XamlRoot is null || Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) is not DependencyObject focused || Where(focused) is not { } at)
		{
			return;
		}

		if (TopMenuKeys.Next(e.Key, at.Row, at.Column, _rows.Count + 1) is { } next && Element(next.Row, next.Column) is Control target)
		{
			target.StartBringIntoView();
			target.Focus(FocusState.Keyboard);
			e.Handled = true;
		}
	}

	/// <summary>The row (0: All libraries) and column (0: the library, 1: its star) of an element of the menu.</summary>
	private (int Row, int Column)? Where(DependencyObject element)
	{
		if (ReferenceEquals(element, AllEntry))
		{
			return (0, 0);
		}

		var column = element is TopMenuEntry ? 0 : 1;
		for (var e = element; e is not null; e = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(e))
		{
			if (e is UIElement row && Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(e) is ItemsRepeater repeater && ReferenceEquals(repeater, Rows))
			{
				var index = Rows.GetElementIndex(row);
				return index >= 0 ? (index + 1, column) : null;
			}
		}

		return null;
	}

	/// <summary>The control at a row and column (the row realized first: a long list is virtualized).</summary>
	private UIElement? Element(int row, int column)
	{
		if (row == 0)
		{
			return AllEntry;
		}

		if (Rows.GetOrCreateElement(row - 1) is not Microsoft.UI.Xaml.Controls.Panel grid)
		{
			return null;
		}

		grid.UpdateLayout();
		return grid.Children.Count > column ? grid.Children[column] : null;
	}

	/// <summary>An entry was chosen: the menu closes and its page opens.</summary>
	public event EventHandler<TopTab>? Chosen;

	/// <summary>The rows (tests, the harness).</summary>
	internal IReadOnlyList<TopLibraryRow> Entries => _rows;

	/// <summary>The menu's first stop: All libraries (keyboard focus when it was opened with the keyboard: its box shows).</summary>
	public void FocusFirst(FocusState how) => AllEntry.Focus(how);

	private void OnAllClick(object sender, RoutedEventArgs e) => Chosen?.Invoke(this, _all);

	internal void Open(TopLibraryRow row) => Chosen?.Invoke(this, row.Tab);

	internal void ToggleStar(TopLibraryRow row)
	{
		if (row.Tab.LibraryId is not { } id)
		{
			return;
		}

		var change = _navigation.ToggleFavourite(id);
		foreach (var r in _rows)
		{
			r.IsFavourite = _navigation.IsFavourite(r.Tab.LibraryId);
		}

		var said = change switch
		{
			FavouriteChange.Added => $"{row.Title} added to the top bar",
			FavouriteChange.Removed => $"{row.Title} removed from the top bar",
			FavouriteChange.Full => TopFavourites.FullMessage(row.Title),
			_ => null,
		};
		FullNote.Text = change == FavouriteChange.Full ? said : "";
		FullNote.Visibility = change == FavouriteChange.Full ? Visibility.Visible : Visibility.Collapsed;
		if (said is not null)
		{
			TopStarAnnouncer.Say(FullNote, said, change == FavouriteChange.Full);
		}
	}

	/// <summary>
	/// Nixie 1.6: the menu in a flyout under <paramref name="placement"/>, dressed as the bar's other menus (the theme's
	/// <c>TopBarFlyoutPresenterStyle</c> when it has one, else the shared glass), 4 px in as a menu is.
	/// </summary>
	public static Flyout Wrap(TopLibrariesMenu menu)
	{
		var presenter = new Style(typeof(FlyoutPresenter));
		if ((Services.ThemeResources.Find("TopBarFlyoutPresenterStyle") ?? Application.Current.Resources["GlassFlyoutPresenterStyle"]) is Style based)
		{
			presenter.BasedOn = based;
		}

		presenter.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4)));
		presenter.Setters.Add(new Setter(AutomationProperties.NameProperty, "Libraries"));
		return new Flyout
		{
			Content = menu,
			Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
			FlyoutPresenterStyle = presenter,
			LightDismissOverlayMode = LightDismissOverlayMode.Off,
		};
	}
}

/// <summary>One library of "Libraries ▾": its name, whether it is the current one, and its star.</summary>
public sealed partial class TopLibraryRow(TopLibrariesMenu menu, TopTab tab, bool current, bool favourite) : ObservableObject
{
	public TopTab Tab { get; } = tab;

	public string Title => Tab.Title;

	public bool IsCurrent { get; } = current;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsNotFavourite), nameof(StarName))]
	public partial bool IsFavourite { get; set; } = favourite;

	public bool IsNotFavourite => !IsFavourite;

	/// <summary>"Add Movies to the top bar", "Remove Movies from the top bar".</summary>
	public string StarName => TopFavourites.StarName(Title, IsFavourite);

	public void Open() => menu.Open(this);

	public void ToggleStar() => menu.ToggleStar(this);
}

/// <summary>Nixie 1.6: what a star did, said to Narrator at once (the star keeps the keyboard).</summary>
internal static class TopStarAnnouncer
{
	public static void Say(UIElement near, string text, bool important)
	{
		var peer = FrameworkElementAutomationPeer.FromElement(near) ?? FrameworkElementAutomationPeer.CreatePeerForElement(near);
		peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted,
			important ? AutomationNotificationProcessing.ImportantMostRecent : AutomationNotificationProcessing.MostRecent, text, "TopBarStar");
	}
}

/// <summary>
/// Nixie 1.6: an entry of the bar's own menus: the account menu's look (MenuFlyoutButton), a menu item to Narrator (selected
/// when it is the current destination), invoked as a button.
/// </summary>
public sealed partial class TopMenuEntry : MenuFlyoutButton
{
	public bool IsCurrent { get; set; }

	protected override AutomationPeer OnCreateAutomationPeer() => new TopMenuEntryAutomationPeer(this);
}

internal sealed partial class TopMenuEntryAutomationPeer(TopMenuEntry owner) : ButtonAutomationPeer(owner), ISelectionItemProvider
{
	protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.MenuItem;

	protected override string GetClassNameCore() => nameof(TopMenuEntry);

	protected override object GetPatternCore(PatternInterface patternInterface) =>
		patternInterface == PatternInterface.SelectionItem ? this : base.GetPatternCore(patternInterface);

	public bool IsSelected => owner.IsCurrent;

	public IRawElementProviderSimple? SelectionContainer => null;

	public void AddToSelection() => Invoke();

	public void RemoveFromSelection()
	{
	}

	public void Select() => Invoke();
}

/// <summary>
/// Nixie 1.6: "Libraries ▾"'s keys: Up and Down a row, round (row 0 is All libraries); Home and End the first and last row;
/// Left and Right between a library (column 0) and its star (column 1). All libraries has no star; a row reached from a star
/// keeps the star's column. Null for any other key.
/// </summary>
public static class TopMenuKeys
{
	public static (int Row, int Column)? Next(Windows.System.VirtualKey key, int row, int column, int rows)
	{
		if (rows <= 0)
		{
			return null;
		}

		(int, int) At(int r, int c) => (r, r == 0 ? 0 : c);
		return key switch
		{
			Windows.System.VirtualKey.Down => At((row + 1) % rows, column),
			Windows.System.VirtualKey.Up => At((row - 1 + rows) % rows, column),
			Windows.System.VirtualKey.Home => At(0, 0),
			Windows.System.VirtualKey.End => At(rows - 1, column),
			Windows.System.VirtualKey.Right when row > 0 && column == 0 => (row, 1),
			Windows.System.VirtualKey.Left when column == 1 => (row, 0),
			Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right => (row, column),
			_ => null,
		};
	}
}

// end 1.6 topbar
