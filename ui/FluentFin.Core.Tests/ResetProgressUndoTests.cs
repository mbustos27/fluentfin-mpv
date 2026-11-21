using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.5 (the button review's L38): the item menu's "Reset watch progress" offers an Undo that puts the resume point back. The
/// review of 1.5 (m6): the point is the server's, asked just before the reset (the card's copy can be stale), and the Undo
/// does nothing when the item was played since.
/// </summary>
[Collection(ItemUserDataCollection.Name)]
public sealed class ResetProgressUndoTests
{
	private static readonly DateTimeOffset Played = new(2026, 11, 20, 20, 0, 0, TimeSpan.Zero);

	private readonly Mock<IJellyfinClient> _client = new();
	private readonly List<long> _positions = [];
	private UserItemDataDto _server = new() { Played = false, PlaybackPositionTicks = 12_345_000, LastPlayedDate = Played };

	public ResetProgressUndoTests()
	{
		_client.Setup(x => x.GetItem(It.IsAny<Guid>())).ReturnsAsync(() => new BaseItemDto { UserData = _server });
		_client.Setup(x => x.SetResumePosition(It.IsAny<Guid>(), It.IsAny<long>()))
			.Callback<Guid, long>((_, ticks) =>
			{
				_positions.Add(ticks);
				_server = new UserItemDataDto { Played = _server.Played, PlaybackPositionTicks = ticks, LastPlayedDate = _server.LastPlayedDate };
			})
			.ReturnsAsync(true);
	}

	// the card's copy: older than the server's (the card was drawn before the last few minutes were watched)
	private static BaseItemDto Episode(long position = 9_000_000) => new()
	{
		Id = Guid.NewGuid(), Type = BaseItemDto_Type.Episode, Name = "The Pilot", SeriesName = "Lost", ParentIndexNumber = 1, IndexNumber = 4,
		UserData = new UserItemDataDto { Played = false, PlaybackPositionTicks = position },
	};

	private async Task<(bool Took, List<UndoOffer> Offers)> Reset(BaseItemDto item)
	{
		var offers = new List<UndoOffer>();
		void OnOffer(object? s, UndoOffer offer) => offers.Add(offer);
		ItemUserData.UndoOffered += OnOffer;
		try
		{
			return (await ItemUserData.ResetProgressAsync(_client.Object, item), offers);
		}
		finally
		{
			ItemUserData.UndoOffered -= OnOffer;
		}
	}

	[Fact]
	public async Task Reset_ClearsTheResumePoint_AndItsUndoPutsTheServersBack()
	{
		var (took, offers) = await Reset(Episode());

		Assert.True(took);
		var offer = Assert.Single(offers);
		Assert.StartsWith("Reset the watch progress of ", offer.Message, StringComparison.Ordinal);
		Assert.Equal([0L], _positions);

		Assert.True(await offer.Undo());
		Assert.Equal([0L, 12_345_000L], _positions); // the server's point, not the card's 9,000,000
	}

	/// <summary>
	/// BH-150-m1: the reset and its Undo change what is on screen: the item's copy takes the server's answer (so its menu
	/// stops offering Reset, then offers it again), and every card and row hears it, with the position at 0 then back.
	/// </summary>
	[Fact]
	public async Task TheResetAndItsUndo_UpdateTheItemsCopy_AndTellEverySurface()
	{
		var item = Episode();
		var heard = new List<(Guid Id, long? Ticks, bool Failed)>();
		void OnChanged(object? s, ItemUserDataChangedEventArgs e) => heard.Add((e.ItemId, e.UserData.PlaybackPositionTicks, e.Failed));
		ItemUserData.Changed += OnChanged;
		try
		{
			var (_, offers) = await Reset(item);

			Assert.Equal([(item.Id!.Value, (long?)0L, false)], heard);
			Assert.Equal(0L, item.UserData!.PlaybackPositionTicks);
			Assert.DoesNotContain(ItemMenuCommand.ResetWatchProgress, Menu(item));

			Assert.True(await Assert.Single(offers).Undo());

			Assert.Equal((item.Id!.Value, (long?)12_345_000L, false), heard[^1]);
			Assert.Equal(2, heard.Count);
			Assert.Equal(12_345_000L, item.UserData!.PlaybackPositionTicks);
			Assert.Contains(ItemMenuCommand.ResetWatchProgress, Menu(item));
		}
		finally
		{
			ItemUserData.Changed -= OnChanged;
		}
	}

	[Fact]
	public async Task AServerThatCannotSayAfterTheReset_StillClearsTheCopysPosition()
	{
		var item = Episode();
		var asked = 0;
		_client.Setup(x => x.GetItem(It.IsAny<Guid>())).ReturnsAsync(() => ++asked == 1 ? new BaseItemDto { UserData = _server } : null);

		var (took, _) = await Reset(item);

		Assert.True(took);
		Assert.Equal(0L, item.UserData!.PlaybackPositionTicks);
		Assert.DoesNotContain(ItemMenuCommand.ResetWatchProgress, Menu(item));
	}

	[Fact]
	public async Task ARefusedReset_LeavesTheCopyAlone()
	{
		_client.Setup(x => x.SetResumePosition(It.IsAny<Guid>(), It.IsAny<long>())).ReturnsAsync(false);
		var item = Episode();

		await Reset(item);

		Assert.Equal(9_000_000L, item.UserData!.PlaybackPositionTicks);
		Assert.Contains(ItemMenuCommand.ResetWatchProgress, Menu(item));
	}

	private static List<ItemMenuCommand> Menu(BaseItemDto item) =>
		[.. ItemMenu.For(item, new UserDto { Policy = new UserPolicy { IsAdministrator = false } }, includePlay: false, canEditSegments: false)
			.SelectMany(x => x).Select(x => x.Command)];

	[Fact]
	public async Task TheUndo_DoesNothing_WhenTheItemWasPlayedSince()
	{
		var (_, offers) = await Reset(Episode());
		var offer = Assert.Single(offers);

		// played again after the reset: a new resume point and a newer last play
		_server = new UserItemDataDto { PlaybackPositionTicks = 3_000_000, LastPlayedDate = Played.AddMinutes(10) };

		Assert.False(await offer.Undo());
		Assert.Equal([0L], _positions);
	}

	[Fact]
	public async Task TheUndo_DoesNothing_WhenPlayedToTheEndSince()
	{
		var (_, offers) = await Reset(Episode());
		_server = new UserItemDataDto { Played = true, PlaybackPositionTicks = 0, LastPlayedDate = Played.AddMinutes(30) };

		Assert.False(await Assert.Single(offers).Undo());
		Assert.Equal([0L], _positions);
	}

	[Fact]
	public async Task NoResumePointOnTheServer_NoUndo()
	{
		_server = new UserItemDataDto { PlaybackPositionTicks = 0 };

		var (took, offers) = await Reset(Episode(position: 9_000_000)); // the card still shows one

		Assert.True(took);
		Assert.Empty(offers);
	}

	[Fact]
	public async Task AServerThatCannotSay_GetsTheResetButNoUndo()
	{
		_client.Setup(x => x.GetItem(It.IsAny<Guid>())).ReturnsAsync((BaseItemDto?)null);

		var (took, offers) = await Reset(Episode());

		Assert.True(took);
		Assert.Empty(offers);
	}

	[Fact]
	public async Task ARefusedReset_OffersNoUndo()
	{
		_client.Setup(x => x.SetResumePosition(It.IsAny<Guid>(), It.IsAny<long>())).ReturnsAsync(false);

		var (took, offers) = await Reset(Episode());

		Assert.False(took);
		Assert.Empty(offers);
	}

	[Theory]
	[InlineData(0L, 0, false)]          // untouched since the reset
	[InlineData(5_000L, 0, true)]       // a resume point again
	[InlineData(0L, 10, true)]          // a newer last play (played to the end)
	[InlineData(0L, -10, false)]
	public void PlayedSince_ReadsTheServersAnswer(long ticks, int minutes, bool played) =>
		Assert.Equal(played, ItemUserData.PlayedSince(new UserItemDataDto { PlaybackPositionTicks = ticks, LastPlayedDate = Played.AddMinutes(minutes) }, Played));

	[Fact]
	public void TheMenuEntry_RunsTheUndoableReset()
	{
		var code = File.ReadAllText(Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "FluentFin/Converters/JellyfinFlyoutConverter.cs"));

		Assert.Contains("ItemMenuCommand.ResetWatchProgress => (App.Commands.ResetWatchProgressOfCommand, dto)", code);
	}
}
