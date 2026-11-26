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
[Collection(SavedSignInCollection.Name)] // 1.7.5: CloseSequence.RunAsync frees the device slot (static), as CloseSequenceDeviceSlotTests asserts
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

/// <summary>
/// 1.7.5 (KNOWN-ISSUES, the 1.6.1 review): a window opened while the copy's last one was still closing (Restart to update
/// reopening Nixie, a close and a quick start) found the first device slot held and signed in as the copy's second window, a
/// device of its own on the server, which a per-device allow list refuses. The close frees the slot as it begins; 1.8.1
/// (BH-175-m4): or, while a play's reports are owed, once they are sent.
/// </summary>
[Collection(SavedSignInCollection.Name)] // InstallDeviceId's data folder is static
public sealed class CloseSequenceDeviceSlotTests : IDisposable
{
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "nixie-175-close-slot", Guid.NewGuid().ToString("N"));
	private readonly FakeLauncher _launcher;
	private readonly MpvBackendHost _host;
	private string? _id;

	/// <summary>1.8.1 (BH-175-m4): the slot a window opened while the backend stops (and reports) took.</summary>
	private int? _openedWhileReporting;

	/// <summary>1.8.1 (the review of BH-175-m4): a window starting while the backend stops (and reports), as a start claims.</summary>
	private Task<int>? _startedWhileReporting;
	private bool _waitingWhileReporting;

	public CloseSequenceDeviceSlotTests()
	{
		_launcher = new FakeLauncher(() => new FakeBackendProcess((_, request) =>
		{
			if ((string?)request["cmd"] == "shutdown" && _id is { } id)
			{
				// a quick start of the copy while this one's backend stops its playback and reports it
				var (slot, claim) = Core.InstallDeviceId.ClaimWindow(id);
				claim.Dispose();
				_openedWhileReporting = slot;
				if (_startedWhileReporting is null && StartAWindowWhileReporting)
				{
					_startedWhileReporting = Task.Run(() =>
					{
						var (started, held) = Core.InstallDeviceId.ClaimWindow(id, TimeSpan.FromSeconds(20));
						held.Dispose();
						return started;
					});
					Thread.Sleep(500); // the backend's reports take a moment
					_waitingWhileReporting = !_startedWhileReporting.IsCompleted;
				}
			}

			return new JsonObject();
		}));
		_host = new(_launcher, new FakeSurface(), new ListLogger<MpvBackendHost>());
	}

	private FakeBackendProcess Backend => _launcher.Started[^1];

	private bool StartAWindowWhileReporting { get; set; }

	public void Dispose()
	{
		_host.Dispose();
		Core.InstallDeviceId.UseDataFolder(null);
		if (Directory.Exists(_folder))
		{
			Directory.Delete(_folder, true);
		}
	}

	private async Task<(Mock<IJellyfinClient> Client, MpvPlaybackService Playback)> Running()
	{
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.Stop()).Returns(Task.CompletedTask);
		Core.InstallDeviceId.UseDataFolder(_folder);
		_id = Core.InstallDeviceId.Current; // this window: the copy's first
		var (busy, second) = Core.InstallDeviceId.ClaimWindow(_id);
		second.Dispose();
		Assert.Equal(1, busy); // a window opened while it runs is the second one, a device of its own
		await _host.StartAsync();
		return (client, new MpvPlaybackService(_host, client.Object, NullLogger<MpvPlaybackService>.Instance));
	}

	[Fact]
	public async Task AWindowOpenedWhileTheLastOneCloses_IsTheSameDevice()
	{
		var (client, playback) = await Running();

		await CloseSequence.RunAsync(client.Object, playback, NullLogger.Instance).WaitAsync(TimeSpan.FromSeconds(30));

		// nothing played: opened while the backend stopped, the first slot (1.7.5: Restart to update, a quick start)
		Assert.Equal(0, _openedWhileReporting);
		// still closing (its last reports), and a window opened now: the first slot, this copy's own device
		var (slot, claim) = Core.InstallDeviceId.ClaimWindow(_id!);
		using (claim)
		{
			Assert.Equal(0, slot);
		}

		Assert.Equal(_id, Core.InstallDeviceId.Current); // the closing window's last reports keep its id
	}

	/// <summary>
	/// 1.8.1 (BH-175-m4): closed during a film and opened again at once with a saved password: the new window took the slot
	/// the close freed as it began, signed in by name as the same device, and the server ended that device's older session,
	/// so the closing window's Stopped report was refused and the film's place lost (its recovery lives in that process).
	/// With a play's reports owed, the slot is freed after them: a window opened meanwhile is the copy's second device, whose
	/// sign-in ends nothing of this one's; once they are sent, the first slot is free again.
	/// </summary>
	[Fact]
	public async Task AWindowOpenedWhileAFilmsStopIsReported_IsAnotherDevice_AndTheSlotIsFreeAfterTheReports()
	{
		var (client, playback) = await Running();
		playback.Begin("a film playing");
		Assert.True(playback.HasPlayback);

		await CloseSequence.RunAsync(client.Object, playback, NullLogger.Instance).WaitAsync(TimeSpan.FromSeconds(30));

		Assert.Equal(1, _openedWhileReporting); // while the backend stopped the film and reported it: not this window's device
		var (slot, claim) = Core.InstallDeviceId.ClaimWindow(_id!);
		using (claim)
		{
			Assert.Equal(0, slot); // the reports sent: the copy's own device again
		}

		Assert.Equal(_id, Core.InstallDeviceId.Current); // its last reports went with its own id
	}

	/// <summary>
	/// 1.8.1 (the review of BH-175-m4): the slot held for the reports made a window started meanwhile the copy's second device
	/// for its whole run, which a per-device allow list refuses (what 1.7.5 had fixed). A window starting while the copy's last
	/// one sends its reports waits for its slot, a few seconds at most, and is the copy's own device once they are sent.
	/// </summary>
	[Fact]
	public async Task AWindowStartedWhileAFilmsStopIsReported_WaitsForTheSlot_AndIsTheSameDevice()
	{
		var (client, playback) = await Running();
		playback.Begin("a film playing");
		StartAWindowWhileReporting = true;

		await CloseSequence.RunAsync(client.Object, playback, NullLogger.Instance).WaitAsync(TimeSpan.FromSeconds(30));

		Assert.Equal(1, _openedWhileReporting); // the slot is still held for the reports
		Assert.True(_waitingWhileReporting); // the starting window waits for it
		Assert.Equal(0, await _startedWhileReporting!.WaitAsync(TimeSpan.FromSeconds(10))); // then takes it: the same device
	}

	/// <summary>
	/// 1.8.1 (the review of BH-175-m4, its probe): Back from a film, then the window closed at once. The page's stop is sent and
	/// the backend goes idle before its Stopped is reported (the stop deferred): no playback left to see, but a report still
	/// owed. The slot was freed as the close began, the original bug in that order: it is held until the report is done.
	/// </summary>
	[Fact]
	public async Task BackThenCloseAtOnce_TheStopStillOwed_TheSlotIsHeldUntilItIsReported()
	{
		var (client, playback) = await Running();
		var generation = playback.Begin("a film playing");
		await Backend.Event("state", new JsonObject { ["state"] = "playing", ["itemId"] = Guid.NewGuid().ToString("N"), ["mediaSourceId"] = "ms-1", ["playSessionId"] = "ps-1" });
		await BridgeClientTests.WaitUntil(() => _host.LastState is { IsActive: true });

		await playback.StopAsync(generation); // Back: the page's stop
		await Backend.Event("state", new JsonObject { ["state"] = "idle" }); // idle before its report
		await BridgeClientTests.WaitUntil(() => _host.LastState is { IsActive: false });
		Assert.False(playback.HasPlayback);
		Assert.True(_host.OwesStopReport);

		await CloseSequence.RunAsync(client.Object, playback, NullLogger.Instance).WaitAsync(TimeSpan.FromSeconds(30));

		Assert.Equal(1, _openedWhileReporting); // the copy's second device while the backend stopped and reported
	}

	/// <summary>
	/// 1.8.1 (the review of BH-175-m4): the backend died during a film, and FluentFin is sending its Stopped (docs/BRIDGE.md §6)
	/// when the window closes: the slot is held while that report is on its way, and free once it is done.
	/// </summary>
	[Fact]
	public async Task ACrashedBackendsStop_StillOnItsWay_HoldsTheSlotUntilItIsSent()
	{
		var item = Guid.NewGuid();
		var (client, playback) = await Running();
		client.SetupGet(x => x.BaseUrl).Returns("http://jf:8096");
		client.SetupGet(x => x.UserId).Returns(Guid.Parse("a11ce000000000000000000000000001"));
		var sending = new TaskCompletionSource();
		client.Setup(x => x.Stop(It.IsAny<Jellyfin.Sdk.Generated.Models.PlaybackStopInfo>())).Returns(sending.Task);
		await _host.ConnectAsync(new BridgeIdentity("http://jf:8096", "srv", "Home", "a11ce000000000000000000000000001", "alice", "token-alice-123", "dev", "PC", "FluentFin", "1.0"));
		await Backend.Event("state", new JsonObject { ["state"] = "playing", ["itemId"] = item.ToString("N"), ["mediaSourceId"] = item.ToString("N"), ["playSessionId"] = "ps-1", ["positionTicks"] = 100 });
		await BridgeClientTests.WaitUntil(() => _host.LastState is { IsActive: true });
		Backend.Exit(3); // the backend died
		await BridgeClientTests.WaitUntil(() => playback.ReportsPending);
		Assert.False(_host.OwesStopReport);

		var closing = CloseSequence.RunAsync(client.Object, playback, NullLogger.Instance, interruptedReportTimeout: TimeSpan.FromSeconds(10));
		await Task.Delay(300);
		var (during, held) = Core.InstallDeviceId.ClaimWindow(_id!);
		held.Dispose();
		sending.SetResult(); // the Stopped reaches the server
		await closing.WaitAsync(TimeSpan.FromSeconds(30));

		Assert.Equal(1, during); // a window opened while it was sent: the copy's second device
		var (after, claim) = Core.InstallDeviceId.ClaimWindow(_id!);
		using (claim)
		{
			Assert.Equal(0, after);
		}
	}
}

