using System.Reflection;
using System.Xml.Linq;
using FluentFin.Core;

namespace FluentFin.Tests;

/// <summary>
/// fluentfin-mpv: the release version, pinned. A release bumps <see cref="Release"/> with ui\FluentFin\FluentFin.csproj,
/// Package.appxmanifest and tools\release\build-release.ps1 (docs\RELEASE.md).
/// </summary>
public sealed class AppVersionTests
{
	private const string Release = "1.7.3";

	private static readonly Assembly App = typeof(FluentFin.App).Assembly;

	[Fact]
	public void TheAppSaysAndSendsTheReleaseVersion()
	{
		Assert.Equal(Release, AppVersion.Of(App));
	}

	[Fact]
	public void FileAndAssemblyVersionsAreTheReleaseWithFourParts()
	{
		Assert.Equal(Release + ".0", App.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version);
		Assert.Equal(new Version(Release + ".0"), App.GetName().Version);
	}

	[Fact]
	public void PackageManifestHasTheReleaseVersion()
	{
		var manifest = FindUp(Path.Combine("ui", "FluentFin", "Package.appxmanifest"));
		var identity = XDocument.Load(manifest).Root!.Elements().Single(e => e.Name.LocalName == "Identity");
		Assert.Equal(Release + ".0", identity.Attribute("Version")?.Value);
	}

	[Theory]
	[InlineData("1.0.0+9026825abc", "1.0.0")]
	[InlineData("1.2.3-beta.1", "1.2.3-beta.1")]
	[InlineData(" 2.0.0 ", "2.0.0")]
	public void InformationalVersion_LosesOnlyItsBuildMetadata(string informational, string expected)
	{
		Assert.Equal(expected, AppVersion.Of(new FakeAssembly(informational, new Version(9, 9, 9, 9))));
	}

	[Fact]
	public void WithoutAnInformationalVersion_TheAssemblyVersionHasThreeParts()
	{
		Assert.Equal("1.4.2", AppVersion.Of(new FakeAssembly(null, new Version(1, 4, 2, 7))));
		Assert.Equal("1.4.0", AppVersion.Of(new Version(1, 4)));
		Assert.Equal("0.0.0", AppVersion.Of((Assembly?)null));
	}

	[Fact]
	public void OnlyTheReleaseMarker_MakesARelease()
	{
		Assert.True(AppVersion.IsRelease([new AssemblyMetadataAttribute("FluentFinMpvRelease", "true")]));
		Assert.False(AppVersion.IsRelease([new AssemblyMetadataAttribute("FluentFinMpvRelease", "false")]));
		Assert.False(AppVersion.IsRelease([new AssemblyMetadataAttribute("Other", "true")]));
		Assert.False(AppVersion.IsRelease([]));
		// a dev build (this test's FluentFin) is not one
		Assert.False(AppVersion.IsRelease(App.GetCustomAttributes<AssemblyMetadataAttribute>()));
	}

	internal static string FindUp(string relative)
	{
		for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
		{
			var candidate = Path.Combine(dir.FullName, relative);
			if (File.Exists(candidate))
			{
				return candidate;
			}
		}

		throw new FileNotFoundException($"{relative} above {AppContext.BaseDirectory}");
	}

	private sealed class FakeAssembly(string? informational, Version version) : Assembly
	{
		public override AssemblyName GetName() => new("Fake") { Version = version };

		// an Attribute[] (Attribute.GetCustomAttributes casts the result to one)
		public override object[] GetCustomAttributes(Type attributeType, bool inherit) =>
			informational is not null && attributeType.IsAssignableFrom(typeof(AssemblyInformationalVersionAttribute))
				? new Attribute[] { new AssemblyInformationalVersionAttribute(informational) }
				: Array.Empty<Attribute>();
	}
}
