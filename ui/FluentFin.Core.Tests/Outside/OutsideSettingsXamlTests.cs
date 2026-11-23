using System.Xml.Linq;
using FluentFin.Core.Outside;

namespace FluentFin.Core.Tests.Outside;

/// <summary>
/// 1.7 keyless: Settings &gt; Look up lists online and About's credits as the user and Narrator meet them: each switch named
/// as its card says, its "What is sent" card and status line named, the place stream T fills, and TMDb's notice.
/// </summary>
public sealed class OutsideSettingsXamlTests
{
	private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

	private static XElement Section => Load("FluentFin/Views/SettingsPage.xaml").Descendants().Single(e => (string?)e.Attribute(X + "Name") == "OutsideListsSection");

	private static XElement Named(XElement root, string name) => root.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

	private static string? Narrator(XElement e) => (string?)e.Attribute("AutomationProperties.Name");

	[Fact]
	public void The_section_has_a_level_2_heading_and_says_what_it_does()
	{
		var heading = Section.Elements().First();
		Assert.Equal("Look up lists online", (string?)heading.Attribute("Text"));
		Assert.Equal("Level2", (string?)heading.Attribute("AutomationProperties.HeadingLevel"));
		Assert.Contains("Nothing about the server you use, your account or your libraries is sent", (string?)Named(Section, "OutsideListsIntro").Attribute("Text"), StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("TmdbExportsExpander", "TmdbExportsSwitch", "TmdbExports", "TMDb")]
	[InlineData("WikidataExpander", "WikidataSwitch", "Wikidata", "Wikidata")]
	public void Each_source_has_a_switch_named_as_its_card_a_What_is_sent_card_and_a_status_line(string expanderName, string switchName, string member, string service)
	{
		var expander = Named(Section, expanderName);
		Assert.Equal($"{{x:Bind OutsideSources.{member}.Name}}", (string?)expander.Attribute("Header"));
		var toggle = Named(expander, switchName);
		Assert.Equal("ToggleSwitch", toggle.Name.LocalName);
		Assert.Equal($"{{x:Bind OutsideSources.{member}.Name}}", Narrator(toggle));
		Assert.Equal($"{{x:Bind OutsideSources.{member}.IsOn, Mode=TwoWay}}", (string?)toggle.Attribute("IsOn"));

		var cards = expander.Descendants().Where(e => e.Name.LocalName == "SettingsCard").ToList();
		var sent = Assert.Single(cards, c => (string?)c.Attribute("Header") == "What is sent");
		Assert.Equal($"What is sent to {service}", Narrator(sent));
		Assert.Equal($"{{x:Bind OutsideSources.{member}.WhatIsSent}}", (string?)sent.Attribute("Description"));
		Assert.Equal($"{service}, opens in your browser", Narrator(sent.Descendants().Single(e => e.Name.LocalName == "HyperlinkButton")));
		var status = Assert.Single(cards, c => (string?)c.Attribute("Header") == "Status");
		Assert.Equal($"{{x:Bind OutsideSources.{member}.StatusName}}", Narrator(status));
		Assert.Equal($"{{x:Bind OutsideSources.{member}.Status, Mode=OneWay}}", (string?)status.Attribute("Description"));
	}

	[Fact]
	public void The_view_model_gives_each_switch_a_name_a_What_is_sent_text_and_a_status_name()
	{
		var hub = new OutsideHub(new HttpClient(new FuncHandler(_ => throw new InvalidOperationException())), OutsideRouting.Real, "1.7.0",
			new OutsideCache(OutsideKit.TempFolder()), new MemoryStore());
		var settings = new OutsideSourcesSettingsViewModel(hub);

		Assert.Equal(["Franchise and keyword names from TMDb", "Awards and genres from Wikidata"], settings.Switches.Select(x => x.Name));
		Assert.All(settings.Switches, s =>
		{
			Assert.StartsWith("Sent: ", s.WhatIsSent, StringComparison.Ordinal);
			Assert.Contains("Not sent: the server you use, your account, libraries or anything you watch.", s.WhatIsSent, StringComparison.Ordinal);
			Assert.Equal(s.Name + " status", s.StatusName);
			Assert.Equal(Uri.UriSchemeHttps, s.Link.Uri.Scheme);
			Assert.EndsWith("Off by default.", s.Description, StringComparison.Ordinal);
		});
	}

	[Fact]
	public void Wikidatas_endpoint_choice_and_address_are_named_and_the_address_shows_only_for_another_address()
	{
		var expander = Named(Section, "WikidataExpander");
		var card = Named(expander, "WikidataEndpointCard");
		Assert.Equal("Wikidata query endpoint", Narrator(card));
		Assert.Equal("{x:Bind OutsideSources.SelectedWikidataEndpoint.Description, Mode=OneWay}", (string?)card.Attribute("Description"));
		var box = Named(card, "WikidataEndpointBox");
		Assert.Equal("Wikidata query endpoint", Narrator(box));
		Assert.Equal("{x:Bind OutsideSources.SelectedWikidataEndpoint, Mode=TwoWay}", (string?)box.Attribute("SelectedItem"));
		var custom = Named(expander, "WikidataCustomCard");
		Assert.Equal("{x:Bind c:Converters.BooleanToVisibility(OutsideSources.IsCustomEndpoint), Mode=OneWay}", (string?)custom.Attribute("Visibility"));
		Assert.Equal("{x:Bind OutsideSources.WikidataCustomNote, Mode=OneWay}", (string?)custom.Attribute("Description"));
		Assert.Equal("Wikidata endpoint address", Narrator(Named(custom, "WikidataCustomBox")));
		Assert.Equal(["query.wikidata.org", "query-next.wikidata.org", "QLever (University of Freiburg)", "Another address"], OutsideSourcesSettingsViewModel.WikidataEndpoints.Select(x => x.Name));
		Assert.All(OutsideSourcesSettingsViewModel.WikidataEndpoints, x => Assert.False(string.IsNullOrWhiteSpace(x.Description)));
		Assert.Contains("App.GetService<Core.Contracts.Services.ILocalSettingsService>()", Code("FluentFin/Views/SettingsPage.xaml.cs"), StringComparison.Ordinal);
	}

	[Fact]
	public void Stream_T_has_a_marked_empty_panel_for_its_two_sources()
	{
		var panel = Named(Section, "TrendingSourcesPanel");
		// 1.7 trending: the panel holds stream T's two sources now (TrendingSettingsTests checks them)
		Assert.Equal(["TraktExpander", "TmdbTrendingExpander"], panel.Elements().Select(e => (string?)e.Attribute(X + "Name")));
		var text = Code("FluentFin/Views/SettingsPage.xaml");
		Assert.Contains("<!--  1.7 trending: stream T adds Trakt and TMDb trending in this panel", text, StringComparison.Ordinal);
		Assert.Contains("// 1.7 trending: stream T's keys", Code("FluentFin.Core/Settings/SettingKeys.cs"), StringComparison.Ordinal);
	}

	[Fact]
	public void About_credits_TMDb_in_its_terms_words_and_Wikidata_with_a_place_for_Trakt()
	{
		var card = Named(Load("FluentFin/Views/SettingsPage.xaml").Root!, "ListCreditsCard");
		Assert.Equal("Lists from outside sources", Narrator(card));
		Assert.Equal("{x:Bind outside:OutsideCredits.TmdbNotice}", (string?)Named(card, "TmdbNotice").Attribute("Text"));
		Assert.Equal("{x:Bind outside:OutsideCredits.WikidataCredit}", (string?)Named(card, "WikidataCredit").Attribute("Text"));
		Assert.Equal("Nixie uses TMDB and the TMDB APIs but is not endorsed, certified, or otherwise approved by TMDB.", OutsideCredits.TmdbNotice);
		Assert.Contains("Wikidata", OutsideCredits.WikidataCredit, StringComparison.Ordinal);
		Assert.Contains("<!--  1.7 trending: Trakt's line goes here  -->", Code("FluentFin/Views/SettingsPage.xaml"), StringComparison.Ordinal);
		Assert.All(card.Descendants().Where(e => e.Name.LocalName == "HyperlinkButton"), b => Assert.EndsWith(", opens in your browser", Narrator(b), StringComparison.Ordinal));
	}

	[Fact]
	public void The_page_follows_the_sources_only_while_shown()
	{
		var code = Code("FluentFin/Views/SettingsPage.xaml.cs");
		Assert.Contains("Loaded += (_, _) => OutsideSources.Attach();", code, StringComparison.Ordinal);
		Assert.Contains("Unloaded += (_, _) => OutsideSources.Detach();", code, StringComparison.Ordinal);
		Assert.Contains("DispatcherQueue.GetForCurrentThread()", code, StringComparison.Ordinal);
	}

	private static XDocument Load(string file) => XDocument.Load(Path.Combine(RepoRoot, file));

	private static string Code(string file) => File.ReadAllText(Path.Combine(RepoRoot, file));

	private static string RepoRoot { get; } = FindRepoRoot();

	private static string FindRepoRoot()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FluentFin.sln")))
		{
			dir = dir.Parent;
		}

		return dir?.FullName ?? throw new DirectoryNotFoundException("FluentFin.sln not found above " + AppContext.BaseDirectory);
	}
}
