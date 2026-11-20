using System.Diagnostics;
using System.Text.RegularExpressions;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.4.0 (bug hunt m2: the release candidate's FluentFin.Tests ran 1406 of 1413 tests; the 7 libmpv probe tests were
/// skipped, and the release script let it through): tools\release\build-release.ps1 runs FluentFin.Tests with the release's
/// libmpv folder next to the tests and FLUENTFIN_MPV_REQUIRE_LIBMPV=1, and stops on a results file with any test not run.
/// </summary>
public sealed class ReleaseGateTests
{
	private static string Script() => File.ReadAllText(AppVersionTests.FindUp(Path.Combine("tools", "release", "build-release.ps1"))).Replace("\r\n", "\n");

	[Fact]
	public void TheReleasesUiTests_GetItsLibmpvFolder_AndMustRunTheLibmpvTests()
	{
		var script = Script();
		var tests = script[script.IndexOf("Step 'tests (UI suites, backend bridge tests)'", StringComparison.Ordinal)..];
		tests = tests[..tests.IndexOf("$mpvCmd = Get-Command", StringComparison.Ordinal)];
		Assert.Contains("Invoke-UiTests (Join-Path $repo 'ui\\FluentFin.Tests') @('-p:Platform=x64', \"-p:LibmpvDir=$LibmpvDir\")", tests);
		Assert.Contains("$env:FLUENTFIN_MPV_REQUIRE_LIBMPV = '1'", tests);
		// set before the suite runs, put back after it
		Assert.True(tests.IndexOf("$env:FLUENTFIN_MPV_REQUIRE_LIBMPV = '1'", StringComparison.Ordinal) < tests.IndexOf("ui\\FluentFin.Tests')", StringComparison.Ordinal));
		Assert.Contains("finally { $env:FLUENTFIN_MPV_REQUIRE_LIBMPV = $savedRequire }", tests);

		// the suites are checked for tests not run, whatever their exit code
		var run = script[script.IndexOf("function Invoke-UiTests", StringComparison.Ordinal)..];
		Assert.True(run.IndexOf("Get-NotExecuted $trx", StringComparison.Ordinal) < run.IndexOf("if ($code -eq 0) { return }", StringComparison.Ordinal));
	}

	[Fact]
	public void ATestNotRun_IsNamed_AndNoneWhenAllRan()
	{
		var function = Regex.Match(Script(), @"function Get-NotExecuted\(\[string\]\$trx\) \{\n.*?\n\}\n", RegexOptions.Singleline).Value;
		Assert.NotEmpty(function);

		var dir = Directory.CreateTempSubdirectory("nixie-release-gate-");
		try
		{
			var skipped = Path.Combine(dir.FullName, "skipped.trx");
			File.WriteAllText(skipped, Trx(("A.B.Passes", "Passed"), ("Mpv.LibmpvProbeTests.TheRealProbe_OfTheVendoredLibmpv_Passes", "NotExecuted")));
			var all = Path.Combine(dir.FullName, "all.trx");
			File.WriteAllText(all, Trx(("A.B.Passes", "Passed"), ("A.B.Other", "Passed")));
			// only the counters say so (a results file without the result rows)
			var counted = Path.Combine(dir.FullName, "counted.trx");
			File.WriteAllText(counted, Trx().Replace("notExecuted=\"0\"", "notExecuted=\"7\""));

			Assert.Equal("Mpv.LibmpvProbeTests.TheRealProbe_OfTheVendoredLibmpv_Passes", RunPowerShell(function, skipped));
			Assert.Equal("", RunPowerShell(function, all));
			Assert.Equal("7 test(s) the counters name NotExecuted", RunPowerShell(function, counted));
		}
		finally
		{
			dir.Delete(recursive: true);
		}
	}

	private static string Trx(params (string Name, string Outcome)[] results)
	{
		var rows = string.Concat(results.Select(r => $"<UnitTestResult testName=\"{r.Name}\" outcome=\"{r.Outcome}\" />"));
		var notExecuted = results.Count(r => r.Outcome == "NotExecuted");
		return "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
			+ "<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\">"
			+ $"<Results>{rows}</Results>"
			+ $"<ResultSummary outcome=\"Completed\"><Counters total=\"{results.Length}\" executed=\"{results.Length - notExecuted}\" notExecuted=\"{notExecuted}\" /></ResultSummary>"
			+ "</TestRun>";
	}

	private static string RunPowerShell(string function, string trx)
	{
		var script = Path.Combine(Path.GetDirectoryName(trx)!, "gate.ps1");
		File.WriteAllText(script, "param([string]$Trx)\n$ErrorActionPreference = 'Stop'\nSet-StrictMode -Version 3\n" + function + "@(Get-NotExecuted $Trx) -join '|'\n");
		var start = new ProcessStartInfo("powershell.exe")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Trx", trx })
		{
			start.ArgumentList.Add(arg);
		}

		using var process = Process.Start(start)!;
		var output = process.StandardOutput.ReadToEnd();
		var error = process.StandardError.ReadToEnd();
		Assert.True(process.WaitForExit(60_000), "powershell did not end");
		Assert.True(process.ExitCode == 0, error);
		return output.Trim();
	}
}
