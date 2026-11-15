using FluentFin.Contracts.Services;
using FluentFin.Core;
using FluentFin.Core.Mpv;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using FluentFin.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace FluentFin.Activation;

/// <summary>
/// Dev self-test, <c>FluentFin.exe --mpv-selftest</c>: skips the login and opens the player page on the main window's
/// frame, which starts the backend with <c>--dev</c>, connects with only the video window and plays mpv's test source.
/// Verifies the embedded video without a Jellyfin server. Runs instead of the default activation (no login attempt).
/// The server list is put under the player page, so the page can be left the way playback leaves it (mpv's stop key,
/// Escape): that runs the page's leave path (full screen restored) and ends on a usable page.
/// </summary>
public class MpvSelfTestActivationHandler([FromKeyedServices(NavigationRegions.InitialSetup)] INavigationService navigationService)
	: ActivationHandler<LaunchActivatedEventArgs>
{
	public static bool IsRequested(IEnumerable<string> commandLine) =>
		commandLine.Any(x => string.Equals(x, MpvSelfTestRequest.CommandLineSwitch, StringComparison.OrdinalIgnoreCase));

	protected override bool CanHandleInternal(LaunchActivatedEventArgs args) => IsRequested(Environment.GetCommandLineArgs().Skip(1));

	protected override Task HandleInternalAsync(LaunchActivatedEventArgs args)
	{
		navigationService.NavigateTo<SelectServerViewModel>();
		navigationService.NavigateTo<VideoPlayerViewModel>(MpvSelfTestRequest.Default);
		return Task.CompletedTask;
	}
}
