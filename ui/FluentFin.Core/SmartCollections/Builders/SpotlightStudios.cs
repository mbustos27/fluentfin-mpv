using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluentFin.Core.SmartCollections.Builders;

/// <summary>
/// Nixie 1.8 (P1-3): a studio with a house style the weekly Spotlight may choose (<see cref="SpotlightStudios"/>): its name as
/// the tile says it, and the other names servers list it under.
/// </summary>
public sealed record SpotlightStudio(string Name, IReadOnlyList<string> Aliases)
{
	/// <summary>Its id part: the name's letters and digits, lower case ("Studio Ghibli" → "studioghibli").</summary>
	public string Key => Make.Key(Name);

	/// <summary>Every name it matches under, as keys (its name's and each alias's).</summary>
	public IEnumerable<string> Keys => Aliases.Prepend(Name).Select(Make.Key).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal);
}

/// <summary>
/// Nixie 1.8 (P1-3; the report: "a short curated list of house-style studios (Ghibli, A24, Pixar types) feeds Spotlight", as
/// Kometa builds its studios from a curated include list that merges aliases): the studios the Spotlight may choose, from the
/// data file <c>SmartCollections\Data\spotlight-studios.json</c> (embedded). A studio's name matches a title's studio when
/// their letters and digits are the same, case aside ("Pixar Animation Studios" is Pixar). A file that can't be read is an
/// empty list: the Spotlight then chooses among people only, and nothing throws.
/// </summary>
public static class SpotlightStudios
{
	/// <summary>The file's name in the assembly.</summary>
	public const string Resource = "FluentFin.Core.SmartCollections.Data.spotlight-studios.json";

	private static readonly Lazy<Loaded> s_loaded = new(() => Load(typeof(SpotlightStudios).Assembly.GetManifestResourceStream(Resource)));

	/// <summary>The curated studios, in the file's order.</summary>
	public static IReadOnlyList<SpotlightStudio> All => s_loaded.Value.Studios;

	/// <summary>The curated studio a title's studio name is, or null.</summary>
	public static SpotlightStudio? Of(string? studio) => s_loaded.Value.Of(studio);

	/// <summary>A list read from <paramref name="json"/> (null, damaged or empty: none).</summary>
	internal static Loaded Load(Stream? json)
	{
		if (json is null)
		{
			return Loaded.None;
		}

		try
		{
			using (json)
			{
				var file = JsonSerializer.Deserialize<StudioFile>(json, s_options);
				var studios = (file?.Studios ?? [])
					.OfType<StudioLine>()
					.Where(x => !string.IsNullOrWhiteSpace(x.Name) && Make.Key(x.Name).Length > 0)
					.Select(x => new SpotlightStudio(x.Name!.Trim(), [.. (x.Aliases ?? []).OfType<string>().Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim())]))
					.ToList();
				return new Loaded(studios);
			}
		}
		catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException or InvalidOperationException)
		{
			return Loaded.None;
		}
	}

	private static readonly JsonSerializerOptions s_options = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

	private sealed class StudioFile
	{
		[JsonPropertyName("studios")]
		public List<StudioLine?>? Studios { get; set; }
	}

	private sealed class StudioLine
	{
		[JsonPropertyName("name")]
		public string? Name { get; set; }

		[JsonPropertyName("aliases")]
		public List<string?>? Aliases { get; set; }
	}

	/// <summary>A list read: the studios, and each of their keys to its studio (a key two studios claim is the first's).</summary>
	internal sealed class Loaded
	{
		private readonly Dictionary<string, SpotlightStudio> _byKey = new(StringComparer.Ordinal);

		public Loaded(IReadOnlyList<SpotlightStudio> studios)
		{
			Studios = studios;
			foreach (var studio in studios)
			{
				foreach (var key in studio.Keys)
				{
					_byKey.TryAdd(key, studio);
				}
			}
		}

		public static Loaded None { get; } = new([]);

		public IReadOnlyList<SpotlightStudio> Studios { get; }

		public SpotlightStudio? Of(string? studio) =>
			string.IsNullOrWhiteSpace(studio) ? null : _byKey.TryGetValue(Make.Key(studio), out var found) ? found : null;
	}
}
