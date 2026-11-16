using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Journal;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Microsoft.Extensions.Logging;
using Moq;

namespace FluentFin.Core.Tests.Journal;

/// <summary>
/// FluentFin's real client making the journal's requests to a fake Jellyfin on localhost (a fake token, fake data): what is
/// asked, how answers are read, and that the token is only ever in a header, never in a url or a log line.
/// </summary>
[Collection(SessionInfoCollection.Name)]
public sealed class JellyfinClientJournalTests : IAsyncLifetime
{
	private const string FakeToken = "fake-journal-token-0123456789";
	private static readonly Guid UserId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
	private static readonly Guid ItemId = Guid.Parse("44444444-4444-4444-4444-444444444444");
	private static readonly Guid SeriesId = Guid.Parse("55555555-5555-5555-5555-555555555555");

	private readonly RecordingJellyfin _server = new();
	private readonly CoreListLogger<JellyfinClient> _log = new();
	private JellyfinClient _client = null!;

	public async Task InitializeAsync()
	{
		_server.Start();
		var settings = new Mock<ISettings>();
		settings.SetupGet(x => x.MediaPlayer).Returns(MediaPlayerType.Mpv);
		_client = new JellyfinClient(_log, Mock.Of<IObserver<IOutboundWebSocketMessage>>(), Mock.Of<IJumpListService>(), Mock.Of<IDeviceProfileFactory>(), settings.Object, TestHomeCache.Unused());
		await _client.Initialize(_server.BaseUrl, new AuthenticationResult
		{
			AccessToken = FakeToken,
			ServerId = "fake-server",
			User = new UserDto { Id = UserId, Name = "alice" },
		});
	}

	public Task DisposeAsync()
	{
		_server.Dispose();
		return Task.CompletedTask;
	}

	private void AssertTokenNeverLeaked()
	{
		Assert.DoesNotContain(_server.Requests, x => x.Url.Contains(FakeToken, StringComparison.Ordinal) || x.Body.Contains(FakeToken, StringComparison.Ordinal));
		Assert.DoesNotContain(_log.Entries, x => x.Message.Contains(FakeToken, StringComparison.Ordinal) || (x.Exception?.ToString().Contains(FakeToken, StringComparison.Ordinal) ?? false));
	}

	[Fact]
	public async Task PlayedEpisodes_AreAskedNewestPlayedFirst_AndRead()
	{
		_server.Respond("/Items", 200, $$$"""
			{"Items":[{"Id":"{{{ItemId:N}}}","Type":"Episode","Name":"Trojan's Horse","SeriesId":"{{{SeriesId:N}}}","SeriesName":"Severance",
			  "SeasonId":"{{{Guid.Empty:N}}}","ParentIndexNumber":2,"IndexNumber":5,"RunTimeTicks":27000000000,
			  "ImageTags":{"Primary":"still-tag"},"SeriesPrimaryImageTag":"poster-tag",
			  "UserData":{"Played":true,"PlayCount":3,"LastPlayedDate":"2026-11-01T20:15:00.0000000Z","IsFavorite":true,"Rating":8}}],
			 "TotalRecordCount":401}
			""");

		var page = await _client.GetPlayedEpisodesAsync(200, 200);

		var request = _server.Requests.Last(x => x.Path == "/Items");
		Assert.Equal("GET", request.Method);
		var query = request.Query;
		Assert.Equal(UserId, Guid.Parse(query["userId"]!));
		Assert.Equal("Episode", query["includeItemTypes"]);
		Assert.Equal("true", query["recursive"], ignoreCase: true);
		Assert.Equal("true", query["isPlayed"], ignoreCase: true);
		Assert.Equal("DatePlayed", query["sortBy"]);
		Assert.Equal("Descending", query["sortOrder"]);
		Assert.Equal(("200", "200"), (query["startIndex"], query["limit"]));
		Assert.Equal("true", query["enableUserData"], ignoreCase: true);

		Assert.Equal(401, page!.TotalCount);
		var episode = Assert.Single(page.Items);
		Assert.Equal(new JournalEpisode(ItemId, "Episode", "Trojan's Horse", SeriesId, "Severance", Guid.Empty, 2, 5, 27000000000, "still-tag", "poster-tag",
			new JournalUserData(true, 3, new DateTimeOffset(2026, 11, 1, 20, 15, 0, TimeSpan.Zero), true, 8)), episode);
		Assert.True(episode.IsEpisode);
		AssertTokenNeverLeaked();
	}

	[Fact]
	public async Task UserData_IsReadForTheUser()
	{
		_server.Respond($"/UserItems/{ItemId}/UserData", 200, """{"Played":false,"PlayCount":1,"LastPlayedDate":null,"IsFavorite":false}""");

		var data = await _client.GetUserDataAsync(ItemId);

		Assert.Equal(new JournalUserData(false, 1, null), data);
		Assert.Equal(UserId, Guid.Parse(_server.Requests.Last().Query["userId"]!));
		AssertTokenNeverLeaked();
	}

