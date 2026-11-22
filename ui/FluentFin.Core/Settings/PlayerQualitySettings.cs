using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Core.Mpv;

namespace FluentFin.Core.Settings;

/// <summary>1.6 player: one entry of Settings' quality lists: "Auto", "Original", "8 Mbps (1080p)".</summary>
public sealed record QualitySettingChoice(QualityChoice Choice, string Name)
{
	public override string ToString() => Name;
}

/// <summary>
/// 1.6 player: Settings &gt; Playback's quality group (Controls\PlayerQualitySettingsPanel): the default quality at home and
/// away, both Auto until the user picks (Nixie never moves them by itself), and "Show the Playback stats button" (off by
/// default). Kept on this PC; a change reaches the next play (<c>set_quality_defaults</c>) and the next player page.
/// </summary>
public sealed partial class PlayerQualitySettings(ISettings settings) : ObservableObject
{
	/// <summary>Auto, Original, then every cap, each with the picture it suits.</summary>
	public static IReadOnlyList<QualitySettingChoice> Choices { get; } =
	[
		new(QualityChoice.Auto, "Auto"),
		new(QualityChoice.Original, "Original"),
		.. QualityChoices.Caps.Select(cap => new QualitySettingChoice(new QualityChoice(cap.Bitrate), $"{QualityChoices.Mbps(cap.Bitrate)} ({QualityChoices.ClassLabel(cap.ResolutionClass)})")),
	];

	public const string HomeHeader = "Quality at home";

	public const string AwayHeader = "Quality away from home";

	public const string HomeDescription = "When the server is on your home network. A lower quality makes the server convert the video, and some servers don't allow it.";

	public const string AwayDescription = "When the server is reached over the internet. A lower quality makes the server convert the video, and some servers don't allow it.";

	public const string StatsHeader = "Show the Playback stats button";

	public const string StatsDescription = "A button in the player for a small panel on how the video plays: format, bitrate, dropped frames, buffer. I opens it too.";

	public QualitySettingChoice SelectedHome
	{
		get => Find(settings.QualityAtHome);
		set
		{
			if (value is not null && value.Choice != (settings.QualityAtHome ?? QualityChoice.Auto))
			{
				settings.QualityAtHome = value.Choice;
				OnPropertyChanged();
			}
		}
	}

	public QualitySettingChoice SelectedAway
	{
		get => Find(settings.QualityAway);
		set
		{
			if (value is not null && value.Choice != (settings.QualityAway ?? QualityChoice.Auto))
			{
				settings.QualityAway = value.Choice;
				OnPropertyChanged();
			}
		}
	}

	public bool ShowStatsButton
	{
		get => settings.ShowPlaybackStatsButton;
		set
		{
			if (settings.ShowPlaybackStatsButton != value)
			{
				settings.ShowPlaybackStatsButton = value;
				OnPropertyChanged();
			}
		}
	}

	/// <summary><c>set_quality_defaults</c>'s args from what is kept (Auto for anything unreadable).</summary>
	public static System.Text.Json.Nodes.JsonObject DefaultsCommand(ISettings settings) =>
		QualityChoices.DefaultsCommand(settings.QualityAtHome ?? QualityChoice.Auto, settings.QualityAway ?? QualityChoice.Auto);

	private static QualitySettingChoice Find(QualityChoice? choice) =>
		Choices.FirstOrDefault(c => c.Choice == (choice ?? QualityChoice.Auto)) ?? Choices[0];
}
