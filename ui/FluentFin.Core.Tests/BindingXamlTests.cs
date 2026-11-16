using System.Text.RegularExpressions;

namespace FluentFin.Core.Tests;

/// <summary>
/// fluentfin-mpv: static checks of x:Bind uses that compile but do not do what they say. Reads the XAML as text: no WinUI.
/// </summary>
public sealed partial class BindingXamlTests
{
	/// <summary>
	/// visual check: a bar opened through a function of its text (<c>IsOpen="{x:Bind HasText(ViewModel.MpvNotice)}"</c>)
	/// never closed. x:Bind does not call a function whose argument is null, so the notice's bar stayed open, and empty,
	/// after its 8 s. Such bars open from a bool of the view model instead.
	/// </summary>
	[Fact]
	public void NoBarOrPopupOpensThroughAFunction()
	{
		var found = new List<string>();
		foreach (var file in AppXamlFiles())
		{
			var lines = File.ReadAllLines(Path.Combine(RepoRoot, file));
			for (var i = 0; i < lines.Length; i++)
			{
				if (OpenedByAFunction().IsMatch(lines[i]))
				{
					found.Add($"{file}:{i + 1}: {lines[i].Trim()}");
				}
			}
		}

		Assert.True(found.Count == 0, "IsOpen bound through an x:Bind function (it is not called when its argument is null): " + string.Join(" | ", found));
	}

	[Fact]
	public void ThePlayerPagesBars_OpenFromTheirBools()
	{
		var page = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin", "Views", "VideoPlayerPage.xaml"));

		Assert.Contains("IsOpen=\"{x:Bind ViewModel.HasMpvMessage, Mode=OneWay}\"", page);
		Assert.Contains("IsOpen=\"{x:Bind ViewModel.HasMpvNotice, Mode=OneWay}\"", page);
	}

	[GeneratedRegex(@"IsOpen=""\{x:Bind\s+[\w.]+\(")]
	private static partial Regex OpenedByAFunction();

	private static IEnumerable<string> AppXamlFiles() =>
		Directory.EnumerateFiles(Path.Combine(RepoRoot, "FluentFin"), "*.xaml", SearchOption.AllDirectories)
			.Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
			.Select(f => Path.GetRelativePath(RepoRoot, f));

	private static string RepoRoot { get; } = FindRepoRoot();

	private static string FindRepoRoot()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FluentFin.sln")))
		{
			dir = dir.Parent;
		}

		return dir?.FullName ?? throw new DirectoryNotFoundException("FluentFin.sln not found above " + AppContext.BaseDirectory);
	}
}
