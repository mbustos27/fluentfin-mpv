using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Tests;

/// <summary>Seaglass: which items Home's hero features, and the words and artwork each slide gets.</summary>
public sealed class HeroSlidesTests
{
	private static BaseItemDto Movie(string name, bool backdrop = true, bool logo = false, long position = 0, int year = 2019, string? rating = null, long runMinutes = 0)
	{
		var dto = new BaseItemDto
		{
			Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.Movie, ProductionYear = year, OfficialRating = rating,
			RunTimeTicks = runMinutes * TimeSpan.TicksPerMinute,
			BackdropImageTags = backdrop ? [$"bd-{name}"] : [],
			ImageTags = new BaseItemDto_ImageTags(),
			UserData = new UserItemDataDto { PlaybackPositionTicks = position },
		};
		if (logo)
		{
			dto.ImageTags.AdditionalData["Logo"] = $"logo-{name}";
		}
		return dto;
	}

	private static BaseItemDto Episode(string series, Guid seriesId, int season, int number, bool parentBackdrop = true, bool parentLogo = false) => new()
	{
		Id = Guid.NewGuid(), Name = $"Ep {number}", Type = BaseItemDto_Type.Episode, SeriesName = series, SeriesId = seriesId,
		ParentIndexNumber = season, IndexNumber = number, ImageTags = new BaseItemDto_ImageTags(),
		ParentBackdropImageTags = parentBackdrop ? [$"pbd-{series}"] : [], ParentBackdropItemId = seriesId,
		ParentLogoImageTag = parentLogo ? $"plogo-{series}" : null, ParentLogoItemId = parentLogo ? seriesId : null,
	};

	// Seaglass 1.1.4 (UX review, prioritised fix 10): the hero features what is new to the user, never a repeat of the
	// Continue Watching row under it

	[Fact]
	public void TheLatestOfEachLibraryInTurn_NotContinueWatching()
	{
		var resume = Movie("Resume", position: 5_000_000_000);
		var a1 = Movie("A1"); var a2 = Movie("A2"); var b1 = Movie("B1");

		var slides = HeroSlides.Pick([resume], [[a1, a2], [b1]], []);

		Assert.Equal(["A1", "B1", "A2"], slides.Select(s => s.Title));
	}

	[Fact]
	public void AnItemOfContinueWatching_IsNotFeaturedEvenWhenItIsAlsoNew()
	{
		var resume = Movie("Resume", position: 5_000_000_000);
		var started = Movie("Started elsewhere"); // the row's copy of the item, without its progress
		started.Id = resume.Id;

		var slides = HeroSlides.Pick([resume], [[started, Movie("Fresh")]], []);

		Assert.Equal(["Fresh"], slides.Select(s => s.Title));
	}

	[Fact]
	public void WatchedOrStartedItemsAreNotNew()
	{
		var watched = Movie("Watched");
		watched.UserData!.Played = true;
		var started = Movie("Started", position: 1_000);
		var show = Series("Seen show", unplayed: 0);
		var newEpisodes = Series("New episodes", unplayed: 3);

		var slides = HeroSlides.Pick([], [[watched, started, show, newEpisodes, Movie("Fresh")]], []);

		Assert.Equal(["New episodes", "Fresh"], slides.Select(s => s.Title));
	}

	[Fact]
	public void NextUpFillsWhatTheNewItemsLeave()
	{
		var show = Guid.NewGuid();
		var other = Guid.NewGuid();

		var slides = HeroSlides.Pick([], [[Movie("New 1")]], [Episode("Next show", show, 1, 2), Episode("Other show", other, 3, 1)]);

		Assert.Equal(["New 1", "Next show", "Other show"], slides.Select(s => s.Title));
	}

	[Fact]
	public void NothingNew_TheHeroIsNextUp_WithoutTheShowsBeingContinued()
	{
		var continued = Guid.NewGuid();
		var next = Guid.NewGuid();
		var resume = Episode("Continued", continued, 1, 4);
		resume.UserData = new UserItemDataDto { PlaybackPositionTicks = 9_000 };

		var slides = HeroSlides.Pick([resume], [], [Episode("Continued", continued, 1, 5), Episode("Next", next, 2, 1)]);

		Assert.Equal(["Next"], slides.Select(s => s.Title));
		Assert.False(slides[0].IsResume);
	}

	[Fact]
	public void NoMoreThanFiveSlides()
	{
		var slides = HeroSlides.Pick([], [[.. Enumerable.Range(0, 9).Select(i => Movie($"M{i}"))]], []);

		Assert.Equal(5, slides.Count);
		Assert.Equal(["M0", "M1", "M2", "M3", "M4"], slides.Select(s => s.Title));
	}

