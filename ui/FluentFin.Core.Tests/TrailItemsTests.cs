using System.Text.Json;
using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.7 (upnav; review of wip/170-upnav, item 3): an episode opened without its ids is asked of the server once. The
/// trail waits for the episode the page's view model fetched rather than asking again, and stops waiting when the page is
/// left.
/// </summary>
public sealed class TrailItemsTests
{
	private static readonly string Episode = typeof(EpisodeViewModel).FullName!;
	private static readonly string Movie = typeof(MovieViewModel).FullName!;

	private readonly Mock<IJellyfinClient> _client = new();

	private static BaseItemDto Server(Guid id) => new()
	{
		Id = id,
		Name = "The Siren",
		Type = BaseItemDto_Type.Episode,
		SeriesId = Guid.NewGuid(),
		SeriesName = "What We Do in the Shadows",
		SeasonId = Guid.NewGuid(),
		ParentIndexNumber = 3,
		IndexNumber = 2,
	};

	private (EpisodeViewModel Page, TopNavigation Top, TaskCompletionSource<BaseItemDto?> Answer, BaseItemDto Bare) Arrange()
	{
		var bare = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Episode };
		var answer = new TaskCompletionSource<BaseItemDto?>();
		_client.Setup(x => x.GetItem(bare.Id!.Value)).Returns(answer.Task);
		_client.Setup(x => x.GetItems(It.IsAny<BaseItemDto>(), It.IsAny<bool>())).ReturnsAsync((BaseItemDtoQueryResult?)null);
		var commands = new GlobalCommands(Mock.Of<INavigationServiceCore>(), _client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>());
		var page = new EpisodeViewModel(_client.Object, commands);
		var top = new TopNavigation(Mock.Of<INavigationServiceCore>(), new NoSettings(), NullLogger<TopNavigation>.Instance)
		{
			Profile = () => "server-a|user-1",
			ItemOf = TrailItems.FromShownPage(() => page, _client.Object.GetItem),
			SeasonsOf = (_, _) => Task.FromResult<IReadOnlyList<BaseItemDto>?>([]),
		};
		top.Reset();
		return (page, top, answer, bare);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task AnEpisodeWithoutItsIds_IsAskedOfTheServerOnce_ThePageAndTheTrailShareIt(bool pageFirst)
	{
		var (page, top, answer, bare) = Arrange();

		// the frame's Navigated: the navigation service starts the page's load, the shell shows the trail (either order)
		Task load;
		if (pageFirst)
		{
			load = page.OnNavigatedTo(bare);
			top.OnPageShown(Episode, bare);
		}
		else
		{
			top.OnPageShown(Episode, bare);
			load = page.OnNavigatedTo(bare);
		}

		answer.SetResult(Server(bare.Id!.Value));
		await load;
		await top.TrailLookup.WaitAsync(TimeSpan.FromSeconds(5));

		_client.Verify(x => x.GetItem(bare.Id!.Value), Times.Once);
		Assert.Equal(("What We Do in the Shadows", "Season 3"), (top.Trail.Series?.Label, top.Trail.Season?.Label));
	}

	[Fact]
	public async Task APageThatCannotLoadItsEpisode_LeavesTheTrailEmpty_AndKeepsNothing()
	{
		var (page, top, answer, bare) = Arrange();

		top.OnPageShown(Episode, bare);
		var load = page.OnNavigatedTo(bare);
		answer.SetResult(null);
		await load;
		await top.TrailLookup.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.True(top.Trail.IsEmpty);
		Assert.Equal(LoadStatus.Failed, page.State.Status);
	}

	[Fact]
	public async Task LeavingThePage_EndsTheTrailsWait()
	{
		var (page, top, _, bare) = Arrange();

		top.OnPageShown(Episode, bare);
		_ = page.OnNavigatedTo(bare); // its episode never comes
		var waiting = top.TrailLookup;
		Assert.False(waiting.IsCompleted);
		top.OnPageShown(Movie, new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Movie });

		await waiting.WaitAsync(TimeSpan.FromSeconds(5)); // ended, not left waiting for the page
		Assert.True(top.Trail.IsEmpty);
	}

	[Fact]
	public async Task AnyOtherPage_AsksTheServer()
	{
		var id = Guid.NewGuid();
		var asked = 0;
		var itemOf = TrailItems.FromShownPage(() => new object(), i => { asked++; return Task.FromResult<BaseItemDto?>(Server(i)); });

		var item = await itemOf(id, CancellationToken.None);

		Assert.Equal((1, id), (asked, item?.Id));
	}

	private sealed class NoSettings : ILocalSettingsService
	{
		private readonly Dictionary<string, JsonNode?> _values = [];

		public JsonNode? ReadSettingRaw(string key) => _values.GetValueOrDefault(key);

		public T ReadSetting<T>(string key, T defaultValue) => _values.TryGetValue(key, out var v) && v is not null ? v.Deserialize<T>()! : defaultValue;

		public void SaveSetting<T>(string key, T value) => _values[key] = JsonNode.Parse(JsonSerializer.Serialize(value));

		public void RemoveSetting(string key) => _values.Remove(key);

		public byte[] GetEntropyBytes() => [];
	}
}
