using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using FluentFin.Core.Settings;
using FluentFin.Core.Updates;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.5, the updater stream: sooner checks (every 4 hours, on return), never two at once, the download's progress, resume,
/// checksum and cancel, the steps the notice and Settings say, the handoff to the installer and what the next start makes of
/// it, a repository rename (BH-140X-m3), and the release notes. Recorded answers and an in-memory server only: no network.
/// </summary>
public sealed class UpdaterTests : IDisposable
{
	private const string Repo = "https://github.com/mbustos27/fluentfin-mpv";
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "nixie-updater-tests-" + Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		if (Directory.Exists(_folder))
		{
			Directory.Delete(_folder, recursive: true);
		}
	}

	private static string Release(string tag = "v1.5.1", string repo = Repo, long size = 196_083_712, string? api = null, string? page = null)
	{
		var v = tag.TrimStart('v');
		var apiUrl = api ?? "https://api.github.com/repos/" + repo["https://github.com/".Length..] + "/releases/1";
		return $$"""
			{ "url": "{{apiUrl}}", "tag_name": "{{tag}}", "draft": false, "prerelease": false,
			  "html_url": "{{page ?? $"{repo}/releases/tag/{tag}"}}", "body": "## Nixie {{v}}\n- Faster",
			  "assets": [
			    { "name": "Nixie-{{v}}-win-x64.msi", "size": {{size}}, "browser_download_url": "{{repo}}/releases/download/{{tag}}/Nixie-{{v}}-win-x64.msi" },
			    { "name": "Nixie-{{v}}-win-x64.msi.sha256", "size": 90, "browser_download_url": "{{repo}}/releases/download/{{tag}}/Nixie-{{v}}-win-x64.msi.sha256" } ] }
			""";
	}

	/// <summary>An answer from <paramref name="answeredFrom"/> (GitHub's redirect for a renamed repository) or from where it was asked.</summary>
	private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> answer, Uri? answeredFrom = null) : HttpMessageHandler
	{
		private readonly object _lock = new();

		public List<HttpRequestMessage> Requests { get; } = [];

		public Func<Task>? Gate { get; set; }

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			lock (_lock)
			{
				Requests.Add(request);
			}

			if (Gate is { } gate)
			{
				await gate();
			}

			var response = answer(request);
			response.RequestMessage ??= answeredFrom is null ? request : new HttpRequestMessage(request.Method, answeredFrom);
			return response;
		}
	}

	private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

	private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

	private static (Mock<ISettings> settings, Func<string> state) FakeSettings(bool enabled = true, string state = "")
	{
		var current = state;
		var settings = new Mock<ISettings>();
		settings.SetupGet(s => s.CheckForUpdates).Returns(enabled);
		settings.SetupGet(s => s.UpdateState).Returns(() => current);
		settings.SetupSet(s => s.UpdateState = It.IsAny<string>()).Callback<string>(v => current = v);
		return (settings, () => current);
	}

	// ---------- 1. sooner checks

	/// <summary>A release published while Nixie is open is found within the new interval (the timer ticks every minute).</summary>
	[Fact]
	public async Task AReleasePublishedWhileNixieIsOpen_IsFoundWithinFourHours()
	{
		var (settings, _) = FakeSettings();
		var start = new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);
		var now = start;
		var latest = "v1.5.0";
		var handler = new Fake(_ => Ok(Release(latest)));
		var coordinator = new UpdateCoordinator(settings.Object, new UpdateChecker(new HttpClient(handler), "1.5.0"), () => now, "1.5.0");

		await coordinator.TickAsync();                    // the start check: up to date
		Assert.Null(coordinator.Notice);
		latest = "v1.5.1";                                // published ten minutes later
		now = start.AddMinutes(10);
		DateTimeOffset? found = null;
		for (var minute = 11; minute <= 6 * 60 && found is null; minute++)
		{
			now = start.AddMinutes(minute);
			await coordinator.TickAsync();
			if (coordinator.Notice is not null)
			{
				found = now;
			}
		}

		Assert.Equal(start + UpdateGate.Interval, found);  // found at the 4-hour check, not a day later
		Assert.Equal(2, handler.Requests.Count);
	}

	[Fact]
	public async Task OnReturn_AfterAnHourAway_Asks_ButNotSooner_NorTwiceInAnHour_NorWhileGitHubAsksToWait()
	{
		var (settings, state) = FakeSettings();
		var now = new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);
		var handler = new Fake(_ => Ok(Release()));
		var coordinator = new UpdateCoordinator(settings.Object, new UpdateChecker(new HttpClient(handler), "1.5.0"), () => now, "1.5.0");
		await coordinator.TickAsync();
		Assert.Single(handler.Requests);

		now = now.AddMinutes(90);
		Assert.Null(await coordinator.ReturnedAsync(TimeSpan.FromMinutes(59)));   // not away long enough
		Assert.Single(handler.Requests);

		var asked = await coordinator.ReturnedAsync(TimeSpan.FromMinutes(61));    // an hour and more away: asks
		Assert.Equal(UpdateCheckStatus.Available, asked!.Status);
		Assert.Equal(2, handler.Requests.Count);

		now = now.AddMinutes(30);
		Assert.Null(await coordinator.ReturnedAsync(TimeSpan.FromHours(2)));      // asked 30 minutes ago: not again within the hour
		Assert.Equal(2, handler.Requests.Count);

		settings.Object.UpdateState = (UpdateState.Parse(state()) with { RetryAfter = now.AddHours(3) }).ToJson();
		now = now.AddHours(2);
		Assert.Null(await coordinator.ReturnedAsync(TimeSpan.FromHours(2)));      // GitHub asked to wait
		Assert.Equal(2, handler.Requests.Count);

		var off = FakeSettings(enabled: false);
		var quiet = new UpdateCoordinator(off.settings.Object, new UpdateChecker(new HttpClient(handler), "1.5.0"), () => now, "1.5.0");
		Assert.Null(await quiet.ReturnedAsync(TimeSpan.FromDays(1)));             // the setting off: never
		Assert.Equal(2, handler.Requests.Count);
	}

	[Fact]
	public async Task NeverTwoChecksAtOnce_ATick_AReturn_AndCheckNow_ShareTheOneOnItsWay()
	{
		var (settings, _) = FakeSettings();
		var now = new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);
		var release = new TaskCompletionSource();
		var handler = new Fake(_ => Ok(Release())) { Gate = () => release.Task };
		var coordinator = new UpdateCoordinator(settings.Object, new UpdateChecker(new HttpClient(handler), "1.5.0"), () => now, "1.5.0");

		var tick = coordinator.TickAsync();
		await Task.Delay(50);
		Assert.True(coordinator.IsChecking);
		var checkNow = coordinator.CheckNowAsync();
		var returned = coordinator.ReturnedAsync(TimeSpan.FromHours(3));
		release.SetResult();
		await tick;
		var a = await checkNow;
		await returned;

		Assert.Single(handler.Requests);
		Assert.Equal(UpdateCheckStatus.Available, a.Status);
		Assert.False(coordinator.IsChecking);
	}

	[Fact]
	public async Task CheckNow_RightAfterAnAnswer_SaysItAgain_WithoutAsking_ThenAsksAgainLater()
	{
		var (settings, _) = FakeSettings(enabled: false);
		var now = new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);
		var handler = new Fake(_ => Ok(Release()));
		var coordinator = new UpdateCoordinator(settings.Object, new UpdateChecker(new HttpClient(handler), "1.5.0"), () => now, "1.5.0");

		await coordinator.CheckNowAsync();
		now = now.AddSeconds(5);
		var again = await coordinator.CheckNowAsync();     // a double click
		Assert.Single(handler.Requests);
		Assert.Equal(UpdateCheckStatus.Available, again.Status);

		now = now.AddSeconds(20);
		await coordinator.CheckNowAsync();
		Assert.Equal(2, handler.Requests.Count);
	}

	// ---------- 2. progress maths

	[Fact]
	public void TheMeter_GivesTheSpeedOverTheLastSeconds_AndTheTimeLeft()
	{
		var now = new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);
		var meter = new ProgressMeter(() => now);
		const long total = 100L * 1024 * 1024;

		var first = meter.Sample(0, total);
		Assert.Null(first.BytesPerSecond);                // no speed from one sample
		Assert.Null(first.Left);

		for (var i = 1; i <= 8; i++)                       // 2 MB every half second: 4 MB/s
		{
			now = now.AddMilliseconds(500);
			first = meter.Sample(i * 2L * 1024 * 1024, total);
		}

		Assert.Equal(4.0 * 1024 * 1024, first.BytesPerSecond!.Value, 1);
		Assert.Equal(TimeSpan.FromSeconds(21), first.Left);  // 84 MB at 4 MB/s
		Assert.Equal(0.16, first.Fraction!.Value, 3);

		// slower now: the window follows the last 5 seconds, not the whole download
		for (var i = 1; i <= 12; i++)
		{
			now = now.AddMilliseconds(500);
			first = meter.Sample(16L * 1024 * 1024 + i * 512L * 1024, total);
		}

		Assert.Equal(1.0 * 1024 * 1024, first.BytesPerSecond!.Value, 1);
		Assert.Equal("22 MB of 100 MB \u00b7 1.0 MB/s \u00b7 about 1 min left", UpdateText.Progress(first));   // 78 MB at 1 MB/s
	}

	[Theory]
	[InlineData(500L, "1 KB")]
	[InlineData(870_400L, "850 KB")]
	[InlineData(9_961_472L, "9.5 MB")]
	[InlineData(196_083_712L, "187 MB")]
	[InlineData(1_288_490_189L, "1.2 GB")]
	public void Sizes_ReadAsWindowsSaysThem(long bytes, string expected) => Assert.Equal(expected, UpdateText.Size(bytes));

	[Theory]
	[InlineData(3, "a few seconds left")]
	[InlineData(17, "about 20 s left")]
	[InlineData(55, "about 55 s left")]
	[InlineData(59, "about 1 min left")]
	[InlineData(150, "about 3 min left")]
	[InlineData(4200, "about 1 h 10 min left")]
	public void TimeLeft_IsRoundedAsAPersonWouldSayIt(int seconds, string expected) => Assert.Equal(expected, UpdateText.TimeLeft(TimeSpan.FromSeconds(seconds)));

	[Fact]
	public void Speeds_AndAges_ReadPlainly()
	{
		Assert.Equal("850 KB/s", UpdateText.Speed(850 * 1024));
		Assert.Equal("6.2 MB/s", UpdateText.Speed(6.2 * 1024 * 1024));
		Assert.Equal("187 MB", UpdateText.Progress(new DownloadProgress(196_083_712, null, null, null)));
		var now = new DateTimeOffset(2026, 11, 20, 14, 0, 0, TimeSpan.Zero);
		Assert.Equal("just now", UpdateText.Ago(now.AddSeconds(-20), now, TimeZoneInfo.Utc));
		Assert.Equal("just now", UpdateText.Ago(now.AddMinutes(5), now, TimeZoneInfo.Utc));   // a clock set back
		Assert.Equal("12 min ago", UpdateText.Ago(now.AddMinutes(-12), now, TimeZoneInfo.Utc));
		Assert.Equal("3 h ago", UpdateText.Ago(now.AddHours(-3), now, TimeZoneInfo.Utc));
		Assert.Equal("yesterday at 22:15", UpdateText.Ago(new DateTimeOffset(2026, 11, 19, 22, 15, 0, TimeSpan.Zero), now, TimeZoneInfo.Utc));
		Assert.Equal("Tue 17 Nov at 09:00", UpdateText.Ago(new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero), now, TimeZoneInfo.Utc));
	}

	// ---------- 3. the download: an in-memory GitHub with ranges

	/// <summary>Serves the installer (honouring Range unless told not to) and its .sha256; counts and keeps every request.</summary>
	private sealed class AssetServer : HttpMessageHandler
	{
		public required ReleaseInfo Release { get; init; }

		public required Func<int, byte[]> Installer { get; init; }

		public required string HashText { get; init; }

		public bool HonourRange { get; init; } = true;

		/// <summary>Called after each chunk sent (the test cancels or breaks the connection there).</summary>
		public Action<long>? AfterChunk { get; init; }

		public int Chunk { get; init; } = 64 * 1024;

		public List<(Uri Uri, RangeHeaderValue? Range, string? Agent)> Seen { get; } = [];

		public int InstallerRequests { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Seen.Add((request.RequestUri!, request.Headers.Range, request.Headers.UserAgent.ToString()));
			var url = request.RequestUri!.ToString();
			HttpResponseMessage response;
			if (url == Release.Sha256Url)
			{
				response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(HashText) };
			}
			else if (url == Release.MsiUrl)
			{
				var data = Installer(InstallerRequests++);
				var from = HonourRange && request.Headers.Range?.Ranges.FirstOrDefault()?.From is { } f ? f : 0;
				if (from >= data.Length && from > 0)
				{
					response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
				}
				else
				{
					response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
					{
						Content = new StreamContent(new ChunkStream(data, (int)from, Chunk, AfterChunk)),
					};
					response.Content.Headers.ContentLength = data.Length - from;
					if (from > 0)
					{
						response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, data.Length - 1, data.Length);
					}
				}
			}
			else
			{
				response = new HttpResponseMessage(HttpStatusCode.NotFound);
			}

			response.RequestMessage = request;
			return Task.FromResult(response);
		}
	}

	private sealed class ChunkStream(byte[] data, int from, int chunk, Action<long>? afterChunk) : Stream
	{
		private int _at = from;

		public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (_at >= data.Length)
			{
				return ValueTask.FromResult(0);
			}

			var n = Math.Min(Math.Min(chunk, buffer.Length), data.Length - _at);
			data.AsSpan(_at, n).CopyTo(buffer.Span);
			_at += n;
			afterChunk?.Invoke(_at);
			return ValueTask.FromResult(n);
		}

		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => data.Length;
		public override long Position { get => _at; set => throw new NotSupportedException(); }
		public override void Flush() { }
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}

	private static byte[] Bytes(int length, int seed = 7)
	{
		var data = new byte[length];
		new Random(seed).NextBytes(data);
		return data;
	}

	private ReleaseInfo TheRelease() => ReleaseInfo.Parse(Release())!;

	[Fact]
	public async Task Download_ReportsProgress_SendsAUserAgent_AndLeavesTheCheckedInstaller_InNixiesOwnFolder()
	{
		var release = TheRelease();
		var msi = Bytes(1_000_000);
		var server = new AssetServer { Release = release, Installer = _ => msi, HashText = $"{Sha(msi)} *{release.MsiName}\n" };
		var reports = new List<DownloadProgress>();
		var verifying = 0;

		var path = await new UpdateDownloader(new HttpClient(server), _folder, "1.5.0").DownloadAsync(release, new Collect(reports), () => verifying++);

		Assert.Equal(Path.Combine(_folder, "Nixie-1.5.1-win-x64.msi"), path);
		Assert.Equal(msi, await File.ReadAllBytesAsync(path));
		Assert.True(File.Exists(path + ".sha256"));
		Assert.False(File.Exists(path + ".part"));
		Assert.Equal(1, verifying);
		Assert.Equal(msi.Length, reports[^1].Done);
		Assert.Equal(msi.Length, reports[^1].Total);
		Assert.All(server.Seen, s => Assert.Equal("Nixie/1.5.0", s.Agent));
		Assert.Equal(path, new UpdateDownloader(new HttpClient(server), _folder).Downloaded(release));   // a later start finds it ready
	}

	private sealed class Collect(List<DownloadProgress> into) : IProgress<DownloadProgress>
	{
		public void Report(DownloadProgress value) => into.Add(value);
	}

	[Fact]
	public async Task Download_AChecksumMismatch_StartsOverOnce_FromTheFirstByte()
	{
		var release = TheRelease();
		var good = Bytes(300_000);
		var bad = Bytes(300_000, seed: 99);
		var server = new AssetServer { Release = release, Installer = n => n == 0 ? bad : good, HashText = $"{Sha(good)} *{release.MsiName}\n" };

		var path = await new UpdateDownloader(new HttpClient(server), _folder).DownloadAsync(release);

		Assert.Equal(good, await File.ReadAllBytesAsync(path));
		Assert.Equal(2, server.InstallerRequests);
		Assert.All(server.Seen.Where(s => s.Uri.ToString() == release.MsiUrl), s => Assert.Null(s.Range));   // both from the first byte
	}

	[Fact]
	public async Task Download_ThatNeverMatches_FailsWithAPlainReason_AndLeavesNothing()
	{
		var release = TheRelease();
		var server = new AssetServer { Release = release, Installer = _ => Bytes(200_000, seed: 3), HashText = $"{Sha(Bytes(10))} *{release.MsiName}\n" };

		var error = await Assert.ThrowsAsync<UpdateException>(() => new UpdateDownloader(new HttpClient(server), _folder).DownloadAsync(release));

		Assert.Equal("The download didn't match its checksum twice, so it was not used.", error.Message);
		Assert.Equal(2, server.InstallerRequests);
		Assert.DoesNotContain(Directory.GetFiles(_folder, "*.msi*"), f => !f.EndsWith(".sha256", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Download_Cancelled_KeepsThePart_AndTheNextOne_ResumesFromIt()
	{
		var release = TheRelease();
		var msi = Bytes(1_000_000);
		using var cancel = new CancellationTokenSource();
		var server = new AssetServer
		{
			Release = release, Installer = _ => msi, HashText = $"{Sha(msi)} *{release.MsiName}\n", Chunk = 100_000,
			AfterChunk = at => { if (at >= 400_000) { cancel.Cancel(); } },
		};
		var downloader = new UpdateDownloader(new HttpClient(server), _folder);

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadAsync(release, null, null, cancel.Token));
		var part = Path.Combine(_folder, "Nixie-1.5.1-win-x64.msi.part");
		Assert.True(File.Exists(part));
		var kept = new FileInfo(part).Length;
		Assert.InRange(kept, 300_000, 500_000);

		var reports = new List<DownloadProgress>();
		var resumer = new AssetServer { Release = release, Installer = _ => msi, HashText = $"{Sha(msi)} *{release.MsiName}\n" };
		var path = await new UpdateDownloader(new HttpClient(resumer), _folder).DownloadAsync(release, new Collect(reports), null);

		Assert.Equal(msi, await File.ReadAllBytesAsync(path));
		var asked = Assert.Single(resumer.Seen, s => s.Uri.ToString() == release.MsiUrl);
		Assert.Equal(kept, asked.Range!.Ranges.Single().From);     // HTTP Range from the part's length
		Assert.Equal(kept, reports[0].Done);                         // the progress starts where it left off
	}

	[Fact]
	public async Task Download_AServerThatIgnoresTheRange_StartsOver_AndAWholePart_IsJustChecked()
	{
		var release = TheRelease();
		var msi = Bytes(500_000);
		Directory.CreateDirectory(_folder);
		var part = Path.Combine(_folder, "Nixie-1.5.1-win-x64.msi.part");
		await File.WriteAllBytesAsync(part, msi[..123_456]);
		var ignoring = new AssetServer { Release = release, Installer = _ => msi, HashText = $"{Sha(msi)} *{release.MsiName}\n", HonourRange = false };

		var path = await new UpdateDownloader(new HttpClient(ignoring), _folder).DownloadAsync(release);
		Assert.Equal(msi, await File.ReadAllBytesAsync(path));        // 200 instead of 206: written from the first byte

		File.Delete(path);
		await File.WriteAllBytesAsync(part, msi);                      // the whole file already in the part: 416
		var whole = new AssetServer { Release = release, Installer = _ => msi, HashText = $"{Sha(msi)} *{release.MsiName}\n" };
		path = await new UpdateDownloader(new HttpClient(whole), _folder).DownloadAsync(release);
		Assert.Equal(msi, await File.ReadAllBytesAsync(path));
	}

	[Fact]
	public async Task Download_ABrokenConnection_SaysSo_AndKeepsThePart()
	{
		var release = TheRelease();
		var msi = Bytes(400_000);
		var server = new AssetServer
		{
			Release = release, Installer = _ => msi, HashText = $"{Sha(msi)} *{release.MsiName}\n", Chunk = 100_000,
			AfterChunk = at => { if (at >= 200_000) { throw new IOException("connection reset"); } },
		};

		var error = await Assert.ThrowsAsync<UpdateException>(() => new UpdateDownloader(new HttpClient(server), _folder).DownloadAsync(release));

		Assert.Equal("The download stopped before the end.", error.Message);
		Assert.True(File.Exists(Path.Combine(_folder, "Nixie-1.5.1-win-x64.msi.part")));
	}

	[Fact]
	public async Task Download_InAHarnessRun_FetchesTheFakeServersCopy_AndAcceptsOnlyThatOrigin()
	{
		var release = TheRelease();
		var msi = Bytes(1000);
		var origin = new Uri("http://127.0.0.1:5123/");
		var handler = new Fake(r => r.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
			? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{Sha(msi)} *{release.MsiName}\n") }
			: new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(msi) });

		var path = await new UpdateDownloader(new HttpClient(handler), _folder, harnessOrigin: origin).DownloadAsync(release);

		Assert.Equal(msi, await File.ReadAllBytesAsync(path));
		Assert.All(handler.Requests, r => Assert.StartsWith("http://127.0.0.1:5123/__harness/release/download/v1.5.1/", r.RequestUri!.ToString()));
	}

	// ---------- 3b. GitHub's digest of the asset: the second witness (second review, 2026-11-20)

	private static ReleaseInfo WithDigest(string? digest)
	{
		var json = Release().Replace("\"size\": 196083712,", digest is null ? "\"size\": 196083712," : $"\"size\": 196083712, \"digest\": \"{digest}\",");
		return ReleaseInfo.Parse(json)!;
	}

	[Fact]
	public void TheDigest_IsReadFromTheInstallersAsset_OnlyAsSha256()
	{
		var hex = new string('A', 64);
		Assert.Equal(new string('a', 64), WithDigest("sha256:" + hex).MsiDigest);
		Assert.Null(WithDigest(null).MsiDigest);
		Assert.Null(WithDigest("sha1:" + new string('a', 40)).MsiDigest);
		Assert.Null(WithDigest("sha256:xyz").MsiDigest);
		Assert.Equal(new string('a', 64), WithDigest("sha256:" + hex).Validated().MsiDigest);
	}

	[Fact]
	public async Task Download_WithGitHubsDigest_MatchesBoth_AndSaysSo()
	{
		var msi = Bytes(200_000);
		var release = WithDigest("sha256:" + Sha(msi));
		var server = new AssetServer { Release = release, Installer = _ => msi, HashText = $"{Sha(msi)} *{release.MsiName}\n" };
		var downloader = new UpdateDownloader(new HttpClient(server), _folder);
		var notes = new List<string>();
		downloader.Noted += notes.Add;

		var path = await downloader.DownloadAsync(release);

		Assert.Equal(msi, await File.ReadAllBytesAsync(path));
		Assert.Contains(notes, n => n.Contains("GitHub's digest and the .sha256 agree", StringComparison.Ordinal));
		Assert.Equal(path, downloader.Downloaded(release));
	}

	[Fact]
	public async Task Download_WhenTheHashFileAndGitHubsDigestDisagree_TakesNothing()
	{
		var msi = Bytes(200_000);
		var swapped = Bytes(200_000, seed: 42);              // an installer and hash file replaced together
		var release = WithDigest("sha256:" + Sha(msi));
		var server = new AssetServer { Release = release, Installer = _ => swapped, HashText = $"{Sha(swapped)} *{release.MsiName}\n" };

		var error = await Assert.ThrowsAsync<UpdateException>(() => new UpdateDownloader(new HttpClient(server), _folder).DownloadAsync(release));

		Assert.Equal("The checksum file and GitHub's record of the installer disagree, so it was not used.", error.Message);
		Assert.Equal(0, server.InstallerRequests);              // the installer itself was never fetched
		Assert.DoesNotContain(Directory.GetFiles(_folder), f => f.EndsWith(".msi", StringComparison.Ordinal) || f.EndsWith(".part", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Download_WithoutADigest_SaysSoInTheLog_AndTheSha256Decides()
	{
		var msi = Bytes(200_000);
		var release = WithDigest(null);
		var server = new AssetServer { Release = release, Installer = _ => msi, HashText = $"{Sha(msi)} *{release.MsiName}\n" };
		var downloader = new UpdateDownloader(new HttpClient(server), _folder);
		var notes = new List<string>();
		downloader.Noted += notes.Add;

		var path = await downloader.DownloadAsync(release);

		Assert.Equal(msi, await File.ReadAllBytesAsync(path));
		Assert.Contains(notes, n => n.Contains("gave no digest", StringComparison.Ordinal));

		// a stored digest that no longer agrees with the kept hash file: the installer is not taken as ready
		Assert.Null(downloader.Downloaded(release with { MsiDigest = new string('0', 64) }));
	}

	// ---------- 4. the center: the steps, installed and portable

	private UpdateCenter Center(bool installed, AssetServer? assets, string latest = "v1.5.1", bool metered = false, string current = "1.5.0")
	{
		var (settings, _) = FakeSettings();
		var api = new Fake(_ => Ok(Release(latest)));
		var coordinator = new UpdateCoordinator(settings.Object, new UpdateChecker(new HttpClient(api), current), () => DateTimeOffset.UtcNow, current);
		var downloader = new UpdateDownloader(new HttpClient((HttpMessageHandler?)assets ?? new Fake(_ => new HttpResponseMessage(HttpStatusCode.NotFound))), _folder);
		return new UpdateCenter(coordinator, downloader, current, installed, () => metered);
	}

	private static async Task<UpdateStatus> Until(UpdateCenter center, Func<UpdateStatus, bool> done)
	{
		for (var i = 0; i < 400 && !done(center.Status); i++)
		{
			await Task.Delay(10);
		}

		return center.Status;
	}

	[Fact]
	public async Task Installed_AFoundUpdate_DownloadsByItself_ThenIsReadyToInstall()
	{
		var release = TheRelease();
		var msi = Bytes(700_000);
		var center = Center(installed: true, new AssetServer { Release = release, Installer = _ => msi, HashText = $"{Sha(msi)} *{release.MsiName}\n" });
		var phases = new List<UpdatePhase>();
		var phasesLock = new object();
		center.Changed += s => { lock (phasesLock) { phases.Add(s.Phase); } };

		await center.CheckNowAsync();
		var status = await Until(center, s => s.Phase == UpdatePhase.Ready);

		Assert.Equal(UpdatePhase.Ready, status.Phase);
		lock (phasesLock)
		{
			Assert.Equal([UpdatePhase.Checking, UpdatePhase.Available, UpdatePhase.Downloading, UpdatePhase.Verifying, UpdatePhase.Ready], phases.Distinct());
		}

		var view = UpdateView.ForNotice(status)!;
		Assert.Equal("Nixie 1.5.1 is ready to install", view.Title);
		Assert.Equal(UpdateAction.RestartToUpdate, view.Primary);
		Assert.Equal([UpdateAction.ReleaseNotes, UpdateAction.Dismiss], view.Others);

		var msiPath = center.BeginInstall();
		Assert.Equal(Path.Combine(_folder, "Nixie-1.5.1-win-x64.msi"), msiPath);
		Assert.Equal(UpdatePhase.Installing, center.Status.Phase);
		Assert.Equal("Installing Nixie 1.5.1\u2026", UpdateView.ForNotice(center.Status)!.Title);
	}

	[Fact]
	public async Task Portable_AFoundUpdate_DownloadsNothing_AndOffersTheReleasePage()
	{
		var release = TheRelease();
		var assets = new AssetServer { Release = release, Installer = _ => Bytes(10), HashText = "" };
		var center = Center(installed: false, assets);

		await center.CheckNowAsync();
		await Task.Delay(100);

		Assert.Equal(UpdatePhase.Available, center.Status.Phase);
		Assert.Empty(assets.Seen);
		var view = UpdateView.ForSettings(center.Status, DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
		Assert.Equal("Nixie 1.5.1 is available", view.Title);
		Assert.Equal("This copy of Nixie is portable, so it updates from the release page.", view.Detail);
		Assert.Equal(UpdateAction.OpenReleasePage, view.Primary);
		Assert.Null(center.BeginInstall());
	}

	[Fact]
	public async Task Metered_AFoundUpdate_WaitsForTheDownloadClick_WithItsSize()
	{
		var release = TheRelease();
		var msi = Bytes(5000);
		var assets = new AssetServer { Release = release, Installer = _ => msi, HashText = $"{Sha(msi)} *{release.MsiName}\n" };
		var center = Center(installed: true, assets, metered: true);

		await center.CheckNowAsync();
		await Task.Delay(100);

		Assert.Equal(UpdatePhase.Available, center.Status.Phase);
		Assert.Empty(assets.Seen);
		var view = UpdateView.ForSettings(center.Status, DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
		Assert.Equal("Nixie 1.5.1 is available \u00b7 187 MB", view.Title);
		Assert.Equal(UpdateAction.Download, view.Primary);

		await center.StartDownload();
		Assert.Equal(UpdatePhase.Ready, center.Status.Phase);
	}

	[Fact]
	public async Task Cancel_StopsTheDownload_ItDoesNotStartAgainByItself_AndDownloadResumesIt()
	{
		var release = TheRelease();
		var msi = Bytes(2_000_000);
		var gate = new ManualResetEventSlim(false);
		var assets = new AssetServer
		{
			Release = release, Installer = _ => msi, HashText = $"{Sha(msi)} *{release.MsiName}\n", Chunk = 100_000,
			AfterChunk = at => { if (at >= 500_000 && at < 600_000) { gate.Wait(TimeSpan.FromSeconds(5)); } },
		};
		var center = Center(installed: true, assets);

		await center.CheckNowAsync();
		await Until(center, s => s.Phase == UpdatePhase.Downloading && s.Progress?.Done > 0);
		center.Cancel();
		gate.Set();
		var status = await Until(center, s => s.Phase == UpdatePhase.Available);
		Assert.Equal(UpdatePhase.Available, status.Phase);
		Assert.Equal(UpdateAction.Download, UpdateView.ForNotice(status)!.Primary);

		var before = assets.InstallerRequests;
		await center.Coordinator.CheckNowAsync();          // the same release found again: not downloaded again by itself
		await Task.Delay(100);
		Assert.Equal(before, assets.InstallerRequests);
		Assert.Equal(UpdatePhase.Available, center.Status.Phase);

		await center.StartDownload();                       // the user's Download: resumes
		Assert.Equal(UpdatePhase.Ready, center.Status.Phase);
		Assert.NotNull(assets.Seen.Last(s => s.Uri.ToString() == release.MsiUrl).Range);
	}

	/// <summary>
	/// Found by the harness (2026-11-20): Check now that GitHub never answers said "You're up to date" (the failed check's own
	/// Checked event put the step back before Check now could say why).
	/// </summary>
	[Fact]
	public async Task CheckNow_ThatFails_SaysWhy_WithRetry_NotUpToDate()
	{
		var (settings, _) = FakeSettings();
		var now = DateTimeOffset.UtcNow;
		var offline = false;
		var api = new Fake(_ => offline ? throw new HttpRequestException("offline") : Ok(Release("v1.5.0")));
		var coordinator = new UpdateCoordinator(settings.Object, new UpdateChecker(new HttpClient(api), "1.5.0"), () => now, "1.5.0");
		var center = new UpdateCenter(coordinator, new UpdateDownloader(new HttpClient(), _folder), "1.5.0", true);
		await center.CheckNowAsync();
		Assert.Equal(UpdatePhase.UpToDate, center.Status.Phase);

		offline = true;
		now = now.AddMinutes(1);
		await center.CheckNowAsync();

		Assert.Equal(UpdatePhase.CheckFailed, center.Status.Phase);
		var view = UpdateView.ForSettings(center.Status, now, TimeZoneInfo.Utc);
		Assert.Equal("Couldn't check: no connection to GitHub", view.Title);
		Assert.Equal(UpdateAction.RetryCheck, view.Primary);
	}

	[Fact]
	public async Task ACheckThatFails_WhileAnUpdateIsReady_LeavesRestartToUpdateOnScreen()
	{
		var release = TheRelease();
		var msi = Bytes(4000);
		var offline = false;
		var (settings, _) = FakeSettings();
		var now = DateTimeOffset.UtcNow;
		var api = new Fake(_ => offline ? throw new HttpRequestException("offline") : Ok(Release()));
		var coordinator = new UpdateCoordinator(settings.Object, new UpdateChecker(new HttpClient(api), "1.5.0"), () => now, "1.5.0");
		var assets = new AssetServer { Release = release, Installer = _ => msi, HashText = $"{Sha(msi)} *{release.MsiName}\n" };
		var center = new UpdateCenter(coordinator, new UpdateDownloader(new HttpClient(assets), _folder), "1.5.0", true);
		await center.CheckNowAsync();
		await Until(center, s => s.Phase == UpdatePhase.Ready);

		offline = true;
		now = now.AddMinutes(1);
		var phases = new List<UpdatePhase>();
		center.Changed += s => phases.Add(s.Phase);
		var result = await center.CheckNowAsync();

		Assert.Equal(UpdateCheckStatus.Failed, result.Status);
		Assert.Equal(UpdatePhase.Ready, center.Status.Phase);
		Assert.Empty(phases);                                  // no "Checking…" over it, no failure in its place
	}

	[Fact]
	public async Task AFailedDownload_SaysWhy_WithRetry_AndTheReleasePage()
	{
		var release = TheRelease();
		var assets = new AssetServer { Release = release, Installer = _ => Bytes(1000), HashText = "not a hash" };
		var center = Center(installed: true, assets);

		await center.CheckNowAsync();
		var status = await Until(center, s => s.Phase == UpdatePhase.DownloadFailed);

		var view = UpdateView.ForNotice(status)!;
		Assert.Equal("Couldn't download Nixie 1.5.1", view.Title);
		Assert.Equal("The checksum file is not a SHA-256 for this installer.", view.Detail);
		Assert.Equal(UpdateAction.Retry, view.Primary);
		Assert.Equal([UpdateAction.OpenReleasePage, UpdateAction.Dismiss], view.Others);
		Assert.True(view.IsError);
	}

	[Fact]
	public void Settings_SaysEachStep()
	{
		var now = new DateTimeOffset(2026, 11, 20, 14, 0, 0, TimeSpan.Zero);
		var release = TheRelease();
		UpdateView S(UpdateStatus s) => UpdateView.ForSettings(s, now, TimeZoneInfo.Utc);

		Assert.Equal("Not checked yet", S(new UpdateStatus(UpdatePhase.Idle)).Title);
		Assert.Equal("Checking for updates\u2026", S(new UpdateStatus(UpdatePhase.Checking)).Title);
		Assert.True(S(new UpdateStatus(UpdatePhase.Checking)).Busy);
		Assert.Equal("You're up to date \u00b7 checked just now", S(new UpdateStatus(UpdatePhase.UpToDate, CheckedAt: now)).Title);
		Assert.Equal("You're up to date \u00b7 checked 3 h ago", S(new UpdateStatus(UpdatePhase.Idle, CheckedAt: now.AddHours(-3))).Title);
		var failed = S(new UpdateStatus(UpdatePhase.CheckFailed, Error: "Couldn't check: no connection to GitHub"));
		Assert.Equal("Couldn't check: no connection to GitHub", failed.Title);
		Assert.Equal(UpdateAction.RetryCheck, failed.Primary);
		Assert.Equal("Retry", UpdateView.Label(failed.Primary));

		var downloading = S(new UpdateStatus(UpdatePhase.Downloading, release, new DownloadProgress(88_080_384, 196_083_712, 6.2 * 1024 * 1024, TimeSpan.FromSeconds(17)), CanSelfUpdate: true));
		Assert.Equal("Downloading Nixie 1.5.1", downloading.Title);       // the title (Narrator's) never carries the ticking numbers
		Assert.Equal("84 MB of 187 MB \u00b7 6.2 MB/s \u00b7 about 20 s left", downloading.Detail);
		Assert.Equal(0.449, downloading.Progress!.Value, 3);
		Assert.Equal([UpdateAction.Cancel], downloading.Others);
		Assert.Equal("Checking the download\u2026", S(new UpdateStatus(UpdatePhase.Verifying, release, CanSelfUpdate: true)).Title);
		Assert.Equal("Nixie 1.5.1 is ready to install", S(new UpdateStatus(UpdatePhase.Ready, release, CanSelfUpdate: true)).Title);
		Assert.Equal([UpdateAction.ReleaseNotes], S(new UpdateStatus(UpdatePhase.Ready, release, CanSelfUpdate: true)).Others);   // no Dismiss in Settings
		var installFailed = S(new UpdateStatus(UpdatePhase.InstallFailed, release, Error: "the installation was cancelled", CanSelfUpdate: true));
		Assert.Equal("Nixie 1.5.1 didn't install", installFailed.Title);
		Assert.Equal("The installation was cancelled.", installFailed.Detail);
		Assert.Equal(UpdateAction.Retry, installFailed.Primary);
	}

	[Fact]
	public void TheNotice_StaysOutOfTheWay_OfChecks_AndSaysUpdatedTo_AfterAnUpdate()
	{
		Assert.Null(UpdateView.ForNotice(new UpdateStatus(UpdatePhase.Checking)));
		Assert.Null(UpdateView.ForNotice(new UpdateStatus(UpdatePhase.UpToDate)));
		Assert.Null(UpdateView.ForNotice(new UpdateStatus(UpdatePhase.CheckFailed, Error: "x")));
		var pending = new PendingInstall("1.5.1", "1.5.0", DateTimeOffset.UtcNow, $"{Repo}/releases/tag/v1.5.1", "notes", true);
		var updated = UpdateView.ForNotice(new UpdateStatus(UpdatePhase.UpToDate, JustUpdated: pending))!;
		Assert.Equal("Updated to Nixie 1.5.1", updated.Title);
		Assert.Equal(UpdateAction.WhatsNew, updated.Primary);
		Assert.Equal("What's new", UpdateView.Label(updated.Primary));

		// Settings says it too (the shell's notice steps aside on Settings), with when it last checked
		var now = DateTimeOffset.UtcNow;
		var settings = UpdateView.ForSettings(new UpdateStatus(UpdatePhase.UpToDate, CheckedAt: now, JustUpdated: pending), now, TimeZoneInfo.Utc);
		Assert.Equal("Updated to Nixie 1.5.1", settings.Title);
		Assert.Equal("You're up to date · checked just now", settings.Detail);
		Assert.Equal(UpdateAction.WhatsNew, settings.Primary);
		Assert.Equal("Not checked yet", UpdateView.ForSettings(new UpdateStatus(UpdatePhase.Idle), now, TimeZoneInfo.Utc).Title);
	}

	// ---------- 5. the handoff and the next start

	[Fact]
	public void TheHelper_IsStartedWithTheInstaller_TheProcessToWaitFor_AndTheAppToReopen()
	{
		var started = new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
		var info = UpdateHandoff.HelperStart(@"C:\d\Updates\Nixie-updater.exe", @"C:\d\Updates\Nixie-1.5.1-win-x64.msi", @"C:\d\Updates", 4242, started,
			@"C:\P\Nixie\app\Nixie.exe", ["--data-dir", @"D:\my data"], reopen: true, quiet: false);

		Assert.False(info.UseShellExecute);
		Assert.Equal(
			[UpdateHandoff.Switch, "--msi", @"C:\d\Updates\Nixie-1.5.1-win-x64.msi", "--log", @"C:\d\Updates\install.log", "--result", @"C:\d\Updates\install-result.txt",
			 "--wait-pid", "4242", "--wait-started", started.ToString(System.Globalization.CultureInfo.InvariantCulture), "--reopen", "--app", @"C:\P\Nixie\app\Nixie.exe", "--", "--data-dir", @"D:\my data"],
			info.ArgumentList);
		var quiet = UpdateHandoff.HelperStart("h.exe", "x.msi", @"C:\u", 1, started, @"C:\a.exe", [], reopen: false, quiet: true);
		Assert.Contains("--quiet", quiet.ArgumentList);
		Assert.DoesNotContain("--reopen", quiet.ArgumentList);
		Assert.Throws<UpdateException>(() => UpdateHandoff.HelperStart("h.exe", "evil.exe", @"C:\u", 1, started, null, [], false, false));
	}

	[Fact]
	public void TheNextStart_ReadsWhatBecameOfTheInstall()
	{
		var now = new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);
		var pending = new PendingInstall("1.5.1", "1.5.0", now.AddMinutes(-2), $"{Repo}/releases/tag/v1.5.1", "notes", true, new ResumePlace("FluentFin.Core.ViewModels.SettingsViewModel"));

		Assert.Equal(AftermathKind.None, UpdateAftermath.Read(null, null, "1.5.0", now, false).Kind);
		Assert.Equal(AftermathKind.Updated, UpdateAftermath.Read(pending, 0, "1.5.1", now, false).Kind);
		Assert.Equal(AftermathKind.Updated, UpdateAftermath.Read(pending, null, "1.5.2", now, false).Kind);
		Assert.Equal(AftermathKind.Installing, UpdateAftermath.Read(pending, null, "1.5.0", now, installRunning: true).Kind);
		var cancelled = UpdateAftermath.Read(pending, 1602, "1.5.0", now, false);
		Assert.Equal(AftermathKind.Failed, cancelled.Kind);
		Assert.Equal("the installation was cancelled", cancelled.Reason);
		Assert.Equal("Windows Installer stopped with an error (1603)", UpdateAftermath.Read(pending, 1603, "1.5.0", now, false).Reason);
		Assert.Equal(AftermathKind.None, UpdateAftermath.Read(pending, 0, "1.5.0", now, false).Kind);           // installed, but an older copy runs
		Assert.Equal("the installer didn't start", UpdateAftermath.Read(pending, null, "1.5.0", now, false).Reason);
		Assert.Equal("the installer didn't finish", UpdateAftermath.Read(pending, null, "1.5.0", now.AddMinutes(30), false).Reason);
		Assert.Equal(AftermathKind.None, UpdateAftermath.Read(pending, null, "1.5.1", now.AddDays(8), false).Kind);   // old news
		Assert.Null(UpdateHandoff.InstallerError(3010));
	}

	[Fact]
	public void ThePendingInstall_AndTheHelpersResult_RoundTripThroughTheUpdatesFolder()
	{
		var pending = new PendingInstall("1.5.1", "1.5.0", DateTimeOffset.UtcNow, $"{Repo}/releases/tag/v1.5.1", "## Notes", true, new ResumePlace("Key", Guid.NewGuid()));
		UpdateHandoff.WritePending(_folder, pending);
		Assert.Equal(pending, UpdateHandoff.ReadPending(_folder));
		Assert.Null(UpdateHandoff.ReadResult(_folder));
		File.WriteAllText(Path.Combine(_folder, UpdateHandoff.ResultFile), "exit=1618\r\nfinished=2026-11-20T08:00:00Z\r\n");
		Assert.Equal(1618, UpdateHandoff.ReadResult(_folder));
		File.WriteAllText(Path.Combine(_folder, UpdateHandoff.PendingFile), "{ not json");
		Assert.Null(UpdateHandoff.ReadPending(_folder));
	}

	[Fact]
	public async Task AnInstallerFromAnEarlierRun_IsReadyAtTheStart_AndAFailedInstall_OffersRetry()
	{
		var release = TheRelease();
		var msi = Bytes(3000);
		var assets = new AssetServer { Release = release, Installer = _ => msi, HashText = $"{Sha(msi)} *{release.MsiName}\n" };
		await new UpdateDownloader(new HttpClient(assets), _folder).DownloadAsync(release);

		var (settings, _) = FakeSettings(state: new UpdateState(DateTimeOffset.UtcNow, release).ToJson());
		var coordinator = new UpdateCoordinator(settings.Object, new UpdateChecker(new HttpClient(new Fake(_ => throw new HttpRequestException())), "1.5.0"), () => DateTimeOffset.UtcNow, "1.5.0");
		var center = new UpdateCenter(coordinator, new UpdateDownloader(new HttpClient(assets), _folder), "1.5.0", true, stored: UpdateState.Parse(settings.Object.UpdateState));
		center.Restore(UpdateAftermath.Nothing);
		Assert.Equal(UpdatePhase.Ready, center.Status.Phase);

		var pending = new PendingInstall("1.5.1", "1.5.0", DateTimeOffset.UtcNow, release.PageUrl, release.Notes, true);
		center.Restore(new UpdateAftermath(AftermathKind.Failed, pending, "the installation was cancelled", 1602));
		Assert.Equal(UpdatePhase.InstallFailed, center.Status.Phase);
		Assert.NotNull(center.BeginInstall());                // Retry installs the kept installer
	}

	/// <summary>
	/// Review M1: the start check (same tick as the aftermath) put a failed install back to "ready", so "didn't install: 1603"
	/// flashed and became Restart to update, and Install when I close Nixie reran msiexec /qn at every close. It stays failed,
	/// this run and the next, until the user retries.
	/// </summary>
	[Fact]
	public async Task AFailedInstall_StaysFailed_ThroughTheStartCheck_AndTheNextStart_UntilRetried()
	{
		var release = TheRelease();
		var msi = Bytes(3000);
		var assets = new AssetServer { Release = release, Installer = _ => msi, HashText = $"{Sha(msi)} *{release.MsiName}\n" };
		await new UpdateDownloader(new HttpClient(assets), _folder).DownloadAsync(release);
		var (settings, state) = FakeSettings(state: new UpdateState(DateTimeOffset.UtcNow.AddHours(-5), release).ToJson());
		UpdateCenter New(out UpdateCoordinator coordinator)
		{
			coordinator = new UpdateCoordinator(settings.Object, new UpdateChecker(new HttpClient(new Fake(_ => Ok(Release()))), "1.5.0"), () => DateTimeOffset.UtcNow, "1.5.0");
			return new UpdateCenter(coordinator, new UpdateDownloader(new HttpClient(assets), _folder), "1.5.0", true, stored: UpdateState.Parse(state()));
		}

		var center = New(out var coordinator);
		var pending = new PendingInstall("1.5.1", "1.5.0", DateTimeOffset.UtcNow, release.PageUrl, release.Notes, true);
		center.Restore(new UpdateAftermath(AftermathKind.Failed, pending, "Windows Installer stopped with an error (1603)", 1603));
		await coordinator.TickAsync();          // the start check finds the same release
		await Task.Delay(100);
		Assert.Equal(UpdatePhase.InstallFailed, center.Status.Phase);
		Assert.Equal("Windows Installer stopped with an error (1603)", center.Status.Error);

		var next = New(out _);                  // the next start: the aftermath was read once; the failure is remembered
		next.Restore(UpdateAftermath.Nothing);
		Assert.Equal(UpdatePhase.InstallFailed, next.Status.Phase);

		Assert.NotNull(next.BeginInstall());    // Retry: the user's own
		var after = New(out _);
		after.Restore(UpdateAftermath.Nothing);
		Assert.Equal(UpdatePhase.Ready, after.Status.Phase);
	}

	/// <summary>
	/// Review m4: a release whose file never matches its checksum was fetched twice more at every check (~190 MB each).
	/// Refused by its own files, it is not fetched again by itself, this run or the next, until the user's Retry; a download
	/// that only lost its connection is still tried again at the next check.
	/// </summary>
	[Fact]
	public async Task AReleaseItsFilesRefuse_IsNotFetchedAgainByItself_UntilRetry()
	{
		var release = TheRelease();
		var now = DateTimeOffset.UtcNow;
		var assets = new AssetServer { Release = release, Installer = _ => Bytes(100_000, seed: 3), HashText = $"{Sha(Bytes(10))} *{release.MsiName}\n" };
		var (settings, state) = FakeSettings();
		UpdateCenter New(out UpdateCoordinator coordinator)
		{
			coordinator = new UpdateCoordinator(settings.Object, new UpdateChecker(new HttpClient(new Fake(_ => Ok(Release()))), "1.5.0"), () => now, "1.5.0");
			return new UpdateCenter(coordinator, new UpdateDownloader(new HttpClient(assets), _folder), "1.5.0", true, clock: () => now, stored: UpdateState.Parse(state()));
		}

		var center = New(out var coordinator);
		await coordinator.TickAsync();
		await Until(center, s => s.Phase == UpdatePhase.DownloadFailed);
		Assert.Equal(2, assets.InstallerRequests);
		now = now.AddHours(4).AddMinutes(1);
		await coordinator.TickAsync();          // the 4-hour check finds it again
		await Task.Delay(200);
		Assert.Equal(2, assets.InstallerRequests);
		Assert.Equal(UpdatePhase.DownloadFailed, center.Status.Phase);
		Assert.Equal("The download didn't match its checksum twice, so it was not used.", center.Status.Error);

		var next = New(out var nextCoordinator);   // the next start
		next.Restore(UpdateAftermath.Nothing);
		await nextCoordinator.TickAsync();
		await Task.Delay(200);
		Assert.Equal(2, assets.InstallerRequests);
		Assert.Equal(UpdatePhase.DownloadFailed, next.Status.Phase);

		await next.StartDownload();              // Retry: fetched again, once more (twice: the start over)
		Assert.Equal(4, assets.InstallerRequests);
	}

	/// <summary>
	/// Review m2: a dismissed version still downloaded (~190 MB) at every start and was Ready, so Install when I close Nixie
	/// installed it. Dismissed, it stops downloading, is not Ready by itself and is not fetched at later starts; the user's
	/// Download still gets it, and a newer version is downloaded as usual.
	/// </summary>
	[Fact]
	public async Task ADismissedVersion_IsNeitherDownloadedNorReady_ByItself_UntilANewerOneOrTheUserAsks()
	{
		var release = TheRelease();
		var msi = Bytes(2_000_000);
		var gate = new ManualResetEventSlim(false);
		var assets = new AssetServer
		{
			Release = release, Installer = _ => msi, HashText = $"{Sha(msi)} *{release.MsiName}\n", Chunk = 100_000,
			AfterChunk = at => { if (at >= 500_000 && at < 600_000) { gate.Wait(TimeSpan.FromSeconds(5)); } },
		};
		var (settings, state) = FakeSettings();
		var latest = "v1.5.1";
		UpdateCenter New(out UpdateCoordinator coordinator, HttpMessageHandler files)
		{
			coordinator = new UpdateCoordinator(settings.Object, new UpdateChecker(new HttpClient(new Fake(_ => Ok(Release(latest)))), "1.5.0"), () => DateTimeOffset.UtcNow, "1.5.0");
			return new UpdateCenter(coordinator, new UpdateDownloader(new HttpClient(files), _folder), "1.5.0", true, stored: UpdateState.Parse(state()));
		}

		var center = New(out var coordinator, assets);
		await coordinator.TickAsync();
		await Until(center, s => s.Phase == UpdatePhase.Downloading && s.Progress?.Done > 0);
		coordinator.Dismiss();                   // what the notice's Dismiss does
		center.Dismissed();
		gate.Set();
		var status = await Until(center, s => s.Phase == UpdatePhase.Available);
		Assert.Equal(UpdatePhase.Available, status.Phase);
		var fetched = assets.InstallerRequests;

		var next = New(out var nextCoordinator, assets);   // the next start: not fetched, not Ready
		next.Restore(UpdateAftermath.Nothing);
		await nextCoordinator.TickAsync();
		await Task.Delay(200);
		Assert.Equal(fetched, assets.InstallerRequests);
		Assert.NotEqual(UpdatePhase.Ready, next.Status.Phase);
		Assert.False(next.DownloadsByItself(next.Status.Release!));

		await next.StartDownload();              // the user asks: Download
		Assert.Equal(UpdatePhase.Ready, next.Status.Phase);
		var restarted = New(out _, assets);      // downloaded at the user's word, still not Ready by itself at a start
		restarted.Restore(UpdateAftermath.Nothing);
		Assert.Equal(UpdatePhase.Available, restarted.Status.Phase);

		latest = "v1.5.2";                       // a newer version: downloaded as usual
		var newer = New(out var newerCoordinator, new AssetServer { Release = ReleaseInfo.Parse(Release("v1.5.2"))!, Installer = _ => msi, HashText = $"{Sha(msi)} *Nixie-1.5.2-win-x64.msi\n" });
		await newerCoordinator.TickAsync();
		Assert.Equal(UpdatePhase.Ready, (await Until(newer, s => s.Phase == UpdatePhase.Ready)).Phase);
	}

	/// <summary>
	/// Review m3: a Check now that failed removed a known update from the notice, with its buttons. The update stays, in the
	/// notice and in Settings, with the failure said beside it; the next good answer clears it.
	/// </summary>
	[Fact]
	public async Task ACheckNowThatFails_KeepsAKnownUpdate_AndSaysWhyBesideIt()
	{
		var (settings, _) = FakeSettings();
		var now = DateTimeOffset.UtcNow;
		var offline = false;
		var api = new Fake(_ => offline ? throw new HttpRequestException("offline") : Ok(Release()));
		var coordinator = new UpdateCoordinator(settings.Object, new UpdateChecker(new HttpClient(api), "1.5.0"), () => now, "1.5.0");
		var center = new UpdateCenter(coordinator, new UpdateDownloader(new HttpClient(), _folder), "1.5.0", canSelfUpdate: false);
		await center.CheckNowAsync();
		Assert.Equal(UpdatePhase.Available, center.Status.Phase);

		offline = true;
		now = now.AddMinutes(1);
		var phases = new List<UpdatePhase>();
		center.Changed += s => phases.Add(s.Phase);
		await center.CheckNowAsync();

		Assert.DoesNotContain(UpdatePhase.Checking, phases);      // the update never left the screen
		var notice = UpdateView.ForNotice(center.Status)!;
		Assert.Equal("Nixie 1.5.1 is available", notice.Title);
		Assert.Equal("Couldn't check: no connection to GitHub", notice.Detail);
		Assert.Equal(UpdateAction.OpenReleasePage, notice.Primary);
		Assert.True(notice.IsError);
		Assert.Equal("Couldn't check: no connection to GitHub", UpdateView.ForSettings(center.Status, now, TimeZoneInfo.Utc).Detail);

		offline = false;
		now = now.AddMinutes(1);
		await center.CheckNowAsync();
		Assert.Equal("This copy of Nixie is portable, so it updates from the release page.", UpdateView.ForNotice(center.Status)!.Detail);
	}

	/// <summary>
	/// Review m5: Restart to update with its installer gone started a download and then said "didn't install" over it, hiding
	/// the progress. Now the download shows, and ReadyToInstall says when the install can go on.
	/// </summary>
	[Fact]
	public async Task RestartToUpdate_WithTheInstallerGone_ShowsTheDownload_ThenSaysTheInstallCanGoOn()
	{
		var release = TheRelease();
		var msi = Bytes(2_000_000);
		var gate = new ManualResetEventSlim(false);
		var assets = new AssetServer
		{
			Release = release, Installer = _ => msi, HashText = $"{Sha(msi)} *{release.MsiName}\n", Chunk = 100_000,
			AfterChunk = at => { if (at >= 1_000_000 && at < 1_100_000) { gate.Wait(TimeSpan.FromSeconds(30)); } },
		};
		await new UpdateDownloader(new HttpClient(new AssetServer { Release = release, Installer = _ => msi, HashText = $"{Sha(msi)} *{release.MsiName}\n" }), _folder).DownloadAsync(release);
		var (settings, _) = FakeSettings(state: new UpdateState(DateTimeOffset.UtcNow, release).ToJson());
		var coordinator = new UpdateCoordinator(settings.Object, new UpdateChecker(new HttpClient(new Fake(_ => throw new HttpRequestException())), "1.5.0"), () => DateTimeOffset.UtcNow, "1.5.0");
		var center = new UpdateCenter(coordinator, new UpdateDownloader(new HttpClient(assets), _folder), "1.5.0", true, stored: UpdateState.Parse(settings.Object.UpdateState));
		center.Restore(UpdateAftermath.Nothing);
		Assert.Equal(UpdatePhase.Ready, center.Status.Phase);
		var ready = 0;
		center.ReadyToInstall += () => Interlocked.Increment(ref ready);

		File.Delete(Path.Combine(_folder, "Nixie-1.5.1-win-x64.msi"));   // a cleanup or an antivirus took it
		Assert.Null(center.BeginInstall());
		Assert.True(center.WaitingToInstall);
		var mid = await Until(center, s => s.Phase == UpdatePhase.Downloading);
		Assert.Equal(UpdatePhase.Downloading, mid.Phase);                 // the download shows, not "didn't install"
		Assert.Equal(0, ready);
		gate.Set();
		await Until(center, s => s.Phase == UpdatePhase.Ready);
		await Task.Delay(50);
		Assert.Equal(1, ready);                                           // then the install goes on
		Assert.False(center.WaitingToInstall);
		Assert.NotNull(center.BeginInstall());
	}

	/// <summary>
	/// Review m8: msiexec's 3010 (restart required: files were in use) counted as success, so the old version, still running,
	/// silently offered the same install again. It says Windows finishes it at its restart, offers nothing to click, keeps
	/// that through checks and starts, and after a Windows restart that did not finish it, says so with Retry.
	/// </summary>
	[Fact]
	public async Task RestartRequired_SaysWindowsFinishesIt_AndIsNotOfferedAgain_UntilARestartThatDidNotFinishIt()
	{
		var now = new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);
		var pending = new PendingInstall("1.5.1", "1.5.0", now.AddMinutes(-2), $"{Repo}/releases/tag/v1.5.1", "notes", true);
		var aftermath = UpdateAftermath.Read(pending, 3010, "1.5.0", now, installRunning: false);
		Assert.Equal(AftermathKind.RestartNeeded, aftermath.Kind);
		Assert.Equal(AftermathKind.RestartNeeded, UpdateAftermath.Read(pending, 1641, "1.5.0", now, false).Kind);
		Assert.Equal(AftermathKind.Updated, UpdateAftermath.Read(pending, 3010, "1.5.1", now, false).Kind);   // already the new one

		var release = TheRelease();
		var msi = Bytes(3000);
		var assets = new AssetServer { Release = release, Installer = _ => msi, HashText = $"{Sha(msi)} *{release.MsiName}\n" };
		await new UpdateDownloader(new HttpClient(assets), _folder).DownloadAsync(release);
		var (settings, state) = FakeSettings(state: new UpdateState(now.AddHours(-5), release).ToJson());
		var boot = now.AddDays(-1);
		UpdateCenter New(out UpdateCoordinator coordinator)
		{
			coordinator = new UpdateCoordinator(settings.Object, new UpdateChecker(new HttpClient(new Fake(_ => Ok(Release()))), "1.5.0"), () => now, "1.5.0");
			return new UpdateCenter(coordinator, new UpdateDownloader(new HttpClient(assets), _folder), "1.5.0", true, clock: () => now, stored: UpdateState.Parse(state())) { BootTime = () => boot };
		}

		var center = New(out var coordinator);
		center.Restore(aftermath);
		await coordinator.TickAsync();
		await Task.Delay(100);
		Assert.Equal(UpdatePhase.RestartPending, center.Status.Phase);
		var view = UpdateView.ForNotice(center.Status)!;
		Assert.Equal("Restart Windows to finish updating to Nixie 1.5.1", view.Title);
		Assert.Equal(UpdateAction.None, view.Primary);
		Assert.Null(center.BeginInstall());                  // nothing installs it again meanwhile (nor as Nixie closes: not Ready)

		now = now.AddHours(1);
		var later = New(out _);                              // a later start, no Windows restart yet
		later.Restore(UpdateAftermath.Nothing);
		Assert.Equal(UpdatePhase.RestartPending, later.Status.Phase);

		boot = now.AddMinutes(10);                           // Windows restarted, and this is still 1.5.0
		now = now.AddHours(1);
		var afterBoot = New(out _);
		afterBoot.Restore(UpdateAftermath.Nothing);
		Assert.Equal(UpdatePhase.InstallFailed, afterBoot.Status.Phase);
		Assert.Equal("Windows restarted, but the install didn't finish", afterBoot.Status.Error);
		Assert.Equal(UpdateAction.Retry, UpdateView.ForNotice(afterBoot.Status)!.Primary);
	}

	[Fact]
	public void TheSecondWindow_IsAnotherProcessOfThisVeryProgram()
	{
		const string App = @"C:\Users\x\AppData\Local\Programs\Nixie\app\Nixie.exe";
		(int, string?)[] running = [(10, App), (11, @"c:\users\x\appdata\local\programs\nixie\APP\Nixie.exe"), (12, @"D:\portable\app\Nixie.exe"), (13, null)];

		Assert.Equal([11], UpdateHandoff.OtherInstances(running, 10, App));
		Assert.Empty(UpdateHandoff.OtherInstances([(10, App)], 10, App));
	}

	[Fact]
	public void Tidy_KeepsTheReleaseInHand_AndTheHandoffsFiles_AndRemovesOldInstallers()
	{
		Directory.CreateDirectory(_folder);
		foreach (var name in new[] { "Nixie-1.4.1-win-x64.msi", "Nixie-1.4.1-win-x64.msi.sha256", "Nixie-1.5.0-win-x64.msi.part", "Nixie-1.5.1-win-x64.msi",
			"Nixie-1.5.1-win-x64.msi.sha256", UpdateHandoff.PendingFile, UpdateHandoff.LogFile, "notes.txt" })
		{
			File.WriteAllText(Path.Combine(_folder, name), "x");
		}

		var (settings, _) = FakeSettings(state: new UpdateState(DateTimeOffset.UtcNow, TheRelease()).ToJson());
		var coordinator = new UpdateCoordinator(settings.Object, new UpdateChecker(new HttpClient(new Fake(_ => Ok(""))), "1.5.0"), () => DateTimeOffset.UtcNow, "1.5.0");
		new UpdateCenter(coordinator, new UpdateDownloader(new HttpClient(), _folder), "1.5.0", true, stored: UpdateState.Parse(settings.Object.UpdateState)).Tidy();

		Assert.Equal(["Nixie-1.5.1-win-x64.msi", "Nixie-1.5.1-win-x64.msi.sha256", "install.log", "notes.txt", "pending.json"],
			Directory.GetFiles(_folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
	}

	/// <summary>The Sandbox upgrade test's switch: only a checked, newer Nixie installer; the reopened Nixie never sees it.</summary>
	[Fact]
	public void TheInstallSwitch_TakesOnlyACheckedNewerNixieInstaller_AndIsNotPassedOn()
	{
		Directory.CreateDirectory(_folder);
		var msi = Path.Combine(_folder, "Nixie-1.5.99-win-x64.msi");
		var bytes = Bytes(5000);
		File.WriteAllBytes(msi, bytes);
		File.WriteAllText(msi + ".sha256", $"{Sha(bytes)} *Nixie-1.5.99-win-x64.msi\n");

		var release = UpdateHandoff.LocalInstaller(["--data-dir", "D:\\d", UpdateHandoff.InstallSwitch, msi], "1.5.0", false, null, out var path, out var error);

		Assert.Null(error);
		Assert.Equal(msi, path);
		Assert.Equal("1.5.99", release!.Version);
		Assert.True(release.HasInstaller);
		Assert.Equal(Sha(bytes), release.MsiDigest);
		Assert.Equal(["--data-dir", "D:\\d"], UpdateHandoff.WithoutInstallSwitch(["--data-dir", "D:\\d", "--install-update", msi]));

		Assert.Null(UpdateHandoff.LocalInstaller(["--data-dir", "x"], "1.5.0", false, null, out _, out error));
		Assert.Null(error);                                                    // no switch: nothing to say
		Assert.Null(UpdateHandoff.LocalInstaller(["--install-update", msi], "1.5.99", false, null, out _, out error));
		Assert.Contains("not newer", error);
		File.WriteAllText(msi + ".sha256", $"{new string('0', 64)} *Nixie-1.5.99-win-x64.msi\n");
		Assert.Null(UpdateHandoff.LocalInstaller(["--install-update", msi], "1.5.0", false, null, out _, out error));
		Assert.Contains("no .sha256", error);
		var other = Path.Combine(_folder, "evil.msi");
		File.WriteAllBytes(other, bytes);
		Assert.Null(UpdateHandoff.LocalInstaller(["--install-update", other], "1.5.0", false, null, out _, out error));
		Assert.Contains("is not a Nixie installer", error);
		Assert.Null(UpdateHandoff.LocalInstaller(["--install-update"], "1.5.0", false, null, out _, out error));
		Assert.NotNull(error);
	}

	/// <summary>
	/// BH-150-m6: a release package takes the switch only with the Sandbox test's marker (the variable naming the host script's
	/// marker file), so a shortcut with arguments can't install an installer with no question asked. A dev build doesn't ask.
	/// </summary>
	[Fact]
	public void TheInstallSwitch_InAReleasePackage_NeedsTheUpgradeTestsMarker()
	{
		Directory.CreateDirectory(_folder);
		var msi = Path.Combine(_folder, "Nixie-1.5.99-win-x64.msi");
		var bytes = Bytes(5000);
		File.WriteAllBytes(msi, bytes);
		File.WriteAllText(msi + ".sha256", $"{Sha(bytes)} *Nixie-1.5.99-win-x64.msi\n");
		string[] arguments = [UpdateHandoff.InstallSwitch, msi];

		Assert.Null(UpdateHandoff.LocalInstaller(arguments, "1.5.0", releasePackage: true, testMarker: null, out var path, out var error));
		Assert.Null(path);
		Assert.Contains("only in the upgrade test", error);
		Assert.Contains(UpdateHandoff.TestMarkerVariable, error);

		var marker = Path.Combine(_folder, UpdateHandoff.TestMarkerName);
		Assert.Null(UpdateHandoff.LocalInstaller(arguments, "1.5.0", true, marker, out _, out error));        // named, not there
		Assert.Contains("only in the upgrade test", error);
		File.WriteAllText(marker, "anything else\n");
		Assert.Null(UpdateHandoff.LocalInstaller(arguments, "1.5.0", true, marker, out _, out error));        // not the script's
		Assert.Contains("only in the upgrade test", error);
		var elsewhere = Path.Combine(_folder, "marker.txt");
		File.WriteAllText(elsewhere, UpdateHandoff.TestMarkerText + "\n");
		Assert.Null(UpdateHandoff.LocalInstaller(arguments, "1.5.0", true, elsewhere, out _, out error));     // not its name
		Assert.Contains("only in the upgrade test", error);

		// what tools\release\upgrade-test-sandbox.ps1 writes (its folder marker, UTF-8 without a BOM)
		File.WriteAllText(marker, UpdateHandoff.TestMarkerText + "\n", new System.Text.UTF8Encoding(false));
		Assert.Equal("1.5.99", UpdateHandoff.LocalInstaller(arguments, "1.5.0", true, marker, out path, out error)!.Version);
		Assert.Null(error);
		Assert.Equal(msi, path);

		Assert.Equal("1.5.99", UpdateHandoff.LocalInstaller(arguments, "1.5.0", false, null, out _, out error)!.Version);   // a dev build
		Assert.Null(UpdateHandoff.LocalInstaller(["--data-dir", "x"], "1.5.0", true, null, out _, out error));
		Assert.Null(error);                                                    // no switch: nothing to say, marker or not
	}

	/// <summary>BH-150-m6: the marker's words are the host script's own, so the Sandbox test's start passes the gate.</summary>
	[Fact]
	public void TheUpgradeTestMarker_IsWhatTheSandboxScriptsWrite()
	{
		var release = Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "..", "tools", "release");
		var host = File.ReadAllText(Path.Combine(release, "upgrade-test-sandbox.ps1"));
		Assert.Contains($"$marker = '{UpdateHandoff.TestMarkerName}'", host);
		Assert.Contains($"\"{UpdateHandoff.TestMarkerText}`n\"", host);
		var inside = File.ReadAllText(Path.Combine(release, "sandbox", "upgrade-inside.ps1"));
		Assert.Contains($"$env:{UpdateHandoff.TestMarkerVariable} = ", inside);
		Assert.Contains(UpdateHandoff.TestMarkerName, inside);
	}

	/// <summary>
	/// Review m7: the helper opens Nixie from the app's folder, so a relative --data-dir (or the variable's) would name another
	/// folder there. The reopened Nixie gets the data folder this run resolved, in full, and never the install switch.
	/// </summary>
	[Fact]
	public void TheReopenedNixie_GetsItsDataFolderInFull_AndNoInstallSwitch()
	{
		var resolved = Path.Combine(_folder, "my data");
		Assert.Equal([KnownFolders.DataDirSwitch, resolved, "--x"],
			UpdateHandoff.ArgumentsForReopen(["--data-dir", "my data", "--install-update", @"C:\u\Nixie-1.5.1-win-x64.msi", "--x"], resolved));
		Assert.Equal([KnownFolders.DataDirSwitch, resolved], UpdateHandoff.ArgumentsForReopen(["--data-dir=my data"], resolved));
		Assert.Equal([KnownFolders.DataDirSwitch, resolved], UpdateHandoff.ArgumentsForReopen([], resolved));   // from the variable
		Assert.Equal(["--x"], UpdateHandoff.ArgumentsForReopen(["--x"], null));                                // the default folder
	}

	[Fact]
	public void ALocalInstaller_IsReadyToInstall_OnAnInstalledCopyOnly()
	{
		var source = Path.Combine(_folder, "given");
		Directory.CreateDirectory(source);
		var msi = Path.Combine(source, "Nixie-1.5.99-win-x64.msi");
		var bytes = Bytes(5000);
		File.WriteAllBytes(msi, bytes);
		File.WriteAllText(msi + ".sha256", $"{Sha(bytes)} *Nixie-1.5.99-win-x64.msi\n");
		var release = UpdateHandoff.LocalInstaller(["--install-update", msi], "1.5.0", false, null, out var path, out _)!;
		var updates = Path.Combine(_folder, "Updates");

		var portable = new UpdateCenter(new UpdateCoordinator(FakeSettings().settings.Object, new UpdateChecker(new HttpClient(), "1.5.0"), () => DateTimeOffset.UtcNow, "1.5.0"),
			new UpdateDownloader(new HttpClient(), updates), "1.5.0", canSelfUpdate: false);
		Assert.False(portable.UseLocalInstaller(release, path!));

		var installed = new UpdateCenter(new UpdateCoordinator(FakeSettings().settings.Object, new UpdateChecker(new HttpClient(), "1.5.0"), () => DateTimeOffset.UtcNow, "1.5.0"),
			new UpdateDownloader(new HttpClient(), updates), "1.5.0", canSelfUpdate: true);
		Assert.True(installed.UseLocalInstaller(release, path!));
		Assert.Equal(UpdatePhase.Ready, installed.Status.Phase);
		Assert.Equal(Path.Combine(updates, "Nixie-1.5.99-win-x64.msi"), installed.BeginInstall());
	}

	// ---------- 6. a repository rename (BH-140X-m3)

	[Fact]
	public async Task ARenamedRepository_ItsReleaseIsTaken_WhenGitHubsRedirectSaysSo()
	{
		const string renamed = "https://github.com/mbustos27/nixie";
		var json = Release(repo: renamed);
		var redirect = new Uri("https://api.github.com/repositories/123456/releases/latest");

		var followed = await new UpdateChecker(new HttpClient(new Fake(_ => Ok(json), answeredFrom: redirect)), "1.5.0").CheckAsync(DateTimeOffset.UtcNow);

		Assert.Equal(UpdateCheckStatus.Available, followed.Status);
		var release = followed.Release!;
		Assert.True(release.HasInstaller);
		Assert.Equal("mbustos27/nixie", release.Repository);
		Assert.Equal($"{renamed}/releases/download/v1.5.1/Nixie-1.5.1-win-x64.msi", release.MsiUrl);
		Assert.Equal($"{renamed}/releases/tag/v1.5.1", release.PageUrl);
		Assert.Equal(release, release.Validated());                     // stored and checked again: still whole
		Assert.True(UpdateSource.IsDownload(release.MsiUrl, release.Source));

		// without GitHub's redirect, an answer naming another repository is not followed (as before 1.5)
		var unredirected = await new UpdateChecker(new HttpClient(new Fake(_ => Ok(json))), "1.5.0").CheckAsync(DateTimeOffset.UtcNow);
		Assert.False(unredirected.Release!.HasInstaller);
		Assert.Null(unredirected.Release.Repository);
		Assert.StartsWith($"{Repo}/releases/", unredirected.Release.PageUrl);
	}

	[Theory]
	[InlineData("https://github.com/insomniachi/FluentFin", null)]                                       // upstream: never
	[InlineData("https://github.com/mbustos27/nixie", "https://api.github.com/repos/someone/else/releases/1")]   // page and API disagree
	public void ARename_IsRefused_ForUpstream_OrWhenTheAnswerDisagreesWithItself(string repo, string? api)
	{
		var release = ReleaseInfo.Parse(Release(repo: repo, api: api), followRename: true)!;

		Assert.False(release.HasInstaller);
		Assert.Null(release.Repository);
	}

	[Theory]
	[InlineData("https://github.com/mbustos27/nixie/releases/tag/v1", "mbustos27/nixie")]
	[InlineData("https://github.com/mbustos27/fluentfin-mpv/releases/tag/v1.5.1", "mbustos27/fluentfin-mpv")]
	[InlineData("https://github.com/insomniachi/FluentFin/releases/tag/v1", null)]
	[InlineData("https://github.com/-bad/nixie/releases/tag/v1", null)]
	[InlineData("https://github.com/a/../releases/tag/v1", null)]
	[InlineData("https://github.com/a/b/issues/1", null)]
	[InlineData("https://evil.example/a/b/releases/tag/v1", null)]
	[InlineData("http://github.com/a/b/releases/tag/v1", null)]
	public void RepositoryOf_ReadsOnlyARealReleasePage(string page, string? expected) => Assert.Equal(expected, UpdateSource.RepositoryOf(page));

	[Fact]
	public void ARedirect_CountsOnlyOnTheSameOrigin()
	{
		var asked = new Uri(UpdateSource.LatestReleaseApi);
		Assert.True(UpdateChecker.Redirected(asked, new Uri("https://api.github.com/repositories/1/releases/latest")));
		Assert.False(UpdateChecker.Redirected(asked, asked));
		Assert.False(UpdateChecker.Redirected(asked, new Uri("https://evil.example/repositories/1/releases/latest")));
		Assert.False(UpdateChecker.Redirected(asked, new Uri("http://api.github.com/repositories/1/releases/latest")));
		Assert.False(UpdateChecker.Redirected(asked, null));
	}

	[Fact]
	public void AStoredRenamedRelease_KeepsItsRepository_OnlyWhenItsPageNamesIt()
	{
		var forged = new ReleaseInfo("1.5.1", $"{Repo}/releases/tag/v1.5.1", "", "Nixie-1.5.1-win-x64.msi",
			"https://github.com/someone/else/releases/download/v1.5.1/Nixie-1.5.1-win-x64.msi",
			"https://github.com/someone/else/releases/download/v1.5.1/Nixie-1.5.1-win-x64.msi.sha256") { Repository = "someone/else" };

		var validated = forged.Validated();

		Assert.Null(validated.Repository);
		Assert.False(validated.HasInstaller);
	}

	/// <summary>BH-140X-m3's permanent check: one repository, named the same by the app and by tools\release.</summary>
	[Fact]
	public void TheRepository_IsTheOneTheReleaseToolsName_AndTheOldNamesInstallerCopyIsStillMade()
	{
		var tools = Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "..", "tools", "release");
		var build = File.ReadAllText(Path.Combine(tools, "build-release.ps1"));
		Assert.Contains($"$SourceUrl = \"{UpdateSource.RepositoryUrl}/tree/v$Version\"", build);
		// Seaglass 1.1.x's updater takes only Seaglass-<version>-win-x64.msi: the build keeps making that copy
		Assert.Contains("$oldNameMsi = Join-Path $dist \"Seaglass-$Version-win-x64.msi\"", build);
		var installer = File.ReadAllText(Path.Combine(tools, "installer", "Nixie.wxs"));
		Assert.Contains($"Value=\"{UpdateSource.ReleasesPage}\"", installer);
	}

	[Fact]
	public async Task GitHubRefusing_WithoutTheRateLimit_IsSaidPlainly()
	{
		var refused = await new UpdateChecker(new HttpClient(new Fake(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))), "1.5.0").CheckAsync(DateTimeOffset.UtcNow);

		Assert.Equal(UpdateCheckStatus.Failed, refused.Status);
		Assert.Equal("GitHub refused the request (403)", refused.Reason);
	}

	/// <summary>
	/// The opt-in live check (tools\release\live-release-check.ps1 sets NIXIE_LIVE_GITHUB=1; otherwise this passes without a
	/// request): the real "latest release" through the updater's own checker, one read-only request with Nixie's User-Agent.
	/// Its installer and checksum must pass the updater's rules (the repository, the names), its page too; the asset's digest
	/// is reported (a release without one is checked against its .sha256 alone, and says so). The answer is written to the
	/// test's output for the record.
	/// </summary>
	[Fact]
	public async Task Live_TheRealLatestRelease_PassesTheUpdatersRules()
	{
		if (Environment.GetEnvironmentVariable("NIXIE_LIVE_GITHUB") != "1")
		{
			return;
		}

		var checker = new UpdateChecker(new HttpClient(), "0.0.1");
		var result = await checker.CheckAsync(DateTimeOffset.UtcNow);
		Assert.True(result.Status == UpdateCheckStatus.Available, $"{result.Status} {result.Reason} {result.RetryAfter}");
		var release = result.Release!;
		Assert.True(release.HasInstaller, $"{release.Version}: no installer that passes the rules");
		Assert.True(UpdateSource.IsReleasePage(release.PageUrl, release.Source), release.PageUrl);
		Assert.Null(release.Repository);   // no rename: the repository is the one the app names
		Assert.Equal(UpdateSource.ApiVersion, checker.ApiVersionSelected);   // BH-150-m8: GitHub used the version pinned
		File.WriteAllText(Path.Combine(Path.GetTempPath(), "nixie-live-release-check.txt"),
			$"{release.Version} {release.MsiName} size {release.MsiSize} digest {release.MsiDigest ?? "none"} api {checker.ApiVersionSelected}\n{release.MsiUrl}\n{release.Sha256Url}\n{release.PageUrl}\n");
	}

	// ---------- 7. the release notes

	[Fact]
	public void TheNotes_ReadAsHeadingsBulletsAndLinks_AndOnlyWebLinksOpen()
	{
		const string body = """
			## 1.5.0
			What's new in **Nixie**:

			- Search finds the best match first ([#12](https://github.com/mbustos27/fluentfin-mpv/issues/12))
			  and groups by it
			  - nested `detail`
			* Updates: see https://github.com/mbustos27/fluentfin-mpv/releases.
			1. [bad](javascript:alert(1)) <b>bold?</b>
			""";

		var blocks = ReleaseNotes.Parse(body);

		Assert.Equal(NotesBlockKind.Heading, blocks[0].Kind);
		Assert.Equal("1.5.0", blocks[0].PlainText);
		Assert.Equal(NotesBlockKind.Paragraph, blocks[1].Kind);
		Assert.Contains(blocks[1].Inlines, i => i.Bold && i.Text == "Nixie");
		Assert.Equal(NotesBlockKind.Bullet, blocks[2].Kind);
		Assert.Equal("Search finds the best match first (#12) and groups by it", blocks[2].PlainText);
		Assert.Equal("https://github.com/mbustos27/fluentfin-mpv/issues/12", blocks[2].Inlines.Single(i => i.Link is not null).Link);
		Assert.Equal(1, blocks[3].Level);
		Assert.Contains(blocks[3].Inlines, i => i.Code && i.Text == "detail");
		Assert.Equal("https://github.com/mbustos27/fluentfin-mpv/releases", blocks[4].Inlines.Single(i => i.Link is not null).Link);
		Assert.Null(blocks[5].Inlines.Single(i => i.Text == "bad").Link);   // javascript: never opens
		Assert.Equal("bad bold?", blocks[5].PlainText);
		Assert.Empty(ReleaseNotes.Parse(null));
		Assert.Single(ReleaseNotes.Parse(string.Join('\n', Enumerable.Repeat("- x", 2000)), maxBlocks: 1));
	}
}
