using System.Xml.Linq;
using FluentFin.Core.Outside;
using FluentFin.Core.Settings;

namespace FluentFin.Core.Tests.Outside;

/// <summary>
/// 1.7 trending: Settings &gt; Look up lists online's Trakt and TMDb trending as the user and Narrator meet them (their switches,
/// "What is sent" cards, status lines and the token field, every one named), the way out of each (off, Remove), Trakt's
/// "Not available in this build", and About's Trakt line.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class TrendingSettingsTests
{
	private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

	private static XDocument Page => XDocument.Load(Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "FluentFin", "Views", "SettingsPage.xaml"));

	private static XElement Named(XElement root, string name) => root.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

	private static string? Narrator(XElement e) => (string?)e.Attribute("AutomationProperties.Name");

	[Theory]
	[InlineData("TraktExpander", "TraktSwitch", "Trakt")]
	[InlineData("TmdbTrendingExpander", "TmdbTrendingSwitch", "TmdbTrending")]
	public void EachSource_HasANamedSwitch_AWhatIsSentCard_AndAStatusLine_InTheMarkedPanel(string expanderName, string switchName, string member)
	{
		var panel = Named(Page.Root!, "TrendingSourcesPanel");
		var expander = Named(panel, expanderName);
		Assert.Equal($"{{x:Bind TrendingSources.{member}.Name}}", (string?)expander.Attribute("Header"));
		Assert.Equal($"{{x:Bind TrendingSources.{member}.Description}}", (string?)expander.Attribute("Description"));
		var toggle = Named(expander, switchName);
		Assert.Equal("ToggleSwitch", toggle.Name.LocalName);
		Assert.Equal($"{{x:Bind TrendingSources.{member}.Name}}", Narrator(toggle));
		Assert.Equal($"{{x:Bind TrendingSources.{member}.IsOn, Mode=TwoWay}}", (string?)toggle.Attribute("IsOn"));

		var cards = expander.Descendants().Where(e => e.Name.LocalName == "SettingsCard").ToList();
		var sent = Assert.Single(cards, c => (string?)c.Attribute("Header") == "What is sent");
		Assert.Equal($"{{x:Bind TrendingSources.{member}.WhatIsSentName}}", Narrator(sent));
		Assert.Equal($"{{x:Bind TrendingSources.{member}.LinkName}}", Narrator(sent.Descendants().Single(e => e.Name.LocalName == "HyperlinkButton")));
		var status = Assert.Single(cards, c => (string?)c.Attribute("Header") == "Status");
		Assert.Equal($"{{x:Bind TrendingSources.{member}.StatusName}}", Narrator(status));
		Assert.Equal($"{{x:Bind TrendingSources.{member}.Status, Mode=OneWay}}", (string?)status.Attribute("Description"));
	}

	[Fact]
	public void TraktsSwitch_IsDisabledInABuildWithoutAClientId_AndTmdbsWithoutAnyToken()
	{
		var toggle = Named(Page.Root!, "TraktSwitch");
		Assert.Equal("{x:Bind TrendingSources.Trakt.IsAvailable}", (string?)toggle.Attribute("IsEnabled"));
		// TMDb's follows the token (entered, removed, or Nixie's key refused)
		Assert.Equal("{x:Bind TrendingSources.TmdbTrending.IsAvailable, Mode=OneWay}", (string?)Named(Page.Root!, "TmdbTrendingSwitch").Attribute("IsEnabled"));
	}

	[Fact]
	public void TheTokenField_IsAPasswordBox_NamedForNarrator_WithSaveAndRemove()
	{
		var card = Named(Page.Root!, "TmdbTokenCard");
		Assert.Equal("Your TMDb token", Narrator(card));
		var box = Named(card, "TmdbTokenBox");
		Assert.Equal("PasswordBox", box.Name.LocalName);   // the token never shows on screen
		Assert.Equal("TMDb API Read Access Token", Narrator(box));
		Assert.Equal("{x:Bind TrendingSources.TokenEntry, Mode=TwoWay}", (string?)box.Attribute("Password"));
		Assert.Equal("Save TMDb token", Narrator(Named(card, "SaveTmdbTokenButton")));
		var remove = Named(card, "RemoveTmdbTokenButton");
		Assert.Equal("Remove TMDb token", Narrator(remove));
		Assert.Equal("{x:Bind c:Converters.BooleanToVisibility(TrendingSources.HasToken), Mode=OneWay}", (string?)remove.Attribute("Visibility"));
		var message = Named(card, "TmdbTokenMessage");
		Assert.Equal("Polite", (string?)message.Attribute("AutomationProperties.LiveSetting"));
	}

	[Fact]
	public void About_CarriesTraktsDisclaimer_InItsMarkedPlace()
	{
		var credits = Named(Page.Root!, "ListCreditsPanel");
		Assert.Equal("{x:Bind outside:TrendingCredits.TraktDisclaimer}", (string?)Named(credits, "TraktDisclaimer").Attribute("Text"));
		Assert.Equal("Nixie uses Trakt but is not endorsed or certified by Trakt.", TrendingCredits.TraktDisclaimer);
		var readme = File.ReadAllText(Path.Combine(Path.GetDirectoryName(HomeAndLibraryXamlTests.RepoRoot)!, "README.md"));
		Assert.Contains("Nixie uses Trakt but is not endorsed or certified by Trakt.", readme, StringComparison.Ordinal);
	}

	[Fact]
	public void About_ShowsTmdbsLogo_BesideItsNotice_SmallerThanNixiesMarks_AndTheReadmeCarriesTheNoticeAsText()
	{
		// the owner's yes (2026-11-22): TMDb's "Alt short (blue)" logo, unaltered in colour, aspect and orientation
		var credits = Named(Page.Root!, "ListCreditsPanel");
		var children = credits.Elements().Select(e => (string?)e.Attribute(X + "Name")).Where(n => n is not null).ToList();
		Assert.True(children.IndexOf("TmdbLogo") >= 0 && children.IndexOf("TmdbLogo") == children.IndexOf("TmdbNotice") - 1, string.Join(",", children));
		var logo = Named(credits, "TmdbLogo");
		Assert.Equal("Image", logo.Name.LocalName);
		Assert.Equal("TMDB logo", Narrator(logo));
		Assert.InRange(double.Parse((string)logo.Attribute("Height")!, System.Globalization.CultureInfo.InvariantCulture), 12, 16);
		Assert.Equal("Uniform", (string?)logo.Attribute("Stretch"));
		var svg = logo.Descendants().Single(e => e.Name.LocalName == "SvgImageSource");
		Assert.Equal("ms-appx:///Assets/Credits/tmdb-logo.svg", (string?)svg.Attribute("UriSource"));
		// rasterized at the logo's own aspect (273.42 x 35.52)
		var aspect = double.Parse((string)svg.Attribute("RasterizePixelWidth")!) / double.Parse((string)svg.Attribute("RasterizePixelHeight")!);
		Assert.InRange(aspect, 273.42 / 35.52 - 0.05, 273.42 / 35.52 + 0.05);

		var file = File.ReadAllText(Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "FluentFin", "Assets", "Credits", "tmdb-logo.svg"));
		Assert.Contains("viewBox=\"0 0 273.42 35.52\"", file, StringComparison.Ordinal);
		foreach (var colour in new[] { "#90cea1", "#3cbec9", "#00b3e5" })
		{
			Assert.Contains($"stop-color=\"{colour}\"", file, StringComparison.Ordinal);
		}

		Assert.Contains("fill=\"url(#linear-gradient)\"", file, StringComparison.Ordinal);
		Assert.DoesNotContain("<style", file, StringComparison.Ordinal);   // Windows' SVG renderer doesn't read style blocks
		Assert.DoesNotContain("<script", file, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("foreignObject", file, StringComparison.Ordinal);
		Assert.DoesNotContain("href=\"http", file, StringComparison.Ordinal);
		Assert.Contains(@"Assets\Credits\tmdb-logo.svg", File.ReadAllText(Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "FluentFin", "FluentFin.csproj")), StringComparison.Ordinal);

		var root = Path.GetDirectoryName(HomeAndLibraryXamlTests.RepoRoot)!;
		Assert.Contains(OutsideCredits.TmdbNotice, File.ReadAllText(Path.Combine(root, "README.md")), StringComparison.Ordinal);
		Assert.DoesNotContain("tmdb-logo", File.ReadAllText(Path.Combine(root, "README.md")), StringComparison.Ordinal);   // text only there
		Assert.Contains(@"TMDB logo (app\Assets\Credits\tmdb-logo.svg)", File.ReadAllText(Path.Combine(root, "tools", "release", "build-release.ps1")), StringComparison.Ordinal);
	}

	[Fact]
	public void ThePage_FollowsTheTrendingSources_OnlyWhileShown()
	{
		var code = File.ReadAllText(Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "FluentFin", "Views", "SettingsPage.xaml.cs"));
		Assert.Contains("Loaded += (_, _) => TrendingSources.Attach();", code, StringComparison.Ordinal);
		Assert.Contains("Unloaded += (_, _) => TrendingSources.Detach();", code, StringComparison.Ordinal);
	}

	[Fact]
	public void TheApp_WiresTheSources_TheHarnessClientId_HomesCache_AndThePagesService()
	{
		var app = File.ReadAllText(Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "FluentFin", "App.xaml.cs"));
		// a harness run's Client ID is the fake's own; a real run's, the one built into Nixie.dll (none in a dev build)
		Assert.Contains("Core.Outside.TraktClientId.Current(harness: hub.Routing.IsHarness)", app, StringComparison.Ordinal);
		Assert.Contains("tmdbAppToken: Core.Outside.TmdbAppToken.Current(harness: hub.Routing.IsHarness)", app, StringComparison.Ordinal);
		Assert.Contains("services.AddSingleton<Core.Outside.TmdbToken>();", app, StringComparison.Ordinal);
		// Home's saved page goes stale when the lists change (the harness's first pass)
		Assert.Contains("sp.GetRequiredService<HomeCache>()));", app, StringComparison.Ordinal);
		Assert.Contains("Core.Outside.TrendingServices.Collections = GetService<Core.Outside.TrendingCollections>();", app, StringComparison.Ordinal);
	}

	[Fact]
	public void TheViewModel_NamesEverything_AndSaysWhatIsSent()
	{
		using var rig = new TrendingRig(appToken: TrendingFake.AppToken);   // as a release: Nixie's own TMDb key built in
		var vm = new TrendingSourcesSettingsViewModel(rig.Sources, rig.Token);

		Assert.Equal(["Trending from Trakt", "Trending from TMDb"], vm.Switches.Select(x => x.Name));
		Assert.Equal(["What is sent to Trakt", "What is sent to TMDb"], vm.Switches.Select(x => x.WhatIsSentName));
		Assert.Equal(["Trakt, opens in your browser", "TMDb, opens in your browser"], vm.Switches.Select(x => x.LinkName));
		Assert.Equal(["Trending from Trakt status", "Trending from TMDb status"], vm.Switches.Select(x => x.StatusName));
		Assert.All(vm.Switches, s =>
		{
			Assert.StartsWith("Sent: ", s.WhatIsSent, StringComparison.Ordinal);
			Assert.Contains("Not sent: your server, account, libraries or anything you watch.", s.WhatIsSent, StringComparison.Ordinal);
			Assert.EndsWith("Off by default.", s.Description, StringComparison.Ordinal);
			Assert.Equal(Uri.UriSchemeHttps, s.Link.Uri.Scheme);
			Assert.False(s.IsOn);
			Assert.Equal(TrendingStatus.Off, s.Status);
		});
		Assert.Contains("never signs you in to Trakt", vm.Trakt.WhatIsSent, StringComparison.Ordinal);
		Assert.Contains("only in the request's Authorization header", vm.TmdbTrending.WhatIsSent, StringComparison.Ordinal);
		Assert.Contains("No pictures are fetched from TMDb", vm.TmdbTrending.WhatIsSent, StringComparison.Ordinal);
	}

	[Fact]
	public void ASwitch_TurnsItsSourceOnAndOff_AndTheStatusFollows()
	{
		using var rig = new TrendingRig();
		var vm = new TrendingSourcesSettingsViewModel(rig.Sources, rig.Token);
		var changed = new List<string?>();
		vm.Trakt.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

		vm.Trakt.IsOn = true;
		Assert.True(rig.Store.ReadSetting(SettingKeys.LookUpTrakt.Name, false));
		Assert.True(vm.Trakt.IsOn);
		Assert.Contains(nameof(TrendingSourceSwitch.Status), changed);
		Assert.NotEqual(TrendingStatus.Off, vm.Trakt.Status);

		// the way out
		vm.Trakt.IsOn = false;
		Assert.False(rig.Store.ReadSetting(SettingKeys.LookUpTrakt.Name, false));
		Assert.Equal(TrendingStatus.Off, vm.Trakt.Status);
	}

	[Fact]
	public void WithoutAClientId_TraktSaysNotAvailable_AndCantBeTurnedOn()
	{
		using var rig = new TrendingRig(clientId: null);
		var vm = new TrendingSourcesSettingsViewModel(rig.Sources, rig.Token);

		Assert.False(vm.Trakt.IsAvailable);
		Assert.StartsWith("Not available in this build", vm.Trakt.Description, StringComparison.Ordinal);
		vm.Trakt.IsOn = true;
		Assert.False(vm.Trakt.IsOn);
		Assert.False(rig.Store.ReadSetting(SettingKeys.LookUpTrakt.Name, false));
		Assert.Equal("Not available in this build.", vm.Trakt.Status);
		// no TMDb key of Nixie's own either, and no token of the user's: not available until one is entered
		Assert.False(vm.TmdbTrending.IsAvailable);
		Assert.Equal(TrendingStatus.TmdbNotAvailable, vm.TmdbTrending.Status);
	}

	[Fact]
	public void WithNixiesOwnTmdbKey_TheSwitchSaysSo_AndYoursCanReplaceIt()
	{
		using var rig = new TrendingRig(appToken: TrendingFake.AppToken);
		var vm = new TrendingSourcesSettingsViewModel(rig.Sources, rig.Token);

		Assert.True(vm.TmdbTrending.IsAvailable);
		Assert.Equal("Films and shows trending this week. Uses Nixie's own TMDb key. You can enter your own instead. Off by default.", vm.TmdbTrending.Description);
		Assert.Contains("Nixie's TMDb key (or yours, if you enter one), only in the request's Authorization header", vm.TmdbTrending.WhatIsSent, StringComparison.Ordinal);
		Assert.StartsWith("Nixie uses its own TMDb key. To use yours instead", vm.TokenDescription, StringComparison.Ordinal);
		Assert.False(vm.HasToken);

		vm.TokenEntry = TrendingFake.Token;
		vm.SaveTokenCommand.Execute(null);
		Assert.StartsWith("Your own token is kept on this PC, encrypted for your Windows account, and used instead of Nixie's key.", vm.TokenDescription, StringComparison.Ordinal);

		vm.RemoveTokenCommand.Execute(null);
		Assert.Equal("Removed. Nixie uses its own TMDb key again.", vm.TokenMessage);
		Assert.True(vm.TmdbTrending.IsAvailable);
	}

	[Fact]
	public void TheToken_IsSaved_NeverShownBack_RefusedWhenItIsTheShortKey_AndRemoved()
	{
		using var rig = new TrendingRig();
		var vm = new TrendingSourcesSettingsViewModel(rig.Sources, rig.Token);
		vm.TmdbTrending.IsOn = true;          // no key at all: it can't turn on
		Assert.False(vm.TmdbTrending.IsOn);
		Assert.Equal(TrendingStatus.TmdbNotAvailable, vm.TmdbTrending.Status);
		Assert.False(vm.SaveTokenCommand.CanExecute(null));

		vm.TokenEntry = "0123456789abcdef0123456789abcdef";
		vm.SaveTokenCommand.Execute(null);
		Assert.Equal("That's TMDb's short API key. Paste the API Read Access Token (the long one) from the same page.", vm.TokenMessage);
		Assert.False(vm.HasToken);

		vm.TokenEntry = TrendingFake.Token;
		vm.SaveTokenCommand.Execute(null);
		Assert.True(vm.HasToken);
		Assert.Equal("", vm.TokenEntry);   // the field empties: the token is never shown back
		Assert.Equal("Saved. Nixie uses it for TMDb trending.", vm.TokenMessage);
		Assert.StartsWith("Your own token is kept on this PC", vm.TokenDescription, StringComparison.Ordinal);
		Assert.Equal(TrendingFake.Token, rig.Token.Read());
		Assert.True(vm.TmdbTrending.IsAvailable);
		vm.TmdbTrending.IsOn = true;
		Assert.True(vm.TmdbTrending.IsOn);
		Assert.NotEqual(TrendingStatus.TmdbNotAvailable, vm.TmdbTrending.Status);

		vm.RemoveTokenCommand.Execute(null);
		Assert.False(vm.HasToken);
		Assert.Null(rig.Token.Read());
		Assert.Equal("Removed.", vm.TokenMessage);
		Assert.Equal(TrendingStatus.TmdbNotAvailable, vm.TmdbTrending.Status);
		Assert.False(vm.TmdbTrending.IsAvailable);
	}

	[Fact]
	public void Detaching_StopsFollowingTheSources()
	{
		using var rig = new TrendingRig();
		var vm = new TrendingSourcesSettingsViewModel(rig.Sources, rig.Token);
		var heard = 0;
		vm.Trakt.PropertyChanged += (_, _) => heard++;

		vm.Detach();
		rig.Hub.SetOn(OutsideSourceId.Trakt, true);
		Assert.Equal(0, heard);

		vm.Attach();
		heard = 0;
		rig.Hub.SetOn(OutsideSourceId.Trakt, false);
		Assert.True(heard > 0);
	}
}
