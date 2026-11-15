using System.Collections.ObjectModel;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Settings;
using ReactiveUI;

namespace FluentFin.Core.ViewModels;

/// <summary>A player in the Settings list.</summary>
public sealed record MediaPlayerChoice(MediaPlayerType Type, string Name)
{
	public override string ToString() => Name;
}

public partial class SettingsViewModel(ISettings settings, IMpvBackend mpvBackend) : ObservableObject, INavigationAware
{
	private IDisposable? _statusSubscription;

	public ObservableCollection<SavedServer> Servers { get; } = settings.Servers;

	public static IReadOnlyList<MediaPlayerChoice> MediaPlayers { get; } =
	[
		new(MediaPlayerType.Mpv, "MPV (jellyfin-mpv-shim)"),
		new(MediaPlayerType.Vlc, "VLC"),
		new(MediaPlayerType.Flyleaf, "Flyleaf"),
		new(MediaPlayerType.WindowsMediaPlayer, "Windows Media Player"),
	];

	public MediaPlayerType MediaPlayerType
	{
		get => field;
		set
		{
			if (SetProperty(ref field, value))
			{
				settings.MediaPlayer = value;
				OnPropertyChanged(nameof(SelectedMediaPlayer));
				OnPropertyChanged(nameof(IsMpvSelected));
			}
		}
	}

	public MediaPlayerChoice? SelectedMediaPlayer
	{
		get => MediaPlayers.FirstOrDefault(x => x.Type == MediaPlayerType);
		set
		{
			if (value is not null)
			{
				MediaPlayerType = value.Type;
			}
		}
	}

	public bool IsMpvSelected => MediaPlayerType == MediaPlayerType.Mpv;

	/// <summary>Empty = found automatically; see <see cref="SettingKeys.MpvBackendCommand"/>.</summary>
	public string MpvBackendCommand
	{
		get => field;
		set
		{
			if (SetProperty(ref field, value ?? ""))
			{
				settings.MpvBackendCommand = field.Trim();
			}
		}
	} = "";

	[ObservableProperty]
	public partial string MpvBackendStatus { get; set; } = "";

	public Task OnNavigatedFrom()
	{
		_statusSubscription?.Dispose();
		_statusSubscription = null;
		return Task.CompletedTask;
	}

	public Task OnNavigatedTo(object parameter)
	{
		MediaPlayerType = settings.MediaPlayer;
		MpvBackendCommand = settings.MpvBackendCommand;

		_statusSubscription?.Dispose();
		_statusSubscription = mpvBackend.StatusChanged
			.ObserveOn(RxSchedulers.MainThreadScheduler)
			.Subscribe(status => MpvBackendStatus = status.Describe());

		return Task.CompletedTask;
	}

	[RelayCommand]
	private void DeleteServer(SavedServer server) => Servers.Remove(server);
}
