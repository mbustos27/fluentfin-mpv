using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

/// <summary>
/// Nixie 1.6 (smart collections phase 0, the plan's section 5): collection (box set) and library names as people read them:
/// "[HUB] Marvel" shows as "Marvel", "!010_Bond" as "Bond", "🎅 Christmas" as "Christmas". For display only: search, a
/// rename and the "Shown on the server as …" line keep the server's name. Movies, shows, seasons, episodes and playlists
/// are never cleaned (<see cref="Display"/> hands their names back as they are, without calling the cleaner). Settings →
/// Collections → "Tidy collection names" turns it off (<see cref="Tidy"/>).
/// <para>
/// The rules, in order, each once: invisible characters; known tags at the start (a bracketed allowlist, never a generic
/// "short capitals in brackets" rule, so "[REC]" stays); known tags at the end; emoji at the edges; leaked sort keys
/// ("!020_", "01. "); sort-forcing punctuation ("!!Marvel", "~Misc", "! Favorites"); "zz" prefixes; " Collection" after a
/// TMDb-named box set; a separator left dangling at an edge. The server's name comes back when nothing readable is left,
/// or when two names would read the same: two of the server's box sets or libraries (1.6.1: <see cref="Display"/> and
/// <see cref="DisplayLibrary"/>, one rule), or two in one view (<see cref="DisplayAll"/>).
/// </para>
/// </summary>
public static partial class CollectionNames
{
	/// <summary>Settings → Collections → "Tidy collection names" (on by default). The app sets it from the setting.</summary>
	public static bool Tidy
	{
		get => s_tidy;
		set
		{
			if (s_tidy == value)
			{
				return;
			}

			s_tidy = value;
			TidyChanged?.Invoke(null, EventArgs.Empty);
		}
	}

	private static volatile bool s_tidy = true;

	/// <summary>
	/// Nixie 1.6.1 (the merge review's R4): <see cref="Tidy"/> changed. Names a view keeps (the top bar's tabs, built once)
	/// are made again: Settings → Collections → "Tidy collection names" renames them at once.
	/// </summary>
	public static event EventHandler? TidyChanged;

	/// <summary>Tests: how many times the cleaner ran (it never runs for a film, a show, a season, an episode or a playlist).</summary>
	internal static int CleanerCalls;

	/// <summary>
	/// The name to show for <paramref name="item"/>: a box set's tidied (<see cref="Tidy"/> on), any other item's as the
	/// server sent it. Never null.
	/// </summary>
	/// <remarks>
	/// Nixie 1.6.1 (bug hunt BH-160-M2): unique, as the server's box sets are. A tidied name that another of the user's box sets
	/// has, or tidies to, keeps the server's name ("Winter Films" and "Winter Films (4K)" both stay as they are) wherever it
	/// shows: search, the Collection page's title, cards, "Part of the collection", the Collections page. The box sets are those
	/// the client read last (<see cref="RememberBoxSets"/>), and any search showed (<see cref="NoteBoxSets"/>). One rule for
	/// box sets and libraries (<see cref="Unique"/>).
	/// </remarks>
	public static string Display(BaseItemDto? item)
	{
		var name = item?.Name ?? "";
		if (!Tidy || item?.Type != BaseItemDto_Type.BoxSet)
		{
			return name;
		}

		// review of 1.6.1 (F2): until this user's box sets are known, a name that would be tidied keeps the server's (another
		// set could read the same: "Winter Films" and "Winter Films (4K)" on a first start, before the list is in)
		var tidied = Clean(name, HasTmdbId(item));
		return string.Equals(tidied, name, StringComparison.Ordinal) ? name
			: BoxSetsFor(Owner()) is { } known ? Unique(name, tidied, known)
			: name;
	}

