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
/// device of its own on the server, which a per-device allow list refuses. The close frees the slot as it begins.
/// </summary>
[Collection(SavedSignInCollection.Name)] // InstallDeviceId's data folder is static
public sealed class CloseSequenceDeviceSlotTests : IDisposable
{
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "nixie-175-close-slot", Guid.NewGuid().ToString("N"));
	private readonly MpvBackendHost _host = new(new FakeLauncher(() => new FakeBackendProcess((_, _) => new JsonObject())), new FakeSurface(), new ListLogger<MpvBackendHost>());

	public void Dispose()
	{
		_host.Dispose();
		Core.InstallDeviceId.UseDataFolder(null);
		if (Directory.Exists(_folder))
		{
			Directory.Delete(_folder, true);
		}
	}

	[Fact]
	public async Task AWindowOpenedWhileTheLastOneCloses_IsTheSameDevice()
	{
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.Stop()).Returns(Task.CompletedTask);
		Core.InstallDeviceId.UseDataFolder(_folder);
		var id = Core.InstallDeviceId.Current; // this window: the copy's first
		var (busy, second) = Core.InstallDeviceId.ClaimWindow(id);
		second.Dispose();
		Assert.Equal(1, busy); // a window opened while it runs is the second one, a device of its own

		await CloseSequence.RunAsync(client.Object, new MpvPlaybackService(_host, client.Object, NullLogger<MpvPlaybackService>.Instance), NullLogger.Instance)
			.WaitAsync(TimeSpan.FromSeconds(30));

		// still closing (its last reports), and a window opened now: the first slot, this copy's own device
		var (slot, claim) = Core.InstallDeviceId.ClaimWindow(id);
		using (claim)
		{
			Assert.Equal(0, slot);
		}

		Assert.Equal(id, Core.InstallDeviceId.Current); // the closing window's last reports keep its id
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
