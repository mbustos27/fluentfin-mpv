using FluentFin.Core.Contracts.Services;
using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Themes.DriveIn;

/// <summary>
/// Drive-in's block in Settings &gt; Appearance (<see cref="DriveInTheme.CreateOptions"/>): Compact Home, the safety valve the
/// concept gives the theme (the lot takes about two thirds of the first screen): on, Home folds the drive-in into a strip
/// and the Coming up sign becomes a row. A per-PC setting (<see cref="DriveInHome.CompactHomeKey"/>), off by default; Home
/// reads it when it is next shown.
/// </summary>
public static class DriveInOptions
{
	public const string CompactHeader = "Compact Home";

	public const string CompactDescription = "Fold the drive-in into a strip at the top of Home, with Coming up as a row.";

	public static UIElement Create()
	{
		var settings = Settings();
		var toggle = new ToggleSwitch
		{
			Header = CompactHeader,
			IsOn = Read(settings),
			OnContent = "On",
			OffContent = "Off",
		};
		AutomationProperties.SetHelpText(toggle, CompactDescription);
		toggle.Toggled += (_, _) => settings?.SaveSetting(DriveInHome.CompactHomeKey, toggle.IsOn);

		var block = new StackPanel { Spacing = 4 };
		block.Children.Add(toggle);
		block.Children.Add(new TextBlock
		{
			Text = CompactDescription,
			TextWrapping = TextWrapping.Wrap,
			Style = (Style)Application.Current.Resources["GlassMetaTextBlockStyle"],
		});
		return block;
	}

	/// <summary>Whether Compact Home is on (off when the setting cannot be read).</summary>
	public static bool Read(ILocalSettingsService? settings)
	{
		try
		{
			return settings?.ReadSetting(DriveInHome.CompactHomeKey, false) ?? false;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static ILocalSettingsService? Settings()
	{
		try
		{
			return App.GetService<ILocalSettingsService>();
		}
		catch (Exception)
		{
			return null;
		}
	}
}
