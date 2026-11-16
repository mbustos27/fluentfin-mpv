using System.Globalization;
using FluentFin.Core.Journal;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FluentFin.Core.Tests.Journal;

public sealed class DiaryViewModelTests : IDisposable
{
	private readonly TempJournal _journal = new();
	private readonly FakeDialogs _dialogs = new();
	private readonly Mock<INavigationServiceCore> _navigation = new();
	private readonly DiaryViewModel _vm;

	public DiaryViewModelTests()
	{
		_vm = new DiaryViewModel(_journal.Service, _journal.NewSync(), _navigation.Object, NullLogger<DiaryViewModel>.Instance)
		{
			TimeZone = ManualClock.UtcMinus8,
			Culture = CultureInfo.GetCultureInfo("en-US"),
			Dialogs = _dialogs,
		};
	}

	public void Dispose() => _journal.Dispose();

	private DiaryEntry Add(string series, int season, int episode, DateTimeOffset? watched, DiaryDateSource source = DiaryDateSource.Session, Guid? seriesId = null) =>
		_journal.Store.AddEntry(TempJournal.Alice, new DiaryEntry
		{
			ItemId = Guid.NewGuid(),
			SeriesId = seriesId ?? _journal.Server.SeriesIds.GetOrAdd(series, _ => Guid.NewGuid()),
			SeriesName = series,
			SeasonNumber = season,
			EpisodeNumber = episode,
			Title = $"{series} {season}x{episode}",
			ImageTag = "still",
			WatchedAt = watched,
			DateSource = watched is null ? DiaryDateSource.Unknown : source,
			Source = DiaryEntrySource.AutoLog,
		})!;

	private static DateTimeOffset Utc(int year, int month, int day, int hour) => new(year, month, day, hour, 0, 0, TimeSpan.Zero);

	[Fact]
	public async Task Entries_AreGroupedByLocalMonth_NewestFirst_UndatedLast()
	{
		Add("Severance", 2, 5, Utc(2026, 11, 14, 20));
		// 05:30 UTC on 1 November is still 31 October here (UTC-8)
		var halloween = Add("Severance", 2, 4, Utc(2026, 11, 1, 5));
		Add("Andor", 1, 1, Utc(2025, 12, 31, 23));
		Add("Andor", 1, 2, null);

		await _vm.OnNavigatedTo(null!);

		Assert.Equal(["November 2026", "October 2026", "December 2025", "Date unknown"], _vm.Groups.Select(x => x.Title));
		Assert.All(_vm.Groups, g => Assert.Single(g));
		var view = _vm.Groups[1][0];
		Assert.Equal(halloween.Id, view.Id);
		Assert.Equal(("31", "Sat", "S02 E04"), (view.DayText, view.WeekdayText, view.EpisodeCode));
		Assert.Equal("Saturday, October 31, 2026, 9:00 PM", Plain(view.DateText));
		Assert.Equal("?", _vm.Groups[3][0].DayText);
		Assert.True(_vm.HasEntries);
		Assert.Equal("4 episodes", _vm.SummaryText);
		Assert.Equal("alice on Home", _vm.Heading);
		Assert.Equal($"http://jf.test:8096/Items/{halloween.ItemId:N}/Images/Primary?fillHeight=180&tag=still", view.ImageUrl);
	}

	[Fact]
	public async Task YearAndShowFilters_NarrowTheDiary_AndSurviveARefresh()
	{
		Add("Severance", 2, 5, Utc(2026, 11, 14, 20));
		Add("Andor", 2, 1, Utc(2026, 5, 1, 20));
		Add("Andor", 1, 1, Utc(2025, 6, 1, 20));
		await _vm.OnNavigatedTo(null!);

		Assert.Equal(["All years", "2026", "2025"], _vm.Years.Select(x => x.Label));
		Assert.Equal(["All shows", "Andor", "Severance"], _vm.Shows.Select(x => x.Label));

		_vm.SelectedYear = _vm.Years[1];
		Assert.Equal(2, _vm.Groups.Sum(x => x.Count));
		_vm.SelectedShow = _vm.Shows[1];
		Assert.Equal("Andor 2x1", Assert.Single(Assert.Single(_vm.Groups)).Title);

		Add("Andor", 2, 2, Utc(2026, 5, 2, 20));
		await _vm.LoadAsync();

		Assert.Equal("2026", _vm.SelectedYear.Label);
		Assert.Equal("Andor", _vm.SelectedShow.Label);
		Assert.Equal(2, _vm.Groups.Sum(x => x.Count));

		_vm.SelectedYear = _vm.Years[2];
		_vm.SelectedShow = _vm.Shows[2];
		Assert.True(_vm.IsEmpty);
		Assert.Equal("No episodes match these filters.", _vm.EmptyText);
	}

