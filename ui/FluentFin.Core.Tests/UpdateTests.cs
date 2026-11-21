using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentFin.Core.Settings;
using FluentFin.Core.Updates;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>Seaglass: the opt-in update notice. Recorded GitHub responses only; nothing here touches the network.</summary>
public sealed class UpdateTests : IDisposable
{
	private const string Repo = "https://github.com/mbustos27/fluentfin-mpv";
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "nixie-update-tests-" + Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		if (Directory.Exists(_folder))
		{
			Directory.Delete(_folder, recursive: true);
		}
	}

	// --- a recorded "latest release" answer (trimmed to what the code reads)
	private static string Release(string tag = "v1.2.0", bool draft = false, bool prerelease = false, string? msiUrl = null, string? shaUrl = null,
		string? page = null, bool assets = true)
	{
		var v = tag.TrimStart('v');
		msiUrl ??= $"{Repo}/releases/download/{tag}/Nixie-{v}-win-x64.msi";
		shaUrl ??= msiUrl + ".sha256";
		return $$"""
			{ "tag_name": "{{tag}}", "draft": {{draft.ToString().ToLowerInvariant()}}, "prerelease": {{prerelease.ToString().ToLowerInvariant()}},
			  "html_url": "{{page ?? $"{Repo}/releases/tag/{tag}"}}", "body": "Notes for {{tag}}",
			  "assets": [ {{(assets ? $$"""
			    { "name": "Nixie-{{v}}-win-x64.zip", "browser_download_url": "{{Repo}}/releases/download/{{tag}}/Nixie-{{v}}-win-x64.zip" },
			    { "name": "Nixie-{{v}}-win-x64.msi", "browser_download_url": "{{msiUrl}}" },
			    { "name": "Nixie-{{v}}-win-x64.msi.sha256", "browser_download_url": "{{shaUrl}}" }
			    """ : "")}} ] }
			""";
	}

	private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
	{
		public List<HttpRequestMessage> Requests { get; } = [];

		public HttpResponseMessage Respond(HttpRequestMessage request) => answer(request);

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests.Add(request);
			var response = answer(request);
			response.RequestMessage ??= request;       // what the real handler does; the redirect's final URL
			return Task.FromResult(response);
		}
	}

	private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

	[Fact]
	public void TheSetting_IsOnByDefault_AndNothingIsRemembered()
	{
		// Nixie 1.3.1: on by default (the user confirmed); it was off
		Assert.True(SettingKeys.CheckForUpdates.Default.Value);
		Assert.Equal("", SettingKeys.UpdateState.Default.Value);
		Assert.Null(UpdateState.Parse("").LastCheck);
		Assert.Null(UpdateState.Parse("{ not json").Release);
	}

	// --- version comparison
	[Theory]
	[InlineData("1.2.0", "1.1.1", true)]
	[InlineData("v1.1.2", "1.1.1", true)]
	[InlineData("1.10.0", "1.9.9", true)]
	[InlineData("2.0.0", "1.99.99", true)]
	[InlineData("1.1.1", "1.1.1", false)]
	[InlineData("1.1.0", "1.1.1", false)]
	[InlineData("1.1.1", "1.1.1-rc.1", true)]
	[InlineData("1.1.1-rc.1", "1.1.1", false)]
	[InlineData("1.1.1-rc.2", "1.1.1-rc.1", true)]
	[InlineData("1.1.1-rc.10", "1.1.1-rc.9", true)]
	[InlineData("1.1.1+abc", "1.1.1", false)]
	[InlineData("1.2", "1.1.9", true)]
	[InlineData("garbage", "1.1.1", false)]
	[InlineData("1.2.0", "garbage", false)]
	[InlineData("", "1.1.1", false)]
	[InlineData(null, "1.1.1", false)]
	[InlineData("1.2.x", "1.1.1", false)]
	[InlineData("1.2.0-", "1.1.1", false)]
	public void IsNewer_FollowsSemanticVersions(string? candidate, string current, bool expected) =>
		Assert.Equal(expected, UpdateVersion.IsNewer(candidate, current));

	// --- the gate: Nixie 1.5, every 4 hours (it was once a day)
	[Fact]
	public void Gate_AsksEveryFourHours()
	{
		var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
		Assert.Equal(TimeSpan.FromHours(4), UpdateGate.Interval);
		Assert.True(UpdateGate.IsDue(null, now));
		Assert.False(UpdateGate.IsDue(now.AddHours(-3.9), now));
		Assert.False(UpdateGate.IsDue(now, now));
		Assert.True(UpdateGate.IsDue(now.AddHours(-4), now));
		Assert.True(UpdateGate.IsDue(now.AddHours(-24), now));
		Assert.True(UpdateGate.IsDue(now.AddDays(-9), now));
		// the clock was set back after a check: ask again (and store the real time) rather than wait for years
		Assert.True(UpdateGate.IsDue(now.AddDays(30), now));
	}

	// --- reading the release
	[Fact]
	public void Parse_TakesTheInstallerAndItsChecksum_FromThisRepositoryOnly()
	{
		var release = ReleaseInfo.Parse(Release())!;

		Assert.Equal("1.2.0", release.Version);
		Assert.Equal("Notes for v1.2.0", release.Notes);
		Assert.Equal("Nixie-1.2.0-win-x64.msi", release.MsiName);
		Assert.EndsWith("/Nixie-1.2.0-win-x64.msi", release.MsiUrl);
		Assert.EndsWith("/Nixie-1.2.0-win-x64.msi.sha256", release.Sha256Url);
		Assert.True(release.HasInstaller);
	}

	[Fact]
	public void TheInstallerAndTheRegistryKey_CarryTheAppsName()
	{
		Assert.Equal("Nixie-1.2.0-win-x64.msi", UpdateSource.InstallerName("1.2.0"));
		Assert.Equal(@"Software\Nixie", UpdateSource.RegistryKey);
	}

	[Fact]
	public void Parse_TakesOnlyTheNixieInstaller_NotOneUnderTheOldName()
	{
		// Nixie 1.2.0's releases may also carry the same MSI as Seaglass-<version>-win-x64.msi for Seaglass 1.1.x's updater
		// (docs\RELEASE.md); Nixie itself only ever takes its own name
		var json = $$"""
			{ "tag_name": "v1.3.0", "draft": false, "prerelease": false, "html_url": "{{Repo}}/releases/tag/v1.3.0", "body": "",
			  "assets": [
			    { "name": "Seaglass-1.3.0-win-x64.msi", "browser_download_url": "{{Repo}}/releases/download/v1.3.0/Seaglass-1.3.0-win-x64.msi" },
			    { "name": "Seaglass-1.3.0-win-x64.msi.sha256", "browser_download_url": "{{Repo}}/releases/download/v1.3.0/Seaglass-1.3.0-win-x64.msi.sha256" } ] }
			""";

		var release = ReleaseInfo.Parse(json)!;

		Assert.Equal("1.3.0", release.Version);
		Assert.False(release.HasInstaller);
		var stored = new ReleaseInfo("1.3.0", $"{Repo}/releases/tag/v1.3.0", "", "Seaglass-1.3.0-win-x64.msi",
			$"{Repo}/releases/download/v1.3.0/Seaglass-1.3.0-win-x64.msi", $"{Repo}/releases/download/v1.3.0/Seaglass-1.3.0-win-x64.msi.sha256");
		Assert.False(stored.Validated().HasInstaller);
	}

	[Fact]
	public void Parse_RefusesAnInstallerFromAnotherRepository_SoUpstreamFluentFinIsNeverOffered()
	{
		var upstream = "https://github.com/insomniachi/fluentfin/releases/download/v1.2.0/Nixie-1.2.0-win-x64.msi";
		var release = ReleaseInfo.Parse(Release(msiUrl: upstream))!;

		Assert.False(release.HasInstaller);
		Assert.Null(release.MsiUrl);
		// a page outside this repository is replaced by this repository's own release page
		Assert.Equal($"{Repo}/releases/tag/v1.2.0", ReleaseInfo.Parse(Release(page: "https://example.com/evil"))!.PageUrl);
	}

	[Fact]
	public void Parse_WithoutAnInstallerAsset_StillOffersTheReleasePage()
	{
		var release = ReleaseInfo.Parse(Release(assets: false))!;

		Assert.False(release.HasInstaller);
		Assert.Equal($"{Repo}/releases/tag/v1.2.0", release.PageUrl);
	}

	[Theory]
	[InlineData("{ \"tag_name\": \"v1.2.0\", \"draft\": true }")]
	[InlineData("{ \"tag_name\": \"v1.2.0\", \"prerelease\": true }")]
	[InlineData("{ \"tag_name\": \"nightly\" }")]
	[InlineData("{ \"tag_name\": \"v1.2.0-rc.1\" }")]
	[InlineData("[]")]
	[InlineData("not json")]
	[InlineData("")]
	public void Parse_IgnoresWhatIsNotAReleaseToOffer(string json) => Assert.Null(ReleaseInfo.Parse(json));

	// --- the check itself
	[Fact]
	public async Task Check_AsksOnce_WithAPlainUserAgent_AndFindsANewerVersion()
	{
		var handler = new Fake(_ => Ok(Release()));
		var result = await new UpdateChecker(new HttpClient(handler), "1.1.1").CheckAsync(DateTimeOffset.UtcNow);

		Assert.Equal(UpdateCheckStatus.Available, result.Status);
		Assert.Equal("1.2.0", result.Release!.Version);
		var request = Assert.Single(handler.Requests);
		Assert.Equal("https://api.github.com/repos/mbustos27/fluentfin-mpv/releases/latest", request.RequestUri!.ToString());
		Assert.Equal("Nixie/1.1.1", request.Headers.UserAgent.ToString());
		Assert.Empty(request.Headers.Authorization is null ? [] : new[] { "auth" });
		Assert.False(request.Headers.Contains("Cookie"));
	}

	[Fact]
	public async Task Check_SameOrOlderRelease_IsUpToDate()
	{
		var same = await new UpdateChecker(new HttpClient(new Fake(_ => Ok(Release("v1.1.1")))), "1.1.1").CheckAsync(DateTimeOffset.UtcNow);
		var older = await new UpdateChecker(new HttpClient(new Fake(_ => Ok(Release("v1.1.0")))), "1.1.1").CheckAsync(DateTimeOffset.UtcNow);

		Assert.Equal(UpdateCheckStatus.UpToDate, same.Status);
		Assert.Equal(UpdateCheckStatus.UpToDate, older.Status);
	}

	[Fact]
	public async Task Check_FailsSilently_OnErrorsAndBadAnswers()
	{
		foreach (var handler in new HttpMessageHandler[]
		{
			new Fake(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)),
			new Fake(_ => new HttpResponseMessage(HttpStatusCode.NotFound)),
			new Fake(_ => Ok("<html>not json</html>")),
			new Fake(_ => throw new HttpRequestException("no network")),
		})
		{
			var result = await new UpdateChecker(new HttpClient(handler), "1.1.1").CheckAsync(DateTimeOffset.UtcNow);
			Assert.Equal(UpdateCheckStatus.Failed, result.Status);
			Assert.Null(result.Release);
		}
	}

	[Fact]
	public async Task Check_HonoursGitHubsRateLimit()
	{
		var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
		var reset = now.AddMinutes(37);
		var limited = new Fake(_ =>
		{
			var r = new HttpResponseMessage(HttpStatusCode.Forbidden);
			r.Headers.Add("x-ratelimit-remaining", "0");
			r.Headers.Add("x-ratelimit-reset", reset.ToUnixTimeSeconds().ToString());
			return r;
		});
		var retry = new Fake(_ =>
		{
			var r = new HttpResponseMessage((HttpStatusCode)429);
			r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
			return r;
		});

		var a = await new UpdateChecker(new HttpClient(limited), "1.1.1").CheckAsync(now);
		var b = await new UpdateChecker(new HttpClient(retry), "1.1.1").CheckAsync(now);

		Assert.Equal(UpdateCheckStatus.RateLimited, a.Status);
		Assert.Equal(reset, a.RetryAfter);
		Assert.Equal(UpdateCheckStatus.RateLimited, b.Status);
		Assert.Equal(now.AddSeconds(90), b.RetryAfter);
		Assert.Single(limited.Requests);   // never retried here
	}

	// --- the coordinator: the setting, the gate, the stored notice
	private static (Mock<ISettings> settings, Func<string> state) FakeSettings(bool enabled, string state = "")
	{
		var current = state;
		var settings = new Mock<ISettings>();
		settings.SetupGet(s => s.CheckForUpdates).Returns(enabled);
		settings.SetupGet(s => s.UpdateState).Returns(() => current);
		settings.SetupSet(s => s.UpdateState = It.IsAny<string>()).Callback<string>(v => current = v);
		return (settings, () => current);
	}

	[Fact]
	public async Task Coordinator_Off_SendsNothing_AndShowsNothing()
	{
		var (settings, _) = FakeSettings(enabled: false);
		var handler = new Fake(_ => Ok(Release()));
		var coordinator = new UpdateCoordinator(settings.Object, new UpdateChecker(new HttpClient(handler), "1.1.1"), () => DateTimeOffset.UtcNow, "1.1.1");

		await coordinator.TickAsync();

		Assert.Empty(handler.Requests);
		Assert.Null(coordinator.Notice);
	}

	// Nixie 1.3.1 (the user: "it should check when we open"): every start asks once; Nixie 1.5: then every 4 hours while it stays open
	[Fact]
	public async Task Coordinator_On_AsksAtEveryStart_OnceARun_ThenEveryFourHours_AndTheNoticeShows()
	{
		var (settings, state) = FakeSettings(enabled: true);
		var now = new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);
		var handler = new Fake(_ => Ok(Release()));
		UpdateCoordinator New() => new(settings.Object, new UpdateChecker(new HttpClient(handler), "1.1.1"), () => now, "1.1.1");

		var first = New();
		await first.TickAsync();          // the start check: a newer version, and the notice shows
		Assert.Equal("1.2.0", first.Notice!.Version);
		Assert.Single(handler.Requests);

		now = now.AddMinutes(1);
		await first.TickAsync();          // the same run, a minute later: no second start check
		now = now.AddHours(3);
		await first.TickAsync();
		Assert.Single(handler.Requests);

		var restarted = New();            // a new run, the same day: it asks at its start
		await restarted.TickAsync();
		Assert.Equal(2, handler.Requests.Count);
		Assert.Equal("1.2.0", restarted.Notice!.Version);
		now = now.AddHours(2);
		await restarted.TickAsync();
		Assert.Equal(2, handler.Requests.Count);

		now = now.AddHours(2);            // 4 hours after its start check, the app still open
		await restarted.TickAsync();
		Assert.Equal(3, handler.Requests.Count);
		Assert.Contains("1.2.0", state());
	}

	[Fact]
	public async Task Coordinator_TheStartCheck_ShowsTheNotice_OnlyForANewerVersion()
	{
		var (settings, _) = FakeSettings(enabled: true);
		var coordinator = new UpdateCoordinator(settings.Object, new UpdateChecker(new HttpClient(new Fake(_ => Ok(Release("v1.3.1")))), "1.3.1"), () => DateTimeOffset.UtcNow, "1.3.1");

		await coordinator.TickAsync();

		Assert.Null(coordinator.Notice);
	}

	// Check now: the user's own request, the setting on or off; the shell's bar only with it on
	[Fact]
	public async Task CheckNow_AsksWithTheSettingOff_AndSaysWhatItFound_WithoutTheBar()
	{
		var (settings, state) = FakeSettings(enabled: false);
		var handler = new Fake(_ => Ok(Release("v1.3.2")));
		var coordinator = new UpdateCoordinator(settings.Object, new UpdateChecker(new HttpClient(handler), "1.3.1"), () => DateTimeOffset.UtcNow, "1.3.1");

		var result = await coordinator.CheckNowAsync();

		Assert.Single(handler.Requests);
		Assert.Equal(UpdateCheckStatus.Available, result.Status);
		Assert.Null(coordinator.Notice);
		Assert.Contains("1.3.2", state());
		var message = UpdateCheckMessage.For(result, "1.3.1");
		Assert.Equal("Nixie 1.3.2 is available", message.Text);
		Assert.Equal("1.3.2", message.Release!.Version);
		Assert.False(message.CanRetry);
	}

	[Fact]
	public async Task CheckNow_WithTheSettingOn_ShowsTheBarToo()
	{
		var (settings, _) = FakeSettings(enabled: true);
		var coordinator = new UpdateCoordinator(settings.Object, new UpdateChecker(new HttpClient(new Fake(_ => Ok(Release("v1.3.2")))), "1.3.1"), () => DateTimeOffset.UtcNow, "1.3.1");

		await coordinator.CheckNowAsync();

		Assert.Equal("1.3.2", coordinator.Notice!.Version);
	}

	[Fact]
	public void CheckNow_SaysEachResultPlainly()
	{
		Assert.Equal("Checking\u2026", UpdateCheckMessage.Checking);
		Assert.Equal("Nixie is up to date (1.3.1)", UpdateCheckMessage.For(new UpdateCheckResult(UpdateCheckStatus.UpToDate), "1.3.1").Text);

		var offline = UpdateCheckMessage.For(new UpdateCheckResult(UpdateCheckStatus.Failed, Reason: "no connection to GitHub"), "1.3.1");
		Assert.Equal("Couldn't check: no connection to GitHub", offline.Text);
		Assert.True(offline.IsError && offline.CanRetry);

		var until = new DateTimeOffset(2026, 11, 20, 14, 5, 0, TimeSpan.Zero);
		var limited = UpdateCheckMessage.For(new UpdateCheckResult(UpdateCheckStatus.RateLimited, RetryAfter: until), "1.3.1", TimeZoneInfo.Utc, until.AddHours(-2));
		Assert.Equal("Couldn't check: GitHub asked Nixie to wait until 14:05", limited.Text);
		// Nixie 1.4.0 (bug hunt m7): a wait that is not today says which day
		Assert.Equal("Couldn't check: GitHub asked Nixie to wait until tomorrow at 14:05",
			UpdateCheckMessage.For(new UpdateCheckResult(UpdateCheckStatus.RateLimited, RetryAfter: until), "1.3.1", TimeZoneInfo.Utc, until.AddHours(-16)).Text);
		Assert.Equal("Couldn't check: GitHub asked Nixie to wait until Fri 27 Nov at 14:05",
			UpdateCheckMessage.For(new UpdateCheckResult(UpdateCheckStatus.RateLimited, RetryAfter: until.AddDays(7)), "1.3.1", TimeZoneInfo.Utc, until.AddHours(-2)).Text);
		Assert.True(limited.CanRetry);
		Assert.Equal("Couldn't check: no answer from GitHub", UpdateCheckMessage.For(new UpdateCheckResult(UpdateCheckStatus.Failed), "1.3.1").Text);
	}

	[Fact]
	public async Task TheChecker_SaysWhyItFailed()
	{
		var offline = await new UpdateChecker(new HttpClient(new Fake(_ => throw new HttpRequestException("offline"))), "1.3.1").CheckAsync(DateTimeOffset.UtcNow);
		var broken = await new UpdateChecker(new HttpClient(new Fake(_ => new HttpResponseMessage(HttpStatusCode.BadGateway))), "1.3.1").CheckAsync(DateTimeOffset.UtcNow);
		var garbled = await new UpdateChecker(new HttpClient(new Fake(_ => Ok("{ not json"))), "1.3.1").CheckAsync(DateTimeOffset.UtcNow);

		Assert.Equal("no connection to GitHub", offline.Reason);
		Assert.Equal("GitHub answered 502", broken.Reason);
		Assert.Equal("GitHub's answer could not be read", garbled.Reason);
	}

	// GitHub's Retry-After is kept across starts and by Check now: nothing goes out before it
	[Fact]
	public async Task RetryAfter_IsRespected_ByTheStartCheck_AndByCheckNow()
	{
		var (settings, state) = FakeSettings(enabled: true);
		var now = new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);
		var limited = true;
		var handler = new Fake(_ =>
		{
			if (!limited)
			{
				return Ok(Release());
			}

			var r = new HttpResponseMessage((HttpStatusCode)429);
			r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(2));
			return r;
		});
		UpdateCoordinator New() => new(settings.Object, new UpdateChecker(new HttpClient(handler), "1.1.1"), () => now, "1.1.1");

		await New().TickAsync();                         // the start check: told to wait two hours
		Assert.Single(handler.Requests);
		Assert.Equal(now.AddHours(2), UpdateState.Parse(state()).RetryAfter);

		limited = false;
		now = now.AddMinutes(30);
		var next = New();
		await next.TickAsync();                          // a new start inside the wait: nothing sent
		var asked = await next.CheckNowAsync();          // nor for Check now
		Assert.Single(handler.Requests);
		Assert.Equal(UpdateCheckStatus.RateLimited, asked.Status);
		Assert.Equal(now.AddMinutes(90), asked.RetryAfter);

		now = now.AddMinutes(91);
		var later = New();
		await later.TickAsync();                         // after it: the start check asks
		Assert.Equal(2, handler.Requests.Count);
		Assert.Equal("1.2.0", later.Notice!.Version);
		Assert.Null(UpdateState.Parse(state()).RetryAfter);
	}

	/// <summary>
	/// Nixie 1.4.0 (bug hunt m7): GitHub's wait survives a clock change. Rate-limited, then the clock set back two days: the
	/// stored wait is now days ahead, which counts as over (the start check and Check now ask). A Retry-After a week away is
	/// kept as a day at most.
	/// </summary>
	[Fact]
	public async Task TheWait_SurvivesAClockSetBack_AndAWeekLongRetryAfter_IsADayAtMost()
	{
		var (settings, state) = FakeSettings(enabled: true);
		var now = new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);
		var limited = true;
		var retry = TimeSpan.FromHours(2);
		var handler = new Fake(_ =>
		{
			if (!limited)
			{
				return Ok(Release());
			}

			var r = new HttpResponseMessage((HttpStatusCode)429);
			r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retry);
			return r;
		});
		UpdateCoordinator New() => new(settings.Object, new UpdateChecker(new HttpClient(handler), "1.1.1"), () => now, "1.1.1");

		await New().TickAsync();
		Assert.Equal(now.AddHours(2), UpdateState.Parse(state()).RetryAfter);

		// the clock set back two days: the wait is 50 hours ahead, more than any GitHub gives: over
		limited = false;
		now = now.AddDays(-2);
		Assert.False(UpdateGate.Waiting(UpdateState.Parse(state()), now));
		var asked = await New().CheckNowAsync();
		Assert.Equal(UpdateCheckStatus.Available, asked.Status);
		Assert.Equal(2, handler.Requests.Count);

		// a Retry-After a week away: stored as a day; Check now waits that day, then asks
		limited = true;
		retry = TimeSpan.FromDays(7);
		Assert.Equal(UpdateCheckStatus.RateLimited, (await New().CheckNowAsync()).Status);
		Assert.Equal(now + UpdateGate.LongestWait, UpdateState.Parse(state()).RetryAfter);
		now = now.AddHours(23);
		Assert.Equal(UpdateCheckStatus.RateLimited, (await New().CheckNowAsync()).Status);
		Assert.Equal(3, handler.Requests.Count);
		limited = false;
		now = now.AddHours(1).AddMinutes(1);
		Assert.Equal(UpdateCheckStatus.Available, (await New().CheckNowAsync()).Status);
		Assert.Equal(4, handler.Requests.Count);

		Assert.Equal(now.AddHours(24), UpdateGate.CapWait(now.AddDays(7), now));
		Assert.Equal(now.AddHours(3), UpdateGate.CapWait(now.AddHours(3), now));
	}

	// Nixie 1.3.1: on by default, also where an older version wrote its old default (off) into the settings
	private sealed class MemorySettings : Contracts.Services.ILocalSettingsService
	{
		public Dictionary<string, System.Text.Json.Nodes.JsonNode?> Values { get; } = [];

		public System.Text.Json.Nodes.JsonNode? ReadSettingRaw(string key) => Values.GetValueOrDefault(key);

		public T ReadSetting<T>(string key, T defaultValue)
		{
			if (!Values.TryGetValue(key, out var node))
			{
				SaveSetting(key, defaultValue);    // as LocalSettingsService does: the default is written out
				return defaultValue;
			}

			return node is null ? defaultValue : System.Text.Json.JsonSerializer.Deserialize<T>(node)!;
		}

		public void SaveSetting<T>(string key, T value) => Values[key] = System.Text.Json.JsonSerializer.SerializeToNode(value);

		public void RemoveSetting(string key) => Values.Remove(key);

		public byte[] GetEntropyBytes() => [];
	}

	[Fact]
	public void UpdateChecks_AreOnForANewInstall()
	{
		var store = new MemorySettings();

		Assert.True(new Settings.Settings(store).CheckForUpdates);
	}

	[Fact]
	public void ASavedOff_FromAnOlderVersionsDefault_BecomesOn_Once_AndAnOffChosenLaterStays()
	{
		var store = new MemorySettings();
		store.SaveSetting("CheckForUpdates", false);   // 1.3.0 wrote its default out the first time it read it
		var settings = new Settings.Settings(store);

		Assert.True(settings.CheckForUpdates);
		settings.CheckForUpdates = false;               // the user turns it off in 1.3.1
		Assert.False(new Settings.Settings(store).CheckForUpdates);
	}

	[Fact]
	public void ASavedOff_AfterTheCheckEverRan_IsTheUsersChoice_AndStaysOff()
	{
		var store = new MemorySettings();
		store.SaveSetting("CheckForUpdates", false);
		store.SaveSetting("UpdateState", new UpdateState(LastAttempt: DateTimeOffset.UtcNow).ToJson());

		Assert.False(new Settings.Settings(store).CheckForUpdates);
	}

	[Fact]
	public void ASavedOn_StaysOn()
	{
		var store = new MemorySettings();
		store.SaveSetting("CheckForUpdates", true);
		store.SaveSetting("UpdateState", new UpdateState(LastAttempt: DateTimeOffset.UtcNow).ToJson());

		Assert.True(new Settings.Settings(store).CheckForUpdates);
	}

	[Fact]
	public void TheApiOverride_IsTheHarnesssOnly_OnThisMachine_NeverInARelease()
	{
		const string fake = "http://127.0.0.1:5123/__harness/release/latest";

		Assert.Equal(fake, UpdateSource.ApiFor(fake, harnessOn: true, releasePackage: false).AbsoluteUri);
		Assert.Equal(UpdateSource.LatestReleaseApi, UpdateSource.ApiFor(fake, harnessOn: true, releasePackage: true).AbsoluteUri);
		Assert.Equal(UpdateSource.LatestReleaseApi, UpdateSource.ApiFor(fake, harnessOn: false, releasePackage: false).AbsoluteUri);
		Assert.Equal(UpdateSource.LatestReleaseApi, UpdateSource.ApiFor("https://evil.example/latest", harnessOn: true, releasePackage: false).AbsoluteUri);
		Assert.Equal(UpdateSource.LatestReleaseApi, UpdateSource.ApiFor(null, harnessOn: true, releasePackage: false).AbsoluteUri);
	}

	[Fact]
	public async Task Coordinator_AFailedCheck_IsNotACheck_ButIsRetriedAtMostOncePerHour()
	{
		var (settings, state) = FakeSettings(enabled: true);
		var now = DateTimeOffset.UtcNow;
		var offline = true;
		var handler = new Fake(_ => offline ? throw new HttpRequestException("offline") : Ok(Release()));
		UpdateCoordinator New() => new(settings.Object, new UpdateChecker(new HttpClient(handler), "1.1.1"), () => now, "1.1.1");

		await New().TickAsync();                 // offline at start
		Assert.Null(UpdateState.Parse(state()).LastCheck);
		now = now.AddMinutes(20);
		await New().TickAsync();                 // the next start within the hour: not asked
		Assert.Single(handler.Requests);

		offline = false;
		now = now.AddMinutes(45);                // the next start after the hour: asked, not a day later
		var online = New();
		await online.TickAsync();
		Assert.Equal(2, handler.Requests.Count);
		Assert.Equal("1.2.0", online.Notice!.Version);
		Assert.NotNull(UpdateState.Parse(state()).LastCheck);
	}

	[Theory]
	[InlineData("https://github.com/mbustos27/fluentfin-mpv/releases/download/v1.2.0/Nixie-1.2.0-win-x64.msi", true)]
	[InlineData("http://github.com/mbustos27/fluentfin-mpv/releases/download/v1.2.0/a.msi", false)]
	[InlineData("https://github.com.evil.example/mbustos27/fluentfin-mpv/releases/download/v1/a.msi", false)]
	[InlineData("https://evil.example/mbustos27/fluentfin-mpv/releases/download/v1/a.msi", false)]
	[InlineData("https://github.com@evil.example/mbustos27/fluentfin-mpv/releases/download/v1/a.msi", false)]
	[InlineData("https://github.com:8443/mbustos27/fluentfin-mpv/releases/download/v1/a.msi", false)]
	[InlineData("https://github.com/mbustos27/fluentfin-mpv/releases/download/../../../../evil/x/raw/a.msi", false)]
	[InlineData("https://github.com/mbustos27/fluentfin-mpv/releases/download/%2e%2e/%2e%2e/a.msi", false)]
	[InlineData("https://github.com/mbustos27/fluentfin-mpv/releases/download/%2E./a.msi", false)]
	[InlineData("https://github.com/mbustos27/fluentfin-mpv/releases/download/.%2e/a.msi", false)]
	[InlineData("https://github.com/mbustos27/fluentfin-mpv/releases/download/v1%2f..%2fa.msi", false)]
	[InlineData("https://github.com/mbustos27/fluentfin-mpv/releases/download/v1\\..\\a.msi", false)]
	[InlineData("https://github.com/mbustos27/fluentfin-mpv/releases/download/", false)]
	[InlineData("https://github.com/mbustos27/fluentfin-mpv2/releases/download/v1/a.msi", false)]
	[InlineData("https://github.com/mbustos27/fluentfin-mpv/releases/download/v1/a.msi#x", false)]
	[InlineData("https://GITHUB.com/mbustos27/fluentfin-mpv/releases/download/v1/a.msi", true)]
	public void IsDownload_AcceptsOnlyThisRepositorysReleaseAssets(string url, bool expected) =>
		Assert.Equal(expected, UpdateSource.IsDownload(url));

	[Theory]
	[InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1/x", true)]
	[InlineData("https://objects.githubusercontent.com/x", true)]
	[InlineData("https://github.com/x", true)]
	[InlineData("http://release-assets.githubusercontent.com/x", false)]
	[InlineData("https://githubusercontent.com.evil.example/x", false)]
	[InlineData("https://evil.example/x", false)]
	public void TheRedirectMayOnlyLandOnGitHub(string url, bool expected) =>
		Assert.Equal(expected, UpdateSource.IsDownloadHost(new Uri(url)));

	[Fact]
	public async Task Coordinator_AStoredReleaseIsCheckedAgain_BeforeItIsOffered()
	{
		var stored = new UpdateState(DateTimeOffset.UtcNow, new ReleaseInfo("1.2.0", "https://evil.example/page", "n", "Nixie-1.2.0-win-x64.msi",
			"https://github.com/mbustos27/fluentfin-mpv/releases/download/%2e%2e/%2e%2e/evil/a.msi",
			"https://github.com/mbustos27/fluentfin-mpv/releases/download/v1.2.0/Nixie-1.2.0-win-x64.msi.sha256"), null).ToJson();
		var (settings, _) = FakeSettings(enabled: true, stored);
		var coordinator = new UpdateCoordinator(settings.Object, new UpdateChecker(new HttpClient(new Fake(_ => throw new HttpRequestException("offline"))), "1.1.1"), () => DateTimeOffset.UtcNow, "1.1.1");

		await coordinator.TickAsync();         // 1.3.1: the start check asks; offline, the stored release is what is offered

		Assert.False(coordinator.Notice!.HasInstaller);
		Assert.StartsWith("https://github.com/mbustos27/fluentfin-mpv/releases/", coordinator.Notice.PageUrl);
	}

	[Fact]
	public async Task Coordinator_Dismiss_HidesThatVersionOnly()
	{
		var (settings, _) = FakeSettings(enabled: true);
		var now = new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);
		var release = "v1.2.0";
		var handler = new Fake(_ => Ok(Release(release)));
		var coordinator = new UpdateCoordinator(settings.Object, new UpdateChecker(new HttpClient(handler), "1.1.1"), () => now, "1.1.1");
		await coordinator.TickAsync();
		Assert.NotNull(coordinator.Notice);

		coordinator.Dismiss();
		Assert.Null(coordinator.Notice);
		now = now.AddHours(1);
		await coordinator.TickAsync();
		Assert.Null(coordinator.Notice);

		release = "v1.3.0";
		now = now.AddDays(2);
		await coordinator.TickAsync();
		Assert.Equal("1.3.0", coordinator.Notice!.Version);
	}

	[Fact]
	public async Task Coordinator_TurningTheSettingOff_ClearsTheNotice()
	{
		var current = "";
		var enabled = true;
		var settings = new Mock<ISettings>();
		settings.SetupGet(s => s.CheckForUpdates).Returns(() => enabled);
		settings.SetupGet(s => s.UpdateState).Returns(() => current);
		settings.SetupSet(s => s.UpdateState = It.IsAny<string>()).Callback<string>(v => current = v);
		var coordinator = new UpdateCoordinator(settings.Object, new UpdateChecker(new HttpClient(new Fake(_ => Ok(Release()))), "1.1.1"), () => DateTimeOffset.UtcNow, "1.1.1");
		await coordinator.TickAsync();
		Assert.NotNull(coordinator.Notice);

		enabled = false;
		await coordinator.TickAsync();

		Assert.Null(coordinator.Notice);
	}

	// --- the hash check
	[Theory]
	[InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef *Nixie-1.2.0-win-x64.msi\n", "Nixie-1.2.0-win-x64.msi", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
	[InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF  Nixie-1.2.0-win-x64.msi\r\n", "Nixie-1.2.0-win-x64.msi", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
	[InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\n", "Nixie-1.2.0-win-x64.msi", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
	[InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef *Other.msi\n", "Nixie-1.2.0-win-x64.msi", null)]
	[InlineData("tooshort *Nixie-1.2.0-win-x64.msi\n", "Nixie-1.2.0-win-x64.msi", null)]
	[InlineData("", "Nixie-1.2.0-win-x64.msi", null)]
	public void Sha256File_ReadsTheHashFileTheBuildWrites(string text, string name, string? expected) => Assert.Equal(expected, Sha256File.Parse(text, name));

	private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

	private static Fake Server(ReleaseInfo release, byte[] msi, string hashFileText) => new(request =>
		request.RequestUri!.ToString() == release.Sha256Url ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(hashFileText) }
		: request.RequestUri!.ToString() == release.MsiUrl ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(msi) }
		: new HttpResponseMessage(HttpStatusCode.NotFound));

	[Fact]
	public async Task Download_ChecksTheHash_AndReturnsTheInstaller()
	{
		var release = ReleaseInfo.Parse(Release())!;
		var msi = Encoding.ASCII.GetBytes("pretend this is an installer");
		var downloader = new UpdateDownloader(new HttpClient(Server(release, msi, $"{Sha(msi)} *{release.MsiName}\n")), _folder);

		var path = await downloader.DownloadAsync(release);

		Assert.Equal(Path.Combine(_folder, "Nixie-1.2.0-win-x64.msi"), path);
		Assert.Equal(msi, await File.ReadAllBytesAsync(path));
		Assert.Empty(Directory.GetFiles(_folder, "*.part"));
	}

	[Fact]
	public async Task Download_RefusesAFileServedFromAnotherHost()
	{
		var release = ReleaseInfo.Parse(Release())!;
		var msi = Encoding.ASCII.GetBytes("pretend this is an installer");
		var inner = Server(release, msi, $"{Sha(msi)} *{release.MsiName}\n");
		var redirected = new Fake(request =>
		{
			var response = inner.Respond(request);
			response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.example/a.msi");
			return response;
		});
		var downloader = new UpdateDownloader(new HttpClient(redirected), _folder);

		await Assert.ThrowsAsync<UpdateException>(() => downloader.DownloadAsync(release));

		Assert.Empty(Directory.GetFiles(_folder));
	}

	[Fact]
	public async Task Download_WithAWrongHash_DeletesTheFile_AndRunsNothing()
	{
		var release = ReleaseInfo.Parse(Release())!;
		var tampered = Encoding.ASCII.GetBytes("not the installer that was published");
		var downloader = new UpdateDownloader(new HttpClient(Server(release, tampered, $"{Sha(Encoding.ASCII.GetBytes("the real one"))} *{release.MsiName}\n")), _folder);

		await Assert.ThrowsAsync<UpdateException>(() => downloader.DownloadAsync(release));

		Assert.Empty(Directory.GetFiles(_folder));
	}

	[Fact]
	public async Task Download_RefusesAReleaseWithoutThisRepositorysInstaller()
	{
		var release = new ReleaseInfo("1.2.0", $"{Repo}/releases/tag/v1.2.0", "", "Nixie-1.2.0-win-x64.msi",
			"https://github.com/insomniachi/fluentfin/releases/download/v1.2.0/Nixie-1.2.0-win-x64.msi", "https://example.com/x.sha256");
		var handler = new Fake(_ => new HttpResponseMessage(HttpStatusCode.OK));

		await Assert.ThrowsAsync<UpdateException>(() => new UpdateDownloader(new HttpClient(handler), _folder).DownloadAsync(release));

		Assert.Empty(handler.Requests);   // nothing was fetched
	}

	// --- installed or portable
	[Theory]
	[InlineData(@"C:\Users\me\AppData\Local\Programs\Seaglass\app\", @"C:\Users\me\AppData\Local\Programs\Seaglass\", true)]
	[InlineData(@"C:\Users\me\AppData\Local\Programs\Seaglass\app", @"C:\Users\me\AppData\Local\Programs\Seaglass", true)]
	[InlineData(@"c:\users\me\appdata\local\programs\seaglass\APP\", @"C:\Users\me\AppData\Local\Programs\Seaglass\", true)]
	[InlineData(@"D:\Downloads\Seaglass-1.1.1-win-x64\app\", @"C:\Users\me\AppData\Local\Programs\Seaglass\", false)]   // portable, with an MSI copy elsewhere
	[InlineData(@"D:\Downloads\Seaglass-1.1.1-win-x64\app\", null, false)]
	[InlineData(@"D:\Downloads\Seaglass-1.1.1-win-x64\app\", "", false)]
	[InlineData(@"C:\Users\me\AppData\Local\Programs\Seaglass\app\", @"C:\Users\me\AppData\Local\Programs\Seaglass2\", false)]
	public void InstallDetector_TellsAnMsiInstallFromAPortableCopy(string appBase, string? registered, bool expected) =>
		Assert.Equal(expected, InstallDetector.IsMsiInstall(appBase, registered));

	[Fact]
	public async Task Flow_AnInstalledCopy_DownloadsAndRunsMsiexec()
	{
		var release = ReleaseInfo.Parse(Release())!;
		string? opened = null;
		System.Diagnostics.ProcessStartInfo? started = null;

		var outcome = await UpdateFlow.InstallAsync(release, isMsiInstall: true, (_, _) => Task.FromResult(@"C:\temp\Nixie-1.2.0-win-x64.msi"),
			i => started = i, url => opened = url);

		Assert.Equal(UpdateOutcome.StartedInstaller, outcome);
		Assert.Null(opened);
		Assert.Equal("msiexec.exe", started!.FileName);
		Assert.Equal(["/i", @"C:\temp\Nixie-1.2.0-win-x64.msi"], started.ArgumentList);
	}

	[Fact]
	public async Task Flow_AnInstalledCopy_ClosesTheAppAfterStartingTheInstaller_AndOnlyThen()
	{
		var release = ReleaseInfo.Parse(Release())!;
		var steps = new List<string>();

		await UpdateFlow.InstallAsync(release, isMsiInstall: true, (_, _) => Task.FromResult(@"C:	emp\Nixie-1.2.0-win-x64.msi"),
			_ => steps.Add("start"), _ => steps.Add("page"), closeApp: () => steps.Add("close"));
		Assert.Equal(["start", "close"], steps);

		steps.Clear();
		await UpdateFlow.InstallAsync(release, isMsiInstall: false, (_, _) => Task.FromResult("x.msi"),
			_ => steps.Add("start"), _ => steps.Add("page"), closeApp: () => steps.Add("close"));
		Assert.Equal(["page"], steps);                      // a portable copy keeps running

		steps.Clear();
		await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateFlow.InstallAsync(release, isMsiInstall: true, (_, _) => throw new InvalidOperationException(),
			_ => steps.Add("start"), _ => steps.Add("page"), closeApp: () => steps.Add("close")));
		Assert.Empty(steps);                                // a failed download closes nothing
	}

	[Fact]
	public async Task Flow_APortableCopy_OpensTheReleasePageAndInstallsNothing()
	{
		var release = ReleaseInfo.Parse(Release())!;
		string? opened = null;
		var downloaded = false;
		var ran = false;

		var outcome = await UpdateFlow.InstallAsync(release, isMsiInstall: false, (_, _) => { downloaded = true; return Task.FromResult("x.msi"); },
			_ => ran = true, url => opened = url);

		Assert.Equal(UpdateOutcome.OpenedReleasePage, outcome);
		Assert.Equal($"{Repo}/releases/tag/v1.2.0", opened);
		Assert.False(downloaded);
		Assert.False(ran);
	}

	[Fact]
	public async Task Flow_AReleaseWithoutAnInstaller_OpensTheReleasePageEvenWhenInstalled()
	{
		var release = ReleaseInfo.Parse(Release(assets: false))!;
		string? opened = null;

		var outcome = await UpdateFlow.InstallAsync(release, isMsiInstall: true, (_, _) => throw new InvalidOperationException("no download"), _ => throw new InvalidOperationException("no run"), url => opened = url);

		Assert.Equal(UpdateOutcome.OpenedReleasePage, outcome);
		Assert.NotNull(opened);
	}

	[Fact]
	public void MsiexecInstall_OnlyRunsAnInstaller() => Assert.Throws<UpdateException>(() => UpdateFlow.MsiexecInstall(@"C:\temp\evil.exe"));
}
