using CommunityToolkit.WinUI;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Controls;

/// <summary>
/// Nixie 1.4.0: the library's toolbar, the same parts in every theme (see LibraryToolbar.xaml): Unwatched in one click, Sort
/// with its direction in words, the direction in one click, Filter, and View.
/// </summary>
public sealed partial class LibraryToolbar : UserControl
{
	[GeneratedDependencyProperty]
	public partial LibraryViewModel? ViewModel { get; set; }

	/// <summary>The theme's style for Sort and Filter (its compact Secondary role).</summary>
	[GeneratedDependencyProperty]
	public partial Style? ButtonStyle { get; set; }

	/// <summary>The theme's style for the Unwatched and Favorites switches.</summary>
	[GeneratedDependencyProperty]
	public partial Style? ToggleStyle { get; set; }

	/// <summary>The theme's style for the direction and View icon buttons.</summary>
	[GeneratedDependencyProperty]
	public partial Style? IconButtonStyle { get; set; }

	/// <summary>Editorial: Favorites is a switch beside Unwatched too.</summary>
	[GeneratedDependencyProperty]
	public partial bool ShowFavorites { get; set; }

	/// <summary>Cinematic: View also offers stills or posters.</summary>
	[GeneratedDependencyProperty]
	public partial bool ShowArtChoice { get; set; }

	/// <summary>View offers the list (the themes whose grid has a list form).</summary>
	[GeneratedDependencyProperty(DefaultValue = true)]
	public partial bool ShowListChoice { get; set; }

	public LibraryToolbar()
	{
		InitializeComponent();
		ApplyStyles();
	}

	partial void OnViewModelChanged(LibraryViewModel? newValue) => Bindings.Update();

	partial void OnButtonStyleChanged(Style? newValue) => ApplyStyles();

	partial void OnToggleStyleChanged(Style? newValue) => ApplyStyles();

	partial void OnIconButtonStyleChanged(Style? newValue) => ApplyStyles();

	partial void OnShowFavoritesChanged(bool newValue) => FavoritesToggle.Visibility = newValue ? Visibility.Visible : Visibility.Collapsed;

	private void ApplyStyles()
	{
		// 1.4.0 style keys: the toolbar role (Compact 32 in every theme), the chip switch (a check and a neutral plate when on)
		var button = ButtonStyle ?? (Style)Application.Current.Resources["NixieToolbarButtonStyle"];
		SortButton.Style = button;
		FilterButton.Style = button;
		if (ToggleStyle is { } toggle)
		{
			UnwatchedToggle.Style = toggle;
			FavoritesToggle.Style = toggle;
		}

		if (IconButtonStyle is { } icon)
		{
			DirectionButton.Style = icon;
			ViewButton.Style = icon;
		}
	}

	/// <summary>The direction button's tooltip: "Switch to newest first".</summary>
	public static string SwitchTo(string? words) => string.IsNullOrEmpty(words) ? "Switch the direction" : $"Switch to {words}";

	/// <summary>The View button's glyph: the grid or the list in use.</summary>
	public static string ViewGlyph(LibraryLayout layout) => layout == LibraryLayout.List ? "" : "";

	/// <summary>The View button for Narrator: "View: medium posters, grid".</summary>
	public static string ViewName(LibraryDensity density, LibraryLayout layout, LibraryArt art, bool artChoice)
	{
		var what = artChoice && art == LibraryArt.Stills ? "stills" : "posters";
		return layout == LibraryLayout.List ? "View: list" : $"View: {DensityWords(density)} {what}, grid";
	}

	public static string DensityWords(LibraryDensity density) => density switch
	{
		LibraryDensity.Small => "small",
		LibraryDensity.Large => "large",
		_ => "medium",
	};

	private void DirectionButton_Click(object sender, RoutedEventArgs e) => ViewModel?.ToggleSortOrder();

	/// <summary>Opens the Filter panel (Editorial's "More filters" at the end of its genre chips), the keyboard in it.</summary>
	public void OpenFilter() => FilterFlyout.ShowAt(FilterButton);

	/// <summary>The keys (the one in use checked; picked again it turns the direction), then both directions in the key's words.</summary>
	private void SortMenu_Opening(object? sender, object e)
	{
		SortMenu.Items.Clear();
		if (ViewModel is not { } vm)
		{
			return;
		}

		foreach (var key in LibraryBrowse.SortKeys)
		{
			var item = new RadioMenuFlyoutItem { Text = LibraryBrowse.SortName(key), GroupName = "LibrarySortKey", IsChecked = !vm.InPlaylistOrder && vm.SortBy == key };
			item.Click += (_, _) => vm.UpdateSortBy(key);
			SortMenu.Items.Add(item);
		}

		SortMenu.Items.Add(new MenuFlyoutSeparator());
		foreach (var order in new[] { SortOrder.Ascending, SortOrder.Descending })
		{
			var words = LibraryBrowse.DirectionWords(vm.SortBy, order);
			var item = new RadioMenuFlyoutItem
			{
				Text = char.ToUpperInvariant(words[0]) + words[1..],
				GroupName = "LibrarySortOrder",
				IsChecked = !vm.InPlaylistOrder && vm.Order == order,
			};
			item.Click += (_, _) => vm.UpdateSortOrder(order);
			SortMenu.Items.Add(item);
		}
	}

	private void ViewMenu_Opening(object? sender, object e)
	{
		ViewMenu.Items.Clear();
		if (ViewModel is not { } vm)
		{
			return;
		}

		foreach (var density in new[] { LibraryDensity.Small, LibraryDensity.Medium, LibraryDensity.Large })
		{
			var words = DensityWords(density);
			var item = new RadioMenuFlyoutItem
			{
				Text = $"{char.ToUpperInvariant(words[0])}{words[1..]} posters",
				GroupName = "LibraryDensity",
				IsChecked = vm.Layout == LibraryLayout.Grid && vm.Density == density,
			};
			item.Click += (_, _) =>
			{
				vm.SetDensity(density);
				vm.SetLayout(LibraryLayout.Grid);
			};
			ViewMenu.Items.Add(item);
		}

		if (ShowListChoice)
		{
			var list = new RadioMenuFlyoutItem { Text = "List", GroupName = "LibraryDensity", IsChecked = vm.Layout == LibraryLayout.List };
			list.Click += (_, _) => vm.SetLayout(LibraryLayout.List);
			ViewMenu.Items.Add(list);
		}

		if (ShowArtChoice)
		{
			ViewMenu.Items.Add(new MenuFlyoutSeparator());
			foreach (var art in new[] { LibraryArt.Stills, LibraryArt.Posters })
			{
				var item = new RadioMenuFlyoutItem { Text = art == LibraryArt.Stills ? "Stills" : "Posters", GroupName = "LibraryArt", IsChecked = vm.Art == art };
				item.Click += (_, _) => vm.SetArt(art);
				ViewMenu.Items.Add(item);
			}
		}
	}
}
