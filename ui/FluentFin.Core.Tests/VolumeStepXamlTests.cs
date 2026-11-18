using System.Xml.Linq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Seaglass 1.1.4 (UX review, prioritised fix 7), read from the XAML: the Volume step's spin buttons are at least 24 px
/// (32 x 32, WinUI's are 32 x 24) through NumberBoxTargets, and its keys are the box's (Up/Down 1, PageUp/PageDown 5, typing);
/// every NumberBox on these settings pages has a name for Narrator. The running app's sizes and keys: offscreen\then-account.ps1.
/// </summary>
public sealed class VolumeStepXamlTests
{
	private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
	private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
	private static readonly XNamespace Local = "using:FluentFin.Controls";

	private const string Settings = "FluentFin/Views/SettingsPage.xaml";

	[Fact]
	public void TheVolumeStep_HasSpinButtonsOf32Px_AndTheBoxesKeys()
	{
		var box = Load(Settings).Descendants(Presentation + "NumberBox").Single(b => (string?)b.Attribute(Xaml + "Name") == "VolumeStepBox");

		// WinUI's inline spin buttons are 32 x 24; NumberBoxTargets makes them at least 32 x 32 (WCAG 2.5.8: 24)
		Assert.Equal("Inline", (string?)box.Attribute("SpinButtonPlacementMode"));
		Assert.True(double.Parse((string?)box.Attribute(Local + "NumberBoxTargets.MinTargetSize") ?? "0", System.Globalization.CultureInfo.InvariantCulture) >= 24);
		// Up/Down a step of 1, PageUp/PageDown 5 (WinUI's default LargeChange is 10: half the range), typing in 1 to 20
		Assert.Equal("1", (string?)box.Attribute("SmallChange"));
		Assert.Equal("5", (string?)box.Attribute("LargeChange"));
		Assert.Equal("1", (string?)box.Attribute("Minimum"));
		Assert.Equal("20", (string?)box.Attribute("Maximum"));
		Assert.Equal("Volume step, in percent", (string?)box.Attribute("AutomationProperties.Name"));
	}

	[Fact]
	public void NumberBoxTargets_SizesWinUIsSpinButtons()
	{
		var code = Code("FluentFin/Controls/NumberBoxTargets.cs");

		// the inline spin buttons' names in WinUI's NumberBox template
		Assert.Contains("[\"UpSpinButton\", \"DownSpinButton\"]", code);
		Assert.Contains("button.MinWidth = Math.Max(button.MinWidth, size);", code);
		Assert.Contains("button.MinHeight = Math.Max(button.MinHeight, size);", code);
	}

	[Fact]
	public void EveryNumberBoxInTheseSettings_HasANameForNarrator()
	{
		foreach (var page in new[] { Settings, "FluentFin/Views/JellyfinSettings/UserProfileEditorPage.xaml" })
		{
			Assert.All(Load(page).Descendants(Presentation + "NumberBox"), b => Assert.False(string.IsNullOrWhiteSpace((string?)b.Attribute("AutomationProperties.Name")), page));
		}
	}

	private static XDocument Load(string repoRelativePath) => XDocument.Load(Path.Combine(RepoRoot, repoRelativePath), LoadOptions.SetLineInfo);

	private static string Code(string repoRelativePath) => File.ReadAllText(Path.Combine(RepoRoot, repoRelativePath));

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
