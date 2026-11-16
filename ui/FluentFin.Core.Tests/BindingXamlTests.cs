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
	/// after its 8 s. Such bars open from a bool of the view model instead; a ProgressRing turns on from one too (the
	/// startup ring, <see cref="TheStartupPanel_ShowsFromItsBool"/>).
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

		Assert.True(found.Count == 0, "IsOpen or IsActive bound through an x:Bind function (it is not called when its argument is null): " + string.Join(" | ", found));
	}

	[Fact]
	public void ThePlayerPagesBars_OpenFromTheirBools()
	{
		var page = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin", "Views", "VideoPlayerPage.xaml"));

		Assert.Contains("IsOpen=\"{x:Bind ViewModel.HasMpvMessage, Mode=OneWay}\"", page);
		Assert.Contains("IsOpen=\"{x:Bind ViewModel.HasMpvNotice, Mode=OneWay}\"", page);
	}

	/// <summary>
	/// The 1.0 test build's ring: the window's startup panel (a ProgressRing over every page) was shown through functions
	/// of <c>StartupStatus</c>. The automatic sign-in sets it to null when it ends, x:Bind did not call the functions with
	/// null, and the ring stayed up, spinning, in the middle of the window: under mpv's own window nobody saw it, over the
	/// video of FluentFin's own mpv (its controls) it sat in the middle of every episode. The panel and its ring show from a
	/// bool of the view model.
	/// </summary>
	[Fact]
	public void TheStartupPanel_ShowsFromItsBool()
	{
		var window = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin", "MainWindow.xaml"));
		var at = window.IndexOf("x:Name=\"StartupPanel\"", StringComparison.Ordinal);
		Assert.True(at >= 0, "MainWindow.xaml names its startup panel StartupPanel");
		var panel = window[at..];
		panel = panel[..panel.IndexOf("</StackPanel>", StringComparison.Ordinal)];

		Assert.Contains("Visibility=\"{x:Bind ViewModel.HasStartupStatus, Mode=OneWay}\"", panel);
		Assert.Contains("<ProgressRing IsActive=\"{x:Bind ViewModel.HasStartupStatus, Mode=OneWay}\" />", panel);
		Assert.DoesNotContain("(ViewModel.StartupStatus)", window);
	}

	[GeneratedRegex(@"(IsOpen|IsActive)=""\{x:Bind\s+[\w.:]+\(")]
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
