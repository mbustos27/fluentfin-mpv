using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Tests;

/// <summary>Seaglass: the chip row of the item pages (year, ratings, runtime, resolution, HDR, audio) and the artwork they sit on.</summary>
public sealed class ItemChipsTests
{
	/// <summary>The server-computed fields have no public setter in the SDK.</summary>
	private static MediaStream Set(MediaStream stream, string property, object? value)
	{
		typeof(MediaStream).GetProperty(property)!.SetValue(stream, value);
		return stream;
	}

	private static MediaStream Video(int w, int h, MediaStream_VideoRangeType? range = null) =>
		Set(new MediaStream { Type = MediaStream_Type.Video, Width = w, Height = h }, nameof(MediaStream.VideoRangeType), range);

	private static MediaStream Audio(string codec, int channels, bool isDefault = false, MediaStream_AudioSpatialFormat? spatial = null) =>
		Set(new MediaStream { Type = MediaStream_Type.Audio, Codec = codec, Channels = channels, IsDefault = isDefault }, nameof(MediaStream.AudioSpatialFormat), spatial);

	[Fact]
	public void AFullMovie_HasEveryChipInOrder()
	{
		var movie = new BaseItemDto
		{
			Type = BaseItemDto_Type.Movie,
			ProductionYear = 2010,
			OfficialRating = "PG-13",
			CommunityRating = 8.77f,
			RunTimeTicks = TimeSpan.FromMinutes(148).Ticks,
			MediaStreams = [Video(3840, 1608, MediaStream_VideoRangeType.DOVIWithHDR10), Audio("eac3", 6, isDefault: true, MediaStream_AudioSpatialFormat.DolbyAtmos)],
		};

		var chips = ItemChips.For(movie);

		Assert.Equal(["2010", "PG-13", "★ 8.8", "2h 28m", "4K", "Dolby Vision", "Dolby Atmos 5.1"], chips.Select(c => c.Text));
		Assert.All(chips, c => Assert.False(string.IsNullOrWhiteSpace(c.Name)));
		Assert.Equal("Community rating 8.8 out of 10", chips[2].Name);
	}

	[Fact]
	public void WhatTheItemDoesNotSay_IsLeftOut()
	{
		Assert.Empty(ItemChips.For(null));
		Assert.Empty(ItemChips.For(new BaseItemDto { Type = BaseItemDto_Type.Movie }));
		Assert.Equal(["1999"], ItemChips.For(new BaseItemDto { Type = BaseItemDto_Type.Movie, ProductionYear = 1999, CommunityRating = 0 }).Select(c => c.Text));
	}

	[Fact]
	public void ASeries_HasNoRuntimeChip_AndAnEpisode_ShowsItsAirDateNotItsYear()
	{
		var series = new BaseItemDto { Type = BaseItemDto_Type.Series, ProductionYear = 2004, RunTimeTicks = TimeSpan.FromMinutes(42).Ticks };
		var episode = new BaseItemDto
		{
			Type = BaseItemDto_Type.Episode,
			ProductionYear = 2004,
			PremiereDate = new DateTimeOffset(2004, 9, 22, 0, 0, 0, TimeSpan.Zero),
			RunTimeTicks = TimeSpan.FromMinutes(42).Ticks,
		};

		Assert.Equal(["2004"], ItemChips.For(series).Select(c => c.Text));
		Assert.Equal(["Sep 22, 2004", "42m"], ItemChips.For(episode).Select(c => c.Text));
	}

	[Fact]
	public void TheStreamsOfTheFirstMediaSource_AreUsedWhenTheItemHasNone()
	{
		var movie = new BaseItemDto
		{
			Type = BaseItemDto_Type.Movie,
			MediaSources = [new MediaSourceInfo { MediaStreams = [Video(1920, 800), Audio("aac", 2)] }],
		};

		Assert.Equal(["1080p", "AAC Stereo"], ItemChips.For(movie).Select(c => c.Text));
	}

	[Fact]
	public void TheDefaultAudioTrack_IsTheOneShown()
	{
		var movie = new BaseItemDto { Type = BaseItemDto_Type.Movie, MediaStreams = [Audio("aac", 2), Audio("dts", 8, isDefault: true)] };

		Assert.Equal(["DTS 7.1"], ItemChips.For(movie).Select(c => c.Text));
	}

	[Theory]
	[InlineData(3840, 2160, "4K")]
	[InlineData(3840, 1600, "4K")]
	[InlineData(1920, 1080, "1080p")]
	[InlineData(1920, 804, "1080p")]
	[InlineData(1280, 720, "720p")]
	[InlineData(720, 480, "SD")]
	[InlineData(null, null, null)]
	public void Resolution_IsNamedAsAPlayerNamesIt(int? width, int? height, string? expected) =>
		Assert.Equal(expected, ItemChips.Resolution(width, height));

