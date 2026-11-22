using System.Globalization;
using System.Text.Json;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

/// <summary>Nixie 1.4.0: the kinds of filter a library has (a facet's, or a switch).</summary>
public enum FilterGroup
{
	Unwatched,
	Favorites,
	Genres,
	Years,
	Ratings,
	Tags,

	/// <summary>1.6 colpage: a Nixie collection's titles, picked in the presets strip.</summary>
	Preset,
}

/// <summary>
/// Nixie 1.4.0 (the UX review's X4, item 2): one filter on, as the applied-filter row shows it ("Comedy ✕"); its button takes
/// that one filter off.
/// </summary>
public sealed record AppliedFilter(FilterGroup Group, string Value, string Label)
{
	/// <summary>What Narrator hears for its button: "Remove filter Comedy".</summary>
	public string Name => $"Remove filter {Label}";
}

/// <summary>Nixie 1.4.0 (item 2): the words of the state a filter that leaves nothing shows (distinct from loading and from an empty library).</summary>
public static class LibraryNoMatches
{
	/// <summary>
	/// "No unwatched movies", "No unwatched favorite shows", "No movies in Comedy or Drama", "No titles from 1999"; with more
	/// kinds on than one line can say, "No movies match these filters".
	/// </summary>
	public static string Title(LibraryFilter filter, BaseItemDto_CollectionType? collection)
	{
		var noun = collection switch
		{
			BaseItemDto_CollectionType.Movies => "movies",
			BaseItemDto_CollectionType.Tvshows => "shows",
			_ => "titles",
		};

		var kinds = (filter.Genres.Count > 0 ? 1 : 0) + (filter.Years.Count > 0 ? 1 : 0) + (filter.OfficialRatings.Count > 0 ? 1 : 0) + (filter.Tags.Count > 0 ? 1 : 0);
		if (kinds > 1)
		{
			return $"No {noun} match these filters";
		}

		var words = new List<string> { "No" };
		if (filter.Unwatched)
		{
			words.Add("unwatched");
		}

		if (filter.Favorites)
		{
			words.Add("favorite");
		}

		words.Add(noun);
		if (filter.Genres.Count > 0)
		{
			words.Add($"in {Or(filter.Genres)}");
		}
		else if (filter.Years.Count > 0)
		{
			words.Add($"from {Or(filter.Years)}");
		}
		else if (filter.OfficialRatings.Count > 0)
		{
			words.Add($"rated {Or(filter.OfficialRatings)}");
		}
		else if (filter.Tags.Count > 0)
		{
			words.Add($"tagged {Or(filter.Tags)}");
		}
		else if (filter.Preset is { } preset)
		{
			words.Add($"in {preset.Label}"); // 1.6 colpage: a Nixie collection's titles
		}

		return string.Join(' ', words);
	}

	/// <summary>"Comedy", "Comedy or Drama", "Comedy, Drama or Horror"; more than three: "any of 5 genres" style is not needed (the chips list them).</summary>
	private static string Or(IReadOnlyList<string> values) => values.Count switch
	{
		1 => values[0],
		2 => $"{values[0]} or {values[1]}",
		_ => $"{string.Join(", ", values.Take(values.Count - 1))} or {values[^1]}",
	};

	/// <summary>What to do about it.</summary>
	public const string Message = "Take a filter off, or clear them all.";
}

/// <summary>Nixie 1.4.0 (item 13): how big a library's posters are.</summary>
public enum LibraryDensity
{
	Small,
	Medium,
	Large,
}

/// <summary>Nixie 1.4.0 (item 13): a library as a grid of posters or a list of rows.</summary>
public enum LibraryLayout
{
	Grid,
	List,
}

/// <summary>Nixie 1.4.0 (item 12): Cinematic's grid of 16:9 stills or of posters.</summary>
public enum LibraryArt
{
	Stills,
	Posters,
}

/// <summary>Nixie 1.4.0 (items 12 and 13): how one library is shown (kept per library, on this PC).</summary>
public sealed record LibraryView(LibraryDensity Density = LibraryDensity.Medium, LibraryLayout Layout = LibraryLayout.Grid, LibraryArt Art = LibraryArt.Stills)
{
	public static LibraryView Default { get; } = new();
}

/// <summary>
/// Nixie 1.4.0: each library's view (density, grid or list, stills or posters), kept on this PC by library id: one JSON
/// setting. Without a settings service (tests), kept for the session.
/// </summary>
public static class LibraryViews
{
	public const string Key = "LibraryViews";

	private static readonly Lock s_lock = new();
	private static readonly Dictionary<string, LibraryView> s_session = [];

	public static LibraryView Read(ILocalSettingsService? settings, Guid? library)
	{
		if (library is not { } id)
		{
			return LibraryView.Default;
		}

		lock (s_lock)
		{
			return All(settings).GetValueOrDefault(id.ToString("N"), LibraryView.Default);
		}
	}

	public static void Save(ILocalSettingsService? settings, Guid? library, LibraryView view)
	{
		if (library is not { } id)
		{
			return;
		}

		lock (s_lock)
		{
			var all = All(settings);
			all[id.ToString("N")] = view;
			if (settings is null)
			{
				return;
			}

			try
			{
				settings.SaveSetting(Key, JsonSerializer.Serialize(all.ToDictionary(x => x.Key, x => Encode(x.Value))));
			}
			catch (Exception)
			{
				// a view that cannot be kept is still the view for this session
			}
		}
	}

	private static Dictionary<string, LibraryView> All(ILocalSettingsService? settings)
	{
		if (settings is null)
		{
			return s_session;
		}

		try
		{
			var json = settings.ReadSetting(Key, "");
			if (string.IsNullOrWhiteSpace(json))
			{
				return [];
			}

			return (JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []).ToDictionary(x => x.Key, x => Decode(x.Value));
		}
		catch (Exception)
		{
			return []; // a hand-edited value reads as the defaults
		}
	}

	/// <summary>"M,grid,stills".</summary>
	internal static string Encode(LibraryView view) =>
		string.Join(',', view.Density.ToString(), view.Layout.ToString(), view.Art.ToString()).ToLower(CultureInfo.InvariantCulture);

	internal static LibraryView Decode(string text)
	{
		var parts = (text ?? "").Split(',');
		return new LibraryView(
			parts.Length > 0 && Enum.TryParse<LibraryDensity>(parts[0], true, out var density) && Enum.IsDefined(density) ? density : LibraryDensity.Medium,
			parts.Length > 1 && Enum.TryParse<LibraryLayout>(parts[1], true, out var layout) && Enum.IsDefined(layout) ? layout : LibraryLayout.Grid,
			parts.Length > 2 && Enum.TryParse<LibraryArt>(parts[2], true, out var art) && Enum.IsDefined(art) ? art : LibraryArt.Stills);
	}
}
