using System.Diagnostics;
using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FluentFin.Tests.Mpv;

/// <summary>
/// What closing FluentFin waits for once its window is hidden (App.AppWindow_Closing): every part bounded, so a hung server
/// never keeps a hidden FluentFin alive, whichever player was used.
/// </summary>
public sealed class CloseSequenceTests : IDisposable
{
	private readonly Mock<IJellyfinClient> _client = new();
	private readonly FakeLauncher _launcher;
	private readonly MpvBackendHost _host;
	private readonly MpvPlaybackService _service;

	public CloseSequenceTests()
	{
		_launcher = new FakeLauncher(() => new FakeBackendProcess((_, request) =>
		{
			if ((string?)request["cmd"] == "shutdown")
			{
				Thread.Sleep(ShutdownAnswerDelay);
			}
			return new JsonObject();
		}));
		_host = new MpvBackendHost(_launcher, new FakeSurface(), new ListLogger<MpvBackendHost>());
		_service = new MpvPlaybackService(_host, _client.Object, NullLogger<MpvPlaybackService>.Instance);
	}

	private TimeSpan ShutdownAnswerDelay { get; set; } = TimeSpan.Zero;

	public void Dispose() => _host.Dispose();

	[Fact]
	public async Task UpstreamsStopReport_OnAHungServer_HoldsTheCloseForFiveSecondsAtMost_AndTheBackendStillStops()
	{
		// VLC, Flyleaf or WMP played last and the server hangs: the HTTP client alone would wait far longer
		_client.Setup(x => x.Stop()).Returns(new TaskCompletionSource().Task);
		await _host.StartAsync();

		var watch = Stopwatch.StartNew();
		await CloseSequence.RunAsync(_client.Object, _service, NullLogger.Instance).WaitAsync(TimeSpan.FromSeconds(30));

		Assert.InRange(watch.Elapsed.TotalSeconds, 4.8, 6.5);
		Assert.Contains("shutdown", _launcher.Started[0].Commands);
		Assert.Equal(MpvBackendState.Stopped, _host.Status.State);
	}

	[Fact]
	public async Task UpstreamsStopReport_ThatThrows_DoesNotStopTheClose()
	{
		_client.Setup(x => x.Stop()).Throws(new InvalidOperationException("no session"));
		await _host.StartAsync();

		var error = await Record.ExceptionAsync(() => CloseSequence.RunAsync(_client.Object, _service, NullLogger.Instance).WaitAsync(TimeSpan.FromSeconds(15)));

		Assert.Null(error);
		Assert.Contains("shutdown", _launcher.Started[0].Commands);
	}

	[Fact]
	public async Task UpstreamsStopReport_AndTheBackendsShutdown_RunSideBySide()
	{
		// worst case about 10 + 2 s instead of 5 + 10 + 2 s, so run-dev.ps1's 16 s wait never cuts a close
		_client.Setup(x => x.Stop()).Returns(() => Task.Delay(TimeSpan.FromSeconds(3)));
		ShutdownAnswerDelay = TimeSpan.FromSeconds(3);
		await _host.StartAsync();

		var watch = Stopwatch.StartNew();
		await CloseSequence.RunAsync(_client.Object, _service, NullLogger.Instance).WaitAsync(TimeSpan.FromSeconds(30));

		Assert.InRange(watch.Elapsed.TotalSeconds, 2.8, 5);
		Assert.Equal(MpvBackendState.Stopped, _host.Status.State);
	}
}
