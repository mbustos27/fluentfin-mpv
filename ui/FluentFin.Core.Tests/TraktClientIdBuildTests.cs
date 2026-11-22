using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentFin.Core.Outside;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.7 trending: the Trakt Client ID is built into a release at build time (tools\release\build-release.ps1 with
/// trakt-client-id.ps1, FluentFin.csproj) and never printed or committed. The helper runs here in a PowerShell of its own
/// with a made-up value; the real one (set on the maintainer's PC) is only ever compared, never shown.
/// </summary>
public sealed class TraktClientIdBuildTests
{
	private const string Fake = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
	private const string Other = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

	private static readonly string[] Variables = ["NIXIE_TRAKT_CLIENT_ID", "NIXIE_LIVE_TRAKT_CLIENT_ID", "FluentFinTraktClientId", "NIXIE_GATE_TRAKT_CLIENT_ID"];

	[Fact]
	public void TheReleaseVariable_WinsOverTheLiveChecks_AndOnlyItsNameIsPrinted()
	{
		var output = RunHelper("$f = Get-NixieTraktClientId -Scopes Process; Format-NixieTraktClientIdNote $f; \"value-ok=$($f.Value -eq '" + Fake + "')\"",
			("NIXIE_TRAKT_CLIENT_ID", Fake), ("NIXIE_LIVE_TRAKT_CLIENT_ID", Other));

		Assert.Contains("Trakt: Production ID (NIXIE_TRAKT_CLIENT_ID)", output);
		Assert.DoesNotContain("fallback", output);
		Assert.Contains("value-ok=True", output);
		Assert.DoesNotContain(Fake, output);
		Assert.DoesNotContain(Other, output);
	}

	[Fact]
	public void TheLiveChecksVariable_IsTheFallback()
	{
		var output = RunHelper("$f = Get-NixieTraktClientId -Scopes Process; Format-NixieTraktClientIdNote $f; \"value-ok=$($f.Value -eq '" + Other + "')\"",
			("NIXIE_LIVE_TRAKT_CLIENT_ID", "  " + Other + "  "));

		// the review's #7: the fallback to the live check's Development app is said loudly
		Assert.Contains("Trakt: Development ID (fallback): NIXIE_TRAKT_CLIENT_ID is not set", output);
		Assert.Contains("(NIXIE_LIVE_TRAKT_CLIENT_ID)", output);
		Assert.Contains("-RequireProductionTraktId", output);
		Assert.Contains("value-ok=True", output);
		Assert.DoesNotContain(Other, output);
	}

	[Fact]
	public void WithoutOne_TheBuildSaysTraktIsNotAvailable()
	{
		var output = RunHelper("$f = Get-NixieTraktClientId -Scopes Process; Format-NixieTraktClientIdNote $f; \"none=$($null -eq $f.Value)\"");

		Assert.Contains("Trakt Client ID: not found (NIXIE_TRAKT_CLIENT_ID, NIXIE_LIVE_TRAKT_CLIENT_ID)", output);
		Assert.Contains("'Not available in this build'", output);
		Assert.Contains("none=True", output);
		Assert.Equal("Not available in this build", TraktClientId.NotAvailable);
	}

	[Theory]
	[InlineData("production", true, "passed")]
	[InlineData("production", false, "passed")]
	[InlineData("development", false, "passed")]
	[InlineData("development", true, "refused: -RequireProductionTraktId: the release needs the Production Trakt Client ID in NIXIE_TRAKT_CLIENT_ID (only the Development fallback in NIXIE_LIVE_TRAKT_CLIENT_ID was found)")]
	[InlineData("none", false, "passed")]
	[InlineData("none", true, "refused: -RequireProductionTraktId: the release needs the Production Trakt Client ID in NIXIE_TRAKT_CLIENT_ID (none was found)")]
	public void RequireProductionTraktId_RefusesTheDevelopmentFallback_AndNone_NeverNamingTheValue(string have, bool require, string said)
	{
		var env = have switch
		{
			"production" => new[] { ("NIXIE_TRAKT_CLIENT_ID", Fake), ("NIXIE_LIVE_TRAKT_CLIENT_ID", Other) },
			"development" => [("NIXIE_LIVE_TRAKT_CLIENT_ID", Other)],
			_ => [],
		};
		var output = RunHelper("$f = Get-NixieTraktClientId -Scopes Process; try { Assert-NixieTraktClientId $f $" + (require ? "true" : "false") + "; 'passed' } catch { \"refused: $($_.Exception.Message)\" }", env);

		Assert.Equal(said, output.Trim());
		Assert.DoesNotContain(Fake, output);
		Assert.DoesNotContain(Other, output);
	}

	[Theory]
	[InlineData("abc;def0123456789abcdef")]   // MSBuild's item separator
	[InlineData("$(Secret)0123456789abcdef")] // an MSBuild property
	[InlineData("short")]
	public void AValueNotShapedLikeAClientId_IsLeftOut_AndNotPrinted(string value)
	{
		var output = RunHelper("$f = Get-NixieTraktClientId -Scopes Process; Format-NixieTraktClientIdNote $f; \"none=$($null -eq $f.Value)\"",
			("NIXIE_TRAKT_CLIENT_ID", value));

		Assert.Contains("NIXIE_TRAKT_CLIENT_ID is not shaped like a Client ID", output);
		Assert.Contains("none=True", output);
		Assert.DoesNotContain(value, output);
	}

	[Fact]
	public void ThePublishVariable_IsSetForTheBlockAlone_AndTakenAwayAfter_EvenOnAFailure()
	{
		var output = RunHelper("""
			$f = Get-NixieTraktClientId -Scopes Process
			Invoke-WithNixieTraktClientId $f { "inside=$($env:FluentFinTraktClientId -eq $f.Value)" }
			"after=$($null -eq $env:FluentFinTraktClientId)"
			try { Invoke-WithNixieTraktClientId $f { throw 'publish failed' } } catch { "caught" }
			"after-failure=$($null -eq $env:FluentFinTraktClientId)"
			$none = Get-NixieTraktClientId -Scopes @()
			Invoke-WithNixieTraktClientId $none { "inside-none=$($null -eq $env:FluentFinTraktClientId)" }
			""", ("NIXIE_TRAKT_CLIENT_ID", Fake));

		Assert.Contains("inside=True", output);
		Assert.Contains("after=True", output);
		Assert.Contains("caught", output);
		Assert.Contains("after-failure=True", output);
		Assert.Contains("inside-none=True", output);
		Assert.DoesNotContain(Fake, output);
	}

	[Fact]
	public void ThePublishsGeneratedCopies_AreDeleted_AndNothingElse()
	{
		var dir = Directory.CreateTempSubdirectory("nixie-trakt-copies-");
		try
		{
			var obj = Directory.CreateDirectory(Path.Combine(dir.FullName, "obj", "FluentFin", "release_win-x64")).FullName;
			File.WriteAllText(Path.Combine(obj, "Nixie.AssemblyInfo.cs"), $"[assembly: System.Reflection.AssemblyMetadata(\"NixieTraktClientId\", \"{Fake}\")]");
			File.WriteAllText(Path.Combine(obj, "Other.AssemblyInfo.cs"), "[assembly: System.Reflection.AssemblyTitle(\"Other\")]");
			File.WriteAllText(Path.Combine(obj, "project.assets.json"), "{}");

			var output = RunHelper($"$f = Get-NixieTraktClientId -Scopes Process; \"removed=$(Remove-NixieTraktClientIdCopies '{dir.FullName}' $f)\"", ("NIXIE_TRAKT_CLIENT_ID", Fake));

			Assert.Contains("removed=1", output);
			Assert.False(File.Exists(Path.Combine(obj, "Nixie.AssemblyInfo.cs")));
			Assert.True(File.Exists(Path.Combine(obj, "Other.AssemblyInfo.cs")));
			Assert.True(File.Exists(Path.Combine(obj, "project.assets.json")));
		}
		finally
		{
			dir.Delete(recursive: true);
		}
	}

	[Fact]
	public void TheReleaseScript_PassesItInTheEnvironment_NeverOnACommandLine_AndGatesThePackage()
	{
		var script = File.ReadAllText(Path.Combine(Tools, "release", "build-release.ps1")).Replace("\r\n", "\n");

		Assert.Contains(". (Join-Path $PSScriptRoot 'trakt-client-id.ps1')", script);
		// the Development fallback is a warning; -RequireProductionTraktId stops the build before any download or build step
		Assert.Contains("if ($trakt.Value -and -not $trakt.Production) { Write-Warning (Format-NixieTraktClientIdNote $trakt) } else { Write-Host (Format-NixieTraktClientIdNote $trakt) }", script);
		Assert.Contains("[switch]$RequireProductionTraktId", script);
		var assertAt = script.IndexOf("Assert-NixieTraktClientId $trakt $RequireProductionTraktId", StringComparison.Ordinal);
		Assert.True(assertAt > 0 && assertAt < script.IndexOf("# --- 1. Python runtime", StringComparison.Ordinal));
		// the publish runs inside the helper that sets and takes away the variable
		var publish = script[script.IndexOf("Invoke-WithNixieTraktClientId $trakt {", StringComparison.Ordinal)..];
		Assert.True(publish.IndexOf("& $dotnet publish", StringComparison.Ordinal) < publish.IndexOf("} }", StringComparison.Ordinal));
		// never a command-line property, never a needle on path_gate's command line, never printed
		Assert.DoesNotMatch(@"[-/]p(roperty)?:FluentFinTraktClientId", script);
		Assert.DoesNotMatch(@"--needle'?,?\s*\$trakt", script);
		// every use of the value is a test of whether there is one, or the gate's environment variable: nothing else
		var uses = script.Split('\n').Where(line => line.Contains("$trakt.Value", StringComparison.Ordinal)).ToList();
		Assert.NotEmpty(uses);
		foreach (var line in uses)
		{
			var rest = line.Replace("if ($trakt.Value)", "").Replace("if ($trakt.Value -and", "").Replace("if (-not $trakt.Value", "")
				.Replace("SetEnvironmentVariable($NixieTraktGateVariable, $trakt.Value, 'Process')", "");
			Assert.False(rest.Contains("$trakt.Value", StringComparison.Ordinal), "the Client ID used beyond a test: " + line.Trim());
		}

		// the gate: the value from the environment, allowed and expected in Nixie.dll only
		Assert.Contains("'--secret-env', $NixieTraktGateVariable", script);
		Assert.Contains("'--allow-secret-in', 'app\\Nixie.dll', '--expect-secret-in', 'app\\Nixie.dll'", script);
		Assert.Contains("finally { [Environment]::SetEnvironmentVariable($NixieTraktGateVariable, $null, 'Process') }", script);

		var helper = File.ReadAllText(Path.Combine(Tools, "release", "trakt-client-id.ps1"));
		Assert.DoesNotMatch(@"Write-(Host|Output|Warning|Verbose|Information)", helper);
		Assert.DoesNotMatch(@"""[^""\n]*\$\(?\$(found\.Value|value|raw)\b", helper);
		Assert.Contains("$NixieTraktPublishVariable = 'FluentFinTraktClientId'", helper);
	}

	[Fact]
	public void TheProject_WritesTheMetadata_InAReleaseBuildOnly_UnderTheKeyTheAppReads()
	{
		var project = XDocument.Load(Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "FluentFin", "FluentFin.csproj"));
		var item = Assert.Single(project.Descendants("AssemblyMetadata"), x => (string?)x.Attribute("Include") == TraktClientId.MetadataKey);

		Assert.Equal("$(FluentFinTraktClientId)", (string?)item.Attribute("Value"));
		var condition = (string?)item.Parent!.Attribute("Condition") ?? "";
		Assert.Contains("'$(FluentFinMpvRelease)' == 'true'", condition);
		Assert.Contains("'$(FluentFinTraktClientId)' != ''", condition);
	}

	[Fact]
	public void TheApp_ReadsItFromItsMetadata_AndTheHarnessHasItsOwn()
	{
		Assert.Null(TraktClientId.Resolve(harness: false, []));
		Assert.Null(TraktClientId.Resolve(harness: false, [new AssemblyMetadataAttribute(TraktClientId.MetadataKey, "  ")]));
		Assert.Equal("abc", TraktClientId.Resolve(harness: false, [new AssemblyMetadataAttribute("Other", "x"), new AssemblyMetadataAttribute(TraktClientId.MetadataKey, " abc ")]));
		Assert.Equal(TraktClientId.HarnessValue, TraktClientId.Resolve(harness: true, [new AssemblyMetadataAttribute(TraktClientId.MetadataKey, "abc")]));
		// a test run is no release: it carries none
		Assert.Null(TraktClientId.Current(harness: false));
	}

	/// <summary>
	/// "Never committed": when this PC has the real Client ID (the maintainer's), no tracked file holds it. Only file names are
	/// reported. Skipped where neither variable is set or git isn't there.
	/// </summary>
	[Fact]
	public void NoTrackedFile_HoldsTheRealClientId()
	{
		var values = Variables[..2]
			.SelectMany(name => new[] { Environment.GetEnvironmentVariable(name), Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) })
			.Select(v => v?.Trim()).Where(v => !string.IsNullOrEmpty(v)).Distinct().ToList();
		if (values.Count == 0 || Git("ls-files", "-z") is not { } listed)
		{
			return;
		}

		var root = Path.GetDirectoryName(HomeAndLibraryXamlTests.RepoRoot)!;
		var holding = new List<string>();
		foreach (var relative in listed.Split('\0', StringSplitOptions.RemoveEmptyEntries))
		{
			var path = Path.Combine(root, relative);
			if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024 * 1024)
			{
				continue;
			}

			var bytes = File.ReadAllBytes(path);
			var text = System.Text.Encoding.Latin1.GetString(bytes);
			var wide = System.Text.Encoding.Unicode.GetString(bytes);
			if (values.Any(v => text.Contains(v!, StringComparison.OrdinalIgnoreCase) || wide.Contains(v!, StringComparison.OrdinalIgnoreCase)))
			{
				holding.Add(relative);
			}
		}

		Assert.True(holding.Count == 0, "tracked files holding the Trakt Client ID (value not shown): " + string.Join(", ", holding));
	}

	private static string Tools => Path.Combine(Path.GetDirectoryName(HomeAndLibraryXamlTests.RepoRoot)!, "tools");

	private static string? Git(params string[] args)
	{
		try
		{
			var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = HomeAndLibraryXamlTests.RepoRoot };
			foreach (var arg in args)
			{
				start.ArgumentList.Add(arg);
			}

			using var git = Process.Start(start)!;
			var output = git.StandardOutput.ReadToEnd();
			git.StandardError.ReadToEnd();
			return git.WaitForExit(60_000) && git.ExitCode == 0 ? output : null;
		}
		catch (System.ComponentModel.Win32Exception)
		{
			return null;
		}
	}

	/// <summary>
	/// The helper dot-sourced in a Windows PowerShell of its own, with none of the Client ID variables but <paramref name="env"/>
	/// (the test process inherits the maintainer's real one: it is taken out first); its whole output, stdout and stderr.
	/// </summary>
	private static string RunHelper(string body, params (string Name, string Value)[] env)
	{
		var dir = Directory.CreateTempSubdirectory("nixie-trakt-helper-");
		try
		{
			var script = Path.Combine(dir.FullName, "run.ps1");
			File.WriteAllText(script, "$ErrorActionPreference = 'Stop'\nSet-StrictMode -Version 3\n. '" + Path.Combine(Tools, "release", "trakt-client-id.ps1").Replace("'", "''") + "'\n" + body + "\n");
			var start = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
			foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
			{
				start.ArgumentList.Add(arg);
			}

			foreach (var name in Variables)
			{
				start.Environment.Remove(name);
			}

			foreach (var (name, value) in env)
			{
				start.Environment[name] = value;
			}

			using var process = Process.Start(start)!;
			var stdout = process.StandardOutput.ReadToEndAsync();
			var stderr = process.StandardError.ReadToEnd();
			Assert.True(process.WaitForExit(60_000), "powershell did not end");
			var all = stdout.Result + stderr;
			Assert.True(process.ExitCode == 0, "the helper failed: " + Redact(all, env));
			return all;
		}
		finally
		{
			dir.Delete(recursive: true);
		}
	}

	private static string Redact(string text, (string Name, string Value)[] env) =>
		env.Aggregate(text, (t, e) => e.Value.Length > 0 ? t.Replace(e.Value, "<value>") : t);
}
