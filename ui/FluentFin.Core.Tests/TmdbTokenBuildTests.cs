using System.Diagnostics;
using System.Reflection;
using System.Xml.Linq;
using FluentFin.Core.Outside;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.7 trending (the owner, 2026-11-22): Nixie's own TMDb token is built into a release at build time
/// (tools\release\build-release.ps1 with tmdb-token.ps1, FluentFin.csproj), as the Trakt Client ID is, and never printed or
/// committed. The helper runs here in a PowerShell of its own with a made-up token; a real one is only ever compared.
/// </summary>
public sealed class TmdbTokenBuildTests
{
	private const string Fake = "eyJhbGciOiJIUzI1NiJ9.eyJidWlsZCI6InRlc3QifQ.YnVpbGQtdGVzdC1zaWduYXR1cmU";

	private static readonly string[] Variables = ["NIXIE_TMDB_TOKEN", "FluentFinTmdbToken", "NIXIE_GATE_TMDB_TOKEN"];

	[Fact]
	public void WithTheToken_TheBuildSaysAppToken_AndNothingMore()
	{
		var output = RunHelper("$f = Get-NixieTmdbToken -Scopes Process; Format-NixieTmdbTokenNote $f; \"value-ok=$($f.Value -eq '" + Fake + "')\"", ("NIXIE_TMDB_TOKEN", "  " + Fake + "  "));

		Assert.Contains("TMDb: app token", output);
		Assert.Contains("value-ok=True", output);
		Assert.DoesNotContain(Fake, output);
		Assert.DoesNotContain("eyJ", output);
	}

	[Theory]
	[InlineData(null, "TMDb: no app token (NIXIE_TMDB_TOKEN is not set; TMDb trending needs the user's own token)")]
	[InlineData("0123456789abcdef0123456789abcdef", "TMDb: no app token (NIXIE_TMDB_TOKEN is not shaped like a TMDb API Read Access Token (the long one, not the 32-digit API key); left out)")]
	[InlineData("eyJ.a.b;c$(x)", "TMDb: no app token (NIXIE_TMDB_TOKEN is not shaped like a TMDb API Read Access Token (the long one, not the 32-digit API key); left out)")]
	public void WithoutAGoodToken_TheBuildSaysNoAppToken_NeverTheValue(string? value, string said)
	{
		var output = RunHelper("$f = Get-NixieTmdbToken -Scopes Process; Format-NixieTmdbTokenNote $f; \"none=$($null -eq $f.Value)\"",
			value is null ? Array.Empty<(string, string)>() : [("NIXIE_TMDB_TOKEN", value)]);

		Assert.Contains(said, output);
		Assert.Contains("none=True", output);
		if (value is not null)
		{
			Assert.DoesNotContain(value, output);
		}
	}

	[Theory]
	[InlineData(true, true, "passed")]
	[InlineData(true, false, "passed")]
	[InlineData(false, false, "passed")]
	[InlineData(false, true, "refused: -RequireAppTmdbToken: the release needs Nixie's own TMDb token (NIXIE_TMDB_TOKEN is not set)")]
	public void RequireAppTmdbToken_RefusesABuildWithoutIt(bool have, bool require, string said)
	{
		var output = RunHelper("$f = Get-NixieTmdbToken -Scopes Process; try { Assert-NixieTmdbToken $f $" + (require ? "true" : "false") + "; 'passed' } catch { \"refused: $($_.Exception.Message)\" }",
			have ? [("NIXIE_TMDB_TOKEN", Fake)] : Array.Empty<(string, string)>());

		Assert.Equal(said, output.Trim());
		Assert.DoesNotContain(Fake, output);
	}

	[Fact]
	public void ThePublishVariable_IsSetForTheBlockAlone_AndTakenAwayAfter_EvenOnAFailure()
	{
		var output = RunHelper("""
			$f = Get-NixieTmdbToken -Scopes Process
			Invoke-WithNixieTmdbToken $f { "inside=$($env:FluentFinTmdbToken -eq $f.Value)" }
			"after=$($null -eq $env:FluentFinTmdbToken)"
			try { Invoke-WithNixieTmdbToken $f { throw 'publish failed' } } catch { "caught" }
			"after-failure=$($null -eq $env:FluentFinTmdbToken)"
			""", ("NIXIE_TMDB_TOKEN", Fake));

		Assert.Contains("inside=True", output);
		Assert.Contains("after=True", output);
		Assert.Contains("after-failure=True", output);
		Assert.DoesNotContain(Fake, output);
	}

