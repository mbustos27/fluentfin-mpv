using System.Net;
using FluentFin.Core.Outside;
using static FluentFin.Core.Tests.Outside.OutsideKit;

namespace FluentFin.Core.Tests.Outside;

/// <summary>
/// 1.7 keyless (review #2): a harness run never reaches a real host, on every way the harness launches the app. run-offscreen.ps1
/// sets FLUENTFIN_MPV_GLASS_ALWAYS_ACTIVE=1 unless -GlassFollowsActivity; drive.ps1 sets NIXIE_OUTSIDE_BASE either way. Both
/// launch environments go through the app's own routing (<see cref="OutsideRouting.FromEnvironment"/>, which
/// OutsideHubFactory calls) and the real loaders. Redirects are checked before they are followed.
/// </summary>
public sealed class OutsideHarnessLaunchTests
{
	private const string Fake = "http://127.0.0.1:5123";

	/// <summary>The app's environment as run-offscreen.ps1 and drive.ps1 start it.</summary>
	private static Func<string, string?> Launch(bool glassFollowsActivity) => name => name switch
	{
		"NIXIE_OUTSIDE_BASE" => Fake,
		OutsideRouting.GlassAlwaysActiveVariable => glassFollowsActivity ? null : "1",
		_ => null,
	};

	[Theory]
	[InlineData(false)]
	[InlineData(true)] // -GlassFollowsActivity: the glass variable unset, NIXIE_OUTSIDE_BASE still set
	public async Task Both_harness_launches_send_every_source_to_the_fake_and_never_a_real_host(bool glassFollowsActivity)
	{
		var routing = OutsideRouting.FromEnvironment(Launch(glassFollowsActivity), releasePackage: false);
		var clock = new TestClock(Noon);
		var recorder = new OutsideRequestRecorder(new FuncHandler(_ => Answer(HttpStatusCode.NotFound))) { AllowHost = uri => uri.IsLoopback };
		var store = new MemoryStore();
		var hub = new OutsideHub(new HttpClient(recorder), routing, "1.7.0", new OutsideCache(TempFolder(), clock.Func), store, clock.Func);

		Assert.Equal(new Uri(Fake + "/"), routing.HarnessOrigin);
		await TmdbExports.LoadAsync(hub, TmdbExports.Collections, clock.Func);
		await WikidataLists.LoadAsync(hub, WikidataLists.MainEndpoint);
		await WikidataLists.LoadAsync(hub, new Uri("https://sparql.example.org/sparql"));

		Assert.NotEmpty(recorder.Requests);
		Assert.All(recorder.Requests, r => Assert.StartsWith(Fake + "/__harness/outside/", r.Url.AbsoluteUri, StringComparison.Ordinal));
	}

	[Fact]
	public void The_glass_variable_alone_still_never_reaches_a_real_host()
	{
		var routing = OutsideRouting.FromEnvironment(name => name == OutsideRouting.GlassAlwaysActiveVariable ? "1" : null, releasePackage: false);

		Assert.Equal(OutsideRouting.ClosedOrigin, routing.HarnessOrigin);
	}

	[Fact]
	public void A_normal_run_and_a_release_package_ask_the_real_hosts()
	{
		Assert.Same(OutsideRouting.Real, OutsideRouting.FromEnvironment(_ => null, releasePackage: false));
		Assert.Same(OutsideRouting.Real, OutsideRouting.FromEnvironment(Launch(glassFollowsActivity: false), releasePackage: true));
		Assert.Same(OutsideRouting.Real, OutsideRouting.FromEnvironment(Launch(glassFollowsActivity: true), releasePackage: true));
	}

	[Fact]
	public void The_scripts_and_the_app_agree_on_how_a_harness_run_is_told()
	{
		var drive = File.ReadAllLines(Path.Combine(RepoRoot, "tools", "ui-harness", "offscreen", "drive.ps1"));
		var set = Array.FindIndex(drive, l => l.Trim() == "$env:NIXIE_OUTSIDE_BASE = $script:serverUrl");
		var start = Array.FindIndex(drive, l => l.Trim() == "Start-App 'window_ms'");
		Assert.True(set >= 0 && start > set, "drive.ps1 sets NIXIE_OUTSIDE_BASE before it starts the app");
		Assert.Equal(Indent(drive[start]), Indent(drive[set])); // in the same block: not under an if
		Assert.Contains("'FLUENTFIN_MPV_GLASS_ALWAYS_ACTIVE=1'", File.ReadAllText(Path.Combine(RepoRoot, "tools", "ui-harness", "run-offscreen.ps1")), StringComparison.Ordinal);
		Assert.Contains($"AlwaysActiveVariable = \"{OutsideRouting.GlassAlwaysActiveVariable}\"", File.ReadAllText(Path.Combine(RepoRoot, "ui", "FluentFin", "Services", "GlassActivity.cs")), StringComparison.Ordinal);
		Assert.Contains("OutsideRouting.FromEnvironment(Environment.GetEnvironmentVariable, AppVersion.IsReleasePackage)", File.ReadAllText(Path.Combine(RepoRoot, "ui", "FluentFin", "Services", "OutsideHubFactory.cs")), StringComparison.Ordinal);
	}

