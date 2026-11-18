using FluentFin.Converters;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Tests;

/// <summary>
/// 1.2.0 (1.1.3 UX audit, layout items 7 and 23): the cards' and the player's lines use the one episode format
/// (ItemLabels.EpisodeCode / EpisodeTitle: "S3 E4", "S3 E4 · Title"), and a year range has an en dash.
/// </summary>
public sealed class EpisodeFormatTests
{
	private static readonly BaseItemDto Episode = new() { Type = BaseItemDto_Type.Episode, SeriesName = "Iron Lighthouses", ParentIndexNumber = 3, IndexNumber = 4, Name = "After the Machine" };

	[Fact]
	public void AnEpisodeCard_ReadsSeasonEpisodeDotTitle()
	{
		Assert.Equal("S3 E4 · After the Machine", Episode.GetCardSubtitle());
		Assert.Equal("S3 E4 · After the Machine", new JellyfinSubtitleConverter().Convert(Episode, typeof(string), null!, ""));
		Assert.Equal("S3 E4 · After the Machine", Episode.ToSessionInfoItemName());
		Assert.Equal("S3 E4", Episode.GetSeasonAndEpisodeNumber());
	}

	[Theory]
	[InlineData(1996, 2000, "Ended", "1996–2000")]
	[InlineData(2011, null, "Continuing", "2011–present")]
	[InlineData(2004, 2004, "Ended", "2004")]
	[InlineData(2004, null, "Ended", "2004")]   // it read "2004 - " without an end date
	public void ASeriesYearRange_HasAnEnDash(int start, int? end, string status, string expected)
	{
		var series = new BaseItemDto
		{
			Type = BaseItemDto_Type.Series,
			ProductionYear = start,
			Status = status,
			EndDate = end is { } y ? new DateTimeOffset(y, 6, 1, 0, 0, 0, TimeSpan.Zero) : null,
		};

		Assert.Equal(expected, series.GetCardSubtitle());
		Assert.Equal(expected, BaseItemDtoConverters.SeriesYears(series));
	}
}
