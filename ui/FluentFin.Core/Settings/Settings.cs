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

	/// <summary>fluentfin-mpv: who draws the controls over the mpv player's video, see <see cref="SettingKeys.MpvPlayerControls"/>.</summary>
	MpvPlayerControls MpvPlayerControls { get; set; }

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

	public MpvPlayerControls MpvPlayerControls
	{
		get => localSettingsService.ReadSetting(SettingKeys.MpvPlayerControls);
		set => localSettingsService.SaveSetting(SettingKeys.MpvPlayerControls, value);
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