	private static int Indent(string line) => line.Length - line.TrimStart().Length;

	// ---------- redirects: checked before they are followed

	[Fact]
	public void The_apps_client_follows_no_redirect_by_itself() => Assert.False(OutsideHub.CreateHandler().AllowAutoRedirect);

	private static HttpResponseMessage Redirect(string to)
	{
		var response = new HttpResponseMessage(HttpStatusCode.Found) { Content = new StringContent("") };
		response.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
		return response;
	}

	[Fact]
	public async Task In_a_harness_run_a_redirect_to_a_real_host_is_not_followed()
	{
		var recorder = new OutsideRequestRecorder(new FuncHandler(_ => Redirect("https://query.wikidata.org/sparql?query=x")));
		var source = Source(recorder, new TestClock(Noon), routing: OutsideRouting.FromEnvironment(Launch(glassFollowsActivity: true), releasePackage: false));

		var result = await source.GetAsync(new Uri("https://query.wikidata.org/sparql?query=x"));

		Assert.Equal(OutsideOutcome.Blocked, result.Outcome);
		Assert.StartsWith("Nixie didn't ask Wikidata: it was sent on to somewhere else", result.Message, StringComparison.Ordinal);
		var only = Assert.Single(recorder.Requests);
		Assert.True(only.Url.IsLoopback);
	}

	[Fact]
	public async Task In_a_real_run_an_https_redirect_is_followed_with_the_user_agent_and_without_a_key_on_another_host()
	{
		var recorder = new OutsideRequestRecorder(new FuncHandler(r => r.RequestUri!.Host == "files.tmdb.org"
			? Redirect("https://cdn.example.org/p/exports/x.json.gz")
			: Answer(HttpStatusCode.OK, "ok")));
		var source = Source(recorder, new TestClock(Noon), OutsideSources.TmdbExports);

		var result = await source.GetAsync(new Uri("https://files.tmdb.org/p/exports/x.json.gz"), new Dictionary<string, string> { ["Authorization"] = "Bearer secret" });

		Assert.True(result.Ok);
		Assert.Equal(["files.tmdb.org", "cdn.example.org"], recorder.Requests.Select(r => r.Url.Host));
		Assert.All(recorder.Requests, r => Assert.Equal(source.UserAgent, string.Join(" ", r.Headers.Where(h => h.Key == "User-Agent").Select(h => h.Value))));
		Assert.Contains(recorder.Requests[0].Headers, h => h.Key == "Authorization");
		Assert.DoesNotContain(recorder.Requests[1].Headers, h => h.Key == "Authorization");
	}

	[Theory]
	[InlineData("http://cdn.example.org/x")]
	[InlineData("https://user:pw@cdn.example.org/x")]
	public async Task In_a_real_run_a_redirect_off_https_or_with_credentials_is_not_followed(string to)
	{
		var recorder = new OutsideRequestRecorder(new FuncHandler(_ => Redirect(to)));
		var source = Source(recorder, new TestClock(Noon), OutsideSources.TmdbExports);

		var result = await source.GetAsync(new Uri("https://files.tmdb.org/p/exports/x.json.gz"));

		Assert.Equal(OutsideOutcome.Blocked, result.Outcome);
		Assert.Single(recorder.Requests);
	}

	[Fact]
	public async Task A_relative_redirect_is_followed_and_a_loop_stops_after_three()
	{
		var hops = 0;
		var recorder = new OutsideRequestRecorder(new FuncHandler(_ => ++hops == 1 ? Redirect("/moved") : Answer(HttpStatusCode.OK, "ok")));
		var source = Source(recorder, new TestClock(Noon), OutsideSources.TmdbExports);
		Assert.True((await source.GetAsync(new Uri("https://files.tmdb.org/p/a"))).Ok);
		Assert.Equal("https://files.tmdb.org/moved", recorder.Requests[^1].Url.AbsoluteUri);

		var looping = new OutsideRequestRecorder(new FuncHandler(r => Redirect(r.RequestUri!.AbsoluteUri + "x")));
		var loop = await Source(looping, new TestClock(Noon), OutsideSources.TmdbExports).GetAsync(new Uri("https://files.tmdb.org/p/a"));
		Assert.Equal(OutsideOutcome.Blocked, loop.Outcome);
		Assert.Equal(1 + OutsideSource.MostRedirects, looping.Requests.Count);
	}

