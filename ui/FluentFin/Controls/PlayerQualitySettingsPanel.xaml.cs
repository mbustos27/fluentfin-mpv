using FluentFin.Core.Settings;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Controls;

/// <summary>1.6 player: Settings &gt; Playback's quality cards and the Playback stats switch (PlayerQualitySettingsPanel.xaml).</summary>
public sealed partial class PlayerQualitySettingsPanel : UserControl
{
	public PlayerQualitySettingsPanel()
	{
		InitializeComponent();
	}

	public PlayerQualitySettings Settings { get; } = new(App.GetService<ISettings>());
}
