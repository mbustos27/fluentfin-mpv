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
	}

	[Fact]
	public void AnEpisode_ShowsItsSeriesAboveItsOwnNameAndNumber()
	{
		var episode = new BaseItemDto { Type = BaseItemDto_Type.Episode, SeriesName = "The Office", Name = "The Fight", ParentIndexNumber = 2, IndexNumber = 3 };

		Assert.Equal("The Office", ItemMeta.HeroTitle(episode));
		Assert.Equal("The Fight", ItemMeta.HeroSubtitle(episode));
		// 1.2.0: the one episode format (layout audit item 7); the teal overline went (item 21)
		Assert.Equal("S2 E3 · The Fight", ItemMeta.EpisodeTitle(episode));
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

	// Nixie 1.7.4: a cast photo's tooltip and Narrator name: the whole name and role (the name under it may be trimmed)
	[Fact]
	public void ACastCard_SpeaksTheWholeNameAndRole()
	{
		Assert.Equal("Thomas McDowell, Commander Evangeline Ashcroft-Reyes", ItemMeta.PersonSpoken(new BaseItemPerson { Name = " Thomas McDowell ", Type = BaseItemPerson_Type.Actor, Role = "Commander Evangeline Ashcroft-Reyes" }));
		Assert.Equal("Priyanka Venkataraman, Writer", ItemMeta.PersonSpoken(new BaseItemPerson { Name = "Priyanka Venkataraman", Type = BaseItemPerson_Type.Writer }));
		Assert.Equal("Kalanikupuapaikalaninui", ItemMeta.PersonSpoken(new BaseItemPerson { Name = "Kalanikupuapaikalaninui", Type = BaseItemPerson_Type.Actor }));
		Assert.Equal("Director", ItemMeta.PersonSpoken(new BaseItemPerson { Type = BaseItemPerson_Type.Director }));
		Assert.Equal("", ItemMeta.PersonSpoken(null));
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
