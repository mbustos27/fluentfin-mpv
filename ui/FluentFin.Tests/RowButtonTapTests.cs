using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FluentFin.Tests;

/// <summary>
/// 174-signin (a friend's report: "when i hit the 3 dots to try and remove it opens up log in"): a button inside an item
/// the list invokes (ItemsView's ItemContainer with IsItemInvokedEnabled) takes the press as its click but not the tap that
/// follows, and ItemContainer invokes its item on a tap: the server list's More options opened the server's sign-in over its
/// menu. Each such button keeps its tap and double tap (handlers that mark them handled), or the page ignores an invoke its
/// button took (the Libraries page's star, 1.6.1). Read from the source: a tap can't be made off-screen (posted clicks do
/// nothing on a desktop nobody sees, HarnessPointer clicks through the automation peer), so this is the check that fails
/// without the fix; tools\ui-harness\offscreen\before-174-servermenu.ps1 presses the menu every other way.
/// </summary>
public sealed class RowButtonTapTests
{
	private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
	private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

	private static string App => Path.Combine(FlyleafEngineTests.RepoRoot, "FluentFin");

	/// <summary>The button kinds a press on goes to the button (its click), not the item.</summary>
	private static readonly string[] Buttons = ["Button", "ToggleButton", "HyperlinkButton", "DropDownButton", "SplitButton", "AppBarButton", "RepeatButton"];

	/// <summary>Pages whose invoke handler already ignores an invoke a button in the item took (and how).</summary>
	private static readonly Dictionary<string, string> GuardedPages = new()
	{
		[Path.Combine("Views", "LibrariesLandingPage.xaml")] = "StarTookTheClick(",
	};

	/// <summary>The buttons inside the item templates of every invoked ItemsView: file, button, its handlers' names.</summary>
	private static IEnumerable<(string File, XElement Button)> ButtonsInInvokedItems()
	{
		foreach (var file in Directory.EnumerateFiles(App, "*.xaml", SearchOption.AllDirectories))
		{
			if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
			{
				continue;
			}

			var text = File.ReadAllText(file);
			if (!text.Contains("IsItemInvokedEnabled=\"True\"", StringComparison.Ordinal))
			{
				continue;
			}

			var doc = XDocument.Parse(text);
			var lists = doc.Descendants(Xaml + "ItemsView").Where(v => (string?)v.Attribute("IsItemInvokedEnabled") == "True").ToList();
			var templates = new List<XElement>();
			foreach (var list in lists)
			{
				// the template inline, or one of the file's resources by its key
				templates.AddRange(list.Elements(Xaml + "ItemsView.ItemTemplate").Elements(Xaml + "DataTemplate"));
				if ((string?)list.Attribute("ItemTemplate") is { } reference && Regex.Match(reference, @"\{StaticResource (\w+)\}") is { Success: true } key)
				{
					templates.AddRange(doc.Descendants(Xaml + "DataTemplate").Where(t => (string?)t.Attribute(X + "Key") == key.Groups[1].Value));
				}
			}

			var relative = Path.GetRelativePath(App, file);
			foreach (var button in templates.SelectMany(t => t.Descendants()).Where(e => e.Name.Namespace == Xaml && Buttons.Contains(e.Name.LocalName)))
			{
				yield return (relative, button);
			}
		}
	}

	[Fact]
	public void TheServerListsMoreOptions_KeepsItsTapAndDoubleTap()
	{
		var buttons = ButtonsInInvokedItems().Where(b => b.File == Path.Combine("Views", "SelectServerPage.xaml")).Select(b => b.Button).ToList();
		var more = Assert.Single(buttons, b => ((string?)b.Attribute("AutomationProperties.Name"))?.Contains("MoreOptionsName", StringComparison.Ordinal) == true);
		var code = File.ReadAllText(Path.Combine(App, "Views", "SelectServerPage.xaml.cs"));

		foreach (var gesture in new[] { "Tapped", "DoubleTapped" })
		{
			var handler = (string?)more.Attribute(gesture);
			Assert.False(string.IsNullOrEmpty(handler), $"More options has no {gesture} handler: its {gesture} reaches the row, which opens the server's sign-in");
			Assert.Matches($@"void {handler}\(object sender, Microsoft\.UI\.Xaml\.Input\.{gesture}RoutedEventArgs e\) => e\.Handled = true;", code);
		}
	}

	/// <summary>
	/// 174-signin: the server list lets go of the saved servers (the settings' collection, which outlives the page) when the
	/// page goes, and takes them again if it comes back. A server page left for the sign-in page kept listening, and Remove on
	/// the next one threw in the old list's handler after the file was saved: the removed row stayed on screen
	/// (before-174-servermenu.ps1 saw it, its log the COMException).
	/// </summary>
	[Fact]
	public void TheServerList_LetsGoOfTheSavedServers_WhenThePageGoes()
	{
		var code = File.ReadAllText(Path.Combine(App, "Views", "SelectServerPage.xaml.cs"));

		Assert.Contains("Unloaded += (_, _) => SavedServersView.ItemsSource = null;", code, StringComparison.Ordinal);
		Assert.Contains("SavedServersView.ItemsSource ??= ViewModel.Servers;", code, StringComparison.Ordinal);
	}

	[Fact]
	public void EveryButtonInAnInvokedItem_KeepsItsTap_OrThePageIgnoresTheInvoke()
	{
		var found = ButtonsInInvokedItems().ToList();
		Assert.NotEmpty(found); // the search still finds the server list's button

		var passing = new List<string>();
		foreach (var (file, button) in found)
		{
			if (button.Attribute("Tapped") is not null && button.Attribute("DoubleTapped") is not null)
			{
				continue;
			}

			if (GuardedPages.TryGetValue(file, out var guard) && File.ReadAllText(Path.Combine(App, file + ".cs")).Contains(guard, StringComparison.Ordinal))
			{
				continue;
			}

			passing.Add($"{file}: {button.Name.LocalName} {(string?)button.Attribute("AutomationProperties.Name") ?? (string?)button.Attribute(X + "Name") ?? ""}");
		}

		Assert.True(passing.Count == 0, "a press on these opens the item under them too:\n" + string.Join("\n", passing));
	}
}
