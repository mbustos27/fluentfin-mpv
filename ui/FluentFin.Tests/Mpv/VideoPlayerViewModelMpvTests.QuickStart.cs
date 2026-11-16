using System.Diagnostics;
using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Moq;

namespace FluentFin.Tests.Mpv;

/// <summary>
/// Round 7 (2026-11-14, the first real server): mpv "never started". The play request was built from FluentFin's playlist, a
/// request per season in turn (31 s for a 250-episode show), the user left meanwhile, and the start carried on anyway. Now:
/// one request for the queue, each step shown, a start whose page is gone stops before the backend, and the page's life is
/// in the log with every change of generation and why.
/// </summary>
public sealed partial class VideoPlayerViewModelMpvTests
{
	[Fact]
	public async Task ALongSeries_StartsAfterOneRequest_NotOneForEachSeason()
	{
		const int seasons = 20;
		var latency = TimeSpan.FromMilliseconds(150);
		var library = new FakeLibrary(_client, latency);
		var show = library.AddSeries(seasons, episodesPerSeason: 12, played: 30);
		var episode = show.Episodes[125];
		var vm = CreateViewModel();

		var watch = Stopwatch.StartNew();
		await vm.OnNavigatedTo(new PlaybackRequest(FakeLibrary.PageCopy(episode), new PlaybackSelection(1, 2)));
		var elapsed = watch.Elapsed;

		_output.WriteLine($"navigation to play: {elapsed.TotalMilliseconds:F0} ms with {library.Requests.Length} request(s) of {latency.TotalMilliseconds:F0} ms ({seasons} seasons)");
		Assert.Equal(["set_fullscreen", "play"], _backend.Commands);
		Assert.Equal([$"GetSeriesEpisodes {show.Series.Name}"], library.Requests);
		// one round trip, not one per season (that was seasons + 3 of them)
		Assert.True(elapsed < latency * (seasons / 2), $"play was sent {elapsed.TotalMilliseconds:F0} ms after the navigation");
		var play = _backend.Args("play");
		Assert.Equal(240, play["itemIds"]!.AsArray().Count);
		Assert.Equal(125, (int?)play["startIndex"]);
		Assert.Equal(126_000L, (long?)play["startPositionTicks"]);
		// what the next real-server log needs to show: how long the queue took, and the play
		Assert.Contains(_pageLog.Messages, x => x.StartsWith($"Queue for Episode {episode.Id} (generation 1): 240 item(s) from index 125, loaded in ", StringComparison.Ordinal));
		Assert.Contains(_pageLog.Messages, x => x.StartsWith("Sent play to the mpv backend ", StringComparison.Ordinal) && x.EndsWith(" ms after the player page opened (generation 1)", StringComparison.Ordinal));
	}

	/// <summary>The cases the old path (FluentFin's playlist) and the new one must agree on.</summary>
	public static TheoryData<string> StartCases => ["movie", "episode mid-series", "season", "series: first unplayed", "series: all played", "episode with picked streams"];

	[Theory]
	[MemberData(nameof(StartCases))]
	public async Task TheQueue_StartsWhereTheOldPathStarted(string startCase)
	{
		var library = new FakeLibrary(_client);
		var show = library.AddSeries(seasons: 4, episodesPerSeason: 5, played: startCase == "series: all played" ? 20 : 7);
		var movie = library.AddMovie(resumeTicks: 5_000_000);
		var (item, selection, startIndex, startTicks) = startCase switch
		{
			"movie" => (movie, (PlaybackSelection?)null, 0, 5_000_000L),
			"episode mid-series" => (show.Episodes[12], null, 12, 13_000L),
			"season" => (show.Seasons[2], null, 7, 8_000L),
			"series: first unplayed" => (show.Series, null, 7, 8_000L),
			"series: all played" => (show.Series, null, 0, 1_000L),
			"episode with picked streams" => (show.Episodes[12], new PlaybackSelection(2, PlaybackSelection.NoSubtitles), 12, 13_000L),
			_ => throw new ArgumentOutOfRangeException(nameof(startCase)),
		};
		var expected = await OldPathRequest(item.Id!.Value, selection);
		library.ClearRequests();
		var page = FakeLibrary.PageCopy(item);

		await CreateViewModel().OnNavigatedTo(selection is null ? page : new PlaybackRequest(page, selection));

		var play = _backend.Args("play");
		Assert.True(JsonNode.DeepEquals(BridgeCommands.Play(expected!), play), $"old: {BridgeCommands.Play(expected!).ToJsonString()}\nnew: {play.ToJsonString()}");
		// and the old path was right: not two agreeing mistakes
		Assert.Equal(startIndex, (int?)play["startIndex"]);
		Assert.Equal(startTicks, (long?)play["startPositionTicks"]);
		Assert.Equal(selection?.AudioStreamIndex, (int?)play["audioStreamIndex"]);
		Assert.Equal(selection?.SubtitleStreamIndex, (int?)play["subtitleStreamIndex"]);
		Assert.Single(library.Requests);
	}