	[Fact]
	public async Task AComboBoxClearingItsSelection_WhenItsListIsReplaced_KeepsTheFilter()
	{
		Add("Severance", 2, 5, Utc(2026, 11, 14, 20));
		Add("Andor", 1, 1, Utc(2025, 6, 1, 20));
		await _vm.OnNavigatedTo(null!);
		_vm.SelectedYear = _vm.Years[2];
		_vm.SelectedShow = _vm.Shows[1];

		// what a WinUI ComboBox bound TwoWay does when its ItemsSource changes under it
		_vm.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(DiaryViewModel.Years))
			{
				_vm.SelectedYear = null!;
			}
			else if (e.PropertyName == nameof(DiaryViewModel.Shows))
			{
				_vm.SelectedShow = null!;
			}
		};
		Add("Andor", 1, 2, Utc(2024, 6, 1, 20));
		Add("Mrs Davis", 1, 1, Utc(2025, 7, 1, 20));
		await _vm.LoadAsync();

		Assert.Equal(("2025", "Andor"), (_vm.SelectedYear.Label, _vm.SelectedShow.Label));
		Assert.Equal("Andor 1x1", Assert.Single(Assert.Single(_vm.Groups)).Title);
	}

	[Fact]
	public async Task AnEmptyDiary_SaysWhatFillsIt()
	{
		await _vm.OnNavigatedTo(null!);

		Assert.True(_vm.IsEmpty);
		Assert.StartsWith("Nothing in your diary yet.", _vm.EmptyText);
		Assert.Equal("Not synced with Jellyfin yet.", _vm.StatusText);
	}

	[Fact]
	public async Task ApproximateDates_SayWhereTheyComeFrom()
	{
		Add("Severance", 1, 1, Utc(2026, 3, 1, 20), DiaryDateSource.LastPlayed);
		await _vm.OnNavigatedTo(null!);

		var view = _vm.Groups[0][0];
		Assert.True(view.IsApproximate);
		Assert.Equal("about Sunday, March 1, 2026", view.DateText);
		Assert.Equal("Approximate: Jellyfin's last-played date", view.DateNote);
	}

	[Fact]
	public async Task Editing_ChangesTheEntryAndTheEpisodesRating()
	{
		var entry = Add("Severance", 2, 5, Utc(2026, 11, 14, 20));
		await _vm.OnNavigatedTo(null!);
		var moved = Utc(2026, 11, 13, 4);
		_dialogs.Edit = new DiaryEditResult(new DiaryEntryEdit(moved, true, "Best of the season", true), 9);

		await _vm.EditCommand.ExecuteAsync(_vm.Groups[0][0]);

		var edited = _journal.Store.GetEntry(TempJournal.Alice, entry.Id)!;
		Assert.Equal((moved, true, "Best of the season", true, DiaryDateSource.Manual), (edited.WatchedAt!.Value, edited.Rewatch, edited.Review, edited.Spoiler, edited.DateSource));
		Assert.Equal(9, _journal.Store.GetRating(TempJournal.Alice, entry.ItemId)!.Value);
		Assert.Equal(9, _vm.Groups[0][0].Rating);
		Assert.Equal("4.5 of 5 stars", _vm.Groups[0][0].RatingText);
		// mirroring is off by default
		Assert.Empty(_journal.Server.Ratings);
	}

	[Fact]
	public async Task ACancelledEdit_ChangesNothing()
	{
		var entry = Add("Severance", 2, 5, Utc(2026, 11, 14, 20));
		await _vm.OnNavigatedTo(null!);

		await _vm.EditCommand.ExecuteAsync(_vm.Groups[0][0]);

		Assert.Equal(entry with { }, _journal.Store.GetEntry(TempJournal.Alice, entry.Id));
	}

	[Fact]
	public async Task Deleting_AsksFirst()
	{
		var entry = Add("Severance", 2, 5, Utc(2026, 11, 14, 20));
		await _vm.OnNavigatedTo(null!);

		_dialogs.ConfirmDelete = false;
		await _vm.DeleteCommand.ExecuteAsync(_vm.Groups[0][0]);
		Assert.True(_vm.HasEntries);

		_dialogs.ConfirmDelete = true;
		await _vm.DeleteCommand.ExecuteAsync(_vm.Groups[0][0]);
		Assert.True(_vm.IsEmpty);
		Assert.NotNull(_journal.Store.GetEntry(TempJournal.Alice, entry.Id)!.DeletedAt);
	}

	[Fact]
	public async Task Exports_AreWrittenWhereTheUserPicked()
	{
		Add("Severance", 2, 5, Utc(2026, 11, 14, 20));
		await _vm.OnNavigatedTo(null!);
		Directory.CreateDirectory(_journal.Folder);
		var json = Path.Combine(_journal.Folder, "out.json");
		var csv = Path.Combine(_journal.Folder, "out.csv");

		_dialogs.ExportPath = json;
		await _vm.ExportJsonCommand.ExecuteAsync(null);
		_dialogs.ExportPath = csv;
		await _vm.ExportCsvCommand.ExecuteAsync(null);

		Assert.Equal(("diary-2026-11-14", ".json"), _dialogs.Picked[0]);
		Assert.Equal(".csv", _dialogs.Picked[1].Extension);
		var jsonBytes = File.ReadAllBytes(json);
		Assert.Equal((byte)'{', jsonBytes[0]);
		Assert.Contains("\"seriesName\": \"Severance\"", File.ReadAllText(json));
		var csvBytes = File.ReadAllBytes(csv);
		Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, csvBytes[..3]);
		Assert.Equal(2, File.ReadAllText(csv).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
		Assert.Equal("Exported 1 entry to out.csv.", _vm.StatusText);

		_dialogs.ExportPath = null;
		await _vm.ExportCsvCommand.ExecuteAsync(null);
		Assert.Equal("Exported 1 entry to out.csv.", _vm.StatusText);
	}

	[Fact]
	public async Task Export_WhenTheSaveDialogCannotOpen_SaysSo_AndWritesNothing()
	{
		// visual-check: the picker threw COMException 0x80070578 (invalid window handle) and Export did nothing visible
		Add("Severance", 2, 5, Utc(2026, 11, 14, 20));
		await _vm.OnNavigatedTo(null!);
		var status = _vm.StatusText;
		_dialogs.PickError = new System.Runtime.InteropServices.COMException("Invalid window handle.", unchecked((int)0x80070578));

		await _vm.ExportCsvCommand.ExecuteAsync(null);

		Assert.True(_vm.HasError);
		Assert.Equal("The save dialog could not open, so nothing was exported. Try again.", _vm.ErrorText);
		Assert.Equal(status, _vm.StatusText);

		// the dialog opens next time and the user cancels: the message about it not opening goes, nothing is written
		_dialogs.PickError = null;
		_dialogs.ExportPath = null;
		await _vm.ExportCsvCommand.ExecuteAsync(null);
		Assert.False(_vm.HasError);
		Assert.Null(_vm.ErrorText);
		Assert.Equal(status, _vm.StatusText);

		// the next export that gets a file goes through
		_dialogs.PickError = new System.Runtime.InteropServices.COMException("Invalid window handle.", unchecked((int)0x80070578));
		await _vm.ExportCsvCommand.ExecuteAsync(null);
		Assert.True(_vm.HasError);
		_dialogs.PickError = null;
		Directory.CreateDirectory(_journal.Folder);
		_dialogs.ExportPath = Path.Combine(_journal.Folder, "after.csv");
		await _vm.ExportCsvCommand.ExecuteAsync(null);
		Assert.False(_vm.HasError);
		Assert.True(File.Exists(_dialogs.ExportPath));
	}

	[Fact]
	public async Task TheSettingsSwitches_ShowAndSaveTheJournalsSettings()
	{
		_journal.Store.SaveSettings(new JournalSettings { AutoLog = false });
		await _vm.OnNavigatedTo(null!);

		Assert.False(_vm.AutoLog);
		Assert.False(_vm.MirrorRating);
		Assert.Equal(new JournalSettings { AutoLog = false }, _journal.Store.GetSettings());

		_vm.MirrorRating = true;
		_vm.AutoLog = true;
		await _vm.SettingsSaved;

		Assert.Equal(new JournalSettings { AutoLog = true, MirrorRatingToJellyfin = true }, _journal.Store.GetSettings());
	}

	[Fact]
	public async Task AnEpisodeLoggedWhileThePageIsOpen_ShowsUp_AndNotAfterLeaving()
	{
		await _vm.OnNavigatedTo(null!);

		Add("Severance", 2, 5, Utc(2026, 11, 14, 20));
		_journal.Service.NotifyChanged(TempJournal.Alice);
		await WaitFor(() => _vm.HasEntries);

		await _vm.OnNavigatedFrom();
		Add("Severance", 2, 6, Utc(2026, 11, 14, 21));
		_journal.Service.NotifyChanged(TempJournal.Alice);
		await Task.Delay(100);
		Assert.Equal(1, _vm.Groups.Sum(x => x.Count));
	}

	[Fact]
	public async Task OpeningAnEntry_GoesToItsEpisode()
	{
		var entry = Add("Severance", 2, 5, Utc(2026, 11, 14, 20));
		await _vm.OnNavigatedTo(null!);

		_vm.OpenCommand.Execute(_vm.Groups[0][0]);

		_navigation.Verify(x => x.NavigateTo(typeof(EpisodeViewModel).FullName!, It.Is<BaseItemDto>(d => d.Id == entry.ItemId && d.Type == BaseItemDto_Type.Episode), false), Times.Once);
	}

	[Fact]
	public async Task Syncing_FromThePage_FillsIt()
	{
		var item = _journal.Server.AddEpisode("Severance", 1, 1) with { UserData = new JournalUserData(true, 1, Utc(2026, 11, 1, 20)) };
		_journal.Server.Played.Add(item);
		await _vm.OnNavigatedTo(null!);

		await _vm.SyncCommand.ExecuteAsync(null);

		Assert.True(_vm.HasEntries);
		Assert.False(_vm.IsSyncing);
		Assert.Equal("Synced 11/14/2026 12:00 PM; dates of plays in other apps are approximate (Jellyfin keeps only the last one).", Plain(_vm.StatusText));
	}

	[Fact]
	public async Task SignedOut_ThePageSaysSo()
	{
		_journal.Session.Current = null;

		await _vm.OnNavigatedTo(null!);

		Assert.True(_vm.HasError);
		Assert.Equal("Sign in to see your diary.", _vm.ErrorText);
		Assert.True(_vm.IsEmpty);
	}

	/// <summary>ICU writes a narrow no-break space before AM/PM; the text reads the same.</summary>
	private static string Plain(string text) => text.Replace(' ', ' ').Replace(' ', ' ');

	private static async Task WaitFor(Func<bool> condition)
	{
		var until = DateTime.UtcNow.AddSeconds(5);
		while (!condition())
		{
			Assert.True(DateTime.UtcNow < until, "timed out");
			await Task.Delay(10);
		}
	}

	private sealed class FakeDialogs : IJournalDialogs
	{
		public DiaryEditResult? Edit { get; set; }

		public bool ConfirmDelete { get; set; }

		public string? ExportPath { get; set; }

		public List<(string Name, string Extension)> Picked { get; } = [];

		public Task<DiaryEditResult?> EditAsync(DiaryEntryViewModel entry) => Task.FromResult(Edit);

		public Task<bool> ConfirmDeleteAsync(DiaryEntryViewModel entry) => Task.FromResult(ConfirmDelete);

		public Exception? PickError { get; set; }

		public Task<string?> PickExportFileAsync(string suggestedName, string extension)
		{
			Picked.Add((suggestedName, extension));
			return PickError is { } error ? Task.FromException<string?>(error) : Task.FromResult(ExportPath);
		}
	}
}

