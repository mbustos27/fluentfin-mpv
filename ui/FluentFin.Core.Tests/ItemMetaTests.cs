using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Tests;

/// <summary>Seaglass: the title lines and the quiet metadata of the item pages.</summary>
public sealed class ItemMetaTests
{
	[Fact]
	public void AMovieAndASeries_ShowTheirOwnNameAndNothingUnderIt()
	{
		var movie = new BaseItemDto { Type = BaseItemDto_Type.Movie, Name = "Heat" };

		Assert.Equal("Heat", ItemMeta.HeroTitle(movie));
		Assert.Equal("", ItemMeta.HeroSubtitle(movie));
		Assert.Equal("", ItemMeta.Eyebrow(movie));
	}

	[Fact]
	public void AnEpisode_ShowsItsSeriesAboveItsOwnNameAndNumber()
	{
		var episode = new BaseItemDto { Type = BaseItemDto_Type.Episode, SeriesName = "The Office", Name = "The Fight", ParentIndexNumber = 2, IndexNumber = 3 };

		Assert.Equal("The Office", ItemMeta.HeroTitle(episode));
		Assert.Equal("The Fight", ItemMeta.HeroSubtitle(episode));
		Assert.Equal("S2 \u00B7 E3", ItemMeta.Eyebrow(episode));
		Assert.Equal("Season 2, episode 3", ItemMeta.EyebrowName(episode));
		Assert.Equal("3. The Fight", ItemMeta.EpisodeTitle(episode));
	}

	[Fact]
	public void ASeasonWithoutItsSeriesName_ShowsItsOwn()
	{
		var season = new BaseItemDto { Type = BaseItemDto_Type.Season, Name = "Season 2" };

		Assert.Equal("Season 2", ItemMeta.HeroTitle(season));
		Assert.Equal("Season 2", ItemMeta.HeroSubtitle(season));
	}

	[Fact]
	public void TheMetadataLists_AreNamesOnly_WithoutBlanksOrRepeats()
	{
		var dto = new BaseItemDto
		{
			GenreItems = [new NameGuidPair { Name = "Drama" }, new NameGuidPair { Name = " drama " }, new NameGuidPair { Name = "" }],
			Studios = [new NameGuidPair { Name = "A24" }],
			Tags = ["cult", "cult"],
			People =
			[
				new BaseItemPerson { Name = "Ann", Type = BaseItemPerson_Type.Director },
				new BaseItemPerson { Name = "Bob", Type = BaseItemPerson_Type.Writer },
				new BaseItemPerson { Name = "Cy", Type = BaseItemPerson_Type.Actor },
			],
		};

		Assert.Equal(["Drama"], ItemMeta.Genres(dto));
		Assert.Equal(["A24"], ItemMeta.Studios(dto));
		Assert.Equal(["cult"], ItemMeta.Tags(dto));
		Assert.Equal(["Ann"], ItemMeta.Directors(dto));
		Assert.Equal(["Bob"], ItemMeta.Writers(dto));
		Assert.Empty(ItemMeta.Genres(null));
	}

	[Fact]
	public void TheTrailer_IsTheFirstWebAddress()
	{
		var dto = new BaseItemDto
		{
			RemoteTrailers = [new MediaUrl { Url = "file:///c:/x.exe" }, new MediaUrl { Url = "not a url" }, new MediaUrl { Url = "https://youtu.be/abc" }],
		};

		Assert.Equal("https://youtu.be/abc", ItemMeta.TrailerUrl(dto)?.AbsoluteUri);
		Assert.Null(ItemMeta.TrailerUrl(new BaseItemDto { RemoteTrailers = [] }));
		Assert.Null(ItemMeta.TrailerUrl(null));
	}

	[Fact]
	public void UnderACastPhoto_TheRoleOrElseWhatTheCrewMemberDid()
	{
		Assert.Equal("Tony Stark", ItemMeta.PersonRole(new BaseItemPerson { Type = BaseItemPerson_Type.Actor, Role = " Tony Stark " }));
		Assert.Equal("Director", ItemMeta.PersonRole(new BaseItemPerson { Type = BaseItemPerson_Type.Director }));
		Assert.Equal("", ItemMeta.PersonRole(new BaseItemPerson { Type = BaseItemPerson_Type.Actor }));
		Assert.Equal("", ItemMeta.PersonRole(null));
	}

	[Fact]
	public void TheMetadataRows_JoinEachListAndSkipTheEmptyOnes()
	{
		var dto = new BaseItemDto
		{
			Genres = ["Drama", "Crime"],
			Studios = [new NameGuidPair { Name = "Warner" }],
			People = [new BaseItemPerson { Type = BaseItemPerson_Type.Director, Name = "Michael Mann" }],
		};

		Assert.Equal([new ItemMetaRow("Genres", "Drama · Crime"), new ItemMetaRow("Director", "Michael Mann"), new ItemMetaRow("Studios", "Warner")], ItemMeta.Rows(dto));
		Assert.Empty(ItemMeta.Rows(null));
	}
}