	[Fact]
	public void AnItemWithoutABackdropIsNotASlide()
	{
		var slides = HeroSlides.Pick([], [[Movie("Plain", backdrop: false), Movie("Pretty")]], []);

		Assert.Equal(["Pretty"], slides.Select(s => s.Title));
	}

	[Fact]
	public void ItemsWithALogoComeFirstWithinTheirGroup_ButNotAheadOfTheNewOnes()
	{
		var slides = HeroSlides.Pick([], [[Movie("L-plain"), Movie("L-logo", logo: true)]], [Episode("N-plain", Guid.NewGuid(), 1, 1), Episode("N-logo", Guid.NewGuid(), 1, 1, parentLogo: true)]);

		Assert.Equal(["L-logo", "L-plain", "N-logo", "N-plain"], slides.Select(s => s.Title));
	}

	[Fact]
	public void OneShowIsOneSlide_ShownWithTheSeriesNameAndItsPlace()
	{
		var show = Guid.NewGuid();
		var slides = HeroSlides.Pick([], [[Episode("Severance", show, 2, 3)]], [Episode("Severance", show, 2, 4)]);

		var slide = Assert.Single(slides);
		Assert.Equal("Severance", slide.Title);
		Assert.Equal("S2 E3", slide.Meta); // the shared episode format (layout audit, item 7)
		Assert.Equal(show, slide.BackdropItemId);
		Assert.Equal("pbd-Severance", slide.BackdropTag);
	}

	private static BaseItemDto Series(string name, int unplayed) => new()
	{
		Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.Series, BackdropImageTags = [$"bd-{name}"], ImageTags = new BaseItemDto_ImageTags(),
		UserData = new UserItemDataDto { UnplayedItemCount = unplayed, Played = unplayed == 0 },
	};

	[Fact]
	public void TheLogoComesFromTheItemOrItsSeries()
	{
		var show = Guid.NewGuid();
		var own = HeroSlides.Make(Movie("Heat", logo: true))!;
		var parent = HeroSlides.Make(Episode("Severance", show, 1, 1, parentLogo: true))!;
		var none = HeroSlides.Make(Movie("Nope"))!;

		Assert.Equal("logo-Heat", own.LogoTag);
		Assert.Equal(show, parent.LogoItemId);
		Assert.Equal("plogo-Severance", parent.LogoTag);
		Assert.False(none.HasLogo);
		Assert.Null(none.LogoUri("http://s", 600));
	}

	[Fact]
	public void ASlideWithPlaybackProgressResumes()
	{
		Assert.True(HeroSlides.Make(Movie("Half", position: 5_000_000_000))!.IsResume);
		Assert.Equal("Resume", HeroSlides.Make(Movie("Half", position: 5_000_000_000))!.PlayLabel);
		Assert.Equal("Play", HeroSlides.Make(Movie("Fresh"))!.PlayLabel);
	}

	[Theory]
	[InlineData(2019, "PG-13", 112, "2019 · PG-13 · 1h 52m")]
	[InlineData(2008, null, 45, "2008 · 45m")]
	[InlineData(2008, "", 120, "2008 · 2h")]
	[InlineData(0, null, 0, "")]
	public void TheMetaLineHasTheYearRatingAndRuntimeThereAre(int year, string? rating, int minutes, string expected) =>
		Assert.Equal(expected, HeroSlides.Meta(Movie("m", year: year, rating: rating, runMinutes: minutes)));

	[Fact]
	public void ImageAddressesCarryTheTagAndTheWidth()
	{
		var slide = HeroSlides.Make(Movie("Heat", logo: true))!;

		Assert.Equal($"http://srv/Items/{slide.BackdropItemId}/Images/Backdrop?fillWidth=1920&tag=bd-Heat", slide.BackdropUri("http://srv", 1920).ToString());
		Assert.Equal($"http://srv/Items/{slide.LogoItemId}/Images/Logo?fillWidth=600&tag=logo-Heat", slide.LogoUri("http://srv", 600)!.ToString());
	}

	[Fact]
	public void TheBackdropBlurHashIsFoundByTag()
	{
		var dto = Movie("Heat");
		dto.ImageBlurHashes = new BaseItemDto_ImageBlurHashes { Backdrop = new BaseItemDto_ImageBlurHashes_Backdrop() };
		dto.ImageBlurHashes.Backdrop.AdditionalData["bd-Heat"] = "LEHV6nWB2yk8pyo0adR*.7kCMdnj";

		Assert.Equal("LEHV6nWB2yk8pyo0adR*.7kCMdnj", HeroSlides.Make(dto)!.BackdropBlurHash);
	}
}
