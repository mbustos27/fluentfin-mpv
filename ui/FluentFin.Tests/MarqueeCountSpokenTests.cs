using FluentFin.Core.ViewModels;
using FluentFin.Views;

namespace FluentFin.Tests;

/// <summary>
/// 1.6.1 (colpage queue, DriveIn.xaml:673): Drive-in's marquee writes its count in tubes beside a word ("6" "FEATURES"); Narrator
/// heard the tubes alone ("6"). The readout now says the count with its word, in plain case.
/// </summary>
public sealed class MarqueeCountSpokenTests
{
	[Theory]
	[InlineData(4, "SCREENS ON THE LOT", "4 screens on the lot")]
	[InlineData(1, "SCREEN ON THE LOT", "1 screen on the lot")]
	[InlineData(7, "", "7")]
	public void TheLibrariesPage_SaysItsCountWithItsWord(int count, string label, string spoken) =>
		Assert.Equal(spoken, new PageTitleFacts("", count, label).FeatureSpoken);

	[Theory]
	[InlineData("6 FEATURES", "6 features")]
	[InlineData("1 FEATURE", "1 feature")]
	[InlineData("12", "12")]
	public void ACollection_SaysItsCountWithItsWord(string features, string spoken)
	{
		var stage = new CollectionStage { Facts = CollectionHeaderFacts.Empty with { Features = features } };

		Assert.Equal(spoken, stage.FeatureSpoken);
		Assert.Equal(features.Split(' ')[0], stage.FeatureCount); // the tubes still show the digits only
	}
}
