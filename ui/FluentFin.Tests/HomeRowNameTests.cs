using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.7.5, stream fx3 (the button review's L6, held since 1.5): Home's row names are in sentence case ("Continue
/// watching", "Up next"), as the rest of the app, in all four themes: the rows' headers, Drive-in's marquee over a loading
/// row, and the See all grid's title. The harness finds the rows by these names (tools\ui-harness\offscreen\then-*.ps1).
/// </summary>
public sealed partial class HomeRowNameTests
{
	private static readonly string[] Pages =
	[
		"Views/HomePage",
		"Themes/DriveIn/Views/DriveInHomePage",
		"Themes/Cinematic/Views/CinematicHomePage",
		"Themes/Editorial/Views/EditorialHomePage",
	];

	private static string Path_(string page, string extension) => AppVersionTests.FindUp(Path.Combine(["ui", "FluentFin", .. (page + extension).Split('/')]));

	/// <summary>A name in sentence case: no word after the first starts with a capital ("Continue watching").</summary>
	internal static bool SentenceCase(string name) =>
		name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).All(w => !char.IsUpper(w[0]));

	[Theory]
	[InlineData("Continue watching", true)]
	[InlineData("Up next", true)]
	[InlineData("New in Movies", false)] // a library's name inside a row's name: the rows that carry one are bound, not literal
	[InlineData("Continue Watching", false)]
	[InlineData("Next Up", false)]
	public void SentenceCase_TheRule(string name, bool sentence) => Assert.Equal(sentence, SentenceCase(name));

	[Fact]
	public void HomesRowNames_AreInSentenceCase_InEveryTheme()
	{
		var literal = new List<string>();
		foreach (var page in Pages)
		{
			var doc = XDocument.Load(Path_(page, ".xaml"));
			// 1.7.5 review: a TextBlock's too (Classic's loading title over the row); a display face's capitals ("UP NEXT") aren't names
			foreach (var e in doc.Descendants().Where(e => e.Name.LocalName is "ShelfRow" or "MarqueeSign" or "LetterBoard" or "TextBlock"))
			{
				foreach (var attribute in new[] { e.Attribute("Header"), e.Attribute("Text") })
				{
					if (attribute is { Value: { Length: > 0 } value } && !value.StartsWith('{') && value != value.ToUpperInvariant())
					{
						literal.Add($"{page}: {value}");
					}
				}
			}

			var code = File.ReadAllText(Path_(page, ".xaml.cs"));
			foreach (Match m in SeeAllTitle().Matches(code))
			{
				literal.Add($"{page}.xaml.cs: {m.Groups[1].Value}");
			}
		}

		Assert.Contains(literal, l => l.EndsWith(": Continue watching", StringComparison.Ordinal));
		Assert.Contains(literal, l => l.EndsWith(": Up next", StringComparison.Ordinal));
		Assert.All(literal, l => Assert.True(SentenceCase(l[(l.IndexOf(": ", StringComparison.Ordinal) + 2)..]), l));
	}

	[GeneratedRegex("new ItemGridRequest\\(\"([^\"]+)\"")]
	private static partial Regex SeeAllTitle();
}
