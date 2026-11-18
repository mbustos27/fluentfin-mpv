using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Services;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Microsoft.Extensions.Logging;
using Moq;
using Serilog.Events;
using Serilog.Parsing;

namespace FluentFin.Tests.Admin;

/// <summary>
/// Nixie 1.2.0 (admin, review fix): the segment and server-option calls sent the token as ?ApiKey= in the URL, and a failed
/// one logged that URL. They send it in the Authorization header now, a failure is logged redacted, and the log file's
/// formatter masks any token that still reaches a line.
/// </summary>
[Collection(FluentFin.Tests.Mpv.AloneCollection.Name)]
public sealed class TokenLogTests : IAsyncLifetime
{
	private readonly AdminFakeServer _server = new();
	private readonly CapturingLogger _log = new();
	private JellyfinClient _client = null!;

	public async Task InitializeAsync()
	{
		_server.Start();
		var settings = new Mock<ISettings>();
		settings.SetupGet(x => x.MediaPlayer).Returns(MediaPlayerType.Mpv);
		_client = new JellyfinClient(_log, Mock.Of<IObserver<IOutboundWebSocketMessage>>(), Mock.Of<IJumpListService>(),
			Mock.Of<IDeviceProfileFactory>(), settings.Object, TestHomeCache.Unused());
		await _client.Initialize(_server.BaseUrl, new AuthenticationResult
		{
			AccessToken = AdminFakeServer.FakeToken,
			ServerId = "fake-admin-server",
			User = new UserDto { Id = AdminFakeServer.AdminId, Name = "admin", Policy = new UserPolicy { IsAdministrator = true } },
		});
		_log.Lines.Clear();
	}

	public Task DisposeAsync()
	{
		_server.Dispose();
		return Task.CompletedTask;
	}

	[Fact]
	public async Task AFailedSegmentDelete_LogsNoToken_AndTheTokenWasInTheHeaderNotTheUrl()
	{
		Assert.False(await _client.DeleteMediaSegment(Guid.NewGuid()));

		Assert.NotEmpty(_log.Lines);
		Assert.DoesNotContain(_log.Lines, x => x.Contains(AdminFakeServer.FakeToken, StringComparison.Ordinal));
		var delete = Assert.Single(_server.Changes);
		Assert.True(delete.SignedIn);
		Assert.DoesNotContain(AdminFakeServer.FakeToken, delete.Url, StringComparison.Ordinal);
	}

	[Fact]
	public async Task TheServerOptionsCalls_SendTheTokenInTheHeader()
	{
		Assert.Null(await _client.GetTranscodeOptions()); // 404 from the fake server: logged
		await _client.CreateMediaSegment(new MediaSegmentDto { Id = Guid.NewGuid(), ItemId = Guid.NewGuid(), Type = MediaSegmentDto_Type.Intro });

		Assert.All(_server.Requests.Where(x => x.Path.Contains("Configuration", StringComparison.OrdinalIgnoreCase) || x.Path.Contains("MediaSegmentsApi", StringComparison.OrdinalIgnoreCase)), request =>
		{
			Assert.True(request.SignedIn, request.Url);
			Assert.DoesNotContain(AdminFakeServer.FakeToken, request.Url, StringComparison.Ordinal);
		});
		Assert.DoesNotContain(_log.Lines, x => x.Contains(AdminFakeServer.FakeToken, StringComparison.Ordinal));
	}

	[Fact]
	public void TheLogFile_MasksTokens_InMessagesAndExceptions()
	{
		var token = "fake-token-0123456789abcdef";
		var failure = new InvalidOperationException($"Call failed: DELETE http://127.0.0.1:8096/MediaSegmentsApi/1?ApiKey={token} (Authorization: MediaBrowser Token=\"{token}\")");
		var logEvent = new LogEvent(DateTimeOffset.Now, LogEventLevel.Error, failure, new MessageTemplateParser().Parse("{Url} failed"),
			[new LogEventProperty("Url", new ScalarValue($"http://127.0.0.1:8096/Videos/1/stream?api_key={token}&static=true"))]);
		using var line = new StringWriter();

		new RedactingLogFormatter().Format(logEvent, line);

		Assert.DoesNotContain(token, line.ToString(), StringComparison.Ordinal);
		Assert.Contains("MediaSegmentsApi", line.ToString(), StringComparison.Ordinal);
		Assert.Contains("static=true", line.ToString(), StringComparison.Ordinal);
	}

	private sealed class CapturingLogger : ILogger<JellyfinClient>
	{
		private readonly object _gate = new();

		public List<string> Lines { get; } = [];

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			lock (_gate)
			{
				Lines.Add(formatter(state, exception) + " | " + exception);
			}
		}
	}
}
