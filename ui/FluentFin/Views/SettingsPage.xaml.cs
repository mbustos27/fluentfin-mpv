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

	/// <summary>1.2.0 merge (the library stream's setting, kept on this PC): Home leaves out Continue Watching items not played for a while.</summary>
	public ContinueWatchingSettingViewModel ContinueWatching { get; } = new(App.GetService<Core.Contracts.Services.ILocalSettingsService>());

	/// <summary>Nixie 1.3.0: Settings &gt; Appearance &gt; Theme: a choice applies at once (ThemeSwitcher) and is saved on this PC.</summary>
	public FluentFin.ViewModels.ThemePickerViewModel ThemePicker { get; } =
		new(App.GetService<FluentFin.Services.ThemeService>(), id => App.GetService<FluentFin.Services.ThemeSwitcher>().SwitchAsync(id));

	public SettingsPage()
	{
		InitializeComponent();

		// Nixie 1.3.0: the active theme's options block: an element as it is, anything else drawn by the theme's ThemeOptionsTemplate
		ShowThemeOptions();
		ThemePicker.PropertyChanged += OnThemePickerChanged;
		Unloaded += (_, _) =>
		{
			ThemePicker.PropertyChanged -= OnThemePickerChanged;
			ThemePicker.Detach();
		};

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

	private void OnThemePickerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(FluentFin.ViewModels.ThemePickerViewModel.OptionsBlock))
		{
			ShowThemeOptions();
		}
	}

	private void ShowThemeOptions() =>
		ThemeOptions.ContentTemplate = ThemePicker.OptionsBlock is UIElement or null ? null : FluentFin.Services.ThemeResources.Find(FluentFin.Themes.ThemeContract.OptionsTemplateKey) as DataTemplate;

	private void OnKeyboardShortcutsClick(object sender, RoutedEventArgs e) => FluentFin.Services.WindowShortcuts.ShowHelp(XamlRoot);

	// Nixie 1.3.1: Check now: asks at once (the setting on or off), says what it found here, and Narrator reads it
	private Core.Updates.ReleaseInfo? _checkedRelease;

	private async void OnCheckNowClick(object sender, RoutedEventArgs e)
	{
		var updates = App.GetService<FluentFin.Services.UpdateNoticeService>();
		CheckNowButton.IsEnabled = false;
		CheckNowRetryButton.Visibility = Visibility.Collapsed;
		CheckNowInstallButton.Visibility = Visibility.Collapsed;
		CheckNowNotesButton.Visibility = Visibility.Collapsed;
		CheckNowResult.Visibility = Visibility.Visible;
		CheckNowRing.IsActive = true;
		CheckNowRing.Visibility = Visibility.Visible;
		CheckNowText.Text = Core.Updates.UpdateCheckMessage.Checking;
		Core.Updates.UpdateCheckMessage message;
		try
		{
			message = Core.Updates.UpdateCheckMessage.For(await updates.Coordinator.CheckNowAsync(), AppVersion.Current);
		}
		catch (Exception)
		{
			message = new Core.Updates.UpdateCheckMessage("Couldn't check: the check did not finish", true, true);
		}

		CheckNowRing.IsActive = false;
		CheckNowRing.Visibility = Visibility.Collapsed;
		CheckNowText.Text = message.Text;
		_checkedRelease = message.Release;
		if (message.Release is { } release)
		{
			CheckNowInstallButton.Content = FluentFin.Services.UpdateNoticeService.IsMsiInstall() && release.HasInstaller ? "Download and install" : "Open the download page";
			CheckNowInstallButton.Visibility = Visibility.Visible;
			CheckNowNotesButton.Visibility = Visibility.Visible;
		}

		CheckNowRetryButton.Visibility = message.CanRetry ? Visibility.Visible : Visibility.Collapsed;
		CheckNowButton.IsEnabled = true;
		var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(CheckNowText)
			?? Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(CheckNowText);
		peer?.RaiseNotificationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationKind.ActionCompleted,
			Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationProcessing.ImportantMostRecent, message.Text, "NixieUpdateCheck");
	}

	private async void OnCheckNowInstallClick(object sender, RoutedEventArgs e)
	{
		if (_checkedRelease is { } release)
		{
			CheckNowInstallButton.IsEnabled = false;
			await App.GetService<FluentFin.Services.UpdateNoticeService>().InstallAsync(release, () => CheckNowText.Text = $"{AppIdentity.Name} will close to update");
			CheckNowInstallButton.IsEnabled = true;
		}
	}

	private void OnCheckNowNotesClick(object sender, RoutedEventArgs e)
	{
		if (_checkedRelease is { } release)
		{
			FluentFin.Services.UpdateNoticeService.OpenPage(release.PageUrl);
		}
	}

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
