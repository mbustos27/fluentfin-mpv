using System.Xml.Linq;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Tests;

/// <summary>
/// Seaglass 1.1.3 (UX review, prioritised fix 3): an item's More menu holds only entries that do something, the administrative
/// ones only for administrators, no Play on item pages and episode rows (they have a Play button), and nothing that puts the
/// access token on the clipboard.
/// </summary>
public sealed class ItemMenuTests
{
	private static readonly UserDto Admin = new() { Policy = new UserPolicy { IsAdministrator = true } };
	private static readonly UserDto Viewer = new() { Policy = new UserPolicy { IsAdministrator = false } };

	private static readonly BaseItemDto_Type[] Types =
	[
		BaseItemDto_Type.Movie, BaseItemDto_Type.Episode, BaseItemDto_Type.Series, BaseItemDto_Type.Season,
		BaseItemDto_Type.BoxSet, BaseItemDto_Type.CollectionFolder, BaseItemDto_Type.Person,
	];

	private static readonly ItemMenuCommand[] AdminOnly =
	[
		ItemMenuCommand.EditMetadata, ItemMenuCommand.EditImages, ItemMenuCommand.EditSegments, ItemMenuCommand.Identify,
		ItemMenuCommand.RefreshMetadata, ItemMenuCommand.Delete,
	];

	private static BaseItemDto Item(BaseItemDto_Type type, bool played = false, bool favorite = false, long? position = null) => new()
	{
		Id = Guid.NewGuid(),
		Name = "Bridge of Avalon",
		Type = type,
		UserData = new UserItemDataDto { Played = played, IsFavorite = favorite, PlaybackPositionTicks = position },
	};

	private static List<ItemMenuEntry> Entries(BaseItemDto dto, UserDto? user, bool includePlay = false, bool segments = true) =>
		[.. ItemMenu.For(dto, user, includePlay, segments).SelectMany(x => x)];

	public static TheoryData<BaseItemDto_Type, bool, bool> EveryCase()
	{
		var data = new TheoryData<BaseItemDto_Type, bool, bool>();
		foreach (var type in Types)
		{
			foreach (var admin in new[] { true, false })
			{
				foreach (var play in new[] { true, false })
				{
					data.Add(type, admin, play);
				}
			}
		}

		return data;
	}

	[Theory]
	[MemberData(nameof(EveryCase))]
	public void NoEntry_IsOneOfTheDeadOrLeakingOnes(BaseItemDto_Type type, bool admin, bool play)
	{
		var texts = Entries(Item(type, position: 100), admin ? Admin : Viewer, play).Select(x => x.Text).ToList();

		foreach (var dead in new[] { "Play all from here", "Shuffle", "Add to playlist", "Add to collection", "Copy Download URL", "Play On" })
		{
			Assert.DoesNotContain(dead, texts);
		}

		Assert.Equal(texts.Distinct().Count(), texts.Count);
	}

	[Theory]
	[MemberData(nameof(EveryCase))]
	public void GroupsAreNeverEmpty(BaseItemDto_Type type, bool admin, bool play)
	{
		Assert.All(ItemMenu.For(Item(type), admin ? Admin : Viewer, play, canEditSegments: true), group => Assert.NotEmpty(group));
	}

	[Theory]
	[InlineData(BaseItemDto_Type.Movie)]
	[InlineData(BaseItemDto_Type.Episode)]
	[InlineData(BaseItemDto_Type.Series)]
	[InlineData(BaseItemDto_Type.Season)]
	public void AViewer_SeesNoAdministrativeEntry(BaseItemDto_Type type)
	{
		var commands = Entries(Item(type), Viewer, segments: true).Select(x => x.Command).ToList();

		Assert.Empty(commands.Intersect(AdminOnly));
		Assert.DoesNotContain(ItemMenuCommand.EditSubtitles, commands);
		Assert.Contains(ItemMenuCommand.ToggleWatched, commands);
		Assert.Contains(ItemMenuCommand.ToggleFavorite, commands);
	}

	[Fact]
	public void NoUser_SeesNoAdministrativeEntry()
	{
		Assert.Empty(Entries(Item(BaseItemDto_Type.Movie), null).Select(x => x.Command).Intersect(AdminOnly));
		Assert.Empty(Entries(Item(BaseItemDto_Type.Movie), new UserDto()).Select(x => x.Command).Intersect(AdminOnly));
	}

	[Fact]
	public void AnAdministrator_GetsTheEditTools_AndDelete()
	{
		var movie = Entries(Item(BaseItemDto_Type.Movie), Admin).Select(x => x.Command).ToList();

		Assert.Equal(
			[ItemMenuCommand.ToggleWatched, ItemMenuCommand.ToggleFavorite, ItemMenuCommand.MediaInfo, ItemMenuCommand.EditMetadata,
			 ItemMenuCommand.EditImages, ItemMenuCommand.EditSegments, ItemMenuCommand.EditSubtitles, ItemMenuCommand.Identify,
			 ItemMenuCommand.RefreshMetadata, ItemMenuCommand.Delete],
			movie);
	}

