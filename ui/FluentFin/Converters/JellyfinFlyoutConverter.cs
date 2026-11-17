using FluentFin.Core;
using FluentFin.Dialogs;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace FluentFin.Converters;

/// <summary>
/// An item's More menu. Seaglass 1.1.3: its entries come from <see cref="ItemMenu"/> (every one does something, the
/// administrative ones only for administrators); Delete asks first, naming the item.
/// </summary>
public partial class JellyfinFlyoutConverter : IValueConverter
{
	/// <summary>
	/// Seaglass 1.1.3: cards set this. A card is one keyboard stop and its menu is the keyboard's way to play it; item pages
	/// and episode rows have a Play button of their own, so their menus do not repeat it.
	/// </summary>
	public bool IncludePlay { get; set; }

	public object? Convert(object value, Type targetType, object parameter, string language)
	{
		if (value is not BaseItemDto dto)
		{
			return null;
		}

		var groups = ItemMenu.For(dto, SessionInfo.CurrentUser, IncludePlay, CanEditSegments());
		if (groups.Count == 0)
		{
			return null;
		}

		var flyout = new MenuBarItemFlyout();
		foreach (var group in groups)
		{
			if (flyout.Items.Count > 0)
			{
				flyout.Items.Add(new MenuFlyoutSeparator());
			}

			foreach (var entry in group)
			{
				flyout.Items.Add(Make(entry, dto));
			}
		}

		return flyout;
	}

	public object ConvertBack(object value, Type targetType, object parameter, string language)
	{
		throw new NotSupportedException();
	}

	// an administrator signed in before the server's plugins were read has no list yet
	private static bool CanEditSegments() => SessionInfo.CurrentUser?.Policy?.IsAdministrator == true && SessionInfo.Plugins is not null && SessionInfo.CanEditMediaSegments();

	private static MenuFlyoutItem Make(ItemMenuEntry entry, BaseItemDto dto)
	{
		var item = new MenuFlyoutItem { Text = entry.Text };
		switch (entry.Command)
		{
			case ItemMenuCommand.Play:
				item.Icon = new SymbolIcon { Symbol = Symbol.Play };
				item.Command = App.Commands.PlayDtoCommand;
				item.CommandParameter = dto;
				break;
			case ItemMenuCommand.ToggleWatched:
				item.Icon = new FontIcon { Glyph = "" };
				item.Command = App.Commands.ToggleWatchedCommand;
				item.CommandParameter = dto;
				break;
			case ItemMenuCommand.ToggleFavorite:
				item.Icon = new FontIcon { Glyph = "" };
				item.Command = App.Commands.ToggleFavoriteCommand;
				item.CommandParameter = dto;
				break;
			case ItemMenuCommand.ResetWatchProgress:
				item.Icon = new FontIcon { Glyph = "" };
				item.Command = App.Commands.ResetWatchProgressCommand;
				item.CommandParameter = dto.Id;
				break;
			case ItemMenuCommand.MediaInfo:
				item.Icon = new SymbolIcon { Symbol = Symbol.MusicInfo };
				item.Command = App.Dialogs.MediaInfoDialogCommand;
				item.CommandParameter = dto;
				break;
			case ItemMenuCommand.EditMetadata:
				item.Icon = new SymbolIcon { Symbol = Symbol.Edit };
				item.Command = App.Dialogs.EditMetadataDialogCommand;
				item.CommandParameter = dto;
				break;
			case ItemMenuCommand.EditImages:
				item.Icon = new FontIcon { Glyph = "" };
				item.Command = App.Dialogs.EditImagesDialogCommand;
				item.CommandParameter = dto;
				break;
			case ItemMenuCommand.EditSegments:
				item.Icon = new FontIcon { Glyph = "" };
				item.Command = App.Commands.NavigateToSegmentsEditorCommand;
				item.CommandParameter = dto;
				break;
			case ItemMenuCommand.EditSubtitles:
				item.Icon = new SymbolIcon { Symbol = Symbol.ClosedCaption };
				item.Command = App.Dialogs.EditSubtitlesDialogCommand;
				item.CommandParameter = dto;
				break;
			case ItemMenuCommand.Identify:
				item.Icon = new SymbolIcon { Symbol = Symbol.Edit };
				item.Command = App.Dialogs.IdentifyDialogCommand;
				item.CommandParameter = dto;
				break;
			case ItemMenuCommand.RefreshMetadata:
				item.Icon = new SymbolIcon { Symbol = Symbol.Refresh };
				item.Command = App.Dialogs.RefreshMetadataDialogCommand;
				item.CommandParameter = dto.Id;
				break;
			case ItemMenuCommand.Delete:
				item.Icon = new SymbolIcon { Symbol = Symbol.Delete };
				item.Click += async (sender, _) =>
				{
					if (await ConfirmDialog.AskAsync((sender as UIElement)?.XamlRoot, ItemMenu.DeletePrompt(dto)))
					{
						await App.Commands.DeleteItemCommand.ExecuteAsync(dto);
					}
				};
				break;
		}

		return item;
	}
}