	[Fact]
	public async Task MarkPlayed_SendsTheDate()
	{
		_server.Respond($"/UserPlayedItems/{ItemId}", 200, """{"Played":true}""");
		var at = new DateTimeOffset(2026, 11, 14, 19, 30, 0, TimeSpan.Zero);

		Assert.True(await _client.MarkPlayedAsync(ItemId, at));

		var request = _server.Requests.Last();
		Assert.Equal("POST", request.Method);
		Assert.Equal(at, DateTimeOffset.Parse(request.Query["datePlayed"]!, System.Globalization.CultureInfo.InvariantCulture));
		AssertTokenNeverLeaked();
	}

	[Fact]
	public async Task ARating_IsSentAsJellyfinsZeroToTen()
	{
		_server.Respond($"/UserItems/{ItemId}/UserData", 200, """{"Rating":7}""");

		Assert.True(await _client.SetRatingAsync(ItemId, 7));

		var request = _server.Requests.Last();
		Assert.Equal("POST", request.Method);
		Assert.Equal(7, JsonNode.Parse(request.Body)!["Rating"]!.GetValue<double>());
		AssertTokenNeverLeaked();
	}

	[Fact]
	public async Task RefusedRequests_AreNullOrFalse_LoggedWithTheStatusOnly()
	{
		_server.Respond($"/UserItems/{ItemId}/UserData", 500, "");
		_server.Respond($"/UserPlayedItems/{ItemId}", 401, "");

		Assert.Null(await _client.GetUserDataAsync(ItemId));
		Assert.False(await _client.MarkPlayedAsync(ItemId, DateTimeOffset.UnixEpoch));
		Assert.Null(await _client.GetItemAsync(ItemId));
		Assert.Null(await _client.GetPlayedEpisodesAsync(0, 10));

		Assert.Contains(_log.Entries, x => x.Level == LogLevel.Warning && x.Message.Contains("HTTP 500", StringComparison.Ordinal) && x.Message.Contains(ItemId.ToString(), StringComparison.Ordinal));
		Assert.Contains(_log.Entries, x => x.Level == LogLevel.Warning && x.Message.Contains("HTTP 401", StringComparison.Ordinal));
		AssertTokenNeverLeaked();
	}

	[Fact]
	public async Task AHungServer_IsNullOrFalse_NeverAnException()
	{
		_server.Hang($"/UserItems/{ItemId}/UserData");
		_server.Hang($"/Items/{ItemId}");
		_server.Hang("/Items");
		_server.Hang($"/UserPlayedItems/{ItemId}");
		UseHttpTimeout(TimeSpan.FromMilliseconds(300));

		// HttpClient's timeout is a TaskCanceledException nobody asked for: a failed request, not a cancellation
		Assert.Null(await _client.GetUserDataAsync(ItemId));
		Assert.Null(await _client.GetItemAsync(ItemId));
		Assert.Null(await _client.GetPlayedEpisodesAsync(0, 10));
		Assert.False(await _client.MarkPlayedAsync(ItemId, DateTimeOffset.UnixEpoch));
		Assert.False(await _client.SetRatingAsync(ItemId, 7));

		Assert.Equal(5, _log.Entries.Count(x => x.Level == LogLevel.Warning && x.Message.Contains("timed out", StringComparison.Ordinal)));
		AssertTokenNeverLeaked();
	}

	/// <summary>
	/// FluentFin's client keeps the SDK's 100 s HttpClient timeout; the test gives it a short one, so a hung server shows
	/// sooner. Same settings (url, identity, token), only the HttpClient differs.
	/// </summary>
	private void UseHttpTimeout(TimeSpan timeout)
	{
		const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
		var settings = (Jellyfin.Sdk.JellyfinSdkSettings)typeof(JellyfinClient).GetField("_settings", Private)!.GetValue(_client)!;
		var api = new Jellyfin.Sdk.JellyfinApiClient(new Jellyfin.Sdk.JellyfinRequestAdapter(new Jellyfin.Sdk.JellyfinAuthenticationProvider(settings), settings, new HttpClient { Timeout = timeout }));
		typeof(JellyfinClient).GetField("_jellyfinApiClient", Private)!.SetValue(_client, api);
	}

	[Fact]
	public async Task PlaybackReporting_IsAskedForThisUser_WithTheTokenInTheHeader()
	{
		_server.Respond("/user_usage_stats/submit_custom_query", 200, $$"""
			{"colums":["DateCreated","ItemId","PlayDuration"],"results":[["2026-11-01 20:15:00","{{ItemId:N}}","2640"]],"message":""}
			""");

		var history = await _client.GetPlaybackHistoryAsync(UserId);

		Assert.True(history.Available);
		// the plugin's server-local time with no zone, read in this PC's zone (the sync corrects for a server elsewhere)
		var wall = new DateTime(2026, 11, 1, 20, 15, 0);
		Assert.Equal(new PlaybackHistoryPlay(ItemId, new DateTimeOffset(wall, TimeZoneInfo.Local.GetUtcOffset(wall)), TimeSpan.FromMinutes(44)), Assert.Single(history.Plays));
		var request = _server.Requests.Last();
		Assert.Equal("POST", request.Method);
		Assert.Equal($"MediaBrowser Token=\"{FakeToken}\"", request.Authorization);
		var body = JsonNode.Parse(request.Body)!;
		Assert.Contains($"'{UserId:N}'", body["CustomQueryString"]!.GetValue<string>());
		Assert.False(body["ReplaceUserId"]!.GetValue<bool>());
		Assert.Empty(request.Query);
		AssertTokenNeverLeaked();
	}

