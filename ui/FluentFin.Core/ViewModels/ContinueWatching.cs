using System.Runtime.CompilerServices;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Kiota.Serialization.Json;

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
	/// less what has not been played for <paramref name="hideAfterDays"/> days. sg-cw: "marked watched" is watched with no
	/// resume point: Jellyfin 12 keeps an episode watched while it is watched again (10.11 unmarks it at the start), so a
	/// show being rewatched is watched and in progress at once, and stays.
	/// </summary>
	/// <param name="includeRewatching">1.5: Settings' "Include shows I'm rewatching"; off, an item marked watched is left out even
	/// with a resume point (the rewatch case), as before 1.4.1.</param>
	public static IReadOnlyList<BaseItemDto> Visible(IReadOnlyList<BaseItemDto> answer, DateTimeOffset now, int hideAfterDays, bool includeRewatching = true) =>
		answer.Any(x => IsWatchedNotInProgress(x, includeRewatching) || IsStale(x, now, hideAfterDays))
			? [.. answer.Where(x => !IsWatchedNotInProgress(x, includeRewatching) && !IsStale(x, now, hideAfterDays))]
			: answer;

	/// <summary>
	/// sg-cw: marked watched and with no resume point (a watched item being watched again has one: Jellyfin 12). 1.5: with
	/// <paramref name="includeRewatching"/> off, any item marked watched.
	/// </summary>
	public static bool IsWatchedNotInProgress(BaseItemDto dto, bool includeRewatching = true) =>
		dto.UserData?.Played == true && (!includeRewatching || dto.UserData.PlaybackPositionTicks is null or <= 0);

	/// <summary>
	/// sg-cw: the server's resume list with the plays the mpv player just ended (and the server's answer does not have yet) put
	/// in: one that left a resume point at the front, at its new place (its card made from <paramref name="recordOf"/>'s record
	/// when the list has none; skipped without one); one watched, or stopped before 5 %, out. A play the rules cannot tell
	/// (<see cref="PlayGuess.Unknown"/>) changes nothing. The list itself is the server's answer, unchanged (the saved page).
	/// </summary>
	public static IReadOnlyList<BaseItemDto> WithPlays(IReadOnlyList<BaseItemDto> answer, IEnumerable<RecentPlay> plays, Func<Guid, BaseItemDto?> recordOf)
	{
		List<BaseItemDto>? list = null;
		foreach (var play in plays.OrderBy(x => x.At))
		{
			switch (play.Guess)
			{
				case PlayGuess.Resumable when ((list ?? answer).FirstOrDefault(x => x.Id == play.ItemId) ?? recordOf(play.ItemId)) is { } record:
					list ??= [.. answer];
					list.RemoveAll(x => x.Id == play.ItemId);
					list.Insert(0, WithPosition(record, play));
					break;
				case PlayGuess.Watched or PlayGuess.NoResumePoint when (list ?? answer).Any(x => x.Id == play.ItemId):
					list ??= [.. answer];
					list.RemoveAll(x => x.Id == play.ItemId);
					break;
			}
		}

		return list ?? answer;
	}

	/// <summary>sg-cw: Next Up without the episodes the mpv player just played to the end (the server's answer has the next ones once it has the play).</summary>
	public static IReadOnlyList<BaseItemDto> NextUpWithoutPlays(IReadOnlyList<BaseItemDto> nextUp, IEnumerable<RecentPlay> plays)
	{
		var watched = plays.Where(x => x.Guess == PlayGuess.Watched).Select(x => x.ItemId).ToHashSet();
		return watched.Count == 0 || !nextUp.Any(x => x.Id is { } id && watched.Contains(id)) ? nextUp : [.. nextUp.Where(x => x.Id is not { } id || !watched.Contains(id))];
	}

	/// <summary>A copy of the item's record (never the server's own: the saved page and the card compare them) at the play's position.</summary>
	internal static BaseItemDto WithPosition(BaseItemDto dto, RecentPlay play)
	{
		using var writer = new JsonSerializationWriter();
		writer.WriteObjectValue(null, dto);
		using var document = JsonDocument.Parse(writer.GetSerializedContent());
		var copy = new JsonParseNode(document.RootElement).GetObjectValue(BaseItemDto.CreateFromDiscriminatorValue) ?? new BaseItemDto { Id = dto.Id, Name = dto.Name, Type = dto.Type };
		copy.UserData ??= new UserItemDataDto { ItemId = play.ItemId };
		copy.UserData.PlaybackPositionTicks = play.PositionTicks;
		copy.UserData.PlayedPercentage = play.Percent;
		copy.UserData.LastPlayedDate = play.At;
		return copy;
	}

	/// <summary>
	/// Next Up without what Continue Watching shows: a server older than Jellyfin 10.9 ignores <c>enableResumable=false</c> and
	/// lists the episode in progress in both rows. sg-cw (the review of wip/continue-watching): one show once on Home. Next
	/// Up asked with rewatching lists the episode after the one watched again too (QA servers: 12.4 put S1 E1 in Continue
	/// Watching and S1 E2 up next; with S1 E2 played again to the end, both servers listed S1 E3 and S2 E2), so Next Up leaves
	/// out a show Continue Watching has, and keeps one episode of a show: the server's first, the one watched toward most
	/// recently (it sorts them by when their show was last watched), as the hero keeps one slide per show (HeroSlide.Key).
	/// </summary>
	public static IReadOnlyList<BaseItemDto> NextUpWithout(IReadOnlyList<BaseItemDto> nextUp, IEnumerable<BaseItemDto> continueWatching)
	{
		var items = new HashSet<Guid>();
		var shows = new HashSet<Guid>();
		foreach (var dto in continueWatching)
		{
			if (dto.Id is { } id)
			{
				items.Add(id);
			}

			if (dto.SeriesId is { } series)
			{
				shows.Add(series);
			}
		}

		var listed = new HashSet<Guid>();
		bool Keep(BaseItemDto dto) =>
			!(dto.Id is { } id && items.Contains(id)) && !(dto.SeriesId is { } series && (shows.Contains(series) || !listed.Add(series)));

		var kept = nextUp.Where(Keep).ToList();
		return kept.Count == nextUp.Count ? nextUp : kept;
	}

	/// <summary>
	/// A name for the notice: the episode's show and place ("Iron Lighthouses S3 E4", the shared format of ItemLabels.EpisodeCode,
	/// which replaces it once the items stream is merged), or the item's name.
	/// </summary>
	public static string NameOf(BaseItemDto dto)
	{
		if (dto.Type == BaseItemDto_Type.Episode && !string.IsNullOrWhiteSpace(dto.SeriesName))
		{
			// the one episode format (ItemLabels.EpisodeCode: "S1 E4")
			return dto.ParentIndexNumber is not null && ItemLabels.EpisodeCode(dto) is { Length: > 0 } code
				? $"{dto.SeriesName.Trim()} {code}"
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
public sealed class ContinueRemoval(BaseItemViewModel item, BaseItemDto dto, int index, long positionTicks, Guid user = default, string server = "")
{
	/// <summary>Who removed it, on which server: an Undo after a sign-in as someone else (or elsewhere) does nothing (the review, item 4).</summary>
	public Guid User { get; } = user;

	public string Server { get; } = server;

	/// <summary>The server's message about the removal itself (position 0) has come.</summary>
	internal bool EchoSeen { get; set; }

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
	public string Header => "Hide Continue watching items not played for";

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

/// <summary>
/// 1.1.4: how long Home's notice after a removal stays: 10 s, held while the pointer or the keyboard is on it and 5 s more
/// once they leave, and never more than <see cref="HardLimit"/> from when it showed (the review, item 1: a notice held for
/// good kept an Undo that could put an old resume point back over newer progress).
/// </summary>
public static class NoticeTiming
{
	public static readonly TimeSpan Time = TimeSpan.FromSeconds(10);

	public static readonly TimeSpan AfterHold = TimeSpan.FromSeconds(5);

	public static readonly TimeSpan HardLimit = TimeSpan.FromSeconds(30);

	/// <summary>When the notice should go, counted from now: <paramref name="shownFor"/> since it showed, <paramref name="held"/> by the pointer or the keyboard, <paramref name="afterHold"/> when a hold just ended.</summary>
	public static TimeSpan Remaining(TimeSpan shownFor, bool held, bool afterHold)
	{
		var hard = HardLimit - shownFor;
		if (hard <= TimeSpan.Zero)
		{
			return TimeSpan.Zero;
		}

		if (held)
		{
			return hard;
		}

		var soft = afterHold ? AfterHold : Time - shownFor;
		return soft < hard ? (soft < TimeSpan.Zero ? TimeSpan.Zero : soft) : hard;
	}

	/// <summary>The timer is up: the notice goes unless it is held, and at the hard limit even then.</summary>
	public static bool Closes(TimeSpan shownFor, bool held) => !held || shownFor >= HardLimit;
}
