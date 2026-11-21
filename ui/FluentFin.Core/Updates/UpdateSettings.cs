using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Settings;

namespace FluentFin.Core.Updates;

/// <summary>
/// Nixie 1.5: Settings > Updates > Install when I close Nixie (off by default, kept on this PC). Only an installed copy can
/// install an update; a portable one shows the switch off and unavailable, with why.
/// </summary>
public sealed partial class UpdateSettings(ILocalSettingsService settings, bool canSelfUpdate) : ObservableObject
{
	public bool CanSelfUpdate { get; } = canSelfUpdate;

	public bool InstallOnClose
	{
		get => CanSelfUpdate && settings.ReadSetting(SettingKeys.InstallUpdatesOnClose);
		set
		{
			if (CanSelfUpdate && settings.ReadSetting(SettingKeys.InstallUpdatesOnClose) != value)
			{
				settings.SaveSetting(SettingKeys.InstallUpdatesOnClose, value);
				OnPropertyChanged();
			}
		}
	}

	/// <summary>The card's description: what the switch does, or why a portable copy has none.</summary>
	public string InstallOnCloseDescription => CanSelfUpdate
		? $"When an update is downloaded and checked, it installs quietly as {AppIdentity.Name} closes, and the next start is the new version. Off by default: otherwise it installs only when you choose Restart to update."
		: $"This copy of {AppIdentity.Name} is portable (unzipped), so it can't install updates itself: it shows each new version with a link to its release page.";
}
