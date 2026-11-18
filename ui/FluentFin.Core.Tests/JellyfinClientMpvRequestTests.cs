using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Microsoft.Extensions.Logging;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// fluentfin-mpv: FluentFin's real client against a fake Jellyfin (HttpListener on localhost, a fake token): the one request the
/// mpv player's queue makes, and the plugin list a user who is not an administrator is refused at login.
/// </summary>
[Collection(SessionInfoCollection.Name)]
public sealed class JellyfinClientMpvRequestTests : IAsyncLifetime
{
	private const string FakeToken = "fake-token-0123456789";
	private static readonly Guid UserId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

	private readonly FakeJellyfin _server = new();
	private readonly CoreListLogger<JellyfinClient> _log = new();
	private JellyfinClient _client = null!;

	public Task InitializeAsync()
	{
		_server.Start();
		return Task.CompletedTask;
	}

	public Task DisposeAsync()
	{
		_server.Dispose();
		return Task.CompletedTask;
	}

	private async Task LogIn()
	{
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

	// Seaglass 1.1.4: Remove from Continue Watching (and Reset Watch Progress, which used to post the whole item to the
	// administrators' metadata endpoint, /Items/{id}, where the user data is never read) set the resume point through the
	// user-data endpoint any user may call, and nothing else of the user's data
	[Theory]
	[InlineData(0L)]
	[InlineData(4_200_000_000L)]
	public async Task SetResumePosition_PostsTheResumePointAlone_ToTheUserDataEndpoint(long ticks)
	{
		var item = Guid.NewGuid();
		_server.Respond($"/UserItems/{item}/UserData", 200, """{"PlaybackPositionTicks":0,"Played":false}""");
		await LogIn();
		var before = _server.ApiRequests.Count;

		Assert.True(await _client.SetResumePosition(item, ticks));

		var request = Assert.Single(_server.ApiRequests.Skip(before));
		Assert.Equal("POST", request.Method);
		Assert.Equal($"/UserItems/{item}/UserData", request.Path, ignoreCase: true);
		Assert.Equal(UserId, Guid.Parse(request.Query["userId"]!));
		using var body = System.Text.Json.JsonDocument.Parse(request.Body);
		Assert.Equal(ticks, body.RootElement.GetProperty("PlaybackPositionTicks").GetInt64());
		Assert.Equal(["PlaybackPositionTicks"], body.RootElement.EnumerateObject().Where(p => p.Value.ValueKind != System.Text.Json.JsonValueKind.Null).Select(p => p.Name));
	}

	[Fact]
	public async Task ResetProgress_IsTheSameUserDataRequest_NotTheMetadataEditor()
	{
		var item = Guid.NewGuid();
		_server.Respond($"/UserItems/{item}/UserData", 200, """{"PlaybackPositionTicks":0}""");
		await LogIn();
		var before = _server.ApiRequests.Count;

		await _client.ResetProgress(item);

		var request = Assert.Single(_server.ApiRequests.Skip(before));
		Assert.Equal(("POST", $"/UserItems/{item}/UserData".ToLowerInvariant()), (request.Method, request.Path.ToLowerInvariant()));
	}

	[Fact]
	public async Task SetResumePosition_TheServerRefusing_IsFalse()
	{
		await LogIn();
		Assert.False(await _client.SetResumePosition(Guid.NewGuid(), 0)); // 404
	}

	[Fact]
	public async Task SeriesEpisodes_IsOneRequest_WithTheUserAndUserData_AndNoImagesOrFields()
	{
		var seriesId = Guid.NewGuid();
		var episodes = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
		_server.Respond($"/Shows/{seriesId}/Episodes", 200, $$$"""
			{"Items":[
			  {"Id":"{{{episodes[0]:N}}}","Type":"Episode","UserData":{"Played":true,"PlaybackPositionTicks":0}},
			  {"Id":"{{{episodes[1]:N}}}","Type":"Episode","UserData":{"Played":false,"PlaybackPositionTicks":4200}},
			  {"Id":"{{{episodes[2]:N}}}","Type":"Episode"}
			],"TotalRecordCount":3}
			""");
		await LogIn();
		var before = _server.ApiRequests.Count;

		var result = await _client.GetSeriesEpisodes(seriesId);

		var request = Assert.Single(_server.ApiRequests.Skip(before));
		Assert.Equal("GET", request.Method);
		Assert.Equal($"/Shows/{seriesId}/Episodes", request.Path, ignoreCase: true);
		var query = request.Query;
		Assert.Equal(UserId, Guid.Parse(query["userId"]!));
		Assert.Equal("false", query["isMissing"], ignoreCase: true);
		Assert.Equal("false", query["enableImages"], ignoreCase: true);
		Assert.Equal("true", query["enableUserData"], ignoreCase: true);
		// nothing the queue does not need
		Assert.Null(query["fields"]);
		Assert.Null(query["enableImageTypes"]);
		Assert.Null(query["imageTypeLimit"]);
		// the token goes in the header, never the URL
		Assert.DoesNotContain(FakeToken, request.Url, StringComparison.Ordinal);

		Assert.Equal(episodes, result!.Items!.Select(x => x.Id!.Value));
		Assert.Equal(4200L, result.Items![1].UserData!.PlaybackPositionTicks);
		Assert.True(result.Items[0].UserData!.Played);
	}

	[Fact]
	public async Task SeriesEpisodes_ThatFail_AreNull_AndLoggedWithoutTheToken()
	{
		var seriesId = Guid.NewGuid();
		_server.Respond($"/Shows/{seriesId}/Episodes", 500, "");
		await LogIn();

		var result = await _client.GetSeriesEpisodes(seriesId);

		Assert.Null(result);
		Assert.Contains(_log.Entries, x => x.Level == LogLevel.Error && x.Message.Contains(seriesId.ToString(), StringComparison.Ordinal));
		Assert.DoesNotContain(_log.Entries, x => x.Message.Contains(FakeToken, StringComparison.Ordinal) || (x.Exception?.ToString().Contains(FakeToken, StringComparison.Ordinal) ?? false));
	}

	[Theory]
	[InlineData(401)]
	[InlineData(403)]
	public async Task Plugins_RefusedToAUserWhoIsNotAnAdministrator_AreNone_AndNoError(int status)
	{
		SessionInfo.Plugins = [new PluginInfo { Name = "Playback Reporting" }];
		_server.Respond("/Plugins", status, "");

		await LogIn();

		Assert.Contains(_server.Requests, x => x.Path.Equals("/Plugins", StringComparison.OrdinalIgnoreCase));
		Assert.NotNull(SessionInfo.Plugins);
		Assert.Empty(SessionInfo.Plugins);
		Assert.DoesNotContain(_log.Entries, x => x.Level >= LogLevel.Error);
		Assert.Contains(_log.Entries, x => x.Level == LogLevel.Debug && x.Message.Contains("plugins", StringComparison.OrdinalIgnoreCase) && x.Message.Contains(status.ToString(), StringComparison.Ordinal));
	}

	[Fact]
	public async Task Plugins_ListedToAnAdministrator_AreRead()
	{
		_server.Respond("/Plugins", 200, """[{"Name":"Playback Reporting","Version":"1.0","Id":"5c534381-91a3-43cb-907a-35aa02eb9d2c","CanUninstall":true,"HasImage":false,"Status":"Active"}]""");

		await LogIn();

		Assert.Equal(["Playback Reporting"], SessionInfo.Plugins.Select(x => x.Name));
		Assert.DoesNotContain(_log.Entries, x => x.Level >= LogLevel.Error);
	}

	[Fact]
	public async Task Plugins_ThatFailOtherwise_AreStillAnError()
	{
		_server.Respond("/Plugins", 500, "");

		await LogIn();

		Assert.Contains(_log.Entries, x => x.Level == LogLevel.Error);
	}
}

[CollectionDefinition(Name)]
public sealed class SessionInfoCollection
{
	/// <summary>Tests that log in set SessionInfo (static): they run one at a time.</summary>
	public const string Name = "SessionInfo";
}

/// <summary>A Jellyfin server as far as FluentFin's login and the tests need it; everything else 404. Records every request.</summary>
internal sealed class FakeJellyfin : IDisposable
{
	private readonly HttpListener _listener = new();
	private readonly ConcurrentDictionary<string, (int Status, string Body)> _responses = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentQueue<FakeRequest> _requests = new();
	private Task? _loop;

