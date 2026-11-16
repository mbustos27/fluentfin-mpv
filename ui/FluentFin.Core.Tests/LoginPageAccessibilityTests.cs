using System.Xml;
using System.Xml.Linq;

namespace FluentFin.Core.Tests;

/// <summary>
/// fluentfin-mpv (startup-perf): the login page names every field and button for Narrator (a UIA scan of the running page,
/// the dev harness's visual check, found six unnamed: the username box and its text field, the password box and the three
/// buttons, whose content is an icon and a text block, which UIA does not read as a name). Reads the XAML as XML, like
/// <see cref="AccessibilityXamlTests"/>; the running page is checked by tools/ui-harness/run-offscreen.ps1's scan.
/// </summary>
public class LoginPageAccessibilityTests
{
	private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
	private const string Page = "FluentFin/Views/LoginPage.xaml";

	private static readonly string[] Interactive = ["Button", "AutoSuggestBox", "TextBox", "PasswordBox", "CheckBox", "ComboBox", "ToggleSwitch", "HyperlinkButton"];

	[Fact]
	public void EveryFieldAndButton_HasAName()
	{
		var unnamed = Load().Descendants()
			.Where(e => e.Name.Namespace == Presentation && Interactive.Contains(e.Name.LocalName))
			.Where(e => string.IsNullOrWhiteSpace((string?)e.Attribute("AutomationProperties.Name")) && !HasTextContent(e))
			.Select(e => $"{Page}:{((IXmlLineInfo)e).LineNumber} {e.Name.LocalName}")
			.ToList();

		Assert.True(unnamed.Count == 0, "Unnamed on the login page:\n" + string.Join("\n", unnamed));
	}

	[Theory]
	[InlineData("{x:Bind ViewModel.SwitchServerCommand}", "Switch server")]
	[InlineData("{x:Bind ViewModel.QuickConnectCommand}", "Quick Connect")]
	[InlineData("{x:Bind ViewModel.LoginCommand}", "Login")]
	public void EachButton_IsNamedAsItReads(string command, string name)
	{
		var button = Assert.Single(Load().Descendants(Presentation + "Button"), b => (string?)b.Attribute("Command") == command);

		Assert.Equal(name, (string?)button.Attribute("AutomationProperties.Name"));
		// the name is the text on the button (speech input matches what is shown)
		Assert.Contains(button.Descendants(Presentation + "TextBlock"), t => (string?)t.Attribute("Text") == name);
	}

	[Fact]
	public void TheFields_AreNamedAfterTheirLabels_AndTheUsernameBoxPassesItsNameToItsTextField()
	{
		var doc = Load();
		var username = Assert.Single(doc.Descendants(Presentation + "AutoSuggestBox"));
		var password = Assert.Single(doc.Descendants(Presentation + "PasswordBox"));

		Assert.Equal("Username", (string?)username.Attribute("AutomationProperties.Name"));
		Assert.Equal("Password", (string?)password.Attribute("AutomationProperties.Name"));
		// AutoSuggestBox does not name the text field of its template: the page does, when the box is loaded
		Assert.Equal("UsernameBox_Loaded", (string?)username.Attribute("Loaded"));
		var code = File.ReadAllText(Path.Combine(RepoRoot, Page + ".cs"));
		Assert.Contains("AutomationProperties.SetName(text, AutomationProperties.GetName(box))", code);
	}

	[Fact]
	public void TheSplashImage_IsDecoration()
	{
		var image = Assert.Single(Load().Descendants(Presentation + "Image"));

		Assert.Equal("Raw", (string?)image.Attribute("AutomationProperties.AccessibilityView"));
	}

	private static bool HasTextContent(XElement element) =>
		!string.IsNullOrWhiteSpace((string?)element.Attribute("Content")) && !((string?)element.Attribute("Content"))!.StartsWith('{');

	private static XDocument Load() => XDocument.Load(Path.Combine(RepoRoot, Page), LoadOptions.SetLineInfo);

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
