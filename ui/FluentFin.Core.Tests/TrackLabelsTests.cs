using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.2.0 (1.1.3 UX audit, layout item 16): tracks are named as streaming apps name them, the language and what tells two
/// tracks apart, never the codec or the container.
/// </summary>
public sealed class TrackLabelsTests
{
	private static MediaStream Audio(string? language, int? channels = 2, string? title = null) =>
		new() { Type = MediaStream_Type.Audio, Language = language, Channels = channels, Title = title, Codec = "aac" };

	private static MediaStream Subtitle(string? language, bool forced = false, bool hearingImpaired = false, string? title = null) =>
		new() { Type = MediaStream_Type.Subtitle, Language = language, IsForced = forced, IsHearingImpaired = hearingImpaired, Title = title, Codec = "subrip" };

	private static string Name(MediaStream stream) => TrackLabels.Name(TrackFacts.From(stream));

	[Theory]
	[InlineData("eng", 2, null, "English")]
	[InlineData("eng", 6, null, "English (5.1)")]
	[InlineData("spa", 8, null, "Spanish (7.1)")]
	[InlineData("fre", 2, null, "French")]                                     // a bibliographic code
	[InlineData("de", 1, null, "German")]
	[InlineData("eng", 2, "AAC Stereo", "English")]                           // a title of codec words says nothing
	[InlineData("eng", 2, "English", "English")]
	[InlineData("eng", 2, "Director's Commentary", "English – Director's Commentary")]
	[InlineData(null, 2, "Commentary", "Commentary")]
	[InlineData("und", 2, null, "Unknown language")]
	[InlineData("xx-not-a-language", 2, null, "xx-not-a-language")]
	public void AnAudioTrack_IsItsLanguage_WithSurroundAndItsOwnTitle(string? language, int? channels, string? title, string expected)
	{
		Assert.Equal(expected, Name(Audio(language, channels, title)));
	}

	[Theory]
	[InlineData("eng", false, false, null, "English")]
	[InlineData("eng", true, false, null, "English (Forced)")]
	[InlineData("eng", false, true, null, "English (CC)")]
	[InlineData("eng", false, false, "English SDH", "English (CC)")]
	[InlineData("eng", false, false, "Forced", "English (Forced)")]
	[InlineData("ger", false, false, "SRT - External", "German")]
	public void ASubtitleTrack_IsItsLanguage_ForcedAndCaptionsSaid(string language, bool forced, bool hearingImpaired, string? title, string expected)
	{
		Assert.Equal(expected, Name(Subtitle(language, forced, hearingImpaired, title)));
	}

	[Fact]
	public void TheServersLocalizedName_Wins()
	{
		var stream = Audio("eng");
		typeof(MediaStream).GetProperty(nameof(MediaStream.LocalizedLanguage))!.SetValue(stream, "Anglais");

		Assert.Equal("Anglais", Name(stream));
	}

	[Fact]
	public void ThePickersNone_IsOff()
	{
		Assert.Equal("Off", Name(new MediaStream()));
		Assert.Equal("Off", TrackLabels.Name(TrackFacts.From(null)));
	}

	[Fact]
	public void TwoTracksThatReadTheSame_AreToldApart_ByChannelsThenByNumber()
	{
		var labels = TrackLabels.Labels([Audio("eng", 2), Audio("eng", 1), Audio("eng", 6), Audio("eng", 2), Audio("spa", 2)]);

		Assert.Equal(["English (Stereo)", "English (Mono)", "English (5.1)", "English (Stereo, 2)", "Spanish"], labels);
	}

	[Fact]
	public void AListOfDistinctTracks_KeepsItsShortLabels()
	{
		Assert.Equal(["Off", "English", "English (CC)", "Spanish (Forced)"],
			TrackLabels.Labels([new MediaStream(), Subtitle("eng"), Subtitle("eng", hearingImpaired: true), Subtitle("spa", forced: true)]));
	}

	[Fact]
	public void TheCodecIsNeverInALabel()
	{
		var labels = TrackLabels.Labels([Audio("eng", 6, "TrueHD 5.1 Atmos"), Subtitle("eng", title: "PGS")]);

		Assert.All(labels, l => Assert.DoesNotContain("TrueHD", l));
		Assert.All(labels, l => Assert.DoesNotContain("PGS", l));
	}
}
