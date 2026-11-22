using FluentFin.Core.Outside;

namespace FluentFin.Core.Tests.Outside;

/// <summary>
/// 1.7: PRIVACY.md says what the code does (the review of 2026-11-21: it named About for the update switch, left out the
/// installer download and Jellyseerr, and gave Wikidata "about a week" where the code keeps 60 days). The ages come from the
/// sources' own rules, so a change there fails here until the policy follows.
/// </summary>
public sealed class PrivacyPolicyTests
{
	private static string Policy => File.ReadAllText(Path.Combine(Path.GetDirectoryName(HomeAndLibraryXamlTests.RepoRoot)!, "PRIVACY.md")).Replace("\r\n", "\n");

	[Fact]
	public void EverySourcesCopy_IsDescribedAsTheCodeKeepsIt_TheSameInThePolicyAndInSettings()
	{
		// the owner, 2026-11-22: the column reads as Nixie's own copy of public lists, not the user's data, and the "What is
		// sent" cards say the same words (OutsideKeep, made from the sources' rules)
		var policy = Policy;
		Assert.Contains("| Nixie's own copy (public lists, not your data) |", policy, StringComparison.Ordinal);
		var hub = new OutsideHub(new HttpClient(new FuncHandler(_ => throw new InvalidOperationException())), OutsideRouting.Real, "1.7.0", new OutsideCache(OutsideKit.TempFolder()), new MemoryStore());
		var keyless = new OutsideSourcesSettingsViewModel(hub);
		var trending = new TrendingSourcesSettingsViewModel(new TrendingSources(hub, null, "client-id-for-the-test", tmdbAppToken: TrendingFake.AppToken), new TmdbToken(new MemoryStore()));
		var cards = keyless.Switches.Select(s => (s.Id, s.WhatIsSent)).Concat(trending.Switches.Select(s => (s.Id, s.WhatIsSent))).ToList();
		Assert.Equal(OutsideSources.All.Count, cards.Count);
		foreach (var source in OutsideSources.All)
		{
			var words = OutsideKeep.Describe(source.Id);
			Assert.Contains($"| {words} |", policy, StringComparison.Ordinal);
			Assert.EndsWith(words, cards.Single(c => c.Id == source.Id).WhatIsSent, StringComparison.Ordinal);
		}

		Assert.Equal("Nixie keeps its own copy of these public lists. Refreshed weekly; the last copy is used for up to 6 months if TMDb can't be reached.", OutsideKeep.Describe(OutsideSourceId.TmdbExports));
		Assert.Equal("Nixie keeps its own copy of these public lists. Refreshed daily; the last copy is used for up to 7 days if TMDb can't be reached.", OutsideKeep.Describe(OutsideSourceId.TmdbTrending));
		Assert.Equal("Nixie keeps its own copy of these public lists. Refreshed weekly; the last copy is used for up to 60 days if Wikidata can't be reached.", OutsideKeep.Describe(OutsideSourceId.Wikidata));
		Assert.StartsWith("Nixie keeps its own copy of these public lists. Refreshed every 6 hours", OutsideKeep.Describe(OutsideSourceId.Trakt), StringComparison.Ordinal);
		Assert.EndsWith("the last copy is used for up to 24 hours if Trakt can't be reached.", OutsideKeep.Describe(OutsideSourceId.Trakt), StringComparison.Ordinal);
		Assert.Contains("| Nixie's own TMDb key (or yours, if you enter one) |", policy, StringComparison.Ordinal);
		Assert.Contains("at most 24 hours for Trakt,\n  7 days for TMDb trending, 60 days for Wikidata and 6 months for TMDb's export files", policy, StringComparison.Ordinal);
		Assert.Equal(TimeSpan.FromHours(24), OutsideSources.Trakt.MaxAge);
		Assert.Equal(TimeSpan.FromDays(7), OutsideSources.TmdbTrending.MaxAge);
		Assert.Equal(TimeSpan.FromDays(60), OutsideSources.Wikidata.MaxAge);
		Assert.Equal(TimeSpan.FromDays(182), OutsideSources.TmdbExports.MaxAge);
		Assert.DoesNotContain("About a week", policy, StringComparison.Ordinal);
		Assert.DoesNotContain("kept at most", policy, StringComparison.Ordinal);
	}

	[Fact]
	public void TheUpdateSwitch_IsWhereSettingsHasIt_AndTheDownloadIsNamed()
	{
		var policy = Policy;
		Assert.Contains("**Settings → Updates → Check for updates**", policy, StringComparison.Ordinal);
		Assert.DoesNotContain("Settings → About → Check for updates", policy, StringComparison.Ordinal);
		Assert.Contains("downloads its installer and checksum from GitHub", policy, StringComparison.Ordinal);
		Assert.DoesNotContain("Only that request is sent", policy, StringComparison.Ordinal);
		var updates = File.ReadAllText(Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "FluentFin", "Controls", "UpdateSettingsPanel.xaml"));
		Assert.Contains("Text=\"Updates\"", updates, StringComparison.Ordinal);
		Assert.Contains("Header=\"Check for updates\"", updates, StringComparison.Ordinal);
	}

	[Fact]
	public void Jellyseerr_IsNamed_OnlyWhenTheUserSetsItUp()
	{
		var policy = Policy;
		Assert.Contains("**Jellyseerr**, only if you set up your own Jellyseerr server in **Settings → Integrations**", policy, StringComparison.Ordinal);
		Assert.Contains("image proxy", policy, StringComparison.Ordinal);
	}
}
