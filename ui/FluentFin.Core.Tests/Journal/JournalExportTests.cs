using System.Text.Json.Nodes;
using FluentFin.Core.Journal;

namespace FluentFin.Core.Tests.Journal;

public sealed class JournalExportTests
{
	private static readonly Guid Series = Guid.Parse("11111111-1111-1111-1111-111111111111");
	private static readonly Guid Episode = Guid.Parse("22222222-2222-2222-2222-222222222222");
	private static readonly DateTimeOffset Watched = new(2026, 11, 1, 5, 30, 0, TimeSpan.Zero);

	private static readonly DiaryEntry Entry = new()
	{
		Id = 7,
		ItemId = Episode,
		SeriesId = Series,
		SeriesName = "Severance",
		SeasonNumber = 2,
		EpisodeNumber = 5,
		Title = "Trojan's Horse, part \"one\"",
		WatchedAt = Watched,
		DateSource = DiaryDateSource.Session,
		Source = DiaryEntrySource.AutoLog,
		Rewatch = true,
		Review = "=HYPERLINK(\"http://evil\")\nsecond line",
		Spoiler = true,
	};

	private static readonly ItemRating Rating = new(Episode, RatedItemKind.Episode, 7, Series, "Trojan's Horse", Watched);

	[Fact]
	public void Json_HasTheJournal_EveryEntry_AndEveryRating()
	{
		var deleted = Entry with { Id = 8, DeletedAt = Watched };
		var undated = Entry with { Id = 9, WatchedAt = null, DateSource = DiaryDateSource.Unknown, Rewatch = false };
		var seriesRating = new ItemRating(Series, RatedItemKind.Series, 10, Series, "Severance", Watched);

		var json = JsonNode.Parse(JournalExport.ToJson(TempJournal.Alice, [Entry, deleted, undated], [Rating, seriesRating], Watched.AddDays(1)))!;

		Assert.Equal("fluentfin-mpv-journal", json["format"]!.GetValue<string>());
		Assert.Equal(1, json["version"]!.GetValue<int>());
		Assert.Equal("server-a", json["journal"]!["serverId"]!.GetValue<string>());
		Assert.Equal(TempJournal.Alice.UserId.ToString("N"), json["journal"]!["userId"]!.GetValue<string>());
		Assert.Equal("alice", json["journal"]!["userName"]!.GetValue<string>());

		var entries = json["entries"]!.AsArray();
		Assert.Equal([7L, 9L], entries.Select(x => x!["id"]!.GetValue<long>()));
		var first = entries[0]!;
		Assert.Equal(Episode.ToString("N"), first["itemId"]!.GetValue<string>());
		Assert.Equal("Trojan's Horse, part \"one\"", first["title"]!.GetValue<string>());
		Assert.Equal(Watched, first["watchedAt"]!.GetValue<DateTimeOffset>());
		Assert.False(first["dateApproximate"]!.GetValue<bool>());
		Assert.Equal("session", first["dateSource"]!.GetValue<string>());
		Assert.Equal("auto", first["source"]!.GetValue<string>());
		Assert.True(first["rewatch"]!.GetValue<bool>());
		Assert.Equal(7, first["rating"]!.GetValue<int>());
		Assert.Equal(Entry.Review, first["review"]!.GetValue<string>());
		Assert.Null(entries[1]!["watchedAt"]);
		Assert.True(entries[1]!["dateApproximate"]!.GetValue<bool>());

		var ratings = json["ratings"]!.AsArray();
		Assert.Equal(2, ratings.Count);
		Assert.Equal(("series", 10, 5.0), (ratings[1]!["kind"]!.GetValue<string>(), ratings[1]!["rating"]!.GetValue<int>(), ratings[1]!["stars"]!.GetValue<double>()));
		Assert.Equal(3.5, ratings[0]!["stars"]!.GetValue<double>());
	}

	[Fact]
	public void Csv_IsOneQuotedLinePerEntry_WithLocalDates_AndNoFormulas()
	{
		var deleted = Entry with { Id = 8, DeletedAt = Watched };

		var csv = JournalExport.ToCsv([Entry, deleted], [Rating], ManualClock.UtcMinus8);

		var lines = csv.Split("\r\n");
		Assert.Equal(string.Join(',', JournalExport.CsvColumns), lines[0]);
		Assert.Equal(
			"2026-11-01T05:30:00Z,2026-10-31,false,session,Severance,2,5,\"Trojan's Horse, part \"\"one\"\"\",3.5,7,true," +
			"\"'=HYPERLINK(\"\"http://evil\"\")\nsecond line\",true,auto,true," + Episode.ToString("N") + "," + Series.ToString("N"),
			string.Join("\r\n", lines[1..^1]));
		Assert.Equal("", lines[^1]);
	}

	[Fact]
	public void Csv_LeavesEmptyFieldsForWhatIsUnknown()
	{
		var bare = new DiaryEntry { ItemId = Episode, DateSource = DiaryDateSource.Unknown, Source = DiaryEntrySource.Backfill };

		var line = JournalExport.ToCsv([bare], [], TimeZoneInfo.Utc).Split("\r\n")[1];

		Assert.Equal(",,true,unknown,,,,,,,false,,false,backfill,true," + Episode.ToString("N") + ",", line);
	}
}
