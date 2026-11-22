using System.Reactive.Subjects;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.6.1: what the 1.5.1 sign-in work left. A series page's season list (BH-151-m1) and the action messages (BH-151-m2: Continue
/// Watching's remove, the administration pages' Save, Playback preferences) say the sign-in ended, not "check the server"; the
/// season list's button is Sign in again. The websocket stops dialling with the dead token (the 1.5.1 review).
/// </summary>
[Collection(SignInStateCollection.Name)]
public sealed class SignInEndedLeftoverTests : IDisposable
{
	static SignInEndedLeftoverTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public SignInEndedLeftoverTests()
	{
		SignInState.SignedIn();
		SessionInfo.ServerName = "Den";
	}

	public void Dispose()
	{
		SignInState.SignedIn();
		SignInState.SignInAgain = null;
		SessionInfo.ServerName = null;
	}

	// ---- BH-151-m1: the series page's season list

	private static readonly Guid SeriesId = Guid.NewGuid();
	private readonly Mock<IJellyfinClient> _client = new();

	private (List<BaseItemDto> Seasons, Func<int> Asked) Series(Func<BaseItemDto, BaseItemDtoQueryResult?> episodes)
	{
		var seasons = Enumerable.Range(1, 3).Select(n => new BaseItemDto
		{
			Id = Guid.NewGuid(), Type = BaseItemDto_Type.Season, Name = $"Season {n}", IndexNumber = n, SeriesId = SeriesId,
			UserData = new UserItemDataDto { Played = false },
		}).ToList();
		var asked = 0;
		_client.Setup(x => x.GetItem(SeriesId)).ReturnsAsync(new BaseItemDto { Id = SeriesId, Type = BaseItemDto_Type.Series, Name = "Lost", UserData = new UserItemDataDto() });
		_client.Setup(x => x.GetItems(It.Is<BaseItemDto>(d => d.Id == SeriesId), It.IsAny<bool>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = seasons });
		_client.Setup(x => x.GetItems(It.Is<BaseItemDto>(d => d.Id != SeriesId), It.IsAny<bool>()))
			.Returns((BaseItemDto d, bool _) => { Interlocked.Increment(ref asked); return Task.FromResult(episodes(d)); });
		_client.Setup(x => x.GetSeriesEpisodes(SeriesId)).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		_client.Setup(x => x.GetNextUp(It.IsAny<BaseItemDto>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		return (seasons, () => Volatile.Read(ref asked));
	}

	private static BaseItemDtoQueryResult Episodes(BaseItemDto season) => new()
	{
		Items = [new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Episode, SeriesId = SeriesId, SeasonId = season.Id, Name = "Pilot", IndexNumber = 1, UserData = new UserItemDataDto() }],
	};

	private async Task<SeriesViewModel> OpenSeries()
	{
		var vm = new SeriesViewModel(_client.Object);
		await vm.OnNavigatedTo(new BaseItemDto { Id = SeriesId, Type = BaseItemDto_Type.Series });
		return vm;
	}

	[Fact]
	public async Task ASeasonThatFails_AfterTheSignInEnded_SaysSo_AndOffersSignInAgain()
	{
		// Season 3's list fails (asked ahead too, so it isn't kept); the sign-in ends (another device's sign-in, a removed
		// device), and then Season 3 is picked
		var ended = true;
		var (seasons, asked) = Series(season => ended && season.IndexNumber == 3 ? null : Episodes(season));
		var vm = await OpenSeries();
		SignInState.MarkEnded();
		var signIns = 0;
		SignInState.SignInAgain = () => { signIns++; return Task.CompletedTask; }; // the sign-in page: still ended

		await vm.SelectSeasonCommand.ExecuteAsync(seasons[2]);
		var before = asked();

		Assert.True(vm.EpisodesFailed);
		Assert.Equal("Your sign-in to Den ended. Select Sign in again.", vm.EpisodesMessage);
		Assert.Equal("Sign in again", vm.EpisodesRetryText);
		Assert.DoesNotContain("Couldn't load", vm.EpisodesMessage);

		// its button signs in again; still ended (the sign-in page), so the list isn't asked again
		await vm.RetrySeasonCommand.ExecuteAsync(null);
		Assert.Equal(1, signIns);
		Assert.Equal(before, asked());

		// signed in again in place (a saved password): the season loads again, and the button is Try again once more
		SignInState.SignInAgain = () => { signIns++; ended = false; SignInState.SignedIn(); return Task.CompletedTask; };
		await vm.RetrySeasonCommand.ExecuteAsync(null);
		Assert.Equal(2, signIns);
		Assert.False(vm.EpisodesFailed);
		Assert.Single(vm.Episodes);
		Assert.False(vm.HasEpisodesMessage);
		Assert.Equal("Try again", vm.EpisodesRetryText);
	}

	[Fact]
	public async Task ASeasonThatFails_WhileTheSignInIsGood_StillSaysCouldntLoad_WithTryAgain()
	{
		var fail = true;
		var (seasons, _) = Series(season => fail && season.IndexNumber == 3 ? null : Episodes(season));
		var vm = await OpenSeries();
		var signIns = 0;
		SignInState.SignInAgain = () => { signIns++; return Task.CompletedTask; };

		await vm.SelectSeasonCommand.ExecuteAsync(seasons[2]);
		Assert.Equal("Couldn't load the episodes of Season 3.", vm.EpisodesMessage);
		Assert.Equal("Try again", vm.EpisodesRetryText);

		fail = false;
		await vm.RetrySeasonCommand.ExecuteAsync(null);
		Assert.Equal(0, signIns);
		Assert.Single(vm.Episodes);
	}

	// ---- BH-151-m2: the action messages

	[Fact]
	public void AnActionsFailure_NamesTheEndedSignIn_NotTheServer()
	{
		Assert.Equal("Couldn't save: the server didn't take the changes. Check that it is running, then save again.",
			LoadMessages.ActionFailed("Couldn't save", ServerConfigurationViewModel.NotSavedText));

		SignInState.MarkEnded();

		var said = LoadMessages.ActionFailed("Couldn't save", ServerConfigurationViewModel.NotSavedText);
		Assert.Equal("Couldn't save: your sign-in to Den ended. Sign in again, then try again.", said);
		Assert.Contains("your sign-in to Den ended", SettingsViewModel.ChangeDidNotReach());
		Assert.DoesNotContain("running", SettingsViewModel.ChangeDidNotReach());
	}

	private sealed class AdminPage(IJellyfinClient client) : ServerConfigurationViewModel(client)
	{
		protected override List<JellyfinConfigItemViewModel> CreateItems(ServerConfiguration config) =>
			[new JellyfinConfigItemViewModel<string>(() => config.ServerName ?? "", value => config.ServerName = value) { DisplayName = "Server name" }];
	}

	[Fact]
	public async Task AnAdministrationPagesSave_AfterTheSignInEnded_SaysSo()
	{
		var config = new ServerConfiguration { ServerName = "Home" };
		_client.Setup(x => x.GetConfiguration()).ReturnsAsync(config);
		_client.Setup(x => x.TrySaveConfiguration(It.IsAny<ServerConfiguration>())).ReturnsAsync(false);
		var page = new AdminPage(_client.Object);
		await page.OnNavigatedTo(null!);
		((JellyfinConfigItemViewModel<string>)page.Items[0]).Value = "Den";
		SignInState.MarkEnded();

		await page.SaveCommand.ExecuteAsync(null);

		Assert.Equal("Couldn't save: your sign-in to Den ended. Sign in again, then try again.", page.SaveStatus);
	}

	[Fact]
	public async Task ContinueWatchingsRemove_AfterTheSignInEnded_SaysSo()
	{
		var folder = Path.Combine(Path.GetTempPath(), "nixie-161-signin-" + Guid.NewGuid().ToString("N"));
		var socket = new Subject<IOutboundWebSocketMessage>();
		var clock = new ManualClock();
		using var cache = new HomeCache(folder, socket, new CoreListLogger<HomeCache>(), clock);
		try
		{
			var settings = new Mock<ILocalSettingsService>();
			settings.Setup(x => x.ReadSetting(ContinueWatchingRules.HideAfterDays.Name, It.IsAny<int>())).Returns(0);
			var item = new BaseItemDto
			{
				Id = Guid.NewGuid(), Name = "Sunny", Type = BaseItemDto_Type.Movie,
				UserData = new UserItemDataDto { PlaybackPositionTicks = 5_000, LastPlayedDate = clock.GetUtcNow() - TimeSpan.FromDays(1) },
			};
			_client.SetupGet(x => x.BaseUrl).Returns("https://jellyfin.example");
			_client.SetupGet(x => x.UserId).Returns(Guid.NewGuid());
			_client.Setup(x => x.GetUserViews()).ReturnsAsync([]);
			_client.Setup(x => x.GetContinueWatching()).ReturnsAsync(new BaseItemDtoQueryResult { Items = [item] });
			_client.Setup(x => x.GetNextUp()).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
			_client.Setup(x => x.SetResumePosition(It.IsAny<Guid>(), It.IsAny<long>())).Returns(() => { SignInState.MarkEnded(); return Task.FromResult(false); });
			var home = new HomeViewModel(_client.Object, socket, new GlobalCommands(Mock.Of<INavigationServiceCore>(), _client.Object, Mock.Of<INavigationViewServiceCore>(),
				Mock.Of<ISettings>()), cache, settings.Object, clock);
			await home.OnNavigatedTo(null!);

			await home.RemoveFromContinueWatching(item);

			Assert.Equal("Couldn't remove Sunny: your sign-in to Den ended. Sign in again, then try again.", home.Notice!.Message);
			Assert.DoesNotContain("didn't answer", home.Notice.Message);
		}
		finally
		{
			if (Directory.Exists(folder))
			{
				Directory.Delete(folder, true);
			}
		}
	}

	// ---- the websocket (the 1.5.1 review, JellyfinClient.cs:70)

	[Fact]
	public async Task TheWebsocket_StopsWhenTheSignInEnds_AndANewSignInStartsItAgain()
	{
		using var server = new FakeJellyfin();
		server.Start();
		var client = new JellyfinClient(new CoreListLogger<JellyfinClient>(), Mock.Of<IObserver<IOutboundWebSocketMessage>>(), Mock.Of<IJumpListService>(),
			Mock.Of<IDeviceProfileFactory>(), Mock.Of<ISettings>(), TestHomeCache.Unused());
		var signIn = new AuthenticationResult { AccessToken = "token-161-socket", ServerId = "fake-server", User = new UserDto { Id = Guid.NewGuid(), Name = "alice" } };
		await client.Initialize(server.BaseUrl, signIn);
		try
		{
			Assert.True(client.WebSocketRunning);

			SignInState.MarkEnded();
			Assert.False(client.WebSocketRunning); // no more dialling with the token the server refuses

			await client.Initialize(server.BaseUrl, signIn);
			Assert.True(client.WebSocketRunning);
			SignInState.SignedIn();               // a change that is not an end leaves it alone
			Assert.True(client.WebSocketRunning);
		}
		finally
		{
			client.StopWebSocket();
		}
	}
}