	/// <summary>
	/// A page title for what a library page opens: a box set's <see cref="Display"/>, a library's (a collection folder or a
	/// user view) <see cref="DisplayLibrary"/>, anything else's name as it is.
	/// </summary>
	public static string DisplayView(BaseItemDto? item) => item?.Type switch
	{
		BaseItemDto_Type.BoxSet => Display(item),
		BaseItemDto_Type.CollectionFolder or BaseItemDto_Type.UserView or BaseItemDto_Type.AggregateFolder => DisplayLibrary(item.Name),
		_ when item?.CollectionType is not null && item.Type is BaseItemDto_Type.Folder => DisplayLibrary(item.Name),
		_ => item?.Name ?? "",
	};

	/// <summary>
	/// The details' "Shown on the server as “[HUB] Marvel”" when the name shown is tidied; "" when it is the server's own (a
	/// search, a rename and the server's other apps use that one).
	/// </summary>
	public static string ServerNameNote(BaseItemDto? item) =>
		item?.Name is { Length: > 0 } name && Display(item) != name ? $"Shown on the server as “{name}”" : "";

	/// <summary>A library's name as shown (the top bar, the Libraries page, Home's "New in …"): tidied like a box set's.</summary>
	/// <remarks>
	/// Review Major 5: unique, as the libraries' own names are. A library's quality suffix is part of its name ("Movies" and
	/// "Movies (4K)" are two libraries), so it stays; and a tidied name that another of the server's libraries has, or tidies
	/// to, keeps the original ("Movies" and "[HUB] Movies" both stay as they are). The server's libraries are those the
	/// client read last (<see cref="RememberLibraries"/>).
	/// </remarks>
	public static string DisplayLibrary(string? name)
	{
		if (!Tidy || string.IsNullOrEmpty(name))
		{
			return name ?? "";
		}

		return Unique(name, Clean(name, fromTmdb: false, library: true), s_libraries is { } list && list.Owner == Owner() ? list.Names : []);
	}

	/// <summary>A name the server has, and how it reads tidied.</summary>
	private readonly record struct Known(string Server, string Tidied);

	/// <summary>Review of 1.6.1 (F2): a list of the server's names and whose it is ("server|user"; null before a sign-in).</summary>
	private sealed record KnownList(string? Owner, Known[] Names);

	/// <summary>
	/// Review of 1.6.1 (F2): whose lists count (the signed-in server and user, as the favourites are kept). A list read for
	/// another user (before a sign-out or a switch) is not this one's: the names it holds neither keep nor tidy theirs.
	/// </summary>
	internal static Func<string?> Owner { get; set; } = FavouritesStore.ProfileKey;

	private static volatile KnownList? s_libraries;

	/// <summary>The user's box sets, null until a list was read (<see cref="RememberBoxSets"/>).</summary>
	private static volatile KnownList? s_boxSets;

	private static readonly object s_boxSetsLock = new();

	private static Known[]? BoxSetsFor(string? owner) => s_boxSets is { } list && list.Owner == owner ? list.Names : null;

	/// <summary>Review of 1.6.1 (F2): this user's box sets were read (until then, names that would be tidied stay the server's).</summary>
	public static bool BoxSetsKnown => BoxSetsFor(Owner()) is not null;

	/// <summary>Tests: back to a first start, no box set list read.</summary>
	internal static void ForgetBoxSets()
	{
		lock (s_boxSetsLock)
		{
			s_boxSets = null;
		}
	}

	/// <summary>
	/// Nixie 1.6.1: the one rule for a tidied name (a box set's and a library's): <paramref name="tidied"/>, unless another of
	/// the server's names (<paramref name="known"/>, any but <paramref name="name"/> itself) is it or reads as it, where the
	/// server's <paramref name="name"/> stays.
	/// </summary>
	private static string Unique(string name, string tidied, Known[] known)
	{
		if (string.Equals(tidied, name, StringComparison.Ordinal))
		{
			return name;
		}

		foreach (var other in known)
		{
			if (!string.Equals(other.Server, name, StringComparison.Ordinal)
				&& (string.Equals(other.Server, tidied, StringComparison.CurrentCultureIgnoreCase) || string.Equals(other.Tidied, tidied, StringComparison.CurrentCultureIgnoreCase)))
			{
				return name;
			}
		}

		return tidied;
	}

