using System.Xml.Linq;
using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Themes;

/// <summary>
/// Nixie 1.3.0: what every <see cref="ILayoutTheme"/> must give, checked without a XAML host (the contract tests, and each
/// theme's own tests): its views are pages the frame can create, and its dictionary defines the shared keys.
/// </summary>
public static class ThemeContract
{
	/// <summary>The keys every theme dictionary defines, with the element each one is.</summary>
	public static IReadOnlyList<(string Key, string Element)> RequiredKeys { get; } =
	[
		("TopBarStyle", "Style"),
		("RowHeaderTemplate", "DataTemplate"),
		("PosterTemplate", "DataTemplate"),
		("StillTemplate", "DataTemplate"),
		("PageHeaderStyle", "Style"),
	];

	/// <summary>The optional DataTemplate the shared LoginPage hosts behind its form (its content: the LoginViewModel).</summary>
	public const string SignInBackdropKey = "SignInBackdropTemplate";

	/// <summary>The key of the shared top bar's style (its TargetType: <see cref="Controls.TopBarChrome"/>).</summary>
	public const string TopBarStyleKey = "TopBarStyle";

	/// <summary>The view models a theme may give its own view; every other page is shared.</summary>
	public static IReadOnlyList<Type> ThemableViewModels { get; } =
	[
		typeof(HomeViewModel),
		typeof(SeriesViewModel),
		typeof(SeasonViewModel),
		typeof(MovieViewModel),
		typeof(EpisodeViewModel),
		typeof(LibraryViewModel),
	];

	public static bool IsThemable(Type viewModelType) => ThemableViewModels.Contains(viewModelType);

	/// <summary>What is wrong with the theme's views (empty when nothing is): each is a Page with a public parameterless
	/// constructor (the frame creates it), and no view stands for two view models.</summary>
	public static IReadOnlyList<string> ViewProblems(ILayoutTheme theme)
	{
		var problems = new List<string>();
		var seen = new Dictionary<Type, Type>();
		foreach (var vm in ThemableViewModels)
		{
			Type? view;
			try
			{
				view = theme.ViewFor(vm);
			}
			catch (Exception ex)
			{
				problems.Add($"{theme.Id}: ViewFor({vm.Name}) threw {ex.GetType().Name}");
				continue;
			}

			if (view is null)
			{
				continue;
			}

			if (!typeof(Page).IsAssignableFrom(view))
			{
				problems.Add($"{theme.Id}: the view for {vm.Name}, {view.Name}, is not a Page");
			}
			else if (view.IsAbstract || view.GetConstructor(Type.EmptyTypes) is null)
			{
				problems.Add($"{theme.Id}: the view for {vm.Name}, {view.Name}, has no public parameterless constructor");
			}

			if (seen.TryGetValue(view, out var other))
			{
				problems.Add($"{theme.Id}: {view.Name} is the view for both {other.Name} and {vm.Name}");
			}
			else
			{
				seen[view] = vm;
			}
		}

		return problems;
	}

	/// <summary>
	/// The keys a theme dictionary defines (its own and its merged dictionaries', following each Source), with the element
	/// of each. <paramref name="load"/> gives the document at an app-relative path ("Themes/DriveIn/Cards.xaml"), or null.
	/// </summary>
	public static IReadOnlyDictionary<string, XElement> DefinedKeys(Uri dictionary, Func<string, XDocument?> load)
	{
		var keys = new Dictionary<string, XElement>();
		Collect(AppPath(dictionary), load, keys, []);
		return keys;
	}

	/// <summary>What the dictionary misses of <see cref="RequiredKeys"/> (empty when nothing): a key absent, or not the element it must be.</summary>
	public static IReadOnlyList<string> KeyProblems(ILayoutTheme theme, Func<string, XDocument?> load)
	{
		var keys = DefinedKeys(theme.Dictionary, load);
		var problems = new List<string>();
		if (keys.Count == 0)
		{
			problems.Add($"{theme.Id}: no dictionary at {theme.Dictionary}");
		}

		foreach (var (key, element) in RequiredKeys)
		{
			if (!keys.TryGetValue(key, out var found))
			{
				problems.Add($"{theme.Id}: {key} is missing");
			}
			else if (found.Name.LocalName != element)
			{
				problems.Add($"{theme.Id}: {key} is a {found.Name.LocalName}, not a {element}");
			}
			else if (key == TopBarStyleKey && !((string?)found.Attribute("TargetType") ?? "").EndsWith("TopBarChrome", StringComparison.Ordinal))
			{
				problems.Add($"{theme.Id}: {key} must target TopBarChrome");
			}
		}

		if (keys.TryGetValue(SignInBackdropKey, out var backdrop) && backdrop.Name.LocalName != "DataTemplate")
		{
			problems.Add($"{theme.Id}: {SignInBackdropKey} is a {backdrop.Name.LocalName}, not a DataTemplate");
		}

		return problems;
	}

	/// <summary>"ms-appx:///Themes/DriveIn/DriveIn.xaml" → "Themes/DriveIn/DriveIn.xaml".</summary>
	public static string AppPath(Uri dictionary) => Uri.UnescapeDataString(dictionary.AbsolutePath).TrimStart('/');

	/// <summary>A merged dictionary's Source, from the dictionary at <paramref name="from"/>, as an app-relative path.</summary>
	public static string ResolveSource(string from, string source)
	{
		if (Uri.TryCreate(source, UriKind.Absolute, out var absolute) && absolute.Scheme == "ms-appx")
		{
			return AppPath(absolute);
		}

		if (source.StartsWith('/'))
		{
			return source.TrimStart('/');
		}

		var folder = Path.GetDirectoryName(from.Replace('\\', '/'))?.Replace('\\', '/') ?? "";
		var parts = new List<string>(folder.Split('/', StringSplitOptions.RemoveEmptyEntries));
		foreach (var part in source.Split('/', StringSplitOptions.RemoveEmptyEntries))
		{
			if (part == "..")
			{
				if (parts.Count > 0)
				{
					parts.RemoveAt(parts.Count - 1);
				}
			}
			else if (part != ".")
			{
				parts.Add(part);
			}
		}

		return string.Join('/', parts);
	}

	private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

	private static void Collect(string path, Func<string, XDocument?> load, Dictionary<string, XElement> keys, HashSet<string> visited)
	{
		if (!visited.Add(path) || load(path)?.Root is not { } root)
		{
			return;
		}

		// the dictionary's own keys win over its merged ones (XAML's lookup order)
		foreach (var element in root.Elements())
		{
			if ((string?)element.Attribute(Xaml + "Key") is { } key)
			{
				keys.TryAdd(key, element);
			}
		}

		foreach (var merged in root.Elements().Where(e => e.Name.LocalName == "ResourceDictionary.MergedDictionaries").Elements().Reverse())
		{
			if ((string?)merged.Attribute("Source") is { } source)
			{
				Collect(ResolveSource(path, source), load, keys, visited);
			}
		}
	}
}