	/// <summary>
	/// The play request as the mpv path built it before round 7, from upstream's PlaylistViewModel: VideoPlayerViewModel.Initialize
	/// and the former BuildMpvRequest, verbatim but for the names.
	/// </summary>
	private async Task<BridgePlayRequest?> OldPathRequest(Guid id, PlaybackSelection? selection)
	{
		var dto = await _client.Object.GetItem(id);
		if (dto is null)
		{
			return null;
		}

		var playlist = dto.Type switch
		{
			BaseItemDto_Type.Movie => PlaylistViewModel.FromMovie(dto),
			BaseItemDto_Type.Episode => await PlaylistViewModel.FromEpisode(_client.Object, dto),
			BaseItemDto_Type.Series => await PlaylistViewModel.FromSeries(_client.Object, dto),
			BaseItemDto_Type.Season => await PlaylistViewModel.FromSeason(_client.Object, dto),
			_ => new PlaylistViewModel()
		};

		var items = playlist.Items.Where(x => x.Dto.Id.HasValue).ToList();
		if (items.Count == 0)
		{
			return null;
		}

		var start = items.FirstOrDefault(x => x.Dto.Id == id);
		if (start is null)
		{
			playlist.AutoSelect();
			start = playlist.SelectedItem is { Dto.Id: not null } selected ? selected : items[0];
		}

		start.Selection = selection ?? start.Selection;
		playlist.SelectedItem = start;

		return BridgePlayRequest.FromPlaylist(items.Select(x => x.Dto.Id!.Value), items.IndexOf(start), start.Dto.UserData?.PlaybackPositionTicks ?? 0, start.Selection);
	}

	[Fact]
	public async Task APageLeftWhileItsQueueLoads_LaunchesNoBackend_AndSendsNoConnect()
	{
		var library = new FakeLibrary(_client);
		var show = library.AddSeries(seasons: 3, episodesPerSeason: 4);
		var answer = new TaskCompletionSource();
		library.Gate = answer.Task;
		var vm = CreateViewModel();

		var navigating = vm.OnNavigatedTo(FakeLibrary.PageCopy(show.Episodes[5]));
		await BridgeClientTests.WaitUntil(() => library.Requests.Length == 1);
		Assert.Equal("Loading the queue…", vm.MpvStatusText);
		await vm.OnNavigatedFrom();
		answer.SetResult();
		await navigating.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(0, _backend.Starts);
		Assert.Empty(_backend.Identities);
		Assert.Empty(_backend.Commands);
		Assert.Contains(_playbackLog.Messages, x => x.StartsWith("Not starting the mpv backend for generation 1: its player page is gone", StringComparison.Ordinal));
	}

	[Fact]
	public async Task APageLeftWhileTheBackendStarts_SendsNoConnect()
	{
		var movie = new FakeLibrary(_client).AddMovie();
		var started = new TaskCompletionSource();
		_backend.StartGate = started.Task;
		var vm = CreateViewModel();

		var navigating = vm.OnNavigatedTo(movie);
		await BridgeClientTests.WaitUntil(() => _backend.Starts == 1);
		Assert.Equal("Starting the player…", vm.MpvStatusText);
		await vm.OnNavigatedFrom();
		started.SetResult();
		await navigating.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Empty(_backend.Identities);
		Assert.Empty(_backend.Commands);
		Assert.Contains(_playbackLog.Messages, x => x.StartsWith("Not connecting the mpv backend for generation 1: its player page is gone", StringComparison.Ordinal));
	}

