using System.Xml.Linq;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.6.1: the Collection page's and library pages' queue from the 1.6 review (colpage): Recently added on a playlist puts the
/// playlist's own order back; Save as collection saves once, whatever the flyout does meanwhile; Drive-in's marquee says its
/// count with its word; Drive-in's letter in view takes the system's colours in high contrast; and Drive-in's library keeps
/// the presets strip's room while its titles load.
/// </summary>
public sealed class ColpageQueue161Tests
{
	static ColpageQueue161Tests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

	private static LibraryPreset Preset(LibraryViewModel vm, string id) => vm.Presets.Single(p => p.Id == id).Preset;

	private static string Lit(LibraryViewModel vm) => string.Join(",", vm.Presets.Where(p => p.IsOn).Select(p => p.Id));

	// ---- Recently added on a playlist (LibraryViewModel.Presets.cs:66, :87)

	[Fact]
	public async Task RecentlyAdded_OnAPlaylistInItsOwnOrder_PutsThatOrderBack()
	{
		var id = Guid.NewGuid();
		var dtos = new[] { ("Zulu", 1), ("Alpha", 3), ("Mike", 2), ("Bravo", 4) }
			.Select(x => new BaseItemDto { Id = Guid.NewGuid(), Name = x.Item1, Type = BaseItemDto_Type.Movie, DateCreated = new DateTimeOffset(2026, 1, x.Item2, 0, 0, 0, TimeSpan.Zero), UserData = new UserItemDataDto() })
			.ToList();
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetPlaylistItems(id)).ReturnsAsync(new BaseItemDtoQueryResult { Items = dtos });
		client.SetupGet(x => x.UserId).Returns(Guid.NewGuid());
		var vm = new LibraryViewModel(client.Object);
		await vm.OnNavigatedTo(new BaseItemDto { Id = id, Type = BaseItemDto_Type.Playlist, Name = "Road trip" });
		string[] own = ["Zulu", "Alpha", "Mike", "Bravo"];
		Assert.Equal(own, vm.Items.Select(i => i.Name));

		vm.TogglePreset(Preset(vm, "recent"));
		Assert.Equal(["Bravo", "Alpha", "Mike", "Zulu"], vm.Items.Select(i => i.Name));
		Assert.Equal("recent", Lit(vm));

		// a click again: the playlist's own order, not titles A to Z
		vm.TogglePreset(Preset(vm, "recent"));
		Assert.Equal(own, vm.Items.Select(i => i.Name));
		Assert.True(vm.InPlaylistOrder);
		Assert.Equal("Playlist order", vm.SortName);
		Assert.Equal("all", Lit(vm));

		// All while Recently added is lit does the same
		vm.TogglePreset(Preset(vm, "recent"));
		vm.TogglePreset(Preset(vm, "all"));
		Assert.Equal(own, vm.Items.Select(i => i.Name));
		Assert.Equal("Playlist order", vm.SortName);

