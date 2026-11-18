using System.Globalization;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core;

/// <summary>One line of an item page's quiet metadata: "Genres" and "Drama · Crime".</summary>
public sealed record ItemMetaRow(string Label, string Text);

/// <summary>
/// Seaglass: the words of an item page's top and its quiet metadata (genres, studios, directors, writers, tags): what the
/// title line says for a movie, a series, a season and an episode, and the lists the metadata rows show.
/// </summary>
public static class ItemMeta
{
	/// <summary>The big title: the item's name, or its series' for a season or an episode (the series logo stands for it).</summary>
	public static string HeroTitle(BaseItemDto? dto) =>
		dto is { Type: BaseItemDto_Type.Season or BaseItemDto_Type.Episode } && !string.IsNullOrWhiteSpace(dto.SeriesName) ? dto.SeriesName! : dto?.Name ?? "";

	/// <summary>The line under it: the season's or the episode's own name; nothing for a movie or a series.</summary>
	public static string HeroSubtitle(BaseItemDto? dto) =>
		dto is { Type: BaseItemDto_Type.Season or BaseItemDto_Type.Episode } ? dto.Name ?? "" : "";

	/// <summary>
	/// An episode row's title: "S1 E3 · The Fight" (1.2.0: the one format, <see cref="ItemLabels.EpisodeTitle"/>; it was
	/// "3. The Fight", and the hero's teal "S1 · E3" overline is gone: the code leads the hero's meta line instead).
	/// </summary>
	public static string EpisodeTitle(BaseItemDto? episode) => ItemLabels.EpisodeTitle(episode);

	/// <summary>Under a cast photo: the role ("Tony Stark") or, for the crew, what they did ("Director"); "" when neither is known.</summary>
	public static string PersonRole(BaseItemPerson? person) =>
		!string.IsNullOrWhiteSpace(person?.Role) ? person.Role.Trim()
		: person?.Type is { } type && type != BaseItemPerson_Type.Actor && type != BaseItemPerson_Type.Unknown && type != BaseItemPerson_Type.GuestStar ? type.ToString()!
		: "";

	/// <summary>The lines under the overview, in order; a line is left out when the item has none of it.</summary>
	public static IReadOnlyList<ItemMetaRow> Rows(BaseItemDto? dto)
	{
		var rows = new List<ItemMetaRow>();
		foreach (var (label, values) in new (string, IReadOnlyList<string>)[]
		{
			("Genres", Genres(dto)), ("Director", Directors(dto)), ("Writers", Writers(dto)), ("Studios", Studios(dto)), ("Tags", Tags(dto)),
		})
		{
			if (values.Count > 0)
			{
				rows.Add(new ItemMetaRow(label, string.Join(" · ", values)));
			}
		}

		return rows;
	}

	public static IReadOnlyList<string> Genres(BaseItemDto? dto) =>
		dto?.GenreItems is { Count: > 0 } items ? Names(items.Select(g => g.Name)) : Names(dto?.Genres);

	public static IReadOnlyList<string> Studios(BaseItemDto? dto) => Names(dto?.Studios?.Select(s => s.Name));

	public static IReadOnlyList<string> Directors(BaseItemDto? dto) => Names(dto?.People?.Where(p => p.Type == BaseItemPerson_Type.Director).Select(p => p.Name));

	public static IReadOnlyList<string> Writers(BaseItemDto? dto) => Names(dto?.People?.Where(p => p.Type == BaseItemPerson_Type.Writer).Select(p => p.Name));

	public static IReadOnlyList<string> Tags(BaseItemDto? dto) => Names(dto?.Tags);

	/// <summary>The first trailer link that is a web address, or null (anything else would be handed to the shell).</summary>
	public static Uri? TrailerUrl(BaseItemDto? dto) =>
		dto?.RemoteTrailers?.Select(t => Uri.TryCreate(t.Url?.Trim(), UriKind.Absolute, out var uri) ? uri : null)
			.FirstOrDefault(u => u is { Scheme: "https" or "http" });

	private static IReadOnlyList<string> Names(IEnumerable<string?>? names) =>
		[.. (names ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];

	private static string Number(int n) => n.ToString(CultureInfo.InvariantCulture);
}