	public string BaseUrl { get; private set; } = "";

	public IReadOnlyList<FakeRequest> Requests => [.. _requests];

	/// <summary>Without the session websocket's connection attempts (they come whenever its client tries).</summary>
	public IReadOnlyList<FakeRequest> ApiRequests => [.. _requests.Where(x => !x.Path.Equals("/socket", StringComparison.OrdinalIgnoreCase))];

	public void Respond(string path, int status, string body) => _responses[path] = (status, body);

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

			var url = context.Request.Url!;
			var sent = "";
			if (context.Request.HasEntityBody)
			{
				using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
				sent = await reader.ReadToEndAsync();
			}

			_requests.Enqueue(new FakeRequest(context.Request.HttpMethod, url.AbsolutePath, url.ToString(), System.Web.HttpUtility.ParseQueryString(url.Query), sent,
				context.Request.Headers["Authorization"] ?? context.Request.Headers["X-Emby-Authorization"], context.Request.UserAgent));
			var (status, body) = _responses.TryGetValue(url.AbsolutePath, out var response) ? response : (404, "");
			try
			{
				context.Response.StatusCode = status;
				if (body.Length > 0)
				{
					var bytes = Encoding.UTF8.GetBytes(body);
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

/// <summary>A request the fake server saw: <see cref="Body"/> what was sent; <see cref="Authorization"/> and <see cref="UserAgent"/> are the client's headers (Nixie: the client name).</summary>
internal sealed record FakeRequest(string Method, string Path, string Url, System.Collections.Specialized.NameValueCollection Query, string Body = "", string? Authorization = null, string? UserAgent = null);

internal sealed class CoreListLogger<T> : ILogger<T>
{
	private readonly ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> _entries = new();

	public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries => [.. _entries];

	public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

	public bool IsEnabled(LogLevel logLevel) => true;

	public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
		_entries.Enqueue((logLevel, formatter(state, exception), exception));
}