	[Theory]
	[InlineData(MediaStream_VideoRangeType.HDR10, "HDR10")]
	[InlineData(MediaStream_VideoRangeType.HDR10Plus, "HDR10+")]
	[InlineData(MediaStream_VideoRangeType.HLG, "HLG")]
	[InlineData(MediaStream_VideoRangeType.DOVI, "Dolby Vision")]
	[InlineData(MediaStream_VideoRangeType.SDR, null)]
	public void DynamicRange_NamesHdrAndDolbyVision_AndNotSdr(MediaStream_VideoRangeType range, string? expected) =>
		Assert.Equal(expected, ItemChips.DynamicRange(Video(1920, 1080, range)));

	[Fact]
	public void Runtime_RoundsToMinutes() =>
		Assert.Equal(["1h 0m", "45m", "1m"], new[] { TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(45), TimeSpan.FromSeconds(20) }.Select(t => ItemChips.Runtime(t.Ticks)));
}

public sealed class ItemArtworkTests
{
	private static readonly Guid Movie = Guid.Parse("11111111-1111-1111-1111-111111111111");
	private static readonly Guid Series = Guid.Parse("22222222-2222-2222-2222-222222222222");

	private static BaseItemDto_ImageTags Tags(string kind, string tag)
	{
		var tags = new BaseItemDto_ImageTags();
		tags.AdditionalData[kind] = tag;
		return tags;
	}

	[Fact]
	public void AMovie_UsesItsOwnBackdropAndLogo()
	{
		var movie = new BaseItemDto { Id = Movie, BackdropImageTags = ["bd"], ImageTags = Tags("Logo", "lg") };

		Assert.Equal($"/Items/{Movie}/Images/Backdrop?tag=bd&maxWidth=1920", ItemArtwork.BackdropPath(movie, 1920));
		Assert.Equal($"/Items/{Movie}/Images/Logo?tag=lg&maxWidth=480", ItemArtwork.LogoPath(movie, 480));
		Assert.True(ItemArtwork.HasLogo(movie));
	}

	[Fact]
	public void AnEpisode_ShowsItsSeriesLogoAndBackdrop()
	{
		var episode = new BaseItemDto
		{
			Id = Movie,
			ParentLogoItemId = Series,
			ParentLogoImageTag = "plg",
			ParentBackdropItemId = Series,
			ParentBackdropImageTags = ["pbd"],
		};

		Assert.Equal($"/Items/{Series}/Images/Logo?tag=plg&maxWidth=480", ItemArtwork.LogoPath(episode, 480));
		Assert.Equal($"/Items/{Series}/Images/Backdrop?tag=pbd&maxWidth=1920", ItemArtwork.BackdropPath(episode, 1920));
	}

	[Fact]
	public void WithoutALogo_ThePageShowsTheTitleAsText()
	{
		var movie = new BaseItemDto { Id = Movie, BackdropImageTags = ["bd"], ImageTags = Tags("Primary", "p") };

		Assert.False(ItemArtwork.HasLogo(movie));
		Assert.Null(ItemArtwork.LogoPath(movie, 480));
		Assert.False(ItemArtwork.HasBackdrop(new BaseItemDto { Id = Movie }));
		Assert.Null(ItemArtwork.BackdropPath(null, 1920));
	}

	[Fact]
	public void AnEpisodePicture_IsItsThumbElseItsPrimary_AndAPersonWithoutAPhotoHasNone()
	{
		var episode = new BaseItemDto { Id = Movie, ImageTags = Tags("Primary", "p1") };
		var person = new BaseItemPerson { Id = Series, PrimaryImageTag = "face" };

		Assert.Equal($"/Items/{Movie}/Images/Primary?tag=p1&maxWidth=320", ItemArtwork.ThumbPath(episode, 320));
		episode.ImageTags!.AdditionalData["Thumb"] = "t1";
		Assert.Equal($"/Items/{Movie}/Images/Thumb?tag=t1&maxWidth=320", ItemArtwork.ThumbPath(episode, 320));
		Assert.Null(ItemArtwork.ThumbPath(new BaseItemDto { Id = Movie }, 320));
		Assert.Equal($"/Items/{Series}/Images/Primary?tag=face&maxWidth=160", ItemArtwork.PersonPath(person, 160));
		Assert.Null(ItemArtwork.PersonPath(new BaseItemPerson { Id = Series }, 160));
	}
}
