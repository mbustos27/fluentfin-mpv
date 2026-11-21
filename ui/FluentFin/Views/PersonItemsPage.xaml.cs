using FluentFin.Controls;
using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Views;

/// <summary>Nixie 1.5: "With Ava Abara" (<see cref="PersonItemsViewModel"/>), what a person in the search results opens.</summary>
public sealed partial class PersonItemsPage : Page
{
	public PersonItemsViewModel ViewModel { get; } = App.GetService<PersonItemsViewModel>();

	private readonly CardGrid _grid;

	public PersonItemsPage()
	{
		InitializeComponent();

		// the Library's grid model (one Tab stop, arrows both ways, Home/End, the last card remembered, "n of m")
		_grid = new CardGrid(PersonScroller, PersonList, PersonRepeater);
		_grid.ForwardWheel(PersonRoot);
	}
}