public sealed class JournalRatingViewModelTests : IDisposable
{
	private readonly TempJournal _journal = new();
	private readonly JournalRatingViewModel _vm;

	public JournalRatingViewModelTests() => _vm = new JournalRatingViewModel(_journal.Service, NullLogger<JournalRatingViewModel>.Instance);

	public void Dispose() => _journal.Dispose();

	private static BaseItemDto Dto(BaseItemDto_Type type, Guid? series = null) => new() { Id = Guid.NewGuid(), Type = type, SeriesId = series, Name = "Thing" };

	[Fact]
	public void Episodes_SeasonsAndSeries_AreRated_OtherItemsNot()
	{
		var series = Guid.NewGuid();
		var episode = Dto(BaseItemDto_Type.Episode, series);

		Assert.Equal(new RatedItem(episode.Id!.Value, RatedItemKind.Episode, series, "Thing"), JournalRatingViewModel.From(episode));
		Assert.Equal(RatedItemKind.Season, JournalRatingViewModel.From(Dto(BaseItemDto_Type.Season, series))!.Kind);
		var show = Dto(BaseItemDto_Type.Series);
		Assert.Equal(new RatedItem(show.Id!.Value, RatedItemKind.Series, show.Id, "Thing"), JournalRatingViewModel.From(show));
		Assert.Null(JournalRatingViewModel.From(Dto(BaseItemDto_Type.Movie)));
		Assert.Null(JournalRatingViewModel.From(new BaseItemDto { Type = BaseItemDto_Type.Episode }));
		Assert.Null(JournalRatingViewModel.From(null));
	}