	[Fact]
	public void EditSegments_NeedsTheServersPlugins_EvenForAnAdministrator()
	{
		Assert.DoesNotContain(ItemMenuCommand.EditSegments, Entries(Item(BaseItemDto_Type.Movie), Admin, segments: false).Select(x => x.Command));
		Assert.DoesNotContain(ItemMenuCommand.EditSegments, Entries(Item(BaseItemDto_Type.Movie), Viewer, segments: true).Select(x => x.Command));
	}

	[Fact]
	public void SubtitleManagement_IsItsOwnPermission()
	{
		var manager = new UserDto { Policy = new UserPolicy { IsAdministrator = false, EnableSubtitleManagement = true } };

		var commands = Entries(Item(BaseItemDto_Type.Episode), manager).Select(x => x.Command).ToList();

		Assert.Contains(ItemMenuCommand.EditSubtitles, commands);
		Assert.Empty(commands.Intersect(AdminOnly));
	}

	[Theory]
	[InlineData(BaseItemDto_Type.Movie)]
	[InlineData(BaseItemDto_Type.Episode)]
	[InlineData(BaseItemDto_Type.Series)]
	[InlineData(BaseItemDto_Type.Season)]
	public void Play_IsOnlyInACardsMenu(BaseItemDto_Type type)
	{
		Assert.DoesNotContain(ItemMenuCommand.Play, Entries(Item(type), Admin, includePlay: false).Select(x => x.Command));
		Assert.Equal(ItemMenuCommand.Play, Entries(Item(type), Viewer, includePlay: true)[0].Command);
	}

	[Fact]
	public void WatchedAndFavorite_SayWhatTheyWillDo()
	{
		var texts = Entries(Item(BaseItemDto_Type.Movie, played: true, favorite: true), Viewer).Select(x => x.Text).ToList();
		Assert.Contains("Mark as unwatched", texts);
		Assert.Contains("Remove from favorites", texts);

		texts = [.. Entries(Item(BaseItemDto_Type.Movie), Viewer).Select(x => x.Text)];
		Assert.Contains("Mark as watched", texts);
		Assert.Contains("Add to favorites", texts);
	}

	[Fact]
	public void ResetWatchProgress_OnlyWithProgress()
	{
		Assert.Contains(ItemMenuCommand.ResetWatchProgress, Entries(Item(BaseItemDto_Type.Episode, position: 10), Viewer).Select(x => x.Command));
		Assert.DoesNotContain(ItemMenuCommand.ResetWatchProgress, Entries(Item(BaseItemDto_Type.Episode), Viewer).Select(x => x.Command));
	}

	[Fact]
	public void ASeasonsDelete_SaysSeason()
	{
		var delete = Assert.Single(Entries(Item(BaseItemDto_Type.Season), Admin), x => x.Command == ItemMenuCommand.Delete);
		Assert.Equal("Delete season…", delete.Text);
	}

	// Seaglass 1.1.4 (layout audit, item 13): sentence case for every command, an ellipsis where it asks for more, and
	// Identify has an icon of its own
	[Fact]
	public void EveryEntry_IsInSentenceCase_TheOnesThatAskForMoreEndInAnEllipsis()
	{
		var item = Item(BaseItemDto_Type.Movie, position: 100);
		var texts = ItemMenu.For(item, Admin, includePlay: true, canEditSegments: true).SelectMany(x => x).Select(x => x.Text).ToList();

		Assert.Equal(["Play", "Mark as watched", "Add to favorites", "Reset watch progress", "Media info", "Edit metadata…", "Edit images…",
			"Edit segments…", "Edit subtitles…", "Identify…", "Refresh metadata…", "Delete movie…"], texts);
		Assert.Contains("Remove from Continue Watching", ItemMenu.For(item, Admin, true, true, inContinueWatching: true).SelectMany(x => x).Select(x => x.Text));

		var code = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Converters/JellyfinFlyoutConverter.cs"));
		Assert.Contains("ItemMenuCommand.Identify => new SymbolIcon { Symbol = Symbol.Find }", code);
	}

