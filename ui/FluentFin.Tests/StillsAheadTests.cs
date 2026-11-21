using System.Xml.Linq;
using FluentFin.Services;
using FluentFin.Themes.Cinematic.Views;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.5 (season speed): the stills of the seasons either side of the one shown are asked ahead into WinINet's cache
/// (WinUI's picture loader reads it), each picture once, at the width the page's rows ask for it, so a click to a season
/// asked ahead shows its pictures with its rows.
/// </summary>
public sealed class StillsAheadTests
{
	private static BaseItemDto Episode(int n) => new() { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Episode, Name = $"Episode {n}", IndexNumber = n };

	private static string? Path(BaseItemDto episode) => episode.IndexNumber is 3 ? null : $"/Items/{episode.Id:N}/Images/Primary?maxWidth=336";

	[Fact]
	public void EachPicture_IsAskedOnce_TheFirstOfASeasonOnly()
	{
		var asked = new HashSet<string>();
		var order = new Queue<string>();
		var season = Enumerable.Range(1, 30).Select(Episode).ToList();

		var first = StillsAhead.Plan(season, Path, "http://server:8096/", asked, order, StillsAhead.MostPerSeason, StillsAhead.MostRemembered);

		Assert.Equal(StillsAhead.MostPerSeason - 1, first.Count); // the first 24, less the one with no picture
		Assert.All(first, url => Assert.StartsWith("http://server:8096/Items/", url, StringComparison.Ordinal));
		Assert.Empty(StillsAhead.Plan(season, Path, "http://server:8096", asked, order, StillsAhead.MostPerSeason, StillsAhead.MostRemembered));
	}

	[Fact]
	public void TheOldestPictures_AreForgottenPastTheMostRemembered()
	{
		var asked = new HashSet<string>();
		var order = new Queue<string>();
		var episodes = Enumerable.Range(10, 6).Select(Episode).ToList();

		StillsAhead.Plan(episodes, Path, "http://server", asked, order, most: 24, remembered: 4);

		Assert.Equal(4, asked.Count);
		Assert.Equal(4, order.Count);
		Assert.Single(StillsAhead.Plan(episodes.Take(1), Path, "http://server", asked, order, most: 24, remembered: 4)); // forgotten: asked again
	}

	// ---- review m4: the asking stops with the page, and forgets only what is not cached -------------------------------------

	private static readonly string[] Urls = ["a", "b", "c", "d", "e"];

	[Fact]
	public async Task EveryPicture_IsAsked_AndNothingForgotten()
	{
		var asked = new System.Collections.Concurrent.ConcurrentBag<string>();
		var forgotten = new System.Collections.Concurrent.ConcurrentBag<string>();

		await StillsAhead.Ask(Urls, () => true, url => { asked.Add(url); return true; }, TimeSpan.Zero, urls => { foreach (var u in urls) { forgotten.Add(u); } });

		Assert.Equal(Urls, asked.Order());
		Assert.Empty(forgotten);
	}

	[Fact]
	public async Task APageLeft_AsksNothingMore_AndForgetsWhatItDidNotAsk()
	{
		var asked = new System.Collections.Concurrent.ConcurrentBag<string>();
		var forgotten = new System.Collections.Concurrent.ConcurrentBag<string>();
		var checks = 0;

		await StillsAhead.Ask(Urls, () => Interlocked.Increment(ref checks) <= 2, url => { asked.Add(url); return true; }, TimeSpan.Zero,
			urls => { foreach (var u in urls) { forgotten.Add(u); } });

		Assert.Equal(["a", "b"], asked.Order());
		Assert.Equal(["c", "d", "e"], forgotten.Order());
	}

	[Fact]
	public async Task AFailedPicture_StopsTheRest_AndOnlyItAndTheUnaskedAreForgotten()
	{
		var asked = new System.Collections.Concurrent.ConcurrentBag<string>();
		var forgotten = new System.Collections.Concurrent.ConcurrentBag<string>();
		using var bForgotten = new ManualResetEventSlim();

		await StillsAhead.Ask(Urls, () => true, url =>
		{
			asked.Add(url);
			if (url == "a")
			{
				bForgotten.Wait(TimeSpan.FromSeconds(5)); // a is still on its way when b fails
			}

			return url != "b";
		}, TimeSpan.Zero, urls =>
		{
			foreach (var u in urls)
			{
				forgotten.Add(u);
				if (u == "b")
				{
					bForgotten.Set();
				}
			}
		});

		Assert.Equal(["a", "b"], asked.Order());
		Assert.Equal(["b", "c", "d", "e"], forgotten.Order()); // a is in the cache: remembered
	}

	/// <summary>The width asked ahead is the one Cinematic's stills ask for, in both templates of both pages.</summary>
	[Theory]
	[InlineData(@"Themes\Cinematic\Views\CinematicSeriesPage.xaml")]
	[InlineData(@"Themes\Cinematic\Views\CinematicSeasonPage.xaml")]
	public void CinematicsStillWidths_AreTheTemplates(string page)
	{
		var xaml = XDocument.Load(System.IO.Path.Combine(FlyleafEngineTests.RepoRoot, "FluentFin", page));
		XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
		string Width(string key) => xaml.Descendants().Single(e => (string?)e.Attribute(x + "Key") == key).Elements().Single().Attribute("DecodeWidth")!.Value;

		Assert.Equal(CinematicSeriesSupport.LargeStillWidth.ToString(), Width("EpisodeLarge"));
		Assert.Equal(CinematicSeriesSupport.SmallStillWidth.ToString(), Width("EpisodeSmall"));
	}

	/// <summary>Every series and season page asks the seasons either side's stills ahead, but Editorial's (its widths follow its layout).</summary>
	[Theory]
	[InlineData(@"Views\SeriesPage.xaml.cs", true)]
	[InlineData(@"Views\SeasonPage.xaml.cs", true)]
	[InlineData(@"Themes\DriveIn\Views\DriveInSeriesPage.xaml.cs", true)]
	[InlineData(@"Themes\DriveIn\Views\DriveInSeasonPage.xaml.cs", true)]
	[InlineData(@"Themes\Cinematic\Views\CinematicSeriesPage.xaml.cs", true)]
	[InlineData(@"Themes\Cinematic\Views\CinematicSeasonPage.xaml.cs", true)]
	[InlineData(@"Themes\Editorial\Views\EditorialSeriesPage.xaml.cs", false)]
	[InlineData(@"Themes\Editorial\Views\EditorialSeasonPage.xaml.cs", false)]
	public void TheSeriesAndSeasonPages_FollowStillsAhead(string page, bool follows)
	{
		var code = File.ReadAllText(System.IO.Path.Combine(FlyleafEngineTests.RepoRoot, "FluentFin", page));
		Assert.Equal(follows, code.Contains("StillsAhead.Follow(this,", StringComparison.Ordinal));
	}
}
