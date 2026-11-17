using FluentFin.Controls;
using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Views;

/// <summary>Seaglass scrolling: the grid behind a row's "See all" (<see cref="ItemGridViewModel"/>), with the Library's grid model.</summary>
public sealed partial class ItemGridPage : Page
{
	public ItemGridViewModel ViewModel { get; } = App.GetService<ItemGridViewModel>();

	private readonly CardGrid _grid;

	public ItemGridPage()
	{
		InitializeComponent();

		_grid = new CardGrid(GridScroller, GridList, GridRepeater);
		_grid.ForwardWheel(GridRoot);
	}
}
