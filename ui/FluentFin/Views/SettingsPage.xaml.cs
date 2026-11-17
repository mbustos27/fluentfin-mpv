using CommunityToolkit.WinUI;
using CommunityToolkit.WinUI.Controls;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Views.JellyfinSettings;

public sealed partial class SettingsPage : Page
{
	public SettingsViewModel ViewModel { get; } = App.GetService<SettingsViewModel>();

	public SettingsPage()
	{
		InitializeComponent();

		// Seaglass: a Glass pick takes effect at once (it is saved by the view model)
		ViewModel.PropertyChanged += OnViewModelPropertyChanged;
		Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
	}

	private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(SettingsViewModel.Glass))
		{
			FluentFin.Services.GlassSetting.Apply(ViewModel.Glass);
		}
	}

	// fluentfin-mpv: where the next four settings live, so changing them here is no surprise elsewhere
	public string ServerPreferencesCaption() =>
		$"Saved to your account on {LoadMessages.ServerName()}, so other Jellyfin apps use them too. They choose each item's default audio and subtitles in every player.";

	private async void RemoveUser(object sender, RoutedEventArgs e)
	{
		if (sender is not FrameworkElement button)
		{
			return;
		}

		if (button.Tag is not SavedUser user)
		{
			return;
		}

		if (button.FindAscendant<SettingsExpander>() is not { } expander)
		{
			return;
		}

		if (expander.Tag is not SavedServer server)
		{
			return;
		}

		// fluentfin-mpv: with the home page saved for that login (never throws)
		await ViewModel.RemoveSavedUser(server, user);
	}
}
