using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Core.Contracts.Services;

namespace FluentFin.Core.Settings;

public interface ISettings
{
	ObservableCollection<SavedServer> Servers { get; }
	MediaPlayerType MediaPlayer { get; set; }

	/// <summary>Command that starts the mpv backend, see <see cref="SettingKeys.MpvBackendCommand"/>. Empty = automatic.</summary>
	string MpvBackendCommand { get; set; }

	/// <summary>fluentfin-mpv: see <see cref="SettingKeys.ShowSkipPrompt"/>.</summary>
	bool ShowSkipPrompt { get; set; }

	/// <summary>fluentfin-mpv: see <see cref="SettingKeys.MouseWheelVolume"/>.</summary>
	bool MouseWheelVolume { get; set; }

	/// <summary>fluentfin-mpv: see <see cref="SettingKeys.VolumeStep"/> (1 to 20).</summary>
	int VolumeStep { get; set; }

	void ListenToChanges();

	void SaveServerDetails();
}

public partial class Settings(ILocalSettingsService localSettingsService) : ObservableObject, ISettings
{
	private bool _isListening;

	public ObservableCollection<SavedServer> Servers { get; set; } = localSettingsService.ReadSetting(SettingKeys.Servers);
	
	public MediaPlayerType MediaPlayer
	{
		get => localSettingsService.ReadSetting(SettingKeys.MediaPlayerType);
		set => localSettingsService.SaveSetting(SettingKeys.MediaPlayerType, value);
	}

	public string MpvBackendCommand
	{
		get => localSettingsService.ReadSetting(SettingKeys.MpvBackendCommand) ?? "";
		set => localSettingsService.SaveSetting(SettingKeys.MpvBackendCommand, value ?? "");
	}

	public bool ShowSkipPrompt
	{
		get => localSettingsService.ReadSetting(SettingKeys.ShowSkipPrompt);
		set => localSettingsService.SaveSetting(SettingKeys.ShowSkipPrompt, value);
	}

	public bool MouseWheelVolume
	{
		get => localSettingsService.ReadSetting(SettingKeys.MouseWheelVolume);
		set => localSettingsService.SaveSetting(SettingKeys.MouseWheelVolume, value);
	}

	public int VolumeStep
	{
		get => Math.Clamp(localSettingsService.ReadSetting(SettingKeys.VolumeStep), 1, 20);
		set => localSettingsService.SaveSetting(SettingKeys.VolumeStep, Math.Clamp(value, 1, 20));
	}

	public void SaveServerDetails() => localSettingsService.SaveSetting(SettingKeys.Servers, Servers);

	public void ListenToChanges()
	{
		if (_isListening)
		{
			return;
		}

		Servers.CollectionChanged += Servers_CollectionChanged;
		foreach (var server in Servers)
		{
			server.Users.CollectionChanged += Users_CollectionChanged;
		}


		_isListening = true;
	}

	private void Servers_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
	{
		if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
		{
			foreach (SavedServer newServer in e.NewItems?.OfType<SavedServer>() ?? [])
			{
				newServer.Users.CollectionChanged += Users_CollectionChanged;
			}
		}

		else if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove)
		{
			foreach (SavedServer oldServer in e.OldItems?.OfType<SavedServer>() ?? [])
			{
				oldServer.Users.CollectionChanged -= Users_CollectionChanged;
			}
		}

		localSettingsService.SaveSetting(SettingKeys.Servers, Servers);
	}

	private void Users_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
	{
		localSettingsService.SaveSetting(SettingKeys.Servers, Servers);
	}
}

