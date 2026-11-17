using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Views;

/// <summary>Seaglass 1.1.4: the search results page (<see cref="SearchResultsViewModel"/>), opened by Enter in the title bar's search.</summary>
public sealed partial class SearchResultsPage : Page
{
	public SearchResultsViewModel ViewModel { get; } = App.GetService<SearchResultsViewModel>();

	public SearchResultsPage()
	{
		InitializeComponent();
	}
}
