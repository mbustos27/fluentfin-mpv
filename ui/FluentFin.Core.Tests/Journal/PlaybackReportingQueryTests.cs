using FluentFin.Core.Journal;

namespace FluentFin.Core.Tests.Journal;

public sealed class PlaybackReportingQueryTests
{
	private static readonly Guid Item = Guid.Parse("33333333-3333-3333-3333-333333333333");

	[Fact]
	public void TheQuery_AsksForThisUsersEpisodes_ByAnIdWeFormatted()
	{
		var sql = PlaybackReportingQuery.For(TempJournal.Alice.UserId);

		Assert.Contains($"= '{TempJournal.Alice.UserId:N}'", sql);
		Assert.Contains("ItemType = 'Episode'", sql);
		Assert.StartsWith("SELECT DateCreated, ItemId, PlayDuration FROM PlaybackActivity", sql);
	}

	[Fact]
	public void TheAnswer_IsReadWithThePluginsSpelling_TextOrNumbers()
	{
		var history = PlaybackReportingQuery.Parse($$"""
			{"colums":["DateCreated","ItemId","PlayDuration"],"results":[
			  ["2026-11-01 20:15:33.1234567","{{Item:N}}","2640"],
			  ["2026-11-02 21:00:00","{{Item}}",1800],
			  ["not a date","{{Item:N}}","10"],
			  ["2026-11-03 21:00:00","not an id","10"],
			  ["2026-11-03 21:00:00","{{Item:N}}","0"]
			],"message":""}
			""", TimeZoneInfo.Utc);

		Assert.True(history.Available);
		Assert.Equal(2, history.Plays.Count);
		Assert.Equal(new PlaybackHistoryPlay(Item, new DateTimeOffset(2026, 11, 1, 20, 15, 33, TimeSpan.Zero).AddTicks(1234567), TimeSpan.FromSeconds(2640)), history.Plays[0]);
		Assert.Equal(TimeSpan.FromMinutes(30), history.Plays[1].Duration);
	}

	[Fact]
	public void ColumnsInAnotherOrderOrCase_AreFound()
	{
		var history = PlaybackReportingQuery.Parse($$"""{"columns":["playduration","itemid","datecreated"],"results":[["60","{{Item:N}}","2026-11-01T10:00:00Z"]]}""", TimeZoneInfo.Utc);

		Assert.Equal(TimeSpan.FromMinutes(1), Assert.Single(history.Plays).Duration);
	}

	[Fact]
	public void ATimeWithNoZone_IsTheServersWallClock_AndOneWithAZoneIsKept()
	{
		// the plugin writes DateTime.Now with no zone: a server eight hours behind UTC wrote 20:15 for 04:15 UTC next day
		var history = PlaybackReportingQuery.Parse($$"""
			{"colums":["DateCreated","ItemId","PlayDuration"],"results":[
			  ["2026-11-01 20:15:00","{{Item:N}}","60"],
			  ["2026-11-01 20:15:00Z","{{Item:N}}","60"],
			  ["2026-11-01T20:15:00+01:00","{{Item:N}}","60"]
			],"message":""}
			""", ManualClock.UtcMinus8);

		Assert.Equal(
			[new DateTimeOffset(2026, 11, 2, 4, 15, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 1, 20, 15, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 1, 19, 15, 0, TimeSpan.Zero)],
			history.Plays.Select(x => x.StartedAt));
		Assert.All(history.Plays, x => Assert.Equal(TimeSpan.Zero, x.StartedAt.Offset));
	}

	[Fact]
	public void TheClockShift_IsTheMedianDifference_FromJellyfinsOwnDates()
	{
		var start = new DateTimeOffset(2026, 11, 1, 20, 0, 0, TimeSpan.Zero);
		(DateTimeOffset, DateTimeOffset) Pair(double hours, int minutes = 0) => (start, start.AddHours(hours).AddMinutes(minutes));

		// a server five hours ahead of this PC: Jellyfin's UTC dates are five hours after the plugin's times read here
		Assert.Equal(TimeSpan.FromHours(5), PlaybackReportingQuery.ClockShift([Pair(5), Pair(5, 1), Pair(5, -2), Pair(0, 3), Pair(5)]));
		Assert.Equal(TimeSpan.FromHours(-3.5), PlaybackReportingQuery.ClockShift([Pair(-3.5), Pair(-3.5, 2), Pair(-3.5, -1)]));
		// the same zone (a few minutes either way), too few pairs, or differences of days (marked played later): none
		Assert.Equal(TimeSpan.Zero, PlaybackReportingQuery.ClockShift([Pair(0, 2), Pair(0, -1), Pair(0, 20)]));
		Assert.Equal(TimeSpan.Zero, PlaybackReportingQuery.ClockShift([Pair(5), Pair(5)]));
		Assert.Equal(TimeSpan.Zero, PlaybackReportingQuery.ClockShift([Pair(48), Pair(72), Pair(96), Pair(30)]));
	}

	[Theory]
	[InlineData("")]
	[InlineData("<html>login</html>")]
	[InlineData("[]")]
	[InlineData("""{"message":"custom queries are off"}""")]
	[InlineData("""{"colums":["a","b"],"results":[]}""")]
	public void AnythingElse_IsNotAvailable(string body)
	{
		var history = PlaybackReportingQuery.Parse(body, TimeZoneInfo.Utc);

		Assert.False(history.Available);
		Assert.Empty(history.Plays);
		Assert.False(string.IsNullOrEmpty(history.Reason));
	}
}
