using FluentFin.Core.Journal;

namespace FluentFin.Core.Tests.Journal;

public sealed class StarRatingTests
{
	[Theory]
	[InlineData(0, "EEEEE")]
	[InlineData(1, "HEEEE")]
	[InlineData(2, "FEEEE")]
	[InlineData(7, "FFFHE")]
	[InlineData(10, "FFFFF")]
	[InlineData(11, "EEEEE")]
	[InlineData(-3, "EEEEE")]
	public void Fills_ShowHalfStars(int value, string expected)
	{
		var drawn = string.Concat(StarRating.Fills(value).Select(x => x switch { StarFill.Full => 'F', StarFill.Half => 'H', _ => 'E' }));

		Assert.Equal(expected, drawn);
	}

	[Theory]
	[InlineData(0, 1)]
	[InlineData(9.9, 1)]
	[InlineData(10, 2)]
	[InlineData(19.9, 2)]
	[InlineData(20, 3)]
	[InlineData(69, 7)]
	[InlineData(99.9, 10)]
	[InlineData(100, 10)]
	[InlineData(150, 10)]
	[InlineData(-5, 1)]
	public void ThePointer_PicksTheHalfStarUnderIt(double x, int expected)
	{
		// five stars in 100 px: each half star is 10 px
		Assert.Equal(expected, StarRating.FromPointer(x, 100));
	}

	[Fact]
	public void APointerOnNothing_IsNoValue()
	{
		Assert.Equal(0, StarRating.FromPointer(10, 0));
		Assert.Equal(0, StarRating.FromPointer(double.NaN, 100));
	}

	[Fact]
	public void ClickingTheSameValue_ClearsIt()
	{
		Assert.Equal(7, StarRating.Click(0, 7));
		Assert.Equal(4, StarRating.Click(7, 4));
		Assert.Equal(0, StarRating.Click(7, 7));
	}

	[Fact]
	public void Keys_StepByHalfAStar_WithinTheRange()
	{
		Assert.Equal(1, StarRating.Step(0, 1));
		Assert.Equal(0, StarRating.Step(1, -1));
		Assert.Equal(0, StarRating.Step(0, -1));
		Assert.Equal(10, StarRating.Step(10, 1));
		Assert.Equal(8, StarRating.Step(7, 1));
	}

	[Fact]
	public void Text_IsStarsOutOfFive()
	{
		Assert.Equal("3.5", StarRating.ToStars(7));
		Assert.Equal("5", StarRating.ToStars(10));
		Assert.Equal("", StarRating.ToStars(0));
		Assert.Equal("0.5 of 5 stars", StarRating.Describe(1));
		Assert.Equal("Not rated", StarRating.Describe(0));
	}

	[Theory]
	[InlineData(null, null)]
	[InlineData(0.0, null)]
	[InlineData(7.0, 7)]
	[InlineData(6.6, 7)]
	[InlineData(0.4, null)]
	[InlineData(12.0, 10)]
	public void JellyfinsRating_IsReadAsHalfStars(double? jellyfin, int? expected)
	{
		Assert.Equal(expected, StarRating.FromJellyfin(jellyfin));
	}
}
