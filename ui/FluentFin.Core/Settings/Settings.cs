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

	/// <summary>fluentfin-mpv: see <see cref="SettingKeys.ShowSkipPrompt"/>.</summary>
	bool ShowSkipPrompt { get; set; }

	/// <summary>fluentfin-mpv: see <see cref="SettingKeys.LookUpIntrosOnline"/>.</summary>
	bool LookUpIntrosOnline { get; set; }

	/// <summary>fluentfin-mpv: see <see cref="SettingKeys.LearnIntros"/>.</summary>
	bool LearnIntros { get; set; }

	/// <summary>fluentfin-mpv: see <see cref="SettingKeys.MouseWheelVolume"/>.</summary>
	bool MouseWheelVolume { get; set; }

	/// <summary>fluentfin-mpv: see <see cref="SettingKeys.VolumeStep"/> (1 to 20).</summary>
	int VolumeStep { get; set; }

	/// <summary>fluentfin-mpv: see <see cref="SettingKeys.SubtitleSize"/> (50 to 200, steps of 10).</summary>
	int SubtitleSize { get; set; }

	/// <summary>Seaglass: see <see cref="SettingKeys.Glass"/>.</summary>
	GlassChoice Glass { get; set; }

	/// <summary>Seaglass: see <see cref="SettingKeys.CheckForUpdates"/>.</summary>
	bool CheckForUpdates { get; set; }

	/// <summary>Seaglass: see <see cref="SettingKeys.UpdateState"/>.</summary>
	string UpdateState { get; set; }

	void ListenToChanges();

	void SaveServerDetails();
}

public partial class Settings(ILocalSettingsService localSettingsService, MediaPlayerAvailability? players = null) : ObservableObject, ISettings
{
	private bool _isListening;

	public ObservableCollection<SavedServer> Servers { get; set; } = localSettingsService.ReadSetting(SettingKeys.Servers);

	public MediaPlayerType MediaPlayer
	{
		// fluentfin-mpv (release 1.0): a saved player this copy cannot run (VLC, Flyleaf in the release) plays with MPV
		get => (players ?? MediaPlayerAvailability.ForThisApp).OrFallback(localSettingsService.ReadSetting(SettingKeys.MediaPlayerType));
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

	public GlassChoice Glass
	{
		// a stored value that is no longer a choice (a hand-edited file) reads as Automatic
		get => Enum.IsDefined(localSettingsService.ReadSetting(SettingKeys.Glass)) ? localSettingsService.ReadSetting(SettingKeys.Glass) : GlassChoice.Automatic;
		set => localSettingsService.SaveSetting(SettingKeys.Glass, value);
	}

	public bool CheckForUpdates
	{
		get => localSettingsService.ReadSetting(SettingKeys.CheckForUpdates);
		set => localSettingsService.SaveSetting(SettingKeys.CheckForUpdates, value);
	}

	public string UpdateState
	{
		get => localSettingsService.ReadSetting(SettingKeys.UpdateState) ?? "";
		set => localSettingsService.SaveSetting(SettingKeys.UpdateState, value ?? "");
	}

	public bool ShowSkipPrompt
	{
		get => localSettingsService.ReadSetting(SettingKeys.ShowSkipPrompt);
		set => localSettingsService.SaveSetting(SettingKeys.ShowSkipPrompt, value);
	}

	public bool LookUpIntrosOnline
	{
		get => localSettingsService.ReadSetting(SettingKeys.LookUpIntrosOnline);
		set => localSettingsService.SaveSetting(SettingKeys.LookUpIntrosOnline, value);
	}

	public bool LearnIntros
	{
		get => localSettingsService.ReadSetting(SettingKeys.LearnIntros);
		set => localSettingsService.SaveSetting(SettingKeys.LearnIntros, value);
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

	public int SubtitleSize
	{
		get => Mpv.SubtitleSizes.Clamp(localSettingsService.ReadSetting(SettingKeys.SubtitleSize));
		set => localSettingsService.SaveSetting(SettingKeys.SubtitleSize, Mpv.SubtitleSizes.Clamp(value));
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

