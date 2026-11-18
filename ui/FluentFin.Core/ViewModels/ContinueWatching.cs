using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

/// <summary>
/// Seaglass 1.1.4: what keeps Home's Continue Watching row clean, apart from the page so it can be tested. All of it is on
/// this PC only (nothing changes on the server), except <see cref="IContinueWatchingRow.Remove"/>, which clears the item's
/// resume point on the server: that is what puts an item in Continue Watching.
/// </summary>
public static class ContinueWatchingRules
{
	/// <summary>
	/// Settings: hide Continue Watching items not played for more than this many days (0: never, the default). Kept on this
	/// PC; the server's resume list is not changed.
	/// </summary>
	public static Key<int> HideAfterDays { get; } = new("ContinueWatchingHideAfterDays", 0);

	/// <summary>The choices Settings offers, in days (0: never).</summary>
	public static IReadOnlyList<int> HideAfterDaysChoices { get; } = [0, 30, 60, 90];

	/// <summary>What Settings says for a choice.</summary>
	public static string HideAfterDaysLabel(int days) => days <= 0 ? "Never" : $"After {days} days";

	/// <summary>The saved choice; one that is not offered (a hand-edited file) reads as the nearest choice that hides less.</summary>
	public static int ReadHideAfterDays(ILocalSettingsService? settings)
	{
		var days = settings?.ReadSetting(HideAfterDays) ?? 0;
		return days <= 0 ? 0 : HideAfterDaysChoices.Where(c => c > 0 && c >= days).DefaultIfEmpty(HideAfterDaysChoices[^1]).Min();
	}

	public static void SaveHideAfterDays(ILocalSettingsService settings, int days) =>
		settings.SaveSetting(HideAfterDays, days <= 0 ? 0 : days);

	/// <summary>Not played for more than <paramref name="days"/> days (0: nothing is). An item with no last-played date is not.</summary>
	public static bool IsStale(BaseItemDto dto, DateTimeOffset now, int days) =>
		days > 0 && dto.UserData?.LastPlayedDate is { } last && now - last > TimeSpan.FromDays(days);

	/// <summary>
	/// What the row shows of the server's resume list: all of it, less what is marked watched (a saved page, or a server's
	/// answer racing a "mark as watched": the server itself drops an item past its "max resume" share, 90 % by default), and
	/// less what has not been played for <paramref name="hideAfterDays"/> days.
	/// </summary>
	public static IReadOnlyList<BaseItemDto> Visible(IReadOnlyList<BaseItemDto> answer, DateTimeOffset now, int hideAfterDays) =>
		answer.Any(x => x.UserData?.Played == true || IsStale(x, now, hideAfterDays))
			? [.. answer.Where(x => x.UserData?.Played != true && !IsStale(x, now, hideAfterDays))]
			: answer;

	/// <summary>
	/// Next Up without what Continue Watching shows: a server older than Jellyfin 10.9 ignores <c>enableResumable=false</c> and
	/// lists the episode in progress in both rows.
	/// </summary>
	public static IReadOnlyList<BaseItemDto> NextUpWithout(IReadOnlyList<BaseItemDto> nextUp, IEnumerable<BaseItemDto> continueWatching)
	{
		var shown = continueWatching.Select(x => x.Id).OfType<Guid>().ToHashSet();
		return shown.Count == 0 ? nextUp : [.. nextUp.Where(x => x.Id is not { } id || !shown.Contains(id))];
	}

	/// <summary>
	/// A name for the notice: the episode's show and place ("Iron Lighthouses S3 E4", the shared format of ItemLabels.EpisodeCode,
	/// which replaces it once the items stream is merged), or the item's name.
	/// </summary>
	public static string NameOf(BaseItemDto dto)
	{
		if (dto.Type == BaseItemDto_Type.Episode && !string.IsNullOrWhiteSpace(dto.SeriesName))
		{
			return dto.ParentIndexNumber is { } season && dto.IndexNumber is { } episode
				? $"{dto.SeriesName.Trim()} S{season} E{episode}"
				: dto.SeriesName.Trim();
		}

		return string.IsNullOrWhiteSpace(dto.Name) ? "the item" : dto.Name.Trim();
	}
}