	// layout audit, item 23: an item page's menu does not repeat its Watched and Favorite buttons; cards and episode rows keep them
	[Fact]
	public void WithoutToggles_TheMenuLeavesOutWatchedAndFavorite()
	{
		var item = Item(BaseItemDto_Type.Movie, position: 100);

		var page = ItemMenu.For(item, Viewer, includePlay: false, canEditSegments: false, includeToggles: false).SelectMany(x => x).Select(x => x.Command).ToList();
		var card = ItemMenu.For(item, Viewer, includePlay: true, canEditSegments: false).SelectMany(x => x).Select(x => x.Command).ToList();

		Assert.DoesNotContain(ItemMenuCommand.ToggleWatched, page);
		Assert.DoesNotContain(ItemMenuCommand.ToggleFavorite, page);
		Assert.Contains(ItemMenuCommand.ResetWatchProgress, page);
		Assert.Contains(ItemMenuCommand.ToggleWatched, card);
		Assert.Contains(ItemMenuCommand.ToggleFavorite, card);
		Assert.Contains("public bool IncludeToggles { get; set; } = true;", File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Converters/JellyfinFlyoutConverter.cs")));
	}

	[Fact]
	public void DeleteAsksFirst_NamingTheItem()
	{
		var prompt = ItemMenu.DeletePrompt(new BaseItemDto { Name = "Bridge of Avalon", Type = BaseItemDto_Type.Movie });

		Assert.Equal("Delete Bridge of Avalon?", prompt.Title);
		Assert.Contains("deleted from the server", prompt.Message);
		Assert.Equal("Delete", prompt.ConfirmText);

		var season = ItemMenu.DeletePrompt(new BaseItemDto { Name = "Season 2", SeriesName = "The Archive", Type = BaseItemDto_Type.Season });
		Assert.Equal("Delete Season 2?", season.Title);
		Assert.Contains("season of The Archive", season.Message);
	}

	// ---- review: Delete asks before it deletes, as behaviour, and the menu has no other way to run an entry

	private static async Task<(bool Ran, List<ItemMenuCommand> Runs, List<ConfirmPrompt> Asked)> Run(ItemMenuCommand command, bool yes)
	{
		List<ItemMenuCommand> runs = [];
		List<ConfirmPrompt> asked = [];
		var ran = await ItemMenu.RunAsync(new ItemMenuEntry(command, "x"), Item(BaseItemDto_Type.Movie),
			prompt => { asked.Add(prompt); return Task.FromResult(yes); },
			(c, _) => { runs.Add(c); return Task.CompletedTask; });
		return (ran, runs, asked);
	}

	[Fact]
	public async Task Delete_AsksFirst_AndANoDeletesNothing()
	{
		var (ran, runs, asked) = await Run(ItemMenuCommand.Delete, yes: false);

		Assert.False(ran);
		Assert.Empty(runs);
		Assert.Equal("Delete Bridge of Avalon?", Assert.Single(asked).Title);
	}

	[Fact]
	public async Task Delete_RunsOnlyAfterAYes()
	{
		var (ran, runs, asked) = await Run(ItemMenuCommand.Delete, yes: true);

		Assert.True(ran);
		Assert.Single(asked);
		Assert.Equal([ItemMenuCommand.Delete], runs);
	}

	[Theory]
	[InlineData(ItemMenuCommand.Play)]
	[InlineData(ItemMenuCommand.ToggleWatched)]
	[InlineData(ItemMenuCommand.EditMetadata)]
	[InlineData(ItemMenuCommand.RefreshMetadata)]
	public async Task TheOthers_RunAtOnce(ItemMenuCommand command)
	{
		var (ran, runs, asked) = await Run(command, yes: false);

		Assert.True(ran);
		Assert.Empty(asked);
		Assert.Equal([command], runs);
	}

	/// <summary>
	/// Every menu item runs through ItemMenu.RunAsync: an entry wired straight to a command (item.Command = DeleteItemCommand)
	/// would delete without asking. Every command has its action, and the dialog is what RunAsync asks with.
	/// </summary>
	[Fact]
	public void TheConverter_RunsEveryEntryThroughRunAsync_NeverStraightToACommand()
	{
		var code = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Converters/JellyfinFlyoutConverter.cs"));

		Assert.DoesNotContain(".Command =", code);
		Assert.DoesNotContain("Command = ", code);
		Assert.DoesNotContain("CommandParameter", code);
		Assert.Contains("await ItemMenu.RunAsync(entry, dto, prompt => ConfirmDialog.AskAsync(root, prompt), Run);", code);
		Assert.Single(System.Text.RegularExpressions.Regex.Matches(code, @"\.Click \+="));
		foreach (var command in Enum.GetValues<ItemMenuCommand>())
		{
			Assert.Contains($"ItemMenuCommand.{command} => (", code);
		}

		Assert.DoesNotContain("void CopyUrlToClipboard(", File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Dialogs/DialogCommands.cs")));
	}

	// ---- review: a menu with nothing in it has no More button

	[Theory]
	[InlineData(BaseItemDto_Type.BoxSet)]
	[InlineData(BaseItemDto_Type.CollectionFolder)]
	[InlineData(BaseItemDto_Type.Person)]
	[InlineData(BaseItemDto_Type.Folder)]
	public void ACollectionOrPerson_HasNoMenu_ForAViewer(BaseItemDto_Type type)
	{
		Assert.Empty(ItemMenu.For(Item(type), Viewer, includePlay: true, canEditSegments: false));
		Assert.NotEmpty(ItemMenu.For(Item(type), Admin, includePlay: true, canEditSegments: false));
	}

	[Fact]
	public void TheCardsMoreButton_ShowsOnlyWithAMenu()
	{
		var card = XDocument.Load(Path.Combine(RepoRoot, "FluentFin/Controls/BaseItemDtoCard.xaml"));
		var more = card.Descendants().Single(e => (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "MoreButton");
		Assert.Equal("{x:Bind c:JellyfinFlyoutConverter.CardMenuVisibility(Model.Dto), Mode=OneWay}", (string?)more.Attribute("Visibility"));

		var converter = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Converters/JellyfinFlyoutConverter.cs"));
		Assert.Contains("dto is not null && Groups(dto, includePlay: true).Count > 0 ? Visibility.Visible : Visibility.Collapsed", converter);
		Assert.Contains("if (MoreButton.Visibility == Visibility.Visible)", File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Controls/BaseItemDtoCard.xaml.cs")));
	}

	[Fact]
	public void TheConfirmDialog_DefaultsToCancel()
	{
		var code = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Dialogs/ConfirmDialog.cs"));

		Assert.Contains("DefaultButton = ContentDialogButton.Close", code);
		Assert.Contains("CloseButtonText = \"Cancel\"", code);
	}

	// Seaglass 1.1.4: a card of Home's Continue Watching row offers to take it out (with an Undo); it replaces Reset Watch
	// Progress there, which does the same on the server; everywhere else the menu is as before
	[Theory]
	[InlineData(BaseItemDto_Type.Movie)]
	[InlineData(BaseItemDto_Type.Episode)]
	public void AContinueWatchingCard_OffersRemoveFromContinueWatching_InsteadOfReset(BaseItemDto_Type type)
	{
		var item = Item(type, position: 100);

		var inRow = ItemMenu.For(item, Viewer, includePlay: true, canEditSegments: false, inContinueWatching: true).SelectMany(x => x).Select(x => x.Command).ToList();
		var elsewhere = Entries(item, Viewer, includePlay: true).Select(x => x.Command).ToList();

		Assert.Contains(ItemMenuCommand.RemoveFromContinueWatching, inRow);
		Assert.DoesNotContain(ItemMenuCommand.ResetWatchProgress, inRow);
		Assert.DoesNotContain(ItemMenuCommand.RemoveFromContinueWatching, elsewhere);
		Assert.Contains(ItemMenuCommand.ResetWatchProgress, elsewhere);
	}

	// Seaglass 1.1.4 (interaction audit, item 3): the menu says what the item is now when it opens, not what it was when its
	// card was made: the converter fills it again on Opening, from ItemMenu.For of the item as it is then
	[Fact]
	public void TheMenu_IsMadeAgainWhenItOpens_FromTheItemAsItIsThen()
	{
		var code = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Converters/JellyfinFlyoutConverter.cs")).Replace("\r\n", "\n");

		Assert.Contains("flyout.Opening += (sender, _) =>", code);
		var opening = code[code.IndexOf("flyout.Opening += (sender, _) =>", StringComparison.Ordinal)..];
		Assert.Contains("Fill(opening, dto, includePlay, includeToggles);", opening[..opening.IndexOf("};", StringComparison.Ordinal)]);
		Assert.Contains("flyout.Items.Clear();", code);

		var item = Item(BaseItemDto_Type.Movie);
		Assert.Contains(Entries(item, Viewer), e => e.Text == "Mark as watched");
		item.UserData!.Played = true;
		item.UserData.IsFavorite = true;
		Assert.Contains(Entries(item, Viewer), e => e.Text == "Mark as unwatched");
		Assert.Contains(Entries(item, Viewer), e => e.Text == "Remove from favorites");
	}

	[Fact]
	public void TheConverter_AsksTheRow_AndRunsTheRemovalThroughIt()
	{
		var code = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Converters/JellyfinFlyoutConverter.cs"));

		Assert.Contains("inContinueWatching: includePlay && ContinueWatchingRows.RowOf(dto) is not null", code);
		Assert.Contains("ContinueWatchingRows.RowOf(dto)?.Remove(dto)", code);
	}

	[Fact]
	public void OnlyCards_AskForPlay()
	{
		XNamespace converters = "using:FluentFin.Converters";
		var card = XDocument.Load(Path.Combine(RepoRoot, "FluentFin/Controls/BaseItemDtoCard.xaml"));

		var converter = Assert.Single(card.Descendants(converters + "JellyfinFlyoutConverter"));
		Assert.Equal("True", (string?)converter.Attribute("IncludePlay"));
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
