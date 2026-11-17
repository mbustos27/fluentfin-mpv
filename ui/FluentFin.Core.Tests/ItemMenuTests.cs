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
		Assert.Equal("Delete Season", delete.Text);
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

	/// <summary>The menu draws every command (an entry with no command did nothing), and its Delete goes through the dialog.</summary>
	[Fact]
	public void TheConverter_GivesEveryEntryAnAction_AndDeleteAsks()
	{
		var code = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Converters/JellyfinFlyoutConverter.cs"));

		foreach (var command in Enum.GetValues<ItemMenuCommand>())
		{
			Assert.Contains($"case ItemMenuCommand.{command}:", code);
		}

		Assert.Contains("ConfirmDialog.AskAsync", code);
		Assert.Contains("ItemMenu.DeletePrompt(dto)", code);
		Assert.DoesNotContain("void CopyUrlToClipboard(", File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Dialogs/DialogCommands.cs")));
	}

	[Fact]
	public void TheConfirmDialog_DefaultsToCancel()
	{
		var code = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Dialogs/ConfirmDialog.cs"));

		Assert.Contains("DefaultButton = ContentDialogButton.Close", code);
		Assert.Contains("CloseButtonText = \"Cancel\"", code);
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