/// <summary>
/// 1.7.5 (the review): <see cref="CloseSequence.RunAsync"/> frees this process's device slot (InstallDeviceId.ReleaseWindow,
/// static), and so does a change of InstallDeviceId's data folder: a test class doing either while another asserts a slot
/// (CloseSequenceDeviceSlotTests) made that one fail now and then. Every such class runs in SavedSignInCollection, one at a time.
/// </summary>
public sealed class DeviceSlotCollectionTests
{
	[Fact]
	public void EveryTestThatFreesTheDeviceSlot_RunsInTheSavedSignInCollection()
	{
		var folder = Path.Combine(FlyleafEngineTests.RepoRoot, "FluentFin.Tests");
		var classes = new System.Text.RegularExpressions.Regex(@"^((?:\[[^\n]*\][^\n]*\n)*)(?:public|internal) (?:sealed )?(?:partial )?class (\w+)", System.Text.RegularExpressions.RegexOptions.Multiline);
		var calls = new System.Text.RegularExpressions.Regex(@"CloseSequence\.RunAsync\(|InstallDeviceId\.(?:UseDataFolder|ReleaseWindow)\(");
		var found = new List<string>();
		var outside = new List<string>();
		foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
			.Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
		{
			var code = File.ReadAllText(file).Replace("\r\n", "\n");
			var declared = classes.Matches(code).ToList();
			foreach (System.Text.RegularExpressions.Match call in calls.Matches(code))
			{
				if (declared.LastOrDefault(c => c.Index < call.Index) is not { } owner || Path.GetFileName(file) == "CloseSequenceTests.cs" && owner.Groups[2].Value == nameof(DeviceSlotCollectionTests))
				{
					continue;
				}

				found.Add(owner.Groups[2].Value);
				if (!owner.Groups[1].Value.Contains("[Collection(SavedSignInCollection.Name)]", StringComparison.Ordinal))
				{
					outside.Add($"{Path.GetFileName(file)}: {owner.Groups[2].Value}");
				}
			}
		}

		Assert.Contains("CloseSequenceTests", found);
		Assert.Contains("BackendExitTests", found);
		Assert.Contains("InterruptedPlaybackTests", found);
		Assert.Empty(outside.Distinct());
	}
}
