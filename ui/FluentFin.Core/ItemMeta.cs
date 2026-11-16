using System.Globalization;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core;

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

	/// <summary>"S2 · E3" above an episode's title; "" for anything else.</summary>
	public static string Eyebrow(BaseItemDto? dto) => dto is { Type: BaseItemDto_Type.Episode } ? (dto.ParentIndexNumber, dto.IndexNumber) switch
	{
		({ } s, { } e) => $"S{Number(s)} \u00B7 E{Number(e)}",
		(null, { } e) => $"E{Number(e)}",
		_ => "",
	} : "";

	/// <summary>Narrator's reading of <see cref="Eyebrow"/>: "Season 2, episode 3".</summary>
	public static string EyebrowName(BaseItemDto? dto) => dto is { Type: BaseItemDto_Type.Episode } ? (dto.ParentIndexNumber, dto.IndexNumber) switch
	{
		({ } s, { } e) => $"Season {Number(s)}, episode {Number(e)}",
		(null, { } e) => $"Episode {Number(e)}",
		_ => "",
	} : "";

	/// <summary>"3. The Fight" for an episode row.</summary>
	public static string EpisodeTitle(BaseItemDto? episode) => episode is null ? "" : episode.IndexNumber is { } n ? $"{Number(n)}. {episode.Name}" : episode.Name ?? "";

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
