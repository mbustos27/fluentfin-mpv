using FluentFin.Controls;
using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml.Controls;


namespace FluentFin.Views;


public sealed partial class LibraryPage : Page
{
	public LibraryViewModel ViewModel { get; } = App.GetService<LibraryViewModel>();

	// Seaglass scrolling: the grid's keyboard and Narrator model, and the wheel over the rest of the page
	private readonly CardGrid _grid;

	public LibraryPage()
	{
		InitializeComponent();

		_grid = new CardGrid(GridScroller, GridList, GridRepeater);
		_grid.ForwardWheel(LibraryRoot);
		_grid.SetPlace(ViewModel.FirstIndex, ViewModel.TotalCount);
		ViewModel.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName is nameof(LibraryViewModel.FirstIndex) or nameof(LibraryViewModel.TotalCount))
			{
				_grid.SetPlace(ViewModel.FirstIndex, ViewModel.TotalCount);
			}
		};
	}
}
