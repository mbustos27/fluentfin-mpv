using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Seaglass 1.1.4: the item page's and the episode row's Watched and Favourite toggles (and the More menu's) go the way their
/// names say ("Mark ... as unwatched" after a first click). The page's copy of the item never changed, so a second click sent
/// the first one's state again: watched stayed watched while the toggle showed it unwatched.
/// </summary>
public sealed class ItemToggleTests
{
	private readonly Mock<IJellyfinClient> _client = new();

	private GlobalCommands Commands() => new(Mock.Of<INavigationServiceCore>(), _client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>());

	[Fact]
	public async Task Watched_TwoClicks_MarkThenUnmark()
	{
		var sent = new List<bool>();
		_client.Setup(x => x.SetPlayed(It.IsAny<BaseItemDto>(), It.IsAny<bool>())).Callback<BaseItemDto, bool>((_, played) => sent.Add(played)).Returns(Task.CompletedTask);
		var dto = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Movie, UserData = new UserItemDataDto { Played = false } };
		var commands = Commands();

		await commands.ToggleWatched(dto);
		Assert.Equal("Mark as unwatched", ItemLabels.WatchedAction(null, dto.UserData!.Played));
		await commands.ToggleWatched(dto);

		Assert.Equal([true, false], sent);
		Assert.False(dto.UserData.Played);
	}

	[Fact]
	public async Task Favorite_TwoClicks_AddThenRemove_EvenWithoutUserData()
	{
		var sent = new List<bool>();
		_client.Setup(x => x.SetIsFavorite(It.IsAny<BaseItemDto>(), It.IsAny<bool>())).Callback<BaseItemDto, bool>((_, favorite) => sent.Add(favorite)).Returns(Task.CompletedTask);
		var dto = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Episode };
		var commands = Commands();

		await commands.ToggleFavorite(dto);
		await commands.ToggleFavorite(dto);

		Assert.Equal([true, false], sent);
		Assert.False(dto.UserData!.IsFavorite);
	}
}
