using System.Xml.Linq;
using FluentFin.Core.Admin;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.2.0 (admin): the user hates side bars ("I HATE THE SIDE BAR"). The two left panes the app had left, the server
/// administration's section list and the Playback Reporting plugin's report list, are text tabs along the top. These fail if
/// either page gets a navigation pane back (the account stream's NoLeftRailTests guards the shell and every other page; at
/// the merge its KnownSectionLists, which still names these two pages, empties).
/// </summary>
public sealed class AdminNoSidePaneTests
{
	private static readonly string[] PaneElements = ["NavigationView", "NavigationViewItem", "SplitView"];
	private static readonly string[] PaneAttributes = ["IsPaneVisible", "PaneDisplayMode", "OpenPaneLength", "CompactPaneLength", "IsPaneOpen"];

	[Theory]
	[InlineData("FluentFin/Views/JellyfinSettingsPage.xaml", "SectionBar")]
	[InlineData("FluentFin.Plugins/Playback Reporting/Views/PlaybackReportingDashboardPage.xaml", "ReportBar")]
	public void ThePage_HasTabsAlongTheTop_AndNoPane(string file, string tabs)
	{
		var doc = XDocument.Load(Path.Combine(RepoRoot, file));

		Assert.DoesNotContain(doc.Descendants(), e => PaneElements.Contains(e.Name.LocalName));
		Assert.DoesNotContain(doc.Descendants().Attributes(), a => PaneAttributes.Contains(a.Name.LocalName));
		var bar = Assert.Single(doc.Descendants(), e => e.Name.LocalName == "SelectorBar" && (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == tabs);
		Assert.False(string.IsNullOrEmpty((string?)bar.Attributes().FirstOrDefault(a => a.Name.LocalName == "AutomationProperties.Name")), "the tab row has a name for Narrator");
		// the tabs sit in the first row, above the page (a column beside it would be a side bar again)
		Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName == "ColumnDefinition");
	}

	[Fact]
	public void TheAdminSections_AreTheWebDashboardsInOrder_WithLibrariesAndPlaybackAsSecondRows()
	{
		Assert.Equal(["Dashboard", "General", "Users", "Libraries", "Playback", "Activity", "Scheduled tasks"], AdminSections.All.Select(x => x.Name));
		Assert.Equal(["Library folders", "Display", "Metadata", "NFO settings"], AdminSections.All.Single(x => x.Name == "Libraries").Pages.Select(x => x.Name));
		Assert.Equal(["Transcoding", "Resume and streaming", "Trickplay"], AdminSections.All.Single(x => x.Name == "Playback").Pages.Select(x => x.Name));
		Assert.Equal(["Libraries", "Playback"], AdminSections.All.Where(x => x.HasPageTabs).Select(x => x.Name));
	}

	[Fact]
	public void EveryPage_FindsItsTab_AndAUsersPage_IsASubPageOfUsers()
	{
		foreach (var section in AdminSections.All)
		{
			foreach (var page in section.Pages)
			{
				var place = AdminSections.Find(page.Key);
				Assert.NotNull(place);
				Assert.Same(section, place.Section);
				Assert.Same(page, place.Page);
				Assert.False(place.IsSubPage);
				Assert.NotNull(typeof(AdminSections).Assembly.GetType(page.Key));
			}
		}

		var editor = AdminSections.Find(typeof(FluentFin.Dialogs.ViewModels.UserEditorViewModel).FullName);
		Assert.Equal("Users", editor!.Section.Name);
		Assert.True(editor.IsSubPage);
		Assert.Null(editor.Page);
		Assert.Null(AdminSections.Find(typeof(ViewModels.HomeViewModel).FullName));
		Assert.Null(AdminSections.Find(null));
	}

	[Fact]
	public void EveryServerAdministrationPage_IsReachableFromATab()
	{
		// the pages of Views\JellyfinSettings the Settings frame shows; the user editor's own sections open in its frame, and
		// ServerConfigurationPage is the form the settings pages are made of
		var notInTheFrame = new[] { "ServerConfigurationPage", "UserProfileEditorPage", "UserAccessEditorPage", "UserParentalControlEditorPage", "UserPasswordEditorPage" };
		var pages = Directory.GetFiles(Path.Combine(RepoRoot, "FluentFin", "Views", "JellyfinSettings"), "*.xaml")
			.Select(Path.GetFileNameWithoutExtension)
			.Except(notInTheFrame)
			.Order(StringComparer.Ordinal)
			.ToList();
		var reachable = AdminSections.All
			.SelectMany(x => x.Pages.Select(p => p.Key).Concat(x.SubPages))
			.Select(key => key[(key.LastIndexOf('.') + 1)..].Replace("ViewModel", "Page", StringComparison.Ordinal))
			.Order(StringComparer.Ordinal)
			.ToList();

		Assert.Equal(pages, reachable);
	}

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
