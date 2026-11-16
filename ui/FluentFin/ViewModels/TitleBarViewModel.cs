using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFin.Contracts.Services;
using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using FluentFin.UI.Core.Contracts.Services;
using FluentFin.Views;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.DependencyInjection;

namespace FluentFin.ViewModels;

public partial class TitleBarViewModel : ObservableObject, ITitleBarViewModel
{
	private readonly INavigationService _navigationService;
	private readonly INavigationService _setupNavigationService;
	private readonly INavigationViewService _navigationViewService;
	private readonly IJellyfinClient _jellyfinClient;
	private readonly MpvPlaybackService _mpvPlayback;

	public TitleBarViewModel(INavigationService navigationService,
							 [FromKeyedServices(NavigationRegions.InitialSetup)] INavigationService setupNavigationService,
							 INavigationViewService navigationViewService,
							 IJellyfinClient jellyfinClient,
							 MpvPlaybackService mpvPlayback)
	{
		_navigationService = navigationService;
		_setupNavigationService = setupNavigationService;
		_navigationViewService = navigationViewService;
		_jellyfinClient = jellyfinClient;
		_mpvPlayback = mpvPlayback;

		navigationService.Navigated += NavigationService_Navigated;
	}

	[ObservableProperty]
	public partial string Title { get; set; } = "";

	[ObservableProperty]
	public partial string Version { get; set; } = Core.AppVersion.Current;

	[ObservableProperty]
	public partial bool CanGoBack { get; set; }

	[ObservableProperty]
	public partial UserDto? User { get; set; }

	[ObservableProperty]
	public partial bool IsVisible { get; set; } = true;

	[ObservableProperty]
	public partial SavedServer? CurrentServer { get; set; }

	// fluentfin-mpv: the name messages use for the server (LoadMessages.ServerName)
	partial void OnCurrentServerChanged(SavedServer? value) => Core.SessionInfo.ServerName = value?.DisplayName;

	public void TogglePane()
	{
		_navigationViewService.TogglePane();
	}

	public void GoBack()
	{
		_navigationService.GoBack();
	}

	// fluentfin-mpv: a sign-out in progress (Log out or Switch user), shown in the title bar; both commands wait for it
	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(LogoutCommand))]
	[NotifyCanExecuteChangedFor(nameof(SwitchUserCommand))]
	public partial bool IsSigningOut { get; set; }

	/// <summary>fluentfin-mpv: what the sign-out in progress waits for, next to its progress ring.</summary>
	[ObservableProperty]
	public partial string SignOutStatus { get; set; } = "";

	private bool CanSignOut() => !IsSigningOut;

	[RelayCommand(CanExecute = nameof(CanSignOut))]
	public Task Logout() => SignOut(() => _setupNavigationService.NavigateTo<SelectServerViewModel>());

	[RelayCommand(CanExecute = nameof(CanSignOut))]
	public Task SwitchUser() => SignOut(() => _setupNavigationService.NavigateTo<LoginViewModel>(CurrentServer));

	/// <summary>
	/// fluentfin-mpv: Log out and Switch user. The mpv playback stops and is reported before the page goes and the token is
	/// revoked (bounded, <see cref="MpvPlaybackService.StopForSignOutAsync"/>), which is shown meanwhile. One at a time: both
	/// commands are disabled until it is done, and one invoked anyway (back to back, before the first got that far) does nothing.
	/// </summary>
	private async Task SignOut(Action navigate)
	{
		if (IsSigningOut)
		{
			return;
		}

		IsSigningOut = true;
		try
		{
			SignOutStatus = _mpvPlayback.HasPlayback ? "Stopping playback…" : "Signing out…";
			await _mpvPlayback.StopForSignOutAsync();
			SignOutStatus = "Signing out…";
			User = null;
			navigate();
			await _jellyfinClient.Logout();
		}
		finally
		{
			SignOutStatus = "";
			IsSigningOut = false;
		}
	}


	[RelayCommand]
	private void GoToDashboard()
	{
		_navigationService.NavigateTo(typeof(JellyfinSettingsViewModel).FullName!, new object());
	}

	private void NavigationService_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
	{
		CanGoBack = _navigationService.CanGoBack;
	}

}
