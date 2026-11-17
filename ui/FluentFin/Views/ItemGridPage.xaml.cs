using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Views;

/// <summary>Seaglass scrolling: the grid behind a row's "See all" (<see cref="ItemGridViewModel"/>).</summary>
public sealed partial class ItemGridPage : Page
{
	public ItemGridViewModel ViewModel { get; } = App.GetService<ItemGridViewModel>();

	public ItemGridPage()
	{
		InitializeComponent();
	}
}
