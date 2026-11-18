using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FluentFin.Core;
using FluentFin.Core.ViewModels;
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

	/// <summary>
	/// Seaglass 1.1.4 (layout audit, item 23): Mark as watched and the favorite in the menu (cards and episode rows). An item
	/// page sets it false: its Watched and Favorite buttons are beside its More button.
	/// </summary>
	public bool IncludeToggles { get; set; } = true;

	public object? Convert(object value, Type targetType, object parameter, string language)
	{
		if (value is not BaseItemDto dto)
		{
			return null;
		}

		if (Groups(dto, IncludePlay, IncludeToggles).Count == 0)
		{
			return null;
		}

		var flyout = new MenuBarItemFlyout();
		var includePlay = IncludePlay;
		var includeToggles = IncludeToggles;
		Fill(flyout, dto, includePlay, includeToggles);
		// Seaglass 1.1.4 (interaction audit, item 3): made again each time it opens, from the item as it is then ("Mark as
		// unwatched" once it was marked, "Remove from favorites" once added): it was made once, when the card was
		flyout.Opening += (sender, _) =>
		{
			if (sender is MenuFlyout opening)
			{
				Fill(opening, dto, includePlay, includeToggles);
			}
		};
		return flyout;
	}

	private static void Fill(MenuFlyout flyout, BaseItemDto dto, bool includePlay, bool includeToggles)
	{
		flyout.Items.Clear();
		foreach (var group in Groups(dto, includePlay, includeToggles))
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

	// Seaglass 1.1.4: a card of Home's Continue Watching row (its item record is registered by the row) offers to take it out
	private static IReadOnlyList<IReadOnlyList<ItemMenuEntry>> Groups(BaseItemDto dto, bool includePlay, bool includeToggles = true) =>
		ItemMenu.For(dto, SessionInfo.CurrentUser, includePlay, CanEditSegments(), inContinueWatching: includePlay && ContinueWatchingRows.RowOf(dto) is not null,
			includeToggles: includeToggles);

	// an administrator signed in before the server's plugins were read has no list yet
	private static bool CanEditSegments() => SessionInfo.CurrentUser?.Policy?.IsAdministrator == true && SessionInfo.Plugins is not null && SessionInfo.CanEditMediaSegments();

	private static MenuFlyoutItem Make(ItemMenuEntry entry, BaseItemDto dto)
	{
		var item = new MenuFlyoutItem { Text = entry.Text, Icon = IconFor(entry.Command, dto) };
		// 1.2.0 (the button review, L46): Delete in the danger ink (it is the last entry, after a separator)
		if (entry.Command is ItemMenuCommand.Delete && Application.Current?.Resources.TryGetValue("NixieDangerForegroundBrush", out var danger) == true && danger is Microsoft.UI.Xaml.Media.Brush ink)
		{
			item.Foreground = ink;
		}
		item.Click += async (sender, _) =>
		{
			var root = (sender as UIElement)?.XamlRoot;
			await ItemMenu.RunAsync(entry, dto, prompt => ConfirmDialog.AskAsync(root, prompt), Run);
		};

		return item;
	}

	internal static string? GlyphFor(ItemMenuCommand command, BaseItemDto? dto) => command switch
	{
		// 1.2.0 (the button review): favourites add / remove by state (L36), Reset is "play from start" (L38), Media info's own (L39)
		ItemMenuCommand.ToggleFavorite => dto?.UserData?.IsFavorite == true ? "" : "",
		ItemMenuCommand.ResetWatchProgress => "",
		ItemMenuCommand.MediaInfo => "",
		_ => null,
	};

	private static IconElement IconFor(ItemMenuCommand command, BaseItemDto dto) => GlyphFor(command, dto) is { } glyph ? new FontIcon { Glyph = glyph } : command switch
	{
		ItemMenuCommand.Play => new SymbolIcon { Symbol = Symbol.Play },
		ItemMenuCommand.ToggleWatched => new FontIcon { Glyph = "" },
		ItemMenuCommand.RemoveFromContinueWatching => new FontIcon { Glyph = "" },
		ItemMenuCommand.EditMetadata => new SymbolIcon { Symbol = Symbol.Edit },
		ItemMenuCommand.EditImages => new FontIcon { Glyph = "" },
		ItemMenuCommand.EditSegments => new FontIcon { Glyph = "" },
		ItemMenuCommand.EditSubtitles => new SymbolIcon { Symbol = Symbol.ClosedCaption },
		ItemMenuCommand.Identify => new SymbolIcon { Symbol = Symbol.Find }, // its own icon (layout audit, item 13): Edit metadata has the pencil
		ItemMenuCommand.RefreshMetadata => new SymbolIcon { Symbol = Symbol.Refresh },
		ItemMenuCommand.Delete => new SymbolIcon { Symbol = Symbol.Delete },
		_ => new FontIcon { Glyph = "" },
	};

	// Seaglass 1.1.4: the row the card is in takes it out (at once, with an Undo on Home's notice)
	private static readonly AsyncRelayCommand<BaseItemDto> s_removeFromContinueWatching =
		new(dto => dto is null ? Task.CompletedTask : ContinueWatchingRows.RowOf(dto)?.Remove(dto) ?? Task.CompletedTask,
			AsyncRelayCommandOptions.AllowConcurrentExecutions); // two cards taken out in a row: the second does not wait for the first's answer

	// what each entry does once ItemMenu.RunAsync lets it run (after the yes, for Delete)
	private static Task Run(ItemMenuCommand command, BaseItemDto dto)
	{
		(ICommand? Action, object? Parameter) target = command switch
		{
			ItemMenuCommand.Play => (App.Commands.PlayDtoCommand, dto),
			ItemMenuCommand.ToggleWatched => (App.Commands.ToggleWatchedCommand, dto),
			ItemMenuCommand.ToggleFavorite => (App.Commands.ToggleFavoriteCommand, dto),
			ItemMenuCommand.ResetWatchProgress => (App.Commands.ResetWatchProgressCommand, dto.Id),
			ItemMenuCommand.RemoveFromContinueWatching => (s_removeFromContinueWatching, dto),
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