	/// <summary>The server's libraries, as the client read them (its views): a tidied name that two would share stays as it is.</summary>
	public static void RememberLibraries(IEnumerable<string?> names) =>
		s_libraries = new(Owner(), [.. names.Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).Distinct(StringComparer.Ordinal)
			.Select(x => new Known(x, Clean(x, fromTmdb: false, library: true)))]);

	/// <summary>
	/// Nixie 1.6.1 (BH-160-M2): the user's box sets, as the client read them (<c>GetBoxSets</c>), in place of those known: a
	/// tidied name that two would share stays as the server has it, wherever one shows.
	/// </summary>
	public static void RememberBoxSets(IEnumerable<BaseItemDto?> boxSets)
	{
		lock (s_boxSetsLock)
		{
			s_boxSets = new(Owner(), KnownBoxSets(boxSets, []));
		}
	}

	/// <summary>
	/// Nixie 1.6.1 (BH-160-M2): box sets a page was given (search's answer) added to the user's list, once it is known (before,
	/// every name that would be tidied keeps the server's anyway).
	/// </summary>
	public static void NoteBoxSets(IEnumerable<BaseItemDto?> items)
	{
		lock (s_boxSetsLock)
		{
			var owner = Owner();
			if (BoxSetsFor(owner) is not { } known)
			{
				return;
			}

			var added = KnownBoxSets(items, known);
			if (added.Length > 0)
			{
				s_boxSets = new(owner, [.. known, .. added]);
			}
		}
	}

	private static Known[] KnownBoxSets(IEnumerable<BaseItemDto?> items, Known[] already) =>
		[.. items.Where(x => x is { Type: BaseItemDto_Type.BoxSet, Name.Length: > 0 }).Select(x => new Known(x!.Name!, Clean(x.Name!, HasTmdbId(x))))
			.Where(k => !already.Contains(k)).Distinct()];

	/// <summary>
	/// The names to show for the items of one view (a page of box sets, the top bar's libraries), in their order: each
	/// <see cref="Display"/>ed, except where two would read the same ("Spider-Man Collection (4K HDR)" and "… (SD)"): those
	/// keep the server's names.
	/// </summary>
	/// <remarks>Review of 1.6.1 (F2): before the user's list is known, the view's own box sets are the list (the Collections page has them all).</remarks>
	public static IReadOnlyList<string> DisplayAll(IReadOnlyList<BaseItemDto> items)
	{
		var known = BoxSetsFor(Owner()) ?? KnownBoxSets(items, []);
		string Shown(BaseItemDto item)
		{
			var name = item.Name ?? "";
			return !Tidy || item.Type != BaseItemDto_Type.BoxSet ? name : Unique(name, Clean(name, HasTmdbId(item)), known);
		}

		return WithoutCollisions([.. items.Select(x => x.Name ?? "")], [.. items.Select(Shown)]);
	}

	/// <summary><see cref="DisplayAll"/> for library names (<see cref="DisplayLibrary"/>).</summary>
	public static IReadOnlyList<string> DisplayAllLibraries(IReadOnlyList<string?> names) =>
		WithoutCollisions([.. names.Select(x => x ?? "")], [.. names.Select(DisplayLibrary)]);

	private static List<string> WithoutCollisions(List<string> server, List<string> shown)
	{
		var counts = shown.GroupBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.CurrentCultureIgnoreCase);
		for (var i = 0; i < shown.Count; i++)
		{
			// a tidied name that two items share, or that is another item's own server name, is the server's again
			if (shown[i] != server[i] && (counts[shown[i]] > 1 || server.Where((s, j) => j != i).Contains(shown[i], StringComparer.CurrentCultureIgnoreCase)))
			{
				shown[i] = server[i];
			}
		}

		return shown;
	}

	private static bool HasTmdbId(BaseItemDto item) =>
		item.ProviderIds?.AdditionalData is { } ids && ids.Any(x => string.Equals(x.Key, "Tmdb", StringComparison.OrdinalIgnoreCase) && x.Value is string s && s.Length > 0);

	/// <summary>
	/// The rules of section 5 on one name. <paramref name="fromTmdb"/>: the box set carries a TMDb id (TMDb named it), so a
	/// trailing " Collection" goes. Returns <paramref name="name"/> itself when nothing readable would be left.
	/// </summary>
	public static string Clean(string name, bool fromTmdb) => Clean(name, fromTmdb, library: false);

	/// <param name="library">A library's name: its quality suffix ("(4K)") stays, since it tells two libraries apart.</param>
	internal static string Clean(string name, bool fromTmdb, bool library)
	{
		Interlocked.Increment(ref CleanerCalls);
		if (string.IsNullOrEmpty(name))
		{
			return name ?? "";
		}

		// 1. invisible characters (a joiner inside an emoji sequence stays), the edges, runs of spaces
		var s = Collapse(WithoutInvisible(name));
		var server = s;

		// rules 2 to 9, again while one uncovers another's decoration ("zz_!!Misc"), so cleaning twice changes nothing.
		// Nixie 1.6.1 (the 1.6 review): 8 and 9 are in the passes too: "Marvel (4K) Collection" (TMDb) lost " Collection"
		// after the passes, which uncovered "(4K)" for a second cleaning, and "[HUB] Marvel Collection -" its separator
		for (var pass = 0; pass < 6; pass++)
		{
			var before = s;
			// 2. known tags at the start, repeated, with a separator after them
			s = StartTags().Replace(s, "", 1);
			// 3. known tags at the end
			s = library ? s : EndTags().Replace(s, "", 1);
			// 4. emoji at the edges (with a space between them and the name)
			s = WithoutEdgeEmoji(s);
			// 5. leaked sort keys: "!020_", "001_", "01. ", "01 - "
			s = SortKey().Replace(s, "", 1);
			// 6. sort-forcing punctuation
			s = SortPunctuation().Replace(s, "", 1);
			// 7. "zz" prefixes
			s = ZzPrefix().Replace(s, "", 1);
			s = Collapse(s);
			// 8. " Collection" after a TMDb-named box set, when more than an article is left
			if (fromTmdb && CollectionSuffix().Match(s) is { Success: true } suffix && !IsNothing(suffix.Groups["name"].Value))
			{
				s = suffix.Groups["name"].Value;
			}

			// 9. a separator left dangling at an edge by the rules above (never one the name had: "Alien / Predator" stays)
			if (s != server)
			{
				s = Collapse(DanglingSeparator().Replace(s, ""));
			}

			if (s == before)
			{
				break;
			}
		}

		// 10. nothing readable left: the server's name
		return IsNothing(s) ? Collapse(WithoutInvisible(name)) is { Length: > 0 } kept ? kept : name : s;
	}

	private static bool IsNothing(string s) =>
		s.Length == 0 || s.All(c => !char.IsLetterOrDigit(c)) || Articles.Contains(s.Trim());

	private static readonly HashSet<string> Articles = new(["the", "a", "an", "le", "la", "les", "der", "die", "das", "el", "los", "las"], StringComparer.OrdinalIgnoreCase);

	private static string Collapse(string s) => Spaces().Replace(s, " ").Trim();

	private static string WithoutInvisible(string s)
	{
		var b = new StringBuilder(s.Length);
		for (var i = 0; i < s.Length; i++)
		{
			var c = s[i];
			if (c is '​' or '‌' or '⁠' or '﻿' or '­')
			{
				continue;
			}

			// a zero-width joiner stays only between two emoji (a joined sequence: 👨‍👩‍👧)
			if (c == '‍' && !(i > 0 && i + 1 < s.Length && IsEmojiAround(s, i)))
			{
				continue;
			}

			b.Append(c);
		}

		return b.ToString();
	}

	private static bool IsEmojiAround(string s, int joiner)
	{
		var before = joiner - 1;
		if (before > 0 && char.IsLowSurrogate(s[before]) && char.IsHighSurrogate(s[before - 1]))
		{
			before--;
		}

		var after = joiner + 1;
		return (Emoji.IsEmojiPresentation(CodePointAt(s, before)) || s[before] == '️' || IsModifier(CodePointAt(s, before)))
			&& Emoji.IsEmojiPresentation(CodePointAt(s, after));
	}

	// skin tones (U+1F3FB to U+1F3FF) end an emoji before a joiner
	private static bool IsModifier(int codePoint) => codePoint is >= 0x1F3FB and <= 0x1F3FF;

	// the code point at i, or the lone surrogate itself (never throws on a broken pair)
	private static int CodePointAt(string s, int i) =>
		char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? char.ConvertToUtf32(s[i], s[i + 1]) : s[i];

	private static string WithoutEdgeEmoji(string s)
	{
		var elements = Elements(s);
		// the start: a run of emoji, then a space
		var first = 0;
		while (first < elements.Count && Emoji.IsEmojiElement(elements[first]))
		{
			first++;
		}

		if (first > 0 && first < elements.Count && elements[first] == " ")
		{
			elements.RemoveRange(0, first + 1);
		}

		// the end: a space, then a run of emoji
		var last = elements.Count;
		while (last > 0 && Emoji.IsEmojiElement(elements[last - 1]))
		{
			last--;
		}

		if (last < elements.Count && last > 0 && elements[last - 1] == " ")
		{
			elements.RemoveRange(last - 1, elements.Count - last + 1);
		}

		return string.Concat(elements);
	}

	private static List<string> Elements(string s)
	{
		var list = new List<string>();
		var e = StringInfo.GetTextElementEnumerator(s);
		while (e.MoveNext())
		{
			list.Add(e.GetTextElement());
		}

		return list;
	}

	private const string StartTagNames = @"HUB|4K|UHD|HD|HDR|TV|MOVIES|COLLECTION|KOMETA|PMM|FRANCHISE|SET|BOX\ SET|SAGA";

	// one or more [TAG] or {TAG} at the start, each followed by a separator (spaces, "-", ":", "|", "–", "—", "·")
	[GeneratedRegex(@"^(?:\s*[\[\{](?:" + StartTagNames + @")[\]\}](?:\s*[-:|–—·]\s*|\s+|(?=[^\w\s])))+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex StartTags();

	[GeneratedRegex(@"\s*(?:\((?:4K|4K\ HDR|4K\ HDR\ DV|UHD|HD|SD|SD\ DVD|1080p|2160p|Box\ Set|Collection)\)|\[HUB\])\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex EndTags();

	// "!020_", "001_", "12_" (digits then "_", a "!" before them allowed), "01. ", "01 - " (a leading zero)
	[GeneratedRegex(@"^(?:!?\d+_\s*|0\d*\.\s+|0\d*\s+-\s+)(?=\S)", RegexOptions.CultureInvariant)]
	private static partial Regex SortKey();

	// two or more of ! ~ + ^ _ at the start; a single ~ ^ _ glued to the name; any of them before a space or "-"
	[GeneratedRegex(@"^(?:[!~+^_]{2,}\s*(?=\S)|[~^_](?=[^\s!~+^_])|[!~+^_](?:\s+|-\s*)(?=\S))", RegexOptions.CultureInvariant)]
	private static partial Regex SortPunctuation();

	[GeneratedRegex(@"^(?:zz |zz_|ZZ_)(?=\S)", RegexOptions.CultureInvariant)]
	private static partial Regex ZzPrefix();

	[GeneratedRegex(@"^(?<name>.+?)\s+Collection$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex CollectionSuffix();

	[GeneratedRegex(@"^\s*[-:|–—·/]\s+|\s+[-:|–—·/]\s*$", RegexOptions.CultureInvariant)]
	private static partial Regex DanglingSeparator();

	[GeneratedRegex(@"\s{2,}")]
	private static partial Regex Spaces();
}

/// <summary>
/// Nixie 1.6: which characters are emoji by default (Unicode's <c>Emoji_Presentation</c> property, emoji-data.txt of Unicode
/// 16.0), for <see cref="CollectionNames"/>' edge rule. ☆ ★ ♪ ♡ are not: they are text characters unless followed by U+FE0F.
/// </summary>
public static class Emoji
{
	// Emoji_Presentation=Yes ranges (emoji-data.txt, Unicode 16.0)
	private static readonly (int From, int To)[] Presentation =
	[
		(0x231A, 0x231B), (0x23E9, 0x23EC), (0x23F0, 0x23F0), (0x23F3, 0x23F3), (0x25FD, 0x25FE), (0x2614, 0x2615),
		(0x2648, 0x2653), (0x267F, 0x267F), (0x2693, 0x2693), (0x26A1, 0x26A1), (0x26AA, 0x26AB), (0x26BD, 0x26BE),
		(0x26C4, 0x26C5), (0x26CE, 0x26CE), (0x26D4, 0x26D4), (0x26EA, 0x26EA), (0x26F2, 0x26F3), (0x26F5, 0x26F5),
		(0x26FA, 0x26FA), (0x26FD, 0x26FD), (0x2705, 0x2705), (0x270A, 0x270B), (0x2728, 0x2728), (0x274C, 0x274C),
		(0x274E, 0x274E), (0x2753, 0x2755), (0x2757, 0x2757), (0x2795, 0x2797), (0x27B0, 0x27B0), (0x27BF, 0x27BF),
		(0x2B1B, 0x2B1C), (0x2B50, 0x2B50), (0x2B55, 0x2B55), (0x1F004, 0x1F004), (0x1F0CF, 0x1F0CF), (0x1F18E, 0x1F18E),
		(0x1F191, 0x1F19A), (0x1F1E6, 0x1F1FF), (0x1F201, 0x1F201), (0x1F21A, 0x1F21A), (0x1F22F, 0x1F22F),
		(0x1F232, 0x1F236), (0x1F238, 0x1F23A), (0x1F250, 0x1F251), (0x1F300, 0x1F320), (0x1F32D, 0x1F335),
		(0x1F337, 0x1F37C), (0x1F37E, 0x1F393), (0x1F3A0, 0x1F3CA), (0x1F3CF, 0x1F3D3), (0x1F3E0, 0x1F3F0),
		(0x1F3F4, 0x1F3F4), (0x1F3F8, 0x1F43E), (0x1F440, 0x1F440), (0x1F442, 0x1F4FC), (0x1F4FF, 0x1F53D),
		(0x1F54B, 0x1F54E), (0x1F550, 0x1F567), (0x1F57A, 0x1F57A), (0x1F595, 0x1F596), (0x1F5A4, 0x1F5A4),
		(0x1F5FB, 0x1F64F), (0x1F680, 0x1F6C5), (0x1F6CC, 0x1F6CC), (0x1F6D0, 0x1F6D2), (0x1F6D5, 0x1F6D7),
		(0x1F6DC, 0x1F6DF), (0x1F6EB, 0x1F6EC), (0x1F6F4, 0x1F6FC), (0x1F7E0, 0x1F7EB), (0x1F7F0, 0x1F7F0),
		(0x1F90C, 0x1F93A), (0x1F93C, 0x1F945), (0x1F947, 0x1F9FF), (0x1FA70, 0x1FA7C), (0x1FA80, 0x1FA89),
		(0x1FA8F, 0x1FAC6), (0x1FACE, 0x1FADC), (0x1FADF, 0x1FAE9), (0x1FAF0, 0x1FAF8),
	];

	public static bool IsEmojiPresentation(int codePoint)
	{
		foreach (var (from, to) in Presentation)
		{
			if (codePoint < from)
			{
				return false;
			}

			if (codePoint <= to)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>One text element (a grapheme) that is an emoji: an emoji-by-default start, or any character asked for as emoji (U+FE0F), or a keycap.</summary>
	public static bool IsEmojiElement(string element)
	{
		if (string.IsNullOrEmpty(element))
		{
			return false;
		}

		var first = char.IsHighSurrogate(element[0]) && element.Length > 1 && char.IsLowSurrogate(element[1]) ? char.ConvertToUtf32(element[0], element[1]) : element[0];
		return IsEmojiPresentation(first) || element.Contains('️') || element.Contains('⃣');
	}
}
