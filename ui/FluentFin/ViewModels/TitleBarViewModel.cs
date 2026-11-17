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

	// Seaglass 1.1.4: whether the player page shows a Back of its own (null: it does; tests)
	private readonly ISettings? _settings;

	public TitleBarViewModel(INavigationService navigationService,
							 [FromKeyedServices(NavigationRegions.InitialSetup)] INavigationService setupNavigationService,
							 INavigationViewService navigationViewService,
							 IJellyfinClient jellyfinClient,
							 MpvPlaybackService mpvPlayback,
							 ISettings? settings = null)
	{
		_navigationService = navigationService;
		_setupNavigationService = setupNavigationService;
		_navigationViewService = navigationViewService;
		_jellyfinClient = jellyfinClient;
		_mpvPlayback = mpvPlayback;
		_settings = settings;

		navigationService.Navigated += NavigationService_Navigated;
	}

	[ObservableProperty]
	public partial string Title { get; set; } = "";

	[ObservableProperty]
	public partial string Version { get; set; } = Core.AppVersion.Current;

	[ObservableProperty]
	public partial bool CanGoBack { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsBackButtonShown))]
	[NotifyPropertyChangedFor(nameof(IsAccountShown))]
	public partial UserDto? User { get; set; }

	/// <summary>
	/// The whole title bar: hidden in full screen (the player page sets it), whatever page is shown. Independent of
	/// <see cref="IsPlayerPageShown"/>: full screen in and out leaves Back and Account as the page wants them.
	/// </summary>
	[ObservableProperty]
	public partial bool IsVisible { get; set; } = true;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ServerName))]
	public partial SavedServer? CurrentServer { get; set; }

	/// <summary>Seaglass 1.1.4: the signed-in server's name, under the user's in the Account menu.</summary>
	public string ServerName => ServerNameOf(CurrentServer);

	/// <summary>A saved server's name as the Account menu shows it: its name, else its address.</summary>
	public static string ServerNameOf(SavedServer? server) =>
		!string.IsNullOrWhiteSpace(server?.DisplayName) ? server.DisplayName.Trim() : server?.PublicUrl ?? "";

	// ---- Seaglass 1.1.4 (UX review, prioritised fix 5): the title bar while the player page is shown

	/// <summary>
	/// The shell's frame shows the player page: set from the frame's navigation by the page's type
	/// (<see cref="OnPageShown"/>), never by the player's own files.
	/// </summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsBackButtonShown))]
	[NotifyPropertyChangedFor(nameof(IsAccountShown))]
	public partial bool IsPlayerPageShown { get; private set; }

	/// <summary>
	/// The title bar's Back: signed in, and not on a player page that has a Back of its own (Seaglass's player controls do;
	/// mpv's own controls and the other players have none, so there it stays).
	/// </summary>
	public bool IsBackButtonShown => User is not null && !(IsPlayerPageShown && PlayerHasItsOwnBack());

	/// <summary>The Account button: signed in, and never over the player (a Sign out over a playing video is a hazard).</summary>
	public bool IsAccountShown => User is not null && !IsPlayerPageShown;

	/// <summary>
	/// The shell's frame now shows a page of <paramref name="pageType"/> (null: none, the shell went: Log out and Switch user
	/// take it away with whatever page it showed). A navigation that fails never gets here, so nothing stays hidden.
	/// </summary>
	public void OnPageShown(Type? pageType) => IsPlayerPageShown = pageType == typeof(VideoPlayerPage);

	/// <summary>Why libmpv cannot be used, null when it can: the real probe (tests probe nothing), as the player page asks it.</summary>
	internal Func<string?> LibmpvUnavailable { get; set; } = FluentFin.Mpv.Libmpv.Unavailable;

	/// <summary>
	/// The player page shows a Back of its own: the mpv player with Seaglass's controls (the same choice the page makes when
	/// it opens, <see cref="FluentFin.Mpv.MpvVideoSurfaceChoice.UsesFluentFinControls(ISettings, out string?)"/>).
	/// </summary>
	private bool PlayerHasItsOwnBack() =>
		_settings is null || (_settings.MediaPlayer == MediaPlayerType.Mpv && FluentFin.Mpv.MpvVideoSurfaceChoice.UsesFluentFinControls(_settings, LibmpvUnavailable, out _));

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


	/// <summary>
	/// Seaglass 1.1.4 (UX review, prioritised fix 15): the Account menu's Settings. Sync Play, Quick Connect and Play On live
	/// there now, and the server's administration (for administrators); the menu's Dashboard went.
	/// </summary>
	[RelayCommand]
	private void GoToSettings()
	{
		_navigationService.NavigateTo<SettingsViewModel>();
	}

	private void NavigationService_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
	{
		CanGoBack = _navigationService.CanGoBack;
	}

}