	[Fact]
	public void Every_harness_script_that_turns_an_outside_source_on_checks_first_that_the_app_sends_it_to_the_fake()
	{
		// BH-170-q1 (the 1.7.0 bug hunt): then-170-keyless turned its switches on with no harness-mode check, unlike
		// then-170-trending; against a release package (which ignores NIXIE_OUTSIDE_BASE) it would send 14 real requests.
		// Every script that touches an outside switch reads the app's "harness run" log line before its first switch.
		var folder = Path.Combine(RepoRoot, "tools", "ui-harness", "offscreen");
		var switches = new System.Text.RegularExpressions.Regex(@"TmdbExportsSwitch|WikidataSwitch|TT-Switch '|TraktSwitch|TmdbTrendingSwitch|LookUp(Trakt|Wikidata|TmdbExports|TmdbTrending)");
		const string Guard = @"\[outside\] harness run: every outside source goes to";
		var checkedScripts = 0;
		foreach (var path in Directory.GetFiles(folder, "then-*.ps1"))
		{
			var text = File.ReadAllText(path);
			var first = switches.Match(text);
			if (!first.Success)
			{
				continue;
			}

			checkedScripts++;
			var guard = text.IndexOf(Guard, StringComparison.Ordinal);
			Assert.True(guard >= 0, $"{Path.GetFileName(path)} turns an outside source on without checking the app's harness-run line");
			Assert.True(guard < first.Index, $"{Path.GetFileName(path)} touches an outside switch ({first.Value}) before its harness-mode check");
		}

		Assert.True(checkedScripts >= 2, $"only {checkedScripts} scripts touch an outside switch: the pattern no longer finds them");
	}

	[Fact]
	public void The_release_build_leaves_out_only_the_opt_in_live_check_and_still_runs_every_other_test()
	{
		// 1.7.0's integration: the release build stops on any test not run (Nixie 1.4.0, bug hunt m2), and the live check of
		// TMDb's export files and Wikidata is skipped unless asked for, so the release build refused 1.7.0. It filters that
		// one category out of the Core run instead, and only it: the trait sits on the live check alone.
		var script = File.ReadAllText(Path.Combine(RepoRoot, "tools", "release", "build-release.ps1"));
		// 1.7.1: the library index's opt-in timing on a local Jellyfin (LiveIndexTests) is the second category left out; 1.7.5
		// (BH-170-q2), the live check of Trakt and TMDb trending through the app's own requests (LiveTrendingSourcesTests) the third
		Assert.Contains(@"Invoke-UiTests (Join-Path $repo 'ui\FluentFin.Core.Tests') @() -Exclude 'Category!=LiveOutside&Category!=LiveIndex&Category!=LiveTrending'", script, StringComparison.Ordinal);
		Assert.Contains("$out = @(& $dotnet test $project @extra @excludeArgs ", script, StringComparison.Ordinal);
		Assert.Contains("if ($Exclude) { $filter = \"($filter)&($Exclude)\" }", script, StringComparison.Ordinal);
		Assert.DoesNotContain("NIXIE_LIVE_OUTSIDE", script, StringComparison.Ordinal);   // the release never turns the live check on
		Assert.DoesNotContain("NIXIE_LIVE_INDEX=", script, StringComparison.Ordinal);    // nor the index timing
		Assert.DoesNotContain("$env:NIXIE_LIVE_INDEX", script, StringComparison.Ordinal);
		Assert.DoesNotContain("$env:NIXIE_LIVE_TRENDING", script, StringComparison.Ordinal); // 1.7.5: nor the trending check

		static Func<Type, bool> Live(string category) => t => t.GetCustomAttributesData().Any(a => a.AttributeType == typeof(TraitAttribute)
			&& a.ConstructorArguments.Select(x => x.Value as string).SequenceEqual(["Category", category]));
		var assembly = typeof(OutsideHarnessLaunchTests).Assembly;
		Assert.Equal([typeof(LiveOutsideSourcesTests)], assembly.GetTypes().Where(Live("LiveOutside")).ToList());
		Assert.Equal([typeof(LiveIndexTests)], assembly.GetTypes().Where(Live("LiveIndex")).ToList());
		Assert.Equal([typeof(LiveTrendingSourcesTests)], assembly.GetTypes().Where(Live("LiveTrending")).ToList());

		// every test the release can't run (an opt-in Skip) sits in one of the three categories left out, so none is "not run"
		var optIn = assembly.GetTypes().Where(t => t.GetMethods().Any(m => m.GetCustomAttributes(typeof(FactAttribute), true)
			.Cast<FactAttribute>().Any(f => !string.IsNullOrEmpty(f.Skip)))).ToList();
		Assert.All(optIn, t => Assert.True(Live("LiveOutside")(t) || Live("LiveIndex")(t) || Live("LiveTrending")(t),
			$"{t.Name} has a test skipped unless asked for, outside the categories the release leaves out: the release build would refuse it"));
	}

	private static string RepoRoot { get; } = FindRepoRoot();

	private static string FindRepoRoot()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ui", "FluentFin.sln")))
		{
			dir = dir.Parent;
		}

		return dir?.FullName ?? throw new DirectoryNotFoundException("the repository above " + AppContext.BaseDirectory);
	}
}
