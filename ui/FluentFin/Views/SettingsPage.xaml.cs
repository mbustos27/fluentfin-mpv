using CommunityToolkit.WinUI;
using CommunityToolkit.WinUI.Controls;
using FluentFin.Core;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using FluentFin.Dialogs;
using FluentFin.Plugins.Jellyseer;
using FluentFin.Contracts.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

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

		// Seaglass 1.1.3: removing a saved server or sign-in asks first, naming it; Cancel is the default button
		ViewModel.Confirm = prompt => ConfirmDialog.AskAsync(XamlRoot, prompt);
	}

	private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(SettingsViewModel.Glass))
		{
			FluentFin.Services.GlassSetting.Apply(ViewModel.Glass);
		}
	}

	private void OnKeyboardShortcutsClick(object sender, RoutedEventArgs e) => FluentFin.Services.WindowShortcuts.ShowHelp(XamlRoot);

	// fluentfin-mpv: where the next settings live, so changing them here is no surprise elsewhere
	public string ServerPreferencesCaption() =>
		$"Saved to your account on {LoadMessages.ServerName()}, so other Jellyfin apps use them too. They choose each item's default audio and subtitles in every player.";

	/// <summary>Seaglass 1.1.3: the Player list only when this copy can run more than one player.</summary>
	public bool HasPlayerChoice => SettingsViewModel.HasPlayerChoice;

	// ---- Seaglass 1.1.3: Settings > Integrations > Jellyseerr (it left the rail); the page it opens asks for the address when there is none

	public string JellyseerrDescription { get; } = JellyseerrEntry.Description(JellyseerOptions.Current, SessionInfo.ServerId);

	public string JellyseerrAction { get; } = JellyseerrEntry.ActionText(JellyseerOptions.Current, SessionInfo.ServerId);

	public string JellyseerrButtonName => $"{JellyseerrAction} {JellyseerrEntry.Name}";

	private void OnJellyseerrClick(object sender, RoutedEventArgs e) => App.GetService<INavigationService>().NavigateTo(JellyseerrEntry.PageKey);

	// ---- Seaglass 1.1.4: Settings > Servers and accounts > Server administration (the account menu's old Dashboard), administrators only

	public bool ShowsServerAdministration => SettingsViewModel.ShowsServerAdministration(SessionInfo.CurrentUser);

	private void OnServerAdministrationClick(object sender, RoutedEventArgs e) =>
		App.GetService<INavigationService>().NavigateTo(typeof(JellyfinSettingsViewModel).FullName!, new object());

	// ---- Seaglass 1.1.3: Settings > About; the cards are buttons, so Enter and Space open the links as a click does

	private async void OnReportProblemClick(object sender, RoutedEventArgs e) => await Launcher.LaunchUriAsync(new Uri(SettingsViewModel.ReportProblemUrl));

	private async void OnContributeClick(object sender, RoutedEventArgs e) => await Launcher.LaunchUriAsync(new Uri(SettingsViewModel.ContributeUrl));

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

		// fluentfin-mpv: with the home page saved for that login (never throws); Seaglass 1.1.3: after a yes (RemoveUserPrompt)
		await ViewModel.RemoveSavedUser(server, user);
	}
}