	[Fact]
	public void ThePublishsGeneratedCopies_AreDeleted()
	{
		var dir = Directory.CreateTempSubdirectory("nixie-tmdb-copies-");
		try
		{
			var obj = Directory.CreateDirectory(Path.Combine(dir.FullName, "obj", "FluentFin")).FullName;
			File.WriteAllText(Path.Combine(obj, "Nixie.AssemblyInfo.cs"), $"[assembly: System.Reflection.AssemblyMetadata(\"NixieTmdbToken\", \"{Fake}\")]");
			File.WriteAllText(Path.Combine(obj, "Other.AssemblyInfo.cs"), "[assembly: System.Reflection.AssemblyTitle(\"Other\")]");

			var output = RunHelper($"$f = Get-NixieTmdbToken -Scopes Process; \"removed=$(Remove-NixieTmdbTokenCopies '{dir.FullName}' $f)\"", ("NIXIE_TMDB_TOKEN", Fake));

			Assert.Contains("removed=1", output);
			Assert.False(File.Exists(Path.Combine(obj, "Nixie.AssemblyInfo.cs")));
			Assert.True(File.Exists(Path.Combine(obj, "Other.AssemblyInfo.cs")));
		}
		finally
		{
			dir.Delete(recursive: true);
		}
	}

	[Fact]
	public void TheReleaseScript_PassesItInTheEnvironment_NeverOnACommandLine_GatesThePackage_AndCanRequireIt()
	{
		var script = File.ReadAllText(Path.Combine(Tools, "release", "build-release.ps1")).Replace("\r\n", "\n");

		Assert.Contains(". (Join-Path $PSScriptRoot 'tmdb-token.ps1')", script);
		Assert.Contains("if ($tmdb.Value) { Write-Host (Format-NixieTmdbTokenNote $tmdb) } else { Write-Warning (Format-NixieTmdbTokenNote $tmdb) }", script);
		Assert.Contains("[switch]$RequireAppTmdbToken", script);
		var assertAt = script.IndexOf("Assert-NixieTmdbToken $tmdb $RequireAppTmdbToken", StringComparison.Ordinal);
		Assert.True(assertAt > 0 && assertAt < script.IndexOf("# --- 1. Python runtime", StringComparison.Ordinal));
		var publish = script[script.IndexOf("Invoke-WithNixieTmdbToken $tmdb {", StringComparison.Ordinal)..];
		Assert.True(publish.IndexOf("& $dotnet publish", StringComparison.Ordinal) < publish.IndexOf("} } }", StringComparison.Ordinal));
		Assert.DoesNotMatch(@"[-/]p(roperty)?:FluentFinTmdbToken", script);
		Assert.DoesNotMatch(@"--needle'?,?\s*\$tmdb", script);
		foreach (var line in script.Split('\n').Where(l => l.Contains("$tmdb.Value", StringComparison.Ordinal)))
		{
			var rest = line.Replace("if ($tmdb.Value)", "").Replace("if ($tmdb.Value -and", "").Replace("if (-not $tmdb.Value", "")
				.Replace("SetEnvironmentVariable($NixieTmdbGateVariable, $tmdb.Value, 'Process')", "");
			Assert.False(rest.Contains("$tmdb.Value", StringComparison.Ordinal), "the token used beyond a test: " + line.Trim());
		}

		Assert.Contains("'--secret-env', $NixieTmdbGateVariable", script);
		Assert.Contains("finally { [Environment]::SetEnvironmentVariable($NixieTmdbGateVariable, $null, 'Process') }", script);

		var helper = File.ReadAllText(Path.Combine(Tools, "release", "tmdb-token.ps1"));
		Assert.DoesNotMatch(@"Write-(Host|Output|Warning|Verbose|Information)", helper);
		Assert.DoesNotMatch(@"""[^""\n]*\$\(?\$(found\.Value|value|raw)\b", helper);
		Assert.Contains("$NixieTmdbPublishVariable = 'FluentFinTmdbToken'", helper);
		// the live check may use the app token when its own isn't set
		var live = File.ReadAllText(Path.Combine(Tools, "live", "live_trending_sources.ps1"));
		Assert.Contains("if (-not $token) { $token = Get-Secret 'NIXIE_TMDB_TOKEN' }", live);
	}

	[Fact]
	public void TheProject_WritesTheMetadata_InAReleaseBuildOnly_UnderTheKeyTheAppReads()
	{
		var project = XDocument.Load(Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "FluentFin", "FluentFin.csproj"));
		var item = Assert.Single(project.Descendants("AssemblyMetadata"), x => (string?)x.Attribute("Include") == TmdbAppToken.MetadataKey);

		Assert.Equal("$(FluentFinTmdbToken)", (string?)item.Attribute("Value"));
		var condition = (string?)item.Parent!.Attribute("Condition") ?? "";
		Assert.Contains("'$(FluentFinMpvRelease)' == 'true'", condition);
		Assert.Contains("'$(FluentFinTmdbToken)' != ''", condition);
	}

	[Fact]
	public void TheApp_ReadsItFromItsMetadata_ShapedLikeAReadAccessToken_AndTheHarnessHasItsOwn()
	{
		Assert.Null(TmdbAppToken.Resolve(harness: false, []));
		Assert.Null(TmdbAppToken.Resolve(harness: false, [new AssemblyMetadataAttribute(TmdbAppToken.MetadataKey, "0123456789abcdef0123456789abcdef")]));
		Assert.Equal(Fake, TmdbAppToken.Resolve(harness: false, [new AssemblyMetadataAttribute("Other", "x"), new AssemblyMetadataAttribute(TmdbAppToken.MetadataKey, " " + Fake + " ")]));
		Assert.Equal(TmdbAppToken.HarnessValue, TmdbAppToken.Resolve(harness: true, [new AssemblyMetadataAttribute(TmdbAppToken.MetadataKey, Fake)]));
		Assert.Null(TmdbLists.TokenProblem(TmdbAppToken.HarnessValue));
		// a test run is no release: it carries none
		Assert.Null(TmdbAppToken.Current(harness: false));
		// the harness's value is the fake's own (outside_tmdb_trending.HARNESS_TOKEN)
		var fake = File.ReadAllText(Path.Combine(Tools, "ui-harness", "fakeserver", "outside_tmdb_trending.py"));
		Assert.Contains($"HARNESS_TOKEN = \"{TmdbAppToken.HarnessValue}\"", fake);
	}

	/// <summary>"Never committed": when this PC has Nixie's TMDb token, no tracked file holds it (only file names are reported).</summary>
	[Fact]
	public void NoTrackedFile_HoldsTheRealToken()
	{
		var values = new[] { "NIXIE_TMDB_TOKEN", "NIXIE_LIVE_TMDB_TOKEN" }
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

			var text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(path));
			if (values.Any(v => text.Contains(v!, StringComparison.Ordinal)))
			{
				holding.Add(relative);
			}
		}

		Assert.True(holding.Count == 0, "tracked files holding Nixie's TMDb token (value not shown): " + string.Join(", ", holding));
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

	private static string RunHelper(string body, params (string Name, string Value)[] env)
	{
		var dir = Directory.CreateTempSubdirectory("nixie-tmdb-helper-");
		try
		{
			var script = Path.Combine(dir.FullName, "run.ps1");
			File.WriteAllText(script, "$ErrorActionPreference = 'Stop'\nSet-StrictMode -Version 3\n. '" + Path.Combine(Tools, "release", "tmdb-token.ps1").Replace("'", "''") + "'\n" + body + "\n");
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
			Assert.True(process.ExitCode == 0, "the helper failed: " + env.Aggregate(all, (t, e) => e.Value.Length > 0 ? t.Replace(e.Value, "<value>") : t));
			return all;
		}
		finally
		{
			dir.Delete(recursive: true);
		}
	}
}