	[Fact]
	public async Task ARating_IsShownAndSaved_InTheJournalOnly_ByDefault()
	{
		var episode = Dto(BaseItemDto_Type.Episode, Guid.NewGuid());
		_journal.Store.SetRating(TempJournal.Alice, episode.Id!.Value, RatedItemKind.Episode, 6);

		await _vm.LoadAsync(episode);
		Assert.True(_vm.IsAvailable);
		Assert.Equal(6, _vm.Value);
		// showing it saved nothing
		Assert.Empty(_journal.Server.Calls);

		_vm.Value = 9;
		await _vm.LastSave;

		Assert.Equal(9, _journal.Store.GetRating(TempJournal.Alice, episode.Id.Value)!.Value);
		Assert.Empty(_journal.Server.Ratings);
	}

	[Fact]
	public async Task WithMirroringOn_ARatingGoesToJellyfinToo_ButAClearDoesNot()
	{
		_journal.Store.SaveSettings(new JournalSettings { MirrorRatingToJellyfin = true });
		var show = Dto(BaseItemDto_Type.Series);
		await _vm.LoadAsync(show);

		_vm.Value = 7;
		await _vm.LastSave;
		_vm.Value = 0;
		await _vm.LastSave;

		Assert.Equal([(show.Id!.Value, 7)], _journal.Server.Ratings);
		Assert.Null(_journal.Store.GetRating(TempJournal.Alice, show.Id.Value));
	}

	[Fact]
	public async Task AMovie_OrNobodySignedIn_ShowsNoRating()
	{
		await _vm.LoadAsync(Dto(BaseItemDto_Type.Movie));
		Assert.False(_vm.IsAvailable);

		_journal.Session.Current = null;
		await _vm.LoadAsync(Dto(BaseItemDto_Type.Episode));
		Assert.False(_vm.IsAvailable);

		_vm.Value = 8;
		await _vm.LastSave;
		Assert.Empty(_journal.Store.GetRatings(TempJournal.Alice));
	}
}
