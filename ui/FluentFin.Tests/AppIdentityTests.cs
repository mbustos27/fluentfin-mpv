using System.Reflection;
using System.Xml.Linq;
using FluentFin.Core;

namespace FluentFin.Tests;

/// <summary>Seaglass: the name a person or a Jellyfin server sees.</summary>
public sealed class AppIdentityTests
{
	private static readonly Assembly App = typeof(FluentFin.App).Assembly;

	[Fact]
	public void TheAppIsSeaglassExe()
	{
		Assert.Equal("Seaglass", App.GetName().Name);
		Assert.Equal("Seaglass.dll", Path.GetFileName(App.Location));
		Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Seaglass.exe")) || File.Exists(Path.Combine(Path.GetDirectoryName(App.Location)!, "Seaglass.exe")));
	}

	[Fact]
	public void TheJellyfinClientIsSeaglass()
	{
		Assert.Equal("Seaglass", AppIdentity.ClientName);
		Assert.Equal("Seaglass", AppIdentity.Name);
	}

	[Fact]
	public void TheManifestSaysSeaglass()
	{
		var root = XDocument.Load(FindUp(Path.Combine("ui", "FluentFin", "Package.appxmanifest"))).Root!;
		Assert.Equal("Seaglass", root.Descendants().Single(e => e.Name.LocalName == "DisplayName").Value);
		Assert.Equal("Seaglass", root.Descendants().Single(e => e.Name.LocalName == "VisualElements").Attribute("DisplayName")?.Value);
	}

	[Theory]
	[InlineData(16)]
	[InlineData(24)]
	[InlineData(32)]
	[InlineData(48)]
	[InlineData(64)]
	[InlineData(128)]
	[InlineData(256)]
	public void TheIconHasEverySize(int size)
	{
		var ico = File.ReadAllBytes(FindUp(Path.Combine("ui", "FluentFin", "Assets", "Seaglass.ico")));
		var count = BitConverter.ToUInt16(ico, 4);
		var sizes = Enumerable.Range(0, count).Select(i => ico[6 + 16 * i] is 0 ? 256 : ico[6 + 16 * i]).ToList();
		Assert.Contains(size, sizes);
		Assert.True(File.Exists(FindUp(Path.Combine("ui", "FluentFin", "Assets", $"Seaglass-{size}.png"))));
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