/// <summary>Seaglass 1.1.4: a Continue Watching row that can take an item out (Home's).</summary>
public interface IContinueWatchingRow
{
	/// <summary>Whether the row is on screen now (its page has not been left).</summary>
	bool IsShown { get; }

	/// <summary>Takes <paramref name="dto"/> out of the row at once and clears its resume point on the server, with an Undo.</summary>
	Task Remove(BaseItemDto dto);
}

/// <summary>
/// Seaglass 1.1.4: which row an item's card is in, so its menu can offer "Remove from Continue Watching" (the menu is made
/// from the card's item record alone). Keyed by the record itself, weakly: the cards of Home's row are made from records the
/// row registers; the same item's card anywhere else (a library, "New in Movies") has its own record and no such entry.
/// </summary>
public static class ContinueWatchingRows
{
	private static readonly ConditionalWeakTable<BaseItemDto, WeakReference<IContinueWatchingRow>> s_rows = new();

	public static void Register(BaseItemDto dto, IContinueWatchingRow row) => s_rows.AddOrUpdate(dto, new WeakReference<IContinueWatchingRow>(row));

	/// <summary>The row the card of <paramref name="dto"/> is in, while that row is on screen; else null.</summary>
	public static IContinueWatchingRow? RowOf(BaseItemDto? dto) =>
		dto is not null && s_rows.TryGetValue(dto, out var weak) && weak.TryGetTarget(out var row) && row.IsShown ? row : null;
}

/// <summary>
/// Seaglass 1.1.4: the short notice Home shows after a removal from Continue Watching: what happened, and an Undo while
/// <see cref="CanUndo"/> (a removal), or only the words (a removal or an undo the server did not take).
/// </summary>
public sealed partial class HomeNotice(string message, ContinueRemoval? removal) : ObservableObject
{
	public string Message { get; } = message;

	public ContinueRemoval? Removal { get; } = removal;

	public bool CanUndo => Removal is not null;
}

/// <summary>Seaglass 1.1.4: an item taken out of Continue Watching: its card, where it was, and the resume point it had.</summary>
public sealed class ContinueRemoval(BaseItemViewModel item, BaseItemDto dto, int index, long positionTicks)
{
	public BaseItemViewModel Item { get; } = item;

	public BaseItemDto Dto { get; } = dto;

	public int Index { get; } = index;

	public long PositionTicks { get; } = positionTicks;

	/// <summary>The server's answer to the removal: true when it took it.</summary>
	internal Task<bool> Done { get; set; } = Task.FromResult(true);

	/// <summary>Undone (or put back because the server did not take it): an Undo does nothing more.</summary>
	internal bool Settled { get; set; }
}

/// <summary>
/// Seaglass 1.1.4: the one Settings card for Continue Watching (the account stream wires it into Settings): "Hide items not
/// played for", a choice of <see cref="Choices"/> (Never, 30, 60, 90 days), kept on this PC; Home reads it on each visit.
/// Bind a ComboBox's ItemsSource to <see cref="Choices"/> (DisplayMemberPath="Label") and SelectedItem to <see cref="Selected"/>.
/// </summary>
public sealed partial class ContinueWatchingSettingViewModel(ILocalSettingsService settings) : ObservableObject
{
	public string Header => "Hide Continue Watching items not played for";

	public string Description => "Only on this PC: nothing changes on the server, and the items come back if you choose Never.";

	public IReadOnlyList<HideAfterChoice> Choices { get; } = [.. ContinueWatchingRules.HideAfterDaysChoices.Select(d => new HideAfterChoice(d, ContinueWatchingRules.HideAfterDaysLabel(d)))];

	public HideAfterChoice Selected
	{
		get => Choices.First(c => c.Days == ContinueWatchingRules.ReadHideAfterDays(settings));
		set
		{
			if (value is null || value.Days == ContinueWatchingRules.ReadHideAfterDays(settings))
			{
				return;
			}

			ContinueWatchingRules.SaveHideAfterDays(settings, value.Days);
			OnPropertyChanged();
		}
	}
}

/// <summary>Seaglass 1.1.4: one choice of the Continue Watching setting.</summary>
public sealed record HideAfterChoice(int Days, string Label)
{
	public override string ToString() => Label;
}
