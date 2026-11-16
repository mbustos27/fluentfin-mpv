
using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Contracts.Services;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core;
using FluentFin.Core.Mpv;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using FluentFin.UI.Core.Contracts.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Navigation;

namespace FluentFin.ViewModels;

public partial class ShellViewModel : ObservableObject, INavigationAware
{
	[ObservableProperty] public partial bool IsBackEnabled { get; set; }
	[ObservableProperty] public partial object? Selected { get; set; }

	public INavigationService NavigationService { get; }
	public INavigationViewService NavigationViewService { get; }
	public bool IsReportingVisible { get; } = SessionInfo.HasPlaybackReporting();

	private readonly MpvPrewarm _mpvPrewarm;
	private readonly ILogger<ShellViewModel> _logger;

	public ShellViewModel(INavigationService navigationService, INavigationViewService navigationViewService, MpvPrewarm mpvPrewarm, ILogger<ShellViewModel> logger)
	{
		NavigationService = navigationService;
		NavigationService.Navigated += OnNavigated;
		NavigationViewService = navigationViewService;
		_mpvPrewarm = mpvPrewarm;
		_logger = logger;
	}

	private void OnNavigated(object sender, NavigationEventArgs e)
	{
		IsBackEnabled = NavigationService.CanGoBack;
		var selectedItem = NavigationViewService.GetSelectedItem(e.SourcePageType);
		Selected = selectedItem;
	}

	public Task OnNavigatedTo(object parameter)
	{
		// fluentfin-mpv (startup-perf): signed in, the shell is up
		PerfLog.Startup(_logger, "shell ready");
		NavigationService.NavigateTo<HomeViewModel>(parameter);
		PerfLog.Startup(_logger, "home requested");
		// fluentfin-mpv (startup-perf): the mpv backend started (and connected, with mpv's controls) in the background, so Play skips that
		_mpvPrewarm.SignedIn();
		return Task.CompletedTask;
	}

	/// <summary>fluentfin-mpv (startup-perf): the shell is left only by signing out (Log out, Switch user).</summary>
	public Task OnNavigatedFrom()
	{
		_mpvPrewarm.SignedOut();
		return Task.CompletedTask;
	}
}
