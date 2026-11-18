using FluentFin.Controls;
using FluentFin.Dialogs;
using FluentFin.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Services;

/// <summary>
/// Nixie 1.3.0: the one-time "Choose a look" step after the first sign-in on a PC with this version: the theme tiles, the
/// saved choice preselected (Drive-in on a new install, Classic after an upgrade from 1.2.x), "Use this look" or "Skip".
/// Offered once whatever the answer; not in a build with one theme (it is offered when the themes arrive) nor while
/// NIXIE_LAYOUT forces a theme (the harness's runs).
/// </summary>
public static class ChooseLookStep
{
	public const string Title = "Choose a look";

	public const string Message = $"Pick how {Core.AppIdentity.Name} lays out your library. You can change it any time in Settings > Appearance.";

	public const string UseText = "Use this look";

	public const string SkipText = "Skip";

	/// <summary>Whether to offer the step now.</summary>
	public static bool ShouldOffer(bool notOfferedYet, int themeCount, bool layoutForced) => notOfferedYet && themeCount > 1 && !layoutForced;

	/// <summary>Offers the step over the window when it is due; returns the theme chosen, null when skipped or not offered.</summary>
	public static async Task<Themes.LayoutThemeId?> OfferAsync(XamlRoot? root)
	{
		var themes = App.GetService<ThemeService>();
		if (root is null || !ShouldOffer(themes.ShouldOfferChooseLook, themes.Themes.Count, themes.Override is not null))
		{
			return null;
		}

		themes.ChooseLookOffered();
		var picker = new ThemePickerViewModel(themes, apply: null, preselect: themes.ChooseLookPreselect);
		var tiles = new ThemePicker { ViewModel = picker };
		var content = new StackPanel { Spacing = 12 };
		content.Children.Add(new TextBlock { Text = Message, TextWrapping = TextWrapping.WrapWholeWords });
		content.Children.Add(tiles);

		var dialog = new ContentDialog
		{
			Title = Title,
			Content = content,
			PrimaryButtonText = UseText,
			CloseButtonText = SkipText,
			DefaultButton = ContentDialogButton.Primary,
			XamlRoot = root,
		};
		if (ButtonRoles.Find("NixieDialogStyle") is { } style)
		{
			dialog.Style = style;
		}

		// four tiles in a row
		GlassDialog.SetPanelMaxWidth(dialog, GlassDialog.PagePanelMaxWidth);
		dialog.Resources["ContentDialogMaxWidth"] = GlassDialog.PagePanelMaxWidth + 48;
		if (root.Content is FrameworkElement window)
		{
			dialog.RequestedTheme = window.ActualTheme;
		}

		dialog.Opened += (_, _) => tiles.FocusSelected();
		try
		{
			var result = await dialog.ShowAsync();
			if (result == ContentDialogResult.Primary && picker.Selected is { } choice)
			{
				await App.GetService<ThemeSwitcher>().SwitchAsync(choice.Id);
				return choice.Id;
			}

			return null;
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
		{
			// another dialog is open (only one can be): offered next time
			App.GetService<Core.Contracts.Services.ILocalSettingsService>().SaveSetting(ThemeChoice.ChooseLookOfferedKey, false);
			return null;
		}
		finally
		{
			picker.Detach();
		}
	}
}
