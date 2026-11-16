using System.ComponentModel;
using FluentFin.Core.Settings;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

public interface IMainWindowViewModel : INotifyPropertyChanged
{
	ITitleBarViewModel TitleBarViewModel { get; }

	/// <summary>
	/// fluentfin-mpv (startup-perf): what the window shows while it has nothing else to show, such as the automatic
	/// sign-in at start-up ("Signing in to Home…"); null or empty = nothing.
	/// </summary>
	string? StartupStatus { get; set; }
}

public interface ITitleBarViewModel : INotifyPropertyChanged
{
	string Title { get; }
	string Version { get; }
	void TogglePane();
	void GoBack();
	bool CanGoBack { get; }
	UserDto? User { get; set; }
	bool IsVisible { get; set; }
	SavedServer? CurrentServer { get; set; }
	Task Logout();
}
