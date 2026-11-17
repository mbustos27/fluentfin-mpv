using System.Reflection;
using System.Xml.Linq;
using FluentFin.Core;

namespace FluentFin.Tests;

/// <summary>Nixie (formerly Seaglass and FluentFin MPV): the name a person or a Jellyfin server sees.</summary>
public sealed class AppIdentityTests
{
	private static readonly Assembly App = typeof(FluentFin.App).Assembly;

	[Fact]
	public void TheAppIsNixieExe()
	{
		Assert.Equal("Nixie", App.GetName().Name);
		Assert.Equal("Nixie.dll", Path.GetFileName(App.Location));
		Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Nixie.exe")) || File.Exists(Path.Combine(Path.GetDirectoryName(App.Location)!, "Nixie.exe")));
	}

	[Fact]
	public void PropertiesDetails_SayNixie()
	{
		Assert.Equal("Nixie", App.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company);
		Assert.Equal("Nixie", App.GetCustomAttribute<AssemblyProductAttribute>()?.Product);
		Assert.Equal("Nixie", App.GetCustomAttribute<AssemblyTitleAttribute>()?.Title);
		Assert.StartsWith("Nixie, ", App.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description);
		Assert.StartsWith("Copyright (c) Nixie contributors", App.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright);
	}

	[Fact]
	public void TheJellyfinClientIsNixie()
	{
		Assert.Equal("Nixie", AppIdentity.ClientName);
		Assert.Equal("Nixie", AppIdentity.Name);
	}

	[Fact]
	public void TheManifestSaysNixie()
	{
		var root = XDocument.Load(FindUp(Path.Combine("ui", "FluentFin", "Package.appxmanifest"))).Root!;
		Assert.Equal("Nixie", root.Descendants().Single(e => e.Name.LocalName == "DisplayName").Value);
		Assert.Equal("Nixie", root.Descendants().Single(e => e.Name.LocalName == "VisualElements").Attribute("DisplayName")?.Value);
		Assert.StartsWith("Nixie (formerly Seaglass and FluentFin MPV", root.Descendants().Single(e => e.Name.LocalName == "PublisherDisplayName").Value);
	}

	[Fact]
	public void TheWindow_IsCalledNixie_AndShowsItsIcon()
	{
		var xaml = File.ReadAllText(FindUp(Path.Combine("ui", "FluentFin", "MainWindow.xaml")));
		var code = File.ReadAllText(FindUp(Path.Combine("ui", "FluentFin", "MainWindow.xaml.cs")));
		Assert.Contains("Title=\"Nixie\"", xaml);
		Assert.Contains("AppWindow.SetIcon(\"Assets/Nixie.ico\")", code);
		Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(App.Location)!, "Assets", "Nixie.ico")), "Assets\\Nixie.ico is copied to the output");
	}

	[Fact]
	public void StartUpMessages_SayNixie()
	{
		Assert.Equal("Nixie could not start", FluentFin.App.CouldNotStart);
		Assert.Equal("Nixie-startup-crash.txt", FluentFin.App.StartupCrashFile);
	}

	[Theory]
	[InlineData("Seaglass")]
	[InlineData("FluentFin MPV")]
	public void AFailedDataCopy_NamesTheOldApp(string oldApp)
	{
		var (title, text) = FluentFin.App.LegacyCopyFailedMessage(oldApp);

		Assert.Equal("Nixie could not copy your data", title);
		Assert.Contains($"couldn't copy the data from {oldApp}", text);
		Assert.Contains("Nothing is lost", text);
	}

	[Fact]
	public void TheThemeVariable_IsReadThroughAppIdentity_SoNixieThemeAndSeaglassThemeBothWork()
	{
		var code = File.ReadAllText(FindUp(Path.Combine("ui", "FluentFin", "App.xaml.cs")));

		Assert.DoesNotContain("GetEnvironmentVariable(\"SEAGLASS_", code);
		Assert.Equal(2, code.Split("AppIdentity.ReadEnvironment(\"THEME\")").Length - 1);
	}

	[Theory]
	[InlineData(16)]
	[InlineData(20)]
	[InlineData(24)]
	[InlineData(32)]
	[InlineData(40)]
	[InlineData(48)]
	[InlineData(64)]
	[InlineData(128)]
	[InlineData(256)]
	public void TheIconHasEverySize(int size)
	{
		var ico = File.ReadAllBytes(FindUp(Path.Combine("ui", "FluentFin", "Assets", "Nixie.ico")));
		var count = BitConverter.ToUInt16(ico, 4);
		var sizes = Enumerable.Range(0, count).Select(i => ico[6 + 16 * i] is 0 ? 256 : ico[6 + 16 * i]).ToList();
		Assert.Contains(size, sizes);
		Assert.True(File.Exists(FindUp(Path.Combine("ui", "FluentFin", "Assets", $"Nixie-{size}.png"))));
	}

	[Fact]
	public void TheIconFilesStaySmall()
	{
		var assets = Path.GetDirectoryName(FindUp(Path.Combine("ui", "FluentFin", "Assets", "Nixie.ico")))!;
		Assert.True(new FileInfo(Path.Combine(assets, "Nixie.ico")).Length < 150_000, "Nixie.ico under 150 KB");
		foreach (var png in Directory.GetFiles(assets, "Nixie-*.png"))
		{
			Assert.True(new FileInfo(png).Length < 80_000, $"{Path.GetFileName(png)} under 80 KB");
		}
	}

	private static string FindUp(string relative)
	{
		for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
		{
			var candidate = Path.Combine(dir.FullName, relative);
			if (File.Exists(candidate))
			{
				return candidate;
			}
		}

		throw new FileNotFoundException(relative);
	}
}
