using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.Journal;

/// <summary>What the Diary page's dialogs and pickers return (the page implements them; the tests fake them).</summary>
public interface IJournalDialogs
{
	/// <summary>The entry edited, with the episode's rating (0-10); null when cancelled.</summary>
	Task<DiaryEditResult?> EditAsync(DiaryEntryViewModel entry);

	Task<bool> ConfirmDeleteAsync(DiaryEntryViewModel entry);

	/// <summary>A file to write the export to; null when cancelled.</summary>
	Task<string?> PickExportFileAsync(string suggestedName, string extension);
}

public sealed record DiaryEditResult(DiaryEntryEdit Edit, int Rating);

/// <summary>
/// The Diary page (docs/JOURNAL.md): the current user's entries grouped by month, newest first, filtered by year and show;
/// edit, delete, export, sync, and the journal's settings. Refreshes itself when the journal changes (an episode finished,
/// a sync added entries) while the page is open.
/// </summary>
public sealed partial class DiaryViewModel(JournalService journal, JournalSync sync, INavigationServiceCore navigation, ILogger<DiaryViewModel> logger)
	: ObservableObject, INavigationAware
{
	public static readonly DiaryFilterOption AllYears = new("All years");
	public static readonly DiaryFilterOption AllShows = new("All shows");

	private IReadOnlyList<DiaryEntry> _entries = [];
	private IReadOnlyDictionary<Guid, ItemRating> _ratings = new Dictionary<Guid, ItemRating>();
	private IDisposable? _changes;
	private SynchronizationContext? _context;
	private bool _loading;

	/// <summary>Set by the page (it owns the XAML root the dialogs open on). Without it, edit, delete and export do nothing.</summary>
	public IJournalDialogs? Dialogs { get; set; }

	/// <summary>The time zone months and days are counted in (the user's; tests set one).</summary>
	public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Local;

	public CultureInfo Culture { get; set; } = CultureInfo.CurrentCulture;

	[ObservableProperty]
	public partial IReadOnlyList<DiaryMonthGroup> Groups { get; set; } = [];

	[ObservableProperty]
	public partial IReadOnlyList<DiaryFilterOption> Years { get; set; } = [AllYears];

	[ObservableProperty]
	public partial DiaryFilterOption SelectedYear { get; set; } = AllYears;

	[ObservableProperty]
	public partial IReadOnlyList<DiaryFilterOption> Shows { get; set; } = [AllShows];

	[ObservableProperty]
	public partial DiaryFilterOption SelectedShow { get; set; } = AllShows;

	[ObservableProperty]
	public partial bool HasEntries { get; set; }

	[ObservableProperty]
	public partial bool IsEmpty { get; set; }

	[ObservableProperty]
	public partial string EmptyText { get; set; } = "";

	[ObservableProperty]
	public partial string SummaryText { get; set; } = "";

	[ObservableProperty]
	public partial string Heading { get; set; } = "";

	[ObservableProperty]
	public partial bool IsSyncing { get; set; }

	[ObservableProperty]
	public partial string StatusText { get; set; } = "";

	[ObservableProperty]
	public partial string? ErrorText { get; set; }

	[ObservableProperty]
	public partial bool HasError { get; set; }

	[ObservableProperty]
	public partial bool AutoLog { get; set; } = true;

	[ObservableProperty]
	public partial bool MirrorRating { get; set; }

	public async Task OnNavigatedTo(object parameter)
	{
		_context = SynchronizationContext.Current;
		_changes?.Dispose();
		_changes = journal.Changed.Subscribe(profile =>
		{
			if (profile == journal.CurrentProfile)
			{
				Post(() => _ = LoadAsync());
			}
		});

		await LoadAsync();
	}

	public Task OnNavigatedFrom()
	{
		_changes?.Dispose();
		_changes = null;
		return Task.CompletedTask;
	}

	private void Post(Action action)
	{
		if (_context is null || _context == SynchronizationContext.Current)
		{
			action();
		}
		else
		{
			_context.Post(_ => action(), null);
		}
	}

	/// <summary>Reads the journal again and shows it.</summary>
	public async Task LoadAsync()
	{
		var profile = journal.CurrentProfile;
		if (profile is null)
		{
			_entries = [];
			Heading = "";
			ShowError("Sign in to see your diary.");
			Rebuild();
			return;
		}

		try
		{
			var (entries, ratings, settings, state) = await Task.Run(() => (journal.GetEntries(), journal.GetRatings(), journal.GetSettings(), journal.Store.GetSyncState(profile)));
			_entries = entries;
			_ratings = ratings;
			_loading = true;
			AutoLog = settings.AutoLog;
			MirrorRating = settings.MirrorRatingToJellyfin;
			_loading = false;
			Heading = profile.UserName is { Length: > 0 } user ? $"{user}{(profile.ServerName is { Length: > 0 } server ? $" on {server}" : "")}" : "";
			StatusText = Describe(state);
			ShowError(state.LastError is { } error ? $"The last sync did not finish: {error}." : null);
		}
		catch (Exception ex) when (ex is JournalUnavailableException or Microsoft.Data.Sqlite.SqliteException)
		{
			// the store turns every SQLite failure into JournalUnavailableException; a raw one must not blank the page either
			_entries = [];
			ShowError(ex is JournalUnavailableException ? ex.Message : $"The journal could not be read: {ex.Message}");
		}

		Rebuild();
	}

	private void ShowError(string? text)
	{
		ErrorText = text;
		HasError = !string.IsNullOrEmpty(text);
	}

	private string Describe(JournalSyncState state)
	{
		if (state.LastSyncAt is not { } last)
		{
			return IsSyncing ? "Reading your watch history from Jellyfin…" : "Not synced with Jellyfin yet.";
		}

		var dates = state.PlaybackReportingAvailable == true
			? "dates of plays in other apps come from Playback Reporting"
			: "dates of plays in other apps are approximate (Jellyfin keeps only the last one)";
		return $"Synced {TimeZoneInfo.ConvertTime(last, TimeZone).ToString("g", Culture)}; {dates}.";
	}

	partial void OnSelectedYearChanged(DiaryFilterOption value)
	{
		// null comes from a combo box clearing its selection, never from the user: "All" then
		if (!_loading && value is not null)
		{
			Rebuild();
		}
	}

	partial void OnSelectedShowChanged(DiaryFilterOption value)
	{
		if (!_loading && value is not null)
		{
			Rebuild();
		}
	}

	partial void OnAutoLogChanged(bool value) => SaveSettings(x => x with { AutoLog = value });

	partial void OnMirrorRatingChanged(bool value) => SaveSettings(x => x with { MirrorRatingToJellyfin = value });

	/// <summary>The settings changes made so far are saved when this completes (they are saved in order, off the UI thread).</summary>
	public Task SettingsSaved { get; private set; } = Task.CompletedTask;

	private void SaveSettings(Func<JournalSettings, JournalSettings> change)
	{
		if (_loading)
		{
			return;
		}

		SettingsSaved = SettingsSaved.ContinueWith(_ =>
		{
			try
			{
				journal.SaveSettings(change(journal.GetSettings()));
			}
			catch (JournalUnavailableException ex)
			{
				Post(() => ShowError(ex.Message));
			}
		}, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
	}

	/// <summary>Builds the filters and the month groups from what was read.</summary>
	private void Rebuild()
	{
		var views = _entries.Select(x => new DiaryEntryViewModel(x, _ratings.TryGetValue(x.ItemId, out var r) ? r.Value : 0, journal.CurrentProfile?.ServerUrl, TimeZone, Culture)).ToList();

		// the selection is taken first: a combo box whose list is replaced may hand back null for its selected item
		var year = SelectedYear ?? AllYears;
		var show = SelectedShow ?? AllShows;
		_loading = true;
		try
		{
			List<DiaryFilterOption> years =
			[
				AllYears,
				.. views.Where(x => x.Local is not null).Select(x => x.Local!.Value.Year).Distinct().OrderDescending()
					.Select(y => new DiaryFilterOption(y.ToString(CultureInfo.InvariantCulture), Year: y)),
			];
			if (!years.SequenceEqual(Years))
			{
				Years = years;
			}

			year = Years.FirstOrDefault(x => x == year) ?? AllYears;
			SelectedYear = year;

			List<DiaryFilterOption> shows =
			[
				AllShows,
				.. views.GroupBy(x => JournalText.ShowKey(x.Entry))
					.Select(g => new DiaryFilterOption(g.First().SeriesName, ShowKey: g.Key))
					.OrderBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase),
			];
			if (!shows.SequenceEqual(Shows))
			{
				Shows = shows;
			}

			show = Shows.FirstOrDefault(x => x == show) ?? AllShows;
			SelectedShow = show;
		}
		finally
		{
			_loading = false;
		}

		var shown = views
			.Where(x => year.Year is not { } y || x.Local?.Year == y)
			.Where(x => show.ShowKey is not { } key || JournalText.ShowKey(x.Entry) == key)
			.ToList();

		var groups = shown.Where(x => x.Local is not null)
			.GroupBy(x => (x.Local!.Value.Year, x.Local.Value.Month))
			.OrderByDescending(g => g.Key)
			.Select(g => new DiaryMonthGroup(new DateTime(g.Key.Year, g.Key.Month, 1).ToString("Y", Culture), g))
			.ToList();
		var undated = shown.Where(x => x.Local is null).ToList();
		if (undated.Count > 0)
		{
			groups.Add(new DiaryMonthGroup("Date unknown", undated));
		}

		Groups = groups;

		HasEntries = shown.Count > 0;
		IsEmpty = !HasEntries;
		EmptyText = _entries.Count == 0
			? "Nothing in your diary yet. Episodes you finish in the MPV player are logged here, and a sync brings in what Jellyfin has as played."
			: "No episodes match these filters.";
		SummaryText = shown.Count == 1 ? "1 episode" : $"{shown.Count} episodes";
	}

	[RelayCommand]
	private async Task Sync()
	{
		if (IsSyncing)
		{
			return;
		}

		IsSyncing = true;
		StatusText = "Syncing with Jellyfin…";
		try
		{
			var result = await sync.SyncAsync();
			if (!result.Completed)
			{
				logger.LogInformation("Journal sync from the Diary page: {Result}", result);
			}
		}
		finally
		{
			IsSyncing = false;
		}

		await LoadAsync();
	}

	[RelayCommand]
	private async Task Edit(DiaryEntryViewModel? entry)
	{
		if (entry is null || Dialogs is null)
		{
			return;
		}

		if (await Dialogs.EditAsync(entry) is not { } result)
		{
			return;
		}

		try
		{
			await Task.Run(async () =>
			{
				journal.UpdateEntry(entry.Id, result.Edit);
				if (StarRating.Normalize(result.Rating) != entry.Rating)
				{
					await journal.SetRatingAsync(new RatedItem(entry.Entry.ItemId, RatedItemKind.Episode, entry.Entry.SeriesId, entry.Entry.Title), result.Rating);
				}
			});
		}
		catch (JournalUnavailableException ex)
		{
			ShowError(ex.Message);
		}

		await LoadAsync();
	}

	[RelayCommand]
	private async Task Delete(DiaryEntryViewModel? entry)
	{
		if (entry is null || Dialogs is null || !await Dialogs.ConfirmDeleteAsync(entry))
		{
			return;
		}

		try
		{
			await Task.Run(() => journal.DeleteEntry(entry.Id));
		}
		catch (JournalUnavailableException ex)
		{
			ShowError(ex.Message);
		}

		await LoadAsync();
	}

	[RelayCommand]
	private void Open(DiaryEntryViewModel? entry)
	{
		if (entry is not null)
		{
			navigation.NavigateTo<EpisodeViewModel>(new BaseItemDto { Id = entry.Entry.ItemId, Type = BaseItemDto_Type.Episode });
		}
	}

	[RelayCommand]
	private Task ExportJson() => ExportAsync(".json", (profile, entries, ratings) =>
		JournalExport.ToJson(profile, entries, ratings, journal.Clock.GetUtcNow()), bom: false);

	[RelayCommand]
	private Task ExportCsv() => ExportAsync(".csv", (_, entries, ratings) =>
		JournalExport.ToCsv(entries, ratings, TimeZone), bom: true);

	private async Task ExportAsync(string extension, Func<JournalProfile, IReadOnlyList<DiaryEntry>, IReadOnlyList<ItemRating>, string> write, bool bom)
	{
		if (Dialogs is null || journal.CurrentProfile is not { } profile)
		{
			return;
		}

		var name = $"diary-{TimeZoneInfo.ConvertTime(journal.Clock.GetUtcNow(), TimeZone):yyyy-MM-dd}";
		if (await Dialogs.PickExportFileAsync(name, extension) is not { Length: > 0 } path)
		{
			return;
		}

		try
		{
			var count = await Task.Run(async () =>
			{
				var entries = journal.Store.GetEntries(profile);
				// a spreadsheet needs the BOM to read UTF-8; JSON must not have one
				await File.WriteAllTextAsync(path, write(profile, entries, journal.Store.GetRatings(profile)), new UTF8Encoding(bom));
				return entries.Count;
			});
			StatusText = $"Exported {count} {(count == 1 ? "entry" : "entries")} to {Path.GetFileName(path)}.";
			ShowError(null);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JournalUnavailableException)
		{
			ShowError($"The export could not be written: {ex.Message}");
		}
	}
}
