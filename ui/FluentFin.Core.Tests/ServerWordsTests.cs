using System.Text;
using System.Text.RegularExpressions;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.7.3, the rule 11 sweep (docs\DESIGN-RULES.md: the user is not the server's admin): no app text says "your server" (the
/// server is someone else's: "the server" or "the server you use"), and none says "Check that the server is running", a step
/// only the server's admin can take (1.7.1's Collections page said it while every other page loaded). Code comments may quote
/// the old words; what the app shows, and the README, FEATURES.md, the package README and the installer's text, may not.
/// Nor does any send the user to the server's dashboard (1.7.3: the sign-in page's and Quick Connect's "Dashboard > General").
/// </summary>
public sealed class ServerWordsTests
{
	private static readonly Regex s_forbidden = new(@"\byour\s+(?:own\s+)?(?:jellyfin\s+)?servers?\b|\bserver\s+is\s+running\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	/// <summary>1.7.3: the server's dashboard, a place only its admin goes ("Dashboard > General > Quick Connect").</summary>
	private static readonly Regex s_dashboard = new(@"\bdashboards?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	/// <summary>
	/// The server administration's own tabs (Settings, for the server's administrators only): its first tab is called
	/// "Dashboard". That is this app's page, not a step it gives the user.
	/// </summary>
	private static readonly string[] s_adminArea = [@"FluentFin.Core\Admin\AdminSections.cs"];

	/// <summary>The app's own projects (not its tests): what the app shows comes from these.</summary>
	private static readonly string[] s_appProjects = ["FluentFin", "FluentFin.Core", "FluentFin.Plugins", "FluentFin.UI.Core"];

	/// <summary>The user-facing documents outside the app, from the repository's root.</summary>
	private static readonly string[] s_documents =
	[
		"README.md", @"docs\FEATURES.md", @"tools\release\README.txt", @"tools\release\installer\Nixie.en-us.wxl",
		"PRIVACY.md", // 1.7.3: it said "Your Jellyfin server" and "your own server"
		.. IssueTemplates(), // 1.7.3: Report a problem leads to them; the bug form said "your server's address" and "Jellyfin's Dashboard shows it"
	];

	/// <summary>GitHub's issue forms (<c>.github\ISSUE_TEMPLATE\*.yml</c>), from the repository's root; never none.</summary>
	private static string[] IssueTemplates()
	{
		var root = Path.GetFullPath(Path.Combine(HomeAndLibraryXamlTests.RepoRoot, ".."));
		var forms = Directory.GetFiles(Path.Combine(root, ".github", "ISSUE_TEMPLATE"), "*.yml").Select(f => Path.GetRelativePath(root, f)).Order(StringComparer.Ordinal).ToArray();
		return forms.Length > 0 ? forms : throw new InvalidOperationException(@".github\ISSUE_TEMPLATE has no .yml form: the scan would cover none");
	}

	[Fact]
	public void NoAppText_SaysYourServer_OrCheckThatTheServerIsRunning()
	{
		var found = new List<string>();
		foreach (var project in s_appProjects)
		{
			foreach (var file in AppFiles(Path.Combine(HomeAndLibraryXamlTests.RepoRoot, project)))
			{
				found.AddRange(Offences(Path.GetRelativePath(HomeAndLibraryXamlTests.RepoRoot, file), Shown(file), s_forbidden));
			}
		}

		foreach (var document in s_documents)
		{
			var file = Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "..", document);
			Assert.True(File.Exists(file), document);
			found.AddRange(Offences(document, WithoutXmlComments(File.ReadAllText(file)), s_forbidden));
		}

		Assert.True(found.Count == 0, "Rule 11 (\"the server\" or \"the server you use\"; no step on the server):\n" + string.Join('\n', found));
		Assert.Contains(@".github\ISSUE_TEMPLATE\bug.yml", s_documents); // the form Report a problem leads to
	}

	// 1.7.3 (rule 11): the sign-in page said "turned off on the server (Dashboard > General > Quick Connect)", and Quick
	// Connect's dialog "An administrator can turn it on in the server's dashboard": steps only the server's admin can take
	[Fact]
	public void NoAppText_SendsTheUserToTheServersDashboard()
	{
		var found = new List<string>();
		foreach (var project in s_appProjects)
		{
			foreach (var file in AppFiles(Path.Combine(HomeAndLibraryXamlTests.RepoRoot, project)))
			{
				var relative = Path.GetRelativePath(HomeAndLibraryXamlTests.RepoRoot, file);
				if (!s_adminArea.Contains(relative, StringComparer.OrdinalIgnoreCase))
				{
					found.AddRange(DashboardOffences(relative, file));
				}
			}
		}

		foreach (var document in s_documents)
		{
			var text = WithoutXmlComments(File.ReadAllText(Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "..", document)));
			found.AddRange(Offences(document, text, s_dashboard));
		}

		Assert.True(found.Count == 0, "Rule 11 (no step on the server's dashboard; say what the user can do on this PC):\n" + string.Join('\n', found));
		// the exemption names a file that still has its tab (else take it off, so the scan covers it)
		Assert.All(s_adminArea, relative => Assert.NotEmpty(DashboardOffences(relative, Path.Combine(HomeAndLibraryXamlTests.RepoRoot, relative))));
	}

	/// <summary>The scan finds the words in what is shown, and only there: not in comments, whatever a string holds.</summary>
	[Fact]
	public void TheScan_ReadsStringsAndMarkup_NotComments()
	{
		Assert.Matches(s_forbidden, "Signing in to your server…");
		Assert.Matches(s_forbidden, "Choose your Jellyfin server");
		Assert.Matches(s_forbidden, "every poster you see comes from your own server");
		Assert.Matches(s_forbidden, "Check that the server is running, then try again.");
		Assert.DoesNotMatch(s_forbidden, "Signing in to the server you use… Your libraries, your account.");
		Assert.Matches(s_dashboard, "it may be turned off on the server (Dashboard > General > Quick Connect)");
		Assert.Matches(s_dashboard, "An administrator can turn it on in the server's dashboard, under General.");
		Assert.DoesNotMatch(s_dashboard, "x:Class=\"FluentFin.Views.JellyfinSettings.DashboardPage\" Key<DashboardViewModel>()");

		const string code = """
			// it said "your server"
			/* your server */ var a = "https://x.example/"; var b = "your server"; // and "server is running"
			/// <summary>Check that the server is running</summary>
			var c = $"{(x ? "http://a" : y)} your Jellyfin server {z}"; var d = @"a ""quoted"" // your server";
			var e = '"'; var f = "\"// still a string, your server";
			""";
		var shown = WithoutCSharpComments(code);
		Assert.Equal(code.Count(ch => ch == '\n'), shown.Count(ch => ch == '\n'));   // lines kept, for the file:line
		Assert.Equal(4, s_forbidden.Matches(shown).Count);
		Assert.Contains("var b = \"your server\";", shown, StringComparison.Ordinal);
		Assert.DoesNotContain("it said", shown, StringComparison.Ordinal);
		Assert.DoesNotContain("<summary>", shown, StringComparison.Ordinal);
		Assert.DoesNotContain("and \"server", shown, StringComparison.Ordinal);
		Assert.Contains("@\"a \"\"quoted\"\" // your server\"", shown, StringComparison.Ordinal);
		Assert.Contains("\"\\\"// still a string, your server\"", shown, StringComparison.Ordinal);

		// 1.7.3: "dashboard" counts in C#'s string literals only, not in code or an interpolation's holes
		const string admin = """
			var dashboard = Get<DashboardViewModel>(); // the dashboard
			var g = $"Open {dashboard.Name}"; var h = "Dashboard > General"; var i = $@"the {(x ? "a" : "b")} dashboard";
			""";
		var strings = new List<(int Start, int End)>();
		var literals = OnlyStrings(WithoutCSharpComments(admin, strings), strings);
		Assert.Equal(admin.Count(ch => ch == '\n'), literals.Count(ch => ch == '\n'));
		Assert.Equal(["Dashboard", "dashboard"], s_dashboard.Matches(literals).Select(m => m.Value));
		Assert.Contains("\"Open {", literals, StringComparison.Ordinal);
		Assert.DoesNotContain("Name", literals, StringComparison.Ordinal);

		var markup = WithoutXmlComments("<!--  your server  -->\n<TextBlock Text=\"Ad breaks your server marks\" />");
		Assert.Single(s_forbidden.Matches(markup));
		Assert.StartsWith("\n", markup, StringComparison.Ordinal);
	}

	private static IEnumerable<string> AppFiles(string root) =>
		Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
			.Where(f => Path.GetExtension(f) is ".cs" or ".xaml" or ".resw")
			.Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"));

	/// <summary>The file's text as the app can show it: C# without its comments, markup without its comments.</summary>
	private static string Shown(string file)
	{
		var text = File.ReadAllText(file);
		return Path.GetExtension(file) == ".cs" ? WithoutCSharpComments(text) : WithoutXmlComments(text);
	}

	/// <summary>
	/// 1.7.3: "dashboard" where the file can show it: in C#, inside its string literals only (the admin pages' code names
	/// its view model, and a local "dashboard"); in markup, anywhere but its comments.
	/// </summary>
	private static IEnumerable<string> DashboardOffences(string relative, string file)
	{
		if (Path.GetExtension(file) != ".cs")
		{
			return Offences(relative, Shown(file), s_dashboard);
		}

		var strings = new List<(int Start, int End)>();
		return Offences(relative, OnlyStrings(WithoutCSharpComments(File.ReadAllText(file), strings), strings), s_dashboard);
	}

	/// <summary>The string literals of <paramref name="shown"/>, the rest blanked; the line breaks stay, so a match's line is the file's.</summary>
	internal static string OnlyStrings(string shown, IReadOnlyList<(int Start, int End)> strings)
	{
		var kept = shown.Select(ch => ch == '\n' ? '\n' : ' ').ToArray();
		foreach (var (start, end) in strings)
		{
			shown.CopyTo(start, kept, start, end - start);
		}

		return new string(kept);
	}

	private static IEnumerable<string> Offences(string file, string text, Regex words)
	{
		foreach (Match m in words.Matches(text))
		{
			var line = text.AsSpan(0, m.Index).Count('\n') + 1;
			var start = text.LastIndexOf('\n', m.Index) + 1;
			var end = text.IndexOf('\n', m.Index);
			yield return $"{file}:{line}: {text[start..(end < 0 ? text.Length : end)].Trim()}";
		}
	}

	internal static string WithoutXmlComments(string markup) =>
		Regex.Replace(markup, "<!--.*?-->", m => new string('\n', m.Value.Count(ch => ch == '\n')), RegexOptions.Singleline);

	/// <summary>
	/// C# without its comments (line, block and doc comments), keeping every string and character literal whole: regular,
	/// verbatim, raw and interpolated strings, and the code in an interpolation's holes (which may hold strings of its own).
	/// The line breaks stay, so a match's line is the file's.
	/// </summary>
	internal static string WithoutCSharpComments(string code) => WithoutCSharpComments(code, null);

	/// <summary>1.7.3: and where its string literals are, in the text returned (start, end), when <paramref name="strings"/> is given.</summary>
	internal static string WithoutCSharpComments(string code, List<(int Start, int End)>? strings)
	{
		var shown = new StringBuilder(code.Length);
		LexCode(code, 0, shown, hole: false, strings);
		return shown.ToString();
	}

	/// <summary>Code from <paramref name="i"/>; in a hole, up to its closing brace (returned, not copied).</summary>
	private static int LexCode(string s, int i, StringBuilder shown, bool hole, List<(int Start, int End)>? strings)
	{
		var depth = 0;
		while (i < s.Length)
		{
			var c = s[i];
			var next = i + 1 < s.Length ? s[i + 1] : '\0';
			if (c == '/' && next == '/')
			{
				while (i < s.Length && s[i] != '\n')
				{
					i++;
				}
			}
			else if (c == '/' && next == '*')
			{
				var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
				end = end < 0 ? s.Length : end + 2;
				shown.Append('\n', s.AsSpan(i, end - i).Count('\n'));
				i = end;
			}
			else if (c == '"')
			{
				i = LexString(s, i, shown, strings);
			}
			else if (c == '\'')
			{
				var j = i + 1;
				while (j < s.Length && s[j] != '\'' && s[j] != '\n')
				{
					j += s[j] == '\\' ? 2 : 1;
				}

				j = Math.Min(j + 1, s.Length);
				shown.Append(s, i, j - i);
				i = j;
			}
			else
			{
				if (c == '{')
				{
					depth++;
				}
				else if (c == '}' && depth-- == 0 && hole)
				{
					return i;
				}

				shown.Append(c);
				i++;
			}
		}

		return i;
	}

	/// <summary>A string literal starting at the quote <paramref name="i"/> (its $ and @ are before it); returns the index after it.</summary>
	private static int LexString(string s, int i, StringBuilder shown, List<(int Start, int End)>? strings)
	{
		var prefix = i;
		while (prefix > 0 && s[prefix - 1] is '$' or '@')
		{
			prefix--;
		}

		var interpolated = s.AsSpan(prefix, i - prefix).Contains('$');
		var verbatim = s.AsSpan(prefix, i - prefix).Contains('@');
		var quotes = 0;
		while (i + quotes < s.Length && s[i + quotes] == '"')
		{
			quotes++;
		}

		// 1.7.3: where the literal's text is in shown (an interpolation's holes are code, so each hole ends a part)
		var part = shown.Length;
		if (quotes >= 3)
		{
			// a raw string: up to the same run of quotes (its holes, if any, are copied as they are)
			var close = s.IndexOf(new string('"', quotes), i + quotes, StringComparison.Ordinal);
			close = close < 0 ? s.Length : close + quotes;
			shown.Append(s, i, close - i);
			strings?.Add((part, shown.Length));
			return close;
		}

		shown.Append('"');
		var j = i + 1;
		while (j < s.Length)
		{
			var c = s[j];
			if (verbatim && c == '"' && j + 1 < s.Length && s[j + 1] == '"')
			{
				shown.Append("\"\"");
				j += 2;
			}
			else if (c == '"' || (!verbatim && c == '\n'))
			{
				shown.Append(c);
				strings?.Add((part, shown.Length));
				return j + 1;
			}
			else if (!verbatim && c == '\\' && j + 1 < s.Length)
			{
				shown.Append(s, j, 2);
				j += 2;
			}
			else if (interpolated && c == '{' && j + 1 < s.Length && s[j + 1] == '{')
			{
				shown.Append("{{");
				j += 2;
			}
			else if (interpolated && c == '{')
			{
				shown.Append('{');
				strings?.Add((part, shown.Length));
				j = LexCode(s, j + 1, shown, hole: true, strings);
				part = shown.Length;
				if (j < s.Length)
				{
					shown.Append('}');
					j++;
				}
			}
			else
			{
				shown.Append(c);
				j++;
			}
		}

		strings?.Add((part, shown.Length));
		return j;
	}
}