	[Fact]
	public async Task AStartOvertakenByANewerPage_WhileItsQueueLoads_StartsNothing()
	{
		var library = new FakeLibrary(_client);
		var show = library.AddSeries(seasons: 2, episodesPerSeason: 3);
		var movie = library.AddMovie();
		var answer = new TaskCompletionSource();
		library.Gate = answer.Task;
		var oldPage = CreateViewModel();
		var newPage = CreateViewModel();

		var oldNavigation = oldPage.OnNavigatedTo(FakeLibrary.PageCopy(show.Series));
		await BridgeClientTests.WaitUntil(() => library.Requests.Length == 1);
		library.Gate = Task.CompletedTask;
		await newPage.OnNavigatedTo(movie);
		answer.SetResult();
		await oldNavigation.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(1, _backend.Starts);
		Assert.Single(_backend.Identities);
		Assert.Equal(["set_fullscreen", "play"], _backend.Commands);
		Assert.Equal(movie.Id!.Value.ToString("N"), (string?)_backend.Args("play")["itemIds"]![0]);
	}

	[Fact]
	public async Task TheStart_ShowsEachStep_ThenTheVideo()
	{
		var movie = new FakeLibrary(_client).AddMovie();
		var vm = CreateViewModel();
		var shown = new List<string>();
		vm.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(VideoPlayerViewModel.MpvStatusText))
			{
				shown.Add(vm.MpvStatusText);
			}
		};

		await vm.OnNavigatedTo(movie);

		Assert.Equal(["Loading the queue…", "Starting the player…", "Connecting to the server…", "Starting playback…", ""], shown);
		Assert.True(vm.IsMpvVideoVisible);
		Assert.Null(vm.MpvMessage);
	}

	[Fact]
	public async Task TheNextStart_DoesNotSayItStartsABackendThatRuns()
	{
		var movie = new FakeLibrary(_client).AddMovie();
		await CreateViewModel().OnNavigatedTo(movie);
		var vm = CreateViewModel();
		var shown = new List<string>();
		vm.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(VideoPlayerViewModel.MpvStatusText))
			{
				shown.Add(vm.MpvStatusText);
			}
		};

		await vm.OnNavigatedTo(movie);

		Assert.Equal(["Loading the queue…", "Connecting to the server…", "Starting playback…", ""], shown);
	}

	[Fact]
	public async Task AQueueThatCannotBeLoaded_SaysSo_AndStartsNoBackend()
	{
		var library = new FakeLibrary(_client) { FailEpisodes = true };
		var show = library.AddSeries(seasons: 2, episodesPerSeason: 2);
		var vm = CreateViewModel();

		await vm.OnNavigatedTo(FakeLibrary.PageCopy(show.Episodes[1]));

		Assert.Equal("Loading the queue failed: The episodes could not be loaded from the server.", vm.MpvMessage);
		Assert.True(vm.MpvMessageIsError);
		Assert.Equal("", vm.MpvStatusText);
		Assert.Equal(0, _backend.Starts);
		Assert.Contains(_pageLog.Entries, x => x.Level == LogLevel.Error && x.Message.Contains("step: LoadingQueue", StringComparison.Ordinal));
	}

	[Fact]
	public async Task AConnectThatFails_SaysSo()
	{
		var movie = new FakeLibrary(_client).AddMovie();
		_backend.ConnectFailure = new BridgeCommandException("connect", "auth_failed", "the server refused the login");
		var vm = CreateViewModel();

		await vm.OnNavigatedTo(movie);

		Assert.Equal("Connecting failed: The backend refused 'connect': auth_failed (the server refused the login)", vm.MpvMessage);
		Assert.True(vm.MpvMessageIsError);
		Assert.DoesNotContain("play", _backend.Commands);
	}

	[Fact]
	public async Task APlayThatIsRefused_SaysWhatTheBackendSaid()
	{
		var movie = new FakeLibrary(_client).AddMovie();
		_backend.CommandFailure = ("play", new BridgeCommandException("play", "not_connected", null));
		var vm = CreateViewModel();

		await vm.OnNavigatedTo(movie);

		Assert.Equal("The backend refused 'play': not_connected", vm.MpvMessage);
		Assert.True(vm.MpvMessageIsError);
		Assert.False(vm.IsMpvVideoVisible);
	}

	[Fact]
	public async Task AStartFailure_NeverShowsOrLogsAToken()
	{
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ThrowsAsync(new HttpRequestException($"GET http://jf:8096/Items/{movie.Id:N}?api_key=the-token failed"));
		var vm = CreateViewModel();

		await vm.OnNavigatedTo(movie);

		Assert.Equal($"Loading the queue failed: GET http://jf:8096/Items/{movie.Id:N}?api_key=*** failed", vm.MpvMessage);
		var error = Assert.Single(_pageLog.Entries, x => x.Level == LogLevel.Error);
		Assert.Contains("api_key=***", error.Message, StringComparison.Ordinal);
		Assert.Contains("HttpRequestException", error.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(_pageLog.Messages.Concat(_playbackLog.Messages), x => x.Contains("the-token", StringComparison.Ordinal));
	}

	[Fact]
	public async Task EveryChangeOfGeneration_IsLoggedWithItsReason_AndSoIsThePagesLife()
	{
		var movie = new FakeLibrary(_client).AddMovie();
		var titleBar = CreateTitleBar();

		var left = CreateViewModel();
		await left.OnNavigatedTo(movie);
		await left.OnNavigatedFrom();
		var removed = CreateViewModel();
		await removed.OnNavigatedTo(movie);
		await removed.OnPageRemoved(navigatedFrom: false, loadedAgain: false);
		var signedOut = CreateViewModel();
		await signedOut.OnNavigatedTo(movie);
		await titleBar.Logout();
		await signedOut.OnPageRemoved(navigatedFrom: false, loadedAgain: false);
		await signedOut.OnNavigatedFrom();

		var bumps = _playbackLog.Messages.Where(x => x.StartsWith("mpv playback generation ", StringComparison.Ordinal)).ToList();
		Assert.Equal([
			"mpv playback generation 0 -> 1: a player page opened",
			"mpv playback generation 1 -> 2: the player page was left by a navigation",
			"mpv playback generation 2 -> 3: a player page opened",
			"mpv playback generation 3 -> 4: the player page was removed from the window without a navigation",
			"mpv playback generation 4 -> 5: a player page opened",
			"mpv playback generation 5 -> 6: signing out",
			"mpv playback generation 5 ended (the player page was removed from the window without a navigation): generation 6 has taken over, nothing to stop",
		], bumps);
		Assert.True(_playback.IsCurrent(6));
		Assert.Contains(_playbackLog.Messages, x => x.StartsWith("Signing out: stopping the mpv playback of generation 5 first", StringComparison.Ordinal));

		var page = _pageLog.Messages;
		var id = movie.Id!.Value.ToString("N");
		Assert.Equal([
			$"Player page opened with the mpv player (generation 1): Movie {id}",
			$"Player page opened with the mpv player (generation 3): Movie {id}",
			$"Player page opened with the mpv player (generation 5): Movie {id}",
		], page.Where(x => x.StartsWith("Player page opened", StringComparison.Ordinal)));
		Assert.Contains("Player page (generation 1): the player page was left by a navigation", page);
		Assert.Contains(page, x => x.StartsWith("The player page (generation 3) was removed from the window without being navigated from", StringComparison.Ordinal));
		Assert.Contains("Player page (generation 5): the player page was removed from the window without a navigation", page);
		Assert.Contains("Player page (generation 5): the player page was left by a navigation, but it was already left; nothing more to stop", page);
		Assert.All(_pageLog.Entries.Concat(_playbackLog.Entries), x => Assert.True(x.Level == LogLevel.Information, $"{x.Level}: {x.Message}"));

		// no token, no server address
		Assert.DoesNotContain(page.Concat(_playbackLog.Messages), x => x.Contains("the-token", StringComparison.Ordinal) || x.Contains("jf:8096", StringComparison.Ordinal));
	}

	[Fact]
	public async Task ASignOutWithNoPagePlaying_SaysSo()
	{
		var movie = new FakeLibrary(_client).AddMovie();
		var vm = CreateViewModel();
		await vm.OnNavigatedTo(movie);
		await vm.OnNavigatedFrom();

		await CreateTitleBar().Logout();

		Assert.Contains("Signing out: no player page plays with mpv (generation 2); waiting only for stop reports still owed", _playbackLog.Messages);
		Assert.DoesNotContain(_playbackLog.Messages, x => x.Contains("signing out", StringComparison.Ordinal));
	}

	[Fact]
	public async Task ALeaveWithNoBackendRunning_IsLoggedToo()
	{
		// the round-6 log was silent here: the page left while its queue loaded, so nothing ran and nothing was said
		var library = new FakeLibrary(_client);
		var movie = library.AddMovie();
		var answer = new TaskCompletionSource();
		library.Gate = answer.Task;
		var vm = CreateViewModel();

		var navigating = vm.OnNavigatedTo(movie);
		await BridgeClientTests.WaitUntil(() => library.Requests.Length == 1);
		await vm.OnNavigatedFrom();
		answer.SetResult();
		await navigating.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Contains("mpv playback generation 1 -> 2: the player page was left by a navigation", _playbackLog.Messages);
		Assert.Contains("Nothing to stop: the mpv backend is not running", _playbackLog.Messages);
	}
}