		// a sort picked in between is what comes back, as in a library
		vm.UpdateSortBy(ItemSortBy.Name);
		vm.TogglePreset(Preset(vm, "recent"));
		vm.TogglePreset(Preset(vm, "recent"));
		Assert.Equal(["Alpha", "Bravo", "Mike", "Zulu"], vm.Items.Select(i => i.Name));
		Assert.False(vm.InPlaylistOrder);
	}

	// ---- Save as collection saves once (AppliedFilterRow.xaml.cs:72)

	private sealed class SlowSave : ISavedFilters
	{
		public List<string> Calls { get; } = [];

		public TaskCompletionSource<bool> Answer { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<bool> SaveFilter(string name, LibrarySavedFilter filter)
		{
			Calls.Add(name);
			return Answer.Task;
		}
	}

	[Fact]
	public async Task SaveAsCollection_OnItsWay_IsNotSavedAgain_UntilItIsDone()
	{
		var saved = new SlowSave();
		var movies = new BaseItemDto { Id = Guid.NewGuid(), Name = "Movies", CollectionType = BaseItemDto_CollectionType.Movies };
		var client = new Mock<IJellyfinClient>();
		client.SetupGet(x => x.UserId).Returns(Guid.NewGuid());
		client.SetupLibrary([new BaseItemDto { Id = Guid.NewGuid(), Name = "A", Type = BaseItemDto_Type.Movie, UserData = new UserItemDataDto() }]);
		var vm = new LibraryViewModel(client.Object, savedFilters: saved);
		await vm.OnNavigatedTo(movies);
		await vm.RestLoaded;
		vm.TogglePreset(Preset(vm, "unwatched"));

		var first = vm.SaveFilterAs("Unwatched");
		Assert.True(vm.IsSavingFilter);
		// the flyout closed and opened again, Save pressed again: nothing is saved a second time (and nothing waits on the first)
		var second = vm.SaveFilterAs("Unwatched");
		Assert.True(second.IsCompleted);
		Assert.False(await second);
		Assert.Equal(["Unwatched"], saved.Calls);

		saved.Answer.SetResult(true);
		Assert.True(await first);
		Assert.False(vm.IsSavingFilter);

		// done: the next Save saves
		saved.Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
		saved.Answer.SetResult(true);
		Assert.True(await vm.SaveFilterAs("Another"));
		Assert.Equal(["Unwatched", "Another"], saved.Calls);
	}

	// ---- Drive-in: the marquee's count spoken, the letter in view in high contrast, the strip's room while loading

	private static string RepoRoot { get; } = FindRepoRoot();

	private static string FindRepoRoot()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FluentFin.sln")))
		{
			dir = dir.Parent;
		}

		return dir?.FullName ?? throw new InvalidOperationException("no FluentFin.sln above the test's folder");
	}

	[Fact]
	public void DriveInsMarquee_SpeaksItsCountWithItsWord()
	{
		var doc = XDocument.Load(Path.Combine(RepoRoot, "FluentFin/Themes/DriveIn/DriveIn.xaml"));
		var frame = doc.Root!.Elements().Single(e => (string?)e.Attribute(X + "Key") == "PageTitleFrameStyle");
		var tubes = frame.Descendants().Single(e => e.Name.LocalName == "NixieReadout");
		Assert.Equal("{Binding FeatureCount}", (string?)tubes.Attribute("Text"));
		Assert.Equal("{Binding FeatureSpoken}", (string?)tubes.Attribute("Spoken"));
		// the word beside the tubes stays out of Narrator's way (said with the count)
		var word = frame.Descendants().Single(e => e.Name.LocalName == "TextBlock" && (string?)e.Attribute("Text") == "{Binding FeatureLabel}");
		Assert.Equal("Raw", (string?)word.Attribute("AutomationProperties.AccessibilityView"));
		// both models a frame binds say it
		Assert.Contains("public string FeatureSpoken", File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Views/PageTitleFacts.cs")), StringComparison.Ordinal);
		Assert.Contains("public string FeatureSpoken", File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Views/CollectionStage.cs")), StringComparison.Ordinal);
	}

	[Fact]
	public void DriveInsLetterInView_TakesTheLitPresetsKeys_NeverAFixedPink()
	{
		var doc = XDocument.Load(Path.Combine(RepoRoot, "FluentFin/Themes/DriveIn/Views/DriveInLibraryPage.xaml"));
		var style = doc.Root!.Descendants().Single(e => (string?)e.Attribute(X + "Key") == "DriveInLetterTileStyle");
		var selected = style.Descendants().Single(e => e.Name.LocalName == "VisualState" && (string?)e.Attribute(X + "Name") == "Selected");
		var setters = selected.Descendants().Where(e => e.Name.LocalName == "Setter").ToDictionary(e => (string)e.Attribute("Target")!, e => (string)e.Attribute("Value")!);
		Assert.DoesNotContain(setters.Values, v => v.StartsWith('#'));
		Assert.Equal("{ThemeResource DriveInPresetLitTileBrush}", setters["Tile.Background"]);
		Assert.Equal("{ThemeResource DriveInPresetLitTextBrush}", setters["Letter.Foreground"]);
		Assert.Equal("{ThemeResource DriveInPresetLitTextBrush}", setters["Tile.BorderBrush"]);
		// no fixed colour anywhere in the letter tiles' states
		var values = style.Descendants().Where(e => e.Name.LocalName == "Setter" && e.Parent?.Name.LocalName == "VisualState.Setters")
			.Select(e => (string?)e.Attribute("Value") ?? "");
		Assert.DoesNotContain(values, v => v.StartsWith('#'));
	}

	[Fact]
	public void DriveInsLibrary_KeepsThePresetsRoom_WhileItsTitlesLoad()
	{
		var code = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Themes/DriveIn/Views/DriveInLibraryPage.xaml.cs"));
		var show = code[code.IndexOf("private void ShowPresets()", StringComparison.Ordinal)..];
		show = show[..show.IndexOf("\n\t}", StringComparison.Ordinal)];
		Assert.Contains("HeaderContent.RowDefinitions[1].MinHeight", show, StringComparison.Ordinal);
		Assert.Contains("LoadStatus.Loading", show, StringComparison.Ordinal);
		Assert.Contains("_presetRoom", show, StringComparison.Ordinal);
		// the strip is in that row
		var page = XDocument.Load(Path.Combine(RepoRoot, "FluentFin/Themes/DriveIn/Views/DriveInLibraryPage.xaml"));
		var strip = page.Root!.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "PresetStrip");
		Assert.Equal("1", (string?)strip.Attribute("Grid.Row"));
		Assert.Equal("HeaderContent", (string?)strip.Parent!.Attribute(X + "Name"));
	}
}