	[Theory]
	[InlineData(401)]
	[InlineData(403)]
	[InlineData(404)]
	public async Task PlaybackReporting_ThatIsNotThereOrNotAllowed_IsUnavailable_Silently(int status)
	{
		_server.Respond("/user_usage_stats/submit_custom_query", status, "");

		var history = await _client.GetPlaybackHistoryAsync(UserId);

		Assert.False(history.Available);
		Assert.DoesNotContain(_log.Entries, x => x.Level >= LogLevel.Warning);
		Assert.Contains(_log.Entries, x => x.Level == LogLevel.Debug && x.Message.Contains($"HTTP {status}", StringComparison.Ordinal));
	}

	[Fact]
	public async Task PlaybackReporting_ThatFailsOtherwise_IsUnavailable_WithAWarning()
	{
		_server.Respond("/user_usage_stats/submit_custom_query", 500, "boom");

		var history = await _client.GetPlaybackHistoryAsync(UserId);

		Assert.False(history.Available);
		Assert.Contains(_log.Entries, x => x.Level == LogLevel.Warning && x.Message.Contains("HTTP 500", StringComparison.Ordinal));
		AssertTokenNeverLeaked();
	}
}

/// <summary>A Jellyfin enough for FluentFin's login and the journal's requests; records method, url, query, Authorization and body.</summary>
internal sealed class RecordingJellyfin : IDisposable
{
	private readonly HttpListener _listener = new();
	private readonly ConcurrentDictionary<string, (int Status, string Body)> _responses = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentQueue<RecordedRequest> _requests = new();
	private Task? _loop;

	public string BaseUrl { get; private set; } = "";

	/// <summary>Without the session websocket's connection attempts and the login's own requests.</summary>
	public IReadOnlyList<RecordedRequest> Requests => [.. _requests.Where(x => x.Path is not ("/socket" or "/Sessions/Capabilities/Full" or "/Plugins" or "/UserViews"))];

	public void Respond(string path, int status, string body) => _responses[path] = (status, body);

	private readonly ConcurrentDictionary<string, bool> _hung = new(StringComparer.OrdinalIgnoreCase);
	private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>Requests to this path get no answer until the server is disposed.</summary>
	public void Hang(string path) => _hung[path] = true;

	public void Start()
	{
		var probe = new TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		var port = ((IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();
		BaseUrl = $"http://localhost:{port}";
		_listener.Prefixes.Add(BaseUrl + "/");
		_listener.Start();
		Respond("/Sessions/Capabilities/Full", 204, "");
		Respond("/Plugins", 200, "[]");
		Respond("/UserViews", 200, """{"Items":[],"TotalRecordCount":0}""");
		_loop = Task.Run(Serve);
	}

	private async Task Serve()
	{
		while (_listener.IsListening)
		{
			HttpListenerContext context;
			try
			{
				context = await _listener.GetContextAsync();
			}
			catch (Exception) when (!_listener.IsListening)
			{
				return;
			}
			catch (HttpListenerException)
			{
				return;
			}

			// each request on its own, so one held back (Hang) does not hold the others
			_ = Task.Run(() => HandleAsync(context));
		}
	}

	private async Task HandleAsync(HttpListenerContext context)
	{
		{
			var request = context.Request;
			string body;
			using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
			{
				body = await reader.ReadToEndAsync();
			}

			var url = request.Url!;
			var query = System.Web.HttpUtility.ParseQueryString(url.Query);
			_requests.Enqueue(new RecordedRequest(request.HttpMethod, url.AbsolutePath, url.ToString(),
				query.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => query[k], StringComparer.OrdinalIgnoreCase),
				request.Headers["Authorization"], body));
			var (status, responseBody) = _responses.TryGetValue(url.AbsolutePath, out var response) ? response : (404, "");
			try
			{
				if (_hung.ContainsKey(url.AbsolutePath))
				{
					// a hung server: no answer until the test ends
					await _released.Task;
				}

				context.Response.StatusCode = status;
				if (responseBody.Length > 0)
				{
					var bytes = Encoding.UTF8.GetBytes(responseBody);
					context.Response.ContentType = "application/json";
					context.Response.ContentLength64 = bytes.Length;
					await context.Response.OutputStream.WriteAsync(bytes);
				}

				context.Response.Close();
			}
			catch (Exception)
			{
				// the client went away
			}
		}
	}

	public void Dispose()
	{
		_released.TrySetResult();
		try
		{
			_listener.Stop();
			_listener.Close();
		}
		catch (ObjectDisposedException)
		{
		}

		_loop?.Wait(TimeSpan.FromSeconds(2));
	}
}

internal sealed record RecordedRequest(string Method, string Path, string Url, Dictionary<string, string?> Query, string? Authorization, string Body);
