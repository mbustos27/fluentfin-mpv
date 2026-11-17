using System.Reactive.Linq;
using FluentFin.Contracts.Services;
using FluentFin.Services;
using FluentFin.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ReactiveUI;
using Windows.System;

namespace FluentFin.Views;

public sealed partial class ShellPage : Page
{
	public ShellViewModel ViewModel
	{
		get;
	}

	// fluentfin-mpv: no navigation pane while a player page is open (docs/research/ux-review.md V9)
	public PlayerChrome Chrome { get; } = App.GetService<PlayerChrome>();

	public ShellPage()
	{
		ViewModel = App.GetService<ShellViewModel>();
		InitializeComponent();

		ViewModel.NavigationService.Frame = NavigationFrame;
		ViewModel.NavigationViewService.Initialize(NavigationViewControl);

		ViewModel.WhenAnyValue(x => x.Selected)
			.WhereNotNull()
			.Where(x => x is NavigationViewItem)
			.ObserveOn(RxSchedulers.MainThreadScheduler)
			.Subscribe(item =>
			{
				NavigationViewControl.SelectedItem = NavigationViewControl.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(x => x.Content == ((NavigationViewItem)item).Content);
			});
	}

	// Seaglass: Alt+Left, the Back and Forward keys and Mouse 4 / 5 are the window's (WindowShortcuts), on every page
	private void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
	{
	}

	private async void OnContributeTapped(object sender, TappedRoutedEventArgs e)
	{
		// fluentfin-mpv: this project's repository (issues and contributions go here; upstream is credited in its README)
		await Launcher.LaunchUriAsync(new Uri("https://github.com/mbustos27/fluentfin-mpv"));
	}
}
