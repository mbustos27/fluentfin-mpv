using FluentFin.Core.ViewModels;
using FluentFin.Services;
using FluentFin.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FluentFin.Views;

public sealed partial class TitleBarControl : UserControl
{
	public TitleBarViewModel ViewModel { get; } = (TitleBarViewModel)App.GetService<ITitleBarViewModel>();

	// fluentfin-mpv: no search while a player page is open (docs/research/ux-review.md V9)
	public PlayerChrome Chrome { get; } = App.GetService<PlayerChrome>();

	public Microsoft.UI.Xaml.Visibility SearchVisibility(UserDto? user, bool isPlayerOpen) =>
		user is not null && !isPlayerOpen ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

	public TitleBarControl()
	{
		InitializeComponent();
		_current = this;
	}

	private static TitleBarControl? _current;

	/// <summary>Seaglass: the search keys (Ctrl+E, /, WindowShortcuts): the search box takes the focus when it shows.</summary>
	public static bool FocusSearch() =>
		_current is { SearchBox.Visibility: Microsoft.UI.Xaml.Visibility.Visible } bar && bar.SearchBox.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);

	private void TitleBar_BackRequested(Microsoft.UI.Xaml.Controls.TitleBar sender, object args)
	{
		ViewModel.GoBack();
	}

	private void CloseTitleBarFooterFlyout(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
	{
		FooterFlyout.Hide();
	}

	private void KeyboardAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		if (FocusManager.GetFocusedElement() is AutoSuggestBox sb && sb == SearchBox)
		{
			return;
		}

		SearchBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
	}
}
