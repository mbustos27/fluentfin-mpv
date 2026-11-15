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
			""");

		Assert.True(history.Available);
		Assert.Equal(2, history.Plays.Count);
		Assert.Equal(new PlaybackHistoryPlay(Item, new DateTimeOffset(2026, 11, 1, 20, 15, 33, TimeSpan.Zero).AddTicks(1234567), TimeSpan.FromSeconds(2640)), history.Plays[0]);
		Assert.Equal(TimeSpan.FromMinutes(30), history.Plays[1].Duration);
	}

	[Fact]
	public void ColumnsInAnotherOrderOrCase_AreFound()
	{
		var history = PlaybackReportingQuery.Parse($$"""{"columns":["playduration","itemid","datecreated"],"results":[["60","{{Item:N}}","2026-11-01T10:00:00Z"]]}""");

		Assert.Equal(TimeSpan.FromMinutes(1), Assert.Single(history.Plays).Duration);
	}

	[Theory]
	[InlineData("")]
	[InlineData("<html>login</html>")]
	[InlineData("[]")]
	[InlineData("""{"message":"custom queries are off"}""")]
	[InlineData("""{"colums":["a","b"],"results":[]}""")]
	public void AnythingElse_IsNotAvailable(string body)
	{
		var history = PlaybackReportingQuery.Parse(body);

		Assert.False(history.Available);
		Assert.Empty(history.Plays);
		Assert.False(string.IsNullOrEmpty(history.Reason));
	}
}
