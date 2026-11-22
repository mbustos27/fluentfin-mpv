namespace FluentFin.Core.SmartCollections;

/// <summary>
/// Nixie 1.6 (the plan's 1.3, Keywords): the TMDb keywords Nixie makes collections of, each with a friendly name. Jellyfin
/// files TMDb's keywords as the items' Tags (movies and shows, since 10.7); a tag typed by hand never matches this list, so
/// it never shows as a keyword. Compared trimmed and without case (10.11 kept "simulated reality " with its space).
/// </summary>
public static class Keywords
{
	/// <summary>TMDb keyword → the collection's name.</summary>
	public static IReadOnlyDictionary<string, string> Allowed { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		["heist"] = "Heists",
		["time travel"] = "Time travel",
		["coming of age"] = "Coming of age",
		["slasher"] = "Slashers",
		["dystopia"] = "Dystopias",
		["based on novel or book"] = "Based on a book",
		["based on comic"] = "Based on a comic",
		["based on manga"] = "Based on a manga",
		["based on true story"] = "True stories",
		["biography"] = "Biographies",
		["superhero"] = "Superheroes",
		["zombie"] = "Zombies",
		["vampire"] = "Vampires",
		["werewolf"] = "Werewolves",
		["haunted house"] = "Haunted houses",
		["ghost"] = "Ghost stories",
		["alien"] = "Aliens",
		["alien invasion"] = "Alien invasions",
		["artificial intelligence (a.i.)"] = "Artificial intelligence",
		["robot"] = "Robots",
		["cyberpunk"] = "Cyberpunk",
		["post-apocalyptic future"] = "After the apocalypse",
		["space"] = "Space",
		["space travel"] = "Space travel",
		["virtual reality"] = "Virtual reality",
		["simulated reality"] = "Simulated worlds",
		["parallel world"] = "Parallel worlds",
		["time loop"] = "Time loops",
		["martial arts"] = "Martial arts",
		["kung fu"] = "Kung fu",
		["samurai"] = "Samurai",
		["spy"] = "Spies",
		["espionage"] = "Espionage",
		["assassin"] = "Assassins",
		["serial killer"] = "Serial killers",
		["detective"] = "Detectives",
		["murder mystery"] = "Murder mysteries",
		["whodunit"] = "Whodunits",
		["film noir"] = "Film noir",
		["neo-noir"] = "Neo-noir",
		["revenge"] = "Revenge",
		["prison"] = "Prison",
		["survival"] = "Survival",
		["road trip"] = "Road trips",
		["buddy cop"] = "Buddy cops",
		["mockumentary"] = "Mockumentaries",
		["parody"] = "Parodies",
		["satire"] = "Satire",
		["dark comedy"] = "Dark comedies",
		["romcom"] = "Romantic comedies",
		["musical"] = "Musicals",
		["sports"] = "Sports",
		["boxing"] = "Boxing",
		["dinosaur"] = "Dinosaurs",
		["dragon"] = "Dragons",
		["magic"] = "Magic",
		["fairy tale"] = "Fairy tales",
		["sword and sorcery"] = "Sword and sorcery",
		["anthology"] = "Anthologies",
		["found footage"] = "Found footage",
		["remake"] = "Remakes",
		["anime"] = "Anime",
		["found family"] = "Found family",
		["short film"] = "Short films",
		["world war ii"] = "World War II",
		["cold war"] = "The Cold War",
		["western"] = "Westerns",
		["black and white"] = "Black and white",
		["silent film"] = "Silent films",
		["stop motion"] = "Stop motion",
	};

	/// <summary>Seasonal keywords the index keeps too (the seasonal Home row), beside the allowlist.</summary>
	public static IReadOnlySet<string> Seasonal { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"halloween", "christmas", "christmas party", "christmas eve", "santa claus", "holiday",
	};

	/// <summary>The tags the index keeps of a title's: those on either list, trimmed and lower-cased.</summary>
	public static string[] Kept(IEnumerable<string?>? tags) =>
		tags is null ? [] : [.. tags.Select(t => t?.Trim().ToLowerInvariant()).Where(t => !string.IsNullOrEmpty(t) && (Allowed.ContainsKey(t) || Seasonal.Contains(t))).Distinct()!];

	// 1.7 keyless (review #1): TMDb's keyword names only confirm the allowlist, never add to it: a server's tags can be any of
	// TMDb's ~100,000 keywords, junk and harmful ones too ("duringcreditsstinger", "suicide"), and only hand-picked keywords
	// may name a collection (the plan's 1.3).
	/// <summary>
	/// 1.7: a keyword's collection name ("Heists"), when it is on the allowlist and, with TMDb's keyword export on
	/// (<paramref name="tmdbKeywords"/>, lower case), TMDb lists it too; null for anything else. With the export off (empty),
	/// the allowlist alone, as before.
	/// </summary>
	public static string? NameOf(string tag, IReadOnlyDictionary<string, string> tmdbKeywords) =>
		NameOf(tag) is { } name && (tmdbKeywords.Count == 0 || tmdbKeywords.ContainsKey(tag.Trim().ToLowerInvariant())) ? name : null;
	// end 1.7 keyless

	/// <summary>A keyword's collection name ("Heists"), or null when it is not on the list.</summary>
	public static string? NameOf(string tag) => Allowed.TryGetValue(tag.Trim(), out var name) ? name : null;
}
