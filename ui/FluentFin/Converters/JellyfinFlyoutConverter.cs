using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FluentFin.Core;
using FluentFin.Dialogs;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace FluentFin.Converters;

/// <summary>
/// An item's More menu. Seaglass 1.1.3: its entries come from <see cref="ItemMenu"/> (every one does something, the
/// administrative ones only for administrators), and every entry runs through <see cref="ItemMenu.RunAsync"/>, so Delete
/// asks first, naming the item. No entry is wired to a command directly.
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

		var groups = Groups(dto, IncludePlay);
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

	/// <summary>
	/// Seaglass 1.1.3: a card's More button shows only when its menu has something (a collection or a person has nothing for
	/// someone who is not an administrator), so it never opens nothing.
	/// </summary>
	public static Visibility CardMenuVisibility(BaseItemDto? dto) =>
		dto is not null && Groups(dto, includePlay: true).Count > 0 ? Visibility.Visible : Visibility.Collapsed;

	private static IReadOnlyList<IReadOnlyList<ItemMenuEntry>> Groups(BaseItemDto dto, bool includePlay) =>
		ItemMenu.For(dto, SessionInfo.CurrentUser, includePlay, CanEditSegments());

	// an administrator signed in before the server's plugins were read has no list yet
	private static bool CanEditSegments() => SessionInfo.CurrentUser?.Policy?.IsAdministrator == true && SessionInfo.Plugins is not null && SessionInfo.CanEditMediaSegments();

	private static MenuFlyoutItem Make(ItemMenuEntry entry, BaseItemDto dto)
	{
		var item = new MenuFlyoutItem { Text = entry.Text, Icon = IconFor(entry.Command) };
		item.Click += async (sender, _) =>
		{
			var root = (sender as UIElement)?.XamlRoot;
			await ItemMenu.RunAsync(entry, dto, prompt => ConfirmDialog.AskAsync(root, prompt), Run);
		};

		return item;
	}

	private static IconElement IconFor(ItemMenuCommand command) => command switch
	{
		ItemMenuCommand.Play => new SymbolIcon { Symbol = Symbol.Play },
		ItemMenuCommand.ToggleWatched => new FontIcon { Glyph = "" },
		ItemMenuCommand.ToggleFavorite => new FontIcon { Glyph = "" },
		ItemMenuCommand.ResetWatchProgress => new FontIcon { Glyph = "" },
		ItemMenuCommand.MediaInfo => new SymbolIcon { Symbol = Symbol.MusicInfo },
		ItemMenuCommand.EditMetadata => new SymbolIcon { Symbol = Symbol.Edit },
		ItemMenuCommand.EditImages => new FontIcon { Glyph = "" },
		ItemMenuCommand.EditSegments => new FontIcon { Glyph = "" },
		ItemMenuCommand.EditSubtitles => new SymbolIcon { Symbol = Symbol.ClosedCaption },
		ItemMenuCommand.Identify => new SymbolIcon { Symbol = Symbol.Edit },
		ItemMenuCommand.RefreshMetadata => new SymbolIcon { Symbol = Symbol.Refresh },
		ItemMenuCommand.Delete => new SymbolIcon { Symbol = Symbol.Delete },
		_ => new FontIcon { Glyph = "" },
	};

	// what each entry does once ItemMenu.RunAsync lets it run (after the yes, for Delete)
	private static Task Run(ItemMenuCommand command, BaseItemDto dto)
	{
		(ICommand? Action, object? Parameter) target = command switch
		{
			ItemMenuCommand.Play => (App.Commands.PlayDtoCommand, dto),
			ItemMenuCommand.ToggleWatched => (App.Commands.ToggleWatchedCommand, dto),
			ItemMenuCommand.ToggleFavorite => (App.Commands.ToggleFavoriteCommand, dto),
			ItemMenuCommand.ResetWatchProgress => (App.Commands.ResetWatchProgressCommand, dto.Id),
			ItemMenuCommand.MediaInfo => (App.Dialogs.MediaInfoDialogCommand, dto),
			ItemMenuCommand.EditMetadata => (App.Dialogs.EditMetadataDialogCommand, dto),
			ItemMenuCommand.EditImages => (App.Dialogs.EditImagesDialogCommand, dto),
			ItemMenuCommand.EditSegments => (App.Commands.NavigateToSegmentsEditorCommand, dto),
			ItemMenuCommand.EditSubtitles => (App.Dialogs.EditSubtitlesDialogCommand, dto),
			ItemMenuCommand.Identify => (App.Dialogs.IdentifyDialogCommand, dto),
			ItemMenuCommand.RefreshMetadata => (App.Dialogs.RefreshMetadataDialogCommand, dto.Id),
			ItemMenuCommand.Delete => (App.Commands.DeleteItemCommand, dto),
			_ => (null, null),
		};
		var (action, parameter) = target;

		if (action is IAsyncRelayCommand asyncAction)
		{
			return asyncAction.CanExecute(parameter) ? asyncAction.ExecuteAsync(parameter) : Task.CompletedTask;
		}

		if (action?.CanExecute(parameter) == true)
		{
			action.Execute(parameter);
		}

		return Task.CompletedTask;
	}
}
