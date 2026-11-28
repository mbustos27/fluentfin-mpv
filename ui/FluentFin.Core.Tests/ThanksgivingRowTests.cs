using FluentFin.Core.SmartCollections;
using FluentFin.Core.SmartCollections.Builders;
using FluentFin.Core.Tests.Outside;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (Collections 2, stream wire): the Thanksgiving row, the 1.9 plan's folded-in item. In the seasonal slot from
/// 1 November to US Thanksgiving Day (the fourth Thursday of November) on the PC's own date, of TMDb's "thanksgiving" keyword
/// (kept since the index's revision 4); in its last week beside Christmas, Thanksgiving first.
/// </summary>
public class ThanksgivingRowTests
{
	private static int s_next;

	private static IndexEntry Film(string name, string[] tags, double rating = 7, bool played = false) => new()
	{
		Id = new Guid(Interlocked.Increment(ref s_next), 1126, 0, new byte[8]),
		Name = name,
		Kind = IndexKind.Movie,
		Year = 2000,
		Tags = tags,
		Genres = ["Comedy"],
		Rating = rating,
		Played = played,
		PrimaryTag = "p",
	};

	private static IReadOnlyList<SmartCollection> Built(IEnumerable<IndexEntry> titles, DateTimeOffset now) =>
		[.. new SeasonalBuilder().Build(BuildContext.For(new IndexSnapshot([], [.. titles], new Dictionary<Guid, SeriesPlays>(), HasPeople: false, now), now))];

	private static readonly IndexEntry[] Thanksgiving =
	[
		Film("Planes, Trains and Automobiles", ["thanksgiving", "road trip"], 7.6),
		Film("Home for the Holidays", ["thanksgiving"], 6.6),
		Film("Pieces of April", ["thanksgiving"], 6.8),
		Film("Funny Business", ["Thanksgiving "], 6.0, played: true), // as a server may file it
	];

	private static readonly IndexEntry[] Christmas = [Film("Home Alone", ["christmas"]), Film("Elf", ["christmas"]), Film("Klaus", ["santa claus"])];

	[Theory]
	[InlineData(2026, 26)]
	[InlineData(2027, 25)]
	[InlineData(2028, 23)] // November begins on a Wednesday
	[InlineData(2029, 22)] // on a Thursday: the earliest
	[InlineData(2030, 28)] // on a Friday: the latest
	public void ThanksgivingDay_IsTheFourthThursdayOfNovember(int year, int day)
	{
		Assert.Equal(new DateOnly(year, 11, day), SeasonalBuilder.ThanksgivingDay(year));
		Assert.Equal(DayOfWeek.Thursday, SeasonalBuilder.ThanksgivingDay(year).DayOfWeek);
	}

	[Theory]
	[InlineData("2026-10-31T23:30:00-08:00", false)] // Halloween night in California (already 1 November in UTC)
	[InlineData("2026-11-01T00:10:00-08:00", true)]
	[InlineData("2026-11-01T00:10:00+09:00", true)]  // the 1st in Tokyo (still 31 October in UTC)
	[InlineData("2026-11-26T23:30:00-08:00", true)]  // Thanksgiving evening in California (already the 27th in UTC)
	[InlineData("2026-11-27T00:10:00-08:00", false)]
	[InlineData("2026-11-27T00:10:00+09:00", false)] // the 27th in Tokyo (still Thanksgiving in UTC)
	public void TheRow_OpensOn1November_AndShowsUpToThanksgivingDay_OnThePcsOwnDate(string when, bool open)
	{
		var now = DateTimeOffset.Parse(when, System.Globalization.CultureInfo.InvariantCulture);

		var made = Built(Thanksgiving, now);

		Assert.Equal(open, SeasonalBuilder.IsThanksgiving(now));
		Assert.Equal(open, made.Any(x => x.Id == "seasonal:thanksgiving"));
	}

	[Fact]
	public void TheRow_IsTheThanksgivingKeywordsTitles_TheUnwatchedFirst_EachPartBestFirst_UntilTheDay()
	{
		var now = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.FromHours(-8));

		var row = Assert.Single(Built([.. Thanksgiving, Film("Not Thanksgiving", ["holiday"]), .. Christmas], now)); // 10 November: no Christmas yet

		Assert.Equal("seasonal:thanksgiving", row.Id);
		Assert.Equal(SmartCollectionKind.Seasonal, row.Kind);
		Assert.Equal("Thanksgiving", row.Title);
		Assert.Null(NixieTitles.Check(row.Title));
		Assert.Equal(["Planes, Trains and Automobiles", "Pieces of April", "Home for the Holidays", "Funny Business"], row.ItemIds.Select(id => Thanksgiving.Single(x => x.Id == id).Name));
		Assert.Equal(["Thanksgiving films and shows"], row.Reason!.Facts);
		Assert.Equal(new DateOnly(2026, 11, 26), row.Reason.Until);
		Assert.Equal("Made by Nixie · In season", row.Eyebrow);
	}

	[Fact]
	public void InItsLastWeek_ItIsBesideChristmas_ThanksgivingFirst_ThenChristmasAlone()
	{
		var wednesday = new DateTimeOffset(2026, 11, 25, 18, 0, 0, TimeSpan.FromHours(-8)); // the day before Thanksgiving
		var friday = new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.FromHours(-8));
		var context = BuildContext.For(new IndexSnapshot([], [.. Thanksgiving, .. Christmas], new Dictionary<Guid, SeriesPlays>(), HasPeople: false, wednesday), wednesday);

		var both = new SeasonalBuilder().Build(context).ToList();

		Assert.Equal(["seasonal:thanksgiving", "seasonal:christmas"], both.Select(x => x.Id));
		Assert.Equal(new BuildGates.Gate(7, 7, 7), context.Gates.Of(SmartCollectionKind.Seasonal)); // both rows' titles counted
		Assert.Equal(["seasonal:christmas"], Built([.. Thanksgiving, .. Christmas], friday).Select(x => x.Id));
		Assert.Empty(Built([.. Thanksgiving, .. Christmas], new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero)).Where(x => x.Id == "seasonal:thanksgiving"));
	}

	[Fact]
	public void FewerThanThree_MakeNoRow()
	{
		Assert.Empty(Built(Thanksgiving.Take(2), new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero)));
	}

	[Fact]
	public void TheIndexKeepsTheKeyword_AndSettingsSaysTheWindow()
	{
		Assert.Contains("thanksgiving", Keywords.Seasonal);
		Assert.Equal(["thanksgiving"], Keywords.Kept(["Thanksgiving", "duringcreditsstinger"]));
		var season = new CollectionsSettingsViewModel(new MemoryStore()).HomeRows.Single(x => x.Name == "In season");
		Assert.Contains("Thanksgiving in November up to the day", season.Description, StringComparison.Ordinal);
		Assert.StartsWith("Halloween in October, ", season.Description, StringComparison.Ordinal);
		Assert.DoesNotContain("your server", season.Description, StringComparison.OrdinalIgnoreCase); // rule 11
	}
}
