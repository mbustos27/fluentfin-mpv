using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentFin.Core.SmartCollections.Builders;

namespace FluentFin.Core.SmartCollections;

/// <summary>
/// Nixie 1.9 (Collections 2): what a micro-genre's rule kept of the titles: how many, how many not watched, and (made only
/// when asked) the titles themselves in the index's order.
/// </summary>
public sealed class MicroGenreMatch
{
	private readonly IndexEntry[] _titles;
	private readonly ulong[] _bits;
	private IReadOnlyList<IndexEntry>? _listed;

	internal MicroGenreMatch(MicroGenre category, IndexEntry[] titles, ulong[] bits, ulong[] unwatched)
	{
		Category = category;
		_titles = titles;
		_bits = bits;
		Count = Bitset.Count(bits);
		Unwatched = Bitset.CountAnd(bits, unwatched);
	}

	public MicroGenre Category { get; }

	/// <summary>Titles the rule keeps.</summary>
	public int Count { get; }

	/// <summary>Of <see cref="Count"/>, those not watched.</summary>
	public int Unwatched { get; }

	/// <summary>The titles the rule keeps, in the index's order (made at the first ask, then kept).</summary>
	public IReadOnlyList<IndexEntry> Titles => _listed ??= Bitset.Each(_bits).Select(i => _titles[i]).ToArray();
}

/// <summary>
/// Nixie 1.9 (Collections 2): matches the micro-genres' rules (<see cref="MicroGenreRule"/>) over the index's titles. Once per
/// <see cref="CanonicalTitles"/> (kept while they are, as <see cref="LineupData"/> keeps its own), it makes posting lists of
/// the titles by genre, keyword, decade, country and studio, films and shows apart: a rule is then a few ANDs and ORs of
/// bitsets, and a check of years, length and ratings on the titles left. Over 26,000 films and 5,000 shows the posting lists
/// take about 35 ms, once for the titles, and 300 rules then about 5 ms (MicroGenreBudgetTests; the budget of both: 50 ms).
/// <para>
/// <see cref="Fits"/> says the same of one title, plainly (the posting lists must agree with it: the tests check). A rule with
/// <see cref="MicroGenreRule.TextAny"/> keeps only the titles whose summary has one of its words (1.9, step 1b:
/// <see cref="TextMatches"/>, from the summaries' pass), and nothing while the pass hasn't read its kind's titles
/// (<see cref="TextCovers"/>): a title not read, or with no summary, is never a guess (rule 4).
/// </para>
/// </summary>
public sealed class MicroGenreMatcher
{
	private static readonly ConditionalWeakTable<CanonicalTitles, MicroGenreMatcher> s_made = new();

	private readonly Postings _films;
	private readonly Postings _shows;
	private readonly CanonicalTitles _canonical; // 1.9 (stream text): a title's copies, each with its own summary
	// 1.9 (stream text): the last matches' coverage, each kind's made at its first ask. The review of wip/190-text, item 6: one
	// immutable object, swapped whole (a struct of three fields read beside its write could pair one matches' answer with
	// another's)
	private Coverage? _covered;

	private sealed record Coverage(TextMatches For, bool? Films, bool? Shows);

	/// <summary>The posting lists of <paramref name="titles"/>, made now (<see cref="For"/> keeps them).</summary>
	internal MicroGenreMatcher(CanonicalTitles titles)
	{
		_canonical = titles;
		var entries = titles.Entries;
		_films = new Postings([.. entries.Where(x => x is not null && x.Kind == IndexKind.Movie)]);
		_shows = new Postings([.. entries.Where(x => x is not null && x.Kind == IndexKind.Series)]);
	}

	/// <summary>The matcher of <paramref name="titles"/>: made at the first ask, then kept while they are.</summary>
	public static MicroGenreMatcher For(CanonicalTitles titles) => s_made.GetValue(titles, x => new MicroGenreMatcher(x));

	/// <summary>How many titles of <paramref name="kind"/> the index has (copies once).</summary>
	public int TitlesOf(IndexKind kind) => Of(kind).Titles.Length;

	// 1.9 (the pre-release fix B)
	/// <summary>
	/// Under this share of a kind's titles with a length (a film's runtime, a show's episode length), a rule's length can't
	/// decide most of them: a title of that kind with no length is judged on the rule's other fields (rule 4: missing data
	/// isn't a decision). At or over it, a title with none is no match, as before. On the owner's library 3 % of the shows had
	/// an episode length, and every rule with a length bound (the sitcoms', the networks' dramas) kept almost nothing; on the
	/// survey's, 99 % had one.
	/// </summary>
	public const double LengthKnownShare = 0.5;

	/// <summary>
	/// Whether a rule's length keeps the titles of <paramref name="kind"/> that have none (<see cref="LengthKnownShare"/>):
	/// fewer than half of them have a length.
	/// </summary>
	public bool LengthUnknownKept(IndexKind kind) => Of(kind).LengthUnknownKept;

	/// <summary>The share of <paramref name="kind"/>'s titles with a length (0 to 1; 1 for a kind with no titles).</summary>
	public double LengthShare(IndexKind kind) => Of(kind).LengthShare;
	// end 1.9 (fix B)

	/// <summary>
	/// 1.9 (stream wire, the census): how many distinct keywords <paramref name="counted"/> says yes to at least one title has,
	/// films and shows together (case aside): a count, never the keywords.
	/// </summary>
	internal int KeywordsSeen(Func<string, bool> counted)
	{
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var postings in new[] { _films, _shows })
		{
			foreach (var keyword in postings.KeywordValues)
			{
				if (counted(keyword))
				{
					seen.Add(keyword);
				}
			}
		}

		return seen.Count;
	}

	/// <summary>
	/// The titles of <paramref name="category"/>'s kind its rule keeps. 1.9 (stream text): a rule with a word list
	/// (<see cref="MicroGenreRule.TextAny"/>) keeps those whose summary has its words in <paramref name="text"/> (one, or its
	/// <see cref="MicroGenreRule.TextMin"/> different ones: the pass decides it as it reads, <see cref="TextPass.Score"/>), and
	/// nothing without matches, or while they don't cover its kind (<see cref="TextCovers"/>).
	/// </summary>
	public MicroGenreMatch Match(MicroGenre category, TextMatches? text = null)
	{
		var postings = Of(category.Kind);
		ulong[]? said = null;
		if (category.Rule.WaitsForText)
		{
			said = new ulong[Bitset.Words(postings.Titles.Length)];
			if (TextCovers(category.Kind, text))
			{
				// a title is in when any of its copies' summaries has the words (each copy is read on its own)
				foreach (var id in text!.Of(category.Id))
				{
					if (_canonical.TitleOf(id) is { } title && postings.PositionOf(title.Id) is { } at)
					{
						Bitset.Set(said, at);
					}
				}
			}
		}

		return new MicroGenreMatch(category, postings.Titles, postings.Keep(category.Rule, said), postings.Unwatched);
	}

	// 1.9 (stream text)
	/// <summary>
	/// Whether <paramref name="text"/> has read enough of this kind's titles for its categories with a word list to be made:
	/// <see cref="TextMatches.CoveredShare"/> of them asked (a title counted when any copy was), and one at least with a
	/// summary. Until then those categories keep nothing: what the pass hasn't read yet is not "not workplace".
	/// </summary>
	public bool TextCovers(IndexKind kind, TextMatches? text)
	{
		if (text is null || text.Count == 0)
		{
			return false;
		}

		var covered = Volatile.Read(ref _covered) is { } made && ReferenceEquals(made.For, text) ? made : new Coverage(text, null, null);
		var shows = kind == IndexKind.Series;
		if ((shows ? covered.Shows : covered.Films) is not { } said)
		{
			said = Covered(shows ? _shows : _films, text, kind);
			covered = shows ? covered with { Shows = said } : covered with { Films = said };
			Volatile.Write(ref _covered, covered); // a race makes it twice, the same
		}

		return said;
	}

	private bool Covered(Postings postings, TextMatches text, IndexKind kind) =>
		text.Covers([.. postings.Titles.Select(x => _canonical.CopyIds(x.Id) is { Count: > 0 } copies ? copies : (IReadOnlyList<Guid>)[x.Id])], kind);
	// end 1.9 (stream text)

	/// <summary>
	/// Whether <paramref name="title"/> is one of <paramref name="category"/>'s, plainly (one title, no posting lists): of its
	/// kind, and every field of the rule kept. What <see cref="Match"/> keeps is exactly the titles this says yes to (with a
	/// word list: when the matches cover the kind). 1.9 (stream text): a word list is kept when the summary of the title, or of
	/// one of its <paramref name="copies"/> (null: the title alone), has one of its words in <paramref name="text"/>.
	/// </summary>
	/// <param name="lengthUnknownKept">
	/// 1.9 (the pre-release fix B): a title with no length passes the rule's length (the matcher's
	/// <see cref="LengthUnknownKept"/> for the title's kind: what <see cref="Match"/> does on that library).
	/// </param>
	public static bool Fits(MicroGenre category, IndexEntry title, TextMatches? text = null, IReadOnlyList<Guid>? copies = null, bool lengthUnknownKept = false)
	{
		var rule = category.Rule;
		if (title is null || title.Kind != category.Kind)
		{
			return false;
		}

		if (rule.WaitsForText && (text is null || !(copies is { Count: > 0 } ? copies : [title.Id]).Any(id => text.Has(category.Id, id))))
		{
			return false; // not read, no summary, or none of its words: never a guess (rule 4)
		}

		var genres = Clean(title.Genres);
		var tags = Clean(title.Tags);
		var countries = Clean(title.Countries);
		var studios = new HashSet<string>((title.Studios ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Make.Key(x)).Where(x => x.Length > 0), StringComparer.Ordinal);
		return rule.GenresAll.All(genres.Contains)
			&& (rule.GenresAny.Count == 0 || rule.GenresAny.Any(genres.Contains))
			&& !rule.GenresNone.Any(genres.Contains)
			&& rule.KeywordsAll.All(tags.Contains)
			&& (rule.KeywordsAny.Count == 0 || rule.KeywordsAny.Any(tags.Contains))
			&& !rule.KeywordsNone.Any(tags.Contains)
			&& (rule.CountriesAny.Count == 0 || rule.CountriesAny.Any(countries.Contains))
			&& (rule.StudiosAny.Count == 0 || rule.StudiosAny.Any(x => studios.Contains(Make.Key(x))))
			&& rule.Years.Contains(title.Year)
			&& rule.LengthMinutes.Contains(Minutes(title.RunTimeTicks), lengthUnknownKept) // 1.9 (fix B)
			&& (rule.CommunityMin is not { } community || (title.Rating is { } rating && rating >= community))
			&& (rule.CriticMin is not { } critic || (title.Critic is { } score && score >= critic));
	}

	/// <summary>A runtime in whole minutes (half a minute rounds up); null for none (or 0).</summary>
	public static int? Minutes(long? ticks) =>
		ticks is { } t && t > 0 ? (int)Math.Round(t / (double)TimeSpan.TicksPerMinute, MidpointRounding.AwayFromZero) : null;

	/// <summary>The decade a year is in: 1975 → 1970.</summary>
	internal static int DecadeOf(int year) => year - (((year % 10) + 10) % 10);

	private Postings Of(IndexKind kind) => kind == IndexKind.Series ? _shows : _films;

	private static HashSet<string> Clean(string[]? values) =>
		new((values ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()), StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// A studio's key as <see cref="Make.Key"/> makes it (lower case, letters and digits only), without its LINQ: a server
	/// lists about 22,000 studio names, and each is made a key once a matcher.
	/// </summary>
	internal static string KeyOf(string name)
	{
		// each UTF-16 unit lowered alone, as the string's invariant lowering does (a surrogate is no letter either way)
		Span<char> buffer = name.Length <= 256 ? stackalloc char[name.Length] : new char[name.Length];
		var n = 0;
		foreach (var c in name)
		{
			var lower = char.ToLowerInvariant(c);
			if (char.IsLetterOrDigit(lower))
			{
				buffer[n++] = lower;
			}
		}

		return new string(buffer[..n]);
	}

	// one kind's titles and their posting lists
	private sealed class Postings
	{
		public readonly IndexEntry[] Titles;
		public readonly ulong[] All;
		public readonly ulong[] Unwatched;
		private readonly int _words;
		private readonly Field<string> _genres;
		private readonly Field<string> _keywords;
		private readonly Field<int> _decades;
		private readonly Field<string> _countries;
		private readonly Field<string> _studios; // by Make.Key
		private readonly int[] _years; // int.MinValue: none
		private readonly int[] _minutes; // -1: none
		private readonly double[] _community; // NaN: none
		private readonly double[] _critic; // NaN: none

		/// <summary>1.9 (stream wire, the census): the keywords these titles have, each once.</summary>
		public IEnumerable<string> KeywordValues => _keywords.Ids.Select(x => x.Key);

		/// <summary>1.9 (fix B): the share of these titles with a length (1 for none).</summary>
		public readonly double LengthShare;

		/// <summary>1.9 (fix B): a rule's length keeps the titles with none (<see cref="LengthKnownShare"/>).</summary>
		public bool LengthUnknownKept => LengthShare < LengthKnownShare;

		public Postings(IndexEntry[] titles)
		{
			Titles = titles;
			var n = titles.Length;
			_words = Bitset.Words(n);
			All = new ulong[_words];
			Unwatched = new ulong[_words];
			_years = new int[n];
			_minutes = new int[n];
			_community = new double[n];
			_critic = new double[n];
			_genres = new Field<string>(n, _words, StringComparer.OrdinalIgnoreCase);
			_keywords = new Field<string>(n, _words, StringComparer.OrdinalIgnoreCase);
			_decades = new Field<int>(n, _words, null);
			_countries = new Field<string>(n, _words, StringComparer.OrdinalIgnoreCase);
			_studios = new Field<string>(n, _words, StringComparer.Ordinal);
			var studioIds = new Dictionary<string, int>(StringComparer.Ordinal); // a studio name's key's id, made once a name (-1: no key)
			for (var i = 0; i < n; i++)
			{
				var title = titles[i];
				Bitset.Set(All, i);
				if (!title.Played)
				{
					Bitset.Set(Unwatched, i);
				}

				Words(_genres, i, title.Genres);
				Words(_keywords, i, title.Tags);
				Words(_countries, i, title.Countries);
				_studios.Begin(i);
				foreach (var studio in title.Studios ?? [])
				{
					if (string.IsNullOrWhiteSpace(studio))
					{
						continue;
					}

					if (!studioIds.TryGetValue(studio, out var id))
					{
						var key = KeyOf(studio);
						studioIds[studio] = id = key.Length > 0 ? _studios.Intern(key) : -1;
					}

					if (id >= 0)
					{
						_studios.AddId(id);
					}
				}

				_decades.Begin(i);
				_years[i] = title.Year ?? int.MinValue;
				if (title.Year is { } year)
				{
					_decades.Add(DecadeOf(year));
				}

				_minutes[i] = Minutes(title.RunTimeTicks) ?? -1;
				_community[i] = title.Rating ?? double.NaN;
				_critic[i] = title.Critic ?? double.NaN;
			}

			_genres.Done();
			_keywords.Done();
			_decades.Done();
			_countries.Done();
			_studios.Done();
			LengthShare = n == 0 ? 1 : _minutes.Count(x => x >= 0) / (double)n; // 1.9 (fix B)
		}

		// 1.9 (stream text): a title's position in Titles, by its id (made at the first ask: only a word list's rule asks)
		private Dictionary<Guid, int>? _at;

		public int? PositionOf(Guid id)
		{
			var at = Volatile.Read(ref _at);
			if (at is null)
			{
				at = new Dictionary<Guid, int>(Titles.Length);
				for (var i = 0; i < Titles.Length; i++)
				{
					at.TryAdd(Titles[i].Id, i);
				}

				Volatile.Write(ref _at, at);
			}

			return at.TryGetValue(id, out var position) ? position : null;
		}

		// the titles the rule keeps, as a bitset over Titles. 1.9 (stream text): with a word list, only those of said (the
		// titles whose summary has one of its words); none without it
		public ulong[] Keep(MicroGenreRule rule, ulong[]? said = null)
		{
			if (rule.WaitsForText && said is null)
			{
				return new ulong[_words]; // no summaries read: nothing, never a guess
			}

			var bits = Structured(rule);
			if (rule.WaitsForText)
			{
				Bitset.And(bits, said!);
			}

			return bits;
		}

		// the rule's fields but its word list
		private ulong[] Structured(MicroGenreRule rule)
		{
			var bits = (ulong[])All.Clone();
			if (!AllOf(bits, _genres, rule.GenresAll) || !AllOf(bits, _keywords, rule.KeywordsAll))
			{
				return new ulong[_words];
			}

			AnyOf(bits, _genres, rule.GenresAny);
			AnyOf(bits, _keywords, rule.KeywordsAny);
			AnyOf(bits, _countries, rule.CountriesAny);
			if (rule.StudiosAny.Count > 0)
			{
				AnyOf(bits, _studios, [.. rule.StudiosAny.Select(KeyOf)]);
			}

			NoneOf(bits, _genres, rule.GenresNone);
			NoneOf(bits, _keywords, rule.KeywordsNone);
			if (!rule.Years.IsOpen)
			{
				// the decades the years touch, then each title's year (a range that isn't whole decades)
				var touched = new ulong[_words];
				foreach (var (decade, id) in _decades.Ids)
				{
					if ((rule.Years.From is not { } from || decade + 9 >= from) && (rule.Years.To is not { } to || decade <= to))
					{
						_decades.OrInto(touched, id);
					}
				}

				Bitset.And(bits, touched);
			}

			var years = rule.Years;
			var length = rule.LengthMinutes;
			var community = rule.CommunityMin;
			var critic = rule.CriticMin;
			var unknownKept = LengthUnknownKept; // 1.9 (fix B): as Fits says
			var aligned =years.IsOpen || ((years.From is not { } f || DecadeOf(f) == f) && (years.To is not { } t || DecadeOf(t) + 9 == t));
			if (aligned && length.IsOpen && community is null && critic is null)
			{
				return bits;
			}

			// the titles left, a word at a time
			for (var w = 0; w < bits.Length; w++)
			{
				var word = bits[w];
				var kept = word;
				while (word != 0)
				{
					var bit = BitOperations.TrailingZeroCount(word);
					word &= word - 1;
					var i = (w << 6) + bit;
					var keep = (aligned || years.Contains(_years[i] == int.MinValue ? null : _years[i]))
						&& length.Contains(_minutes[i] < 0 ? null : _minutes[i], unknownKept)
						&& (community is not { } c || _community[i] >= c) // NaN (none) is never >=
						&& (critic is not { } k || _critic[i] >= k);
					if (!keep)
					{
						kept &= ~(1UL << bit);
					}
				}

				bits[w] = kept;
			}

			return bits;
		}

		// every one of the words' titles; false when a word has none (nothing can match)
		private static bool AllOf<TKey>(ulong[] bits, Field<TKey> field, IReadOnlyList<TKey> keys) where TKey : notnull
		{
			foreach (var key in keys)
			{
				if (!field.TryGet(key, out var id))
				{
					return false;
				}

				field.AndInto(bits, id);
			}

			return true;
		}

		// any of the words' titles (a list given: none of them in the index keeps nothing)
		private void AnyOf<TKey>(ulong[] bits, Field<TKey> field, IReadOnlyList<TKey> keys) where TKey : notnull
		{
			if (keys.Count == 0)
			{
				return;
			}

			var any = new ulong[_words];
			foreach (var key in keys)
			{
				if (field.TryGet(key, out var id))
				{
					field.OrInto(any, id);
				}
			}

			Bitset.And(bits, any);
		}

		private static void NoneOf<TKey>(ulong[] bits, Field<TKey> field, IReadOnlyList<TKey> keys) where TKey : notnull
		{
			foreach (var key in keys)
			{
				if (field.TryGet(key, out var id))
				{
					field.AndNotInto(bits, id);
				}
			}
		}

		private static void Words(Field<string> field, int title, string[]? values)
		{
			field.Begin(title);
			foreach (var value in values ?? [])
			{
				if (!string.IsNullOrWhiteSpace(value))
				{
					field.Add(value.Trim());
				}
			}
		}
	}

	// one field's posting lists, laid out flat: each value an id (by the field's comparer), each title's ids in turn, then every
	// id's titles in one array, in order. A list a value made about 25,000 lists of a server's studio names, and the
	// collector's pauses with them (the budget test: 45 to 100 ms, most of it the studios)
	private sealed class Field<TKey> where TKey : notnull
	{
		private readonly Dictionary<TKey, int> _ids;
		private readonly int _words;
		private readonly int[] _starts; // each title's first id in _flat; the last, the end
		private int[] _counts = new int[16];
		private int[] _flat;
		private int _length;
		private int _title;

		// made by Done: an id's titles are _members[_offsets[id].._offsets[id + 1]], and its bitset when dense
		private int[] _offsets = [0];
		private int[] _members = [];
		private ulong[]?[] _dense = [];

		public Field(int titles, int words, IEqualityComparer<TKey>? comparer)
		{
			_ids = new Dictionary<TKey, int>(comparer);
			_words = words;
			_starts = new int[titles + 1];
			_flat = new int[Math.Max(16, titles)];
		}

		/// <summary>The values and their ids.</summary>
		public IEnumerable<KeyValuePair<TKey, int>> Ids => _ids;

		/// <summary>The next title's values follow (every title in turn, each once).</summary>
		public void Begin(int title)
		{
			_title = title;
			_starts[title] = _length;
		}

		public int Intern(TKey key)
		{
			// one lookup, found or added
			ref var id = ref CollectionsMarshal.GetValueRefOrAddDefault(_ids, key, out var found);
			if (!found)
			{
				id = _ids.Count - 1;
				if (id == _counts.Length)
				{
					Array.Resize(ref _counts, id * 2);
				}
			}

			return id;
		}

		public void Add(TKey key) => AddId(Intern(key));

		public void AddId(int id)
		{
			// a title listing a value twice ("Drama", "drama") is in its list once
			for (var j = _starts[_title]; j < _length; j++)
			{
				if (_flat[j] == id)
				{
					return;
				}
			}

			if (_length == _flat.Length)
			{
				Array.Resize(ref _flat, _flat.Length * 2);
			}

			_flat[_length++] = id;
			_counts[id]++;
		}

		/// <summary>Every title's values are in: each id's titles laid out, a bitset for each dense one.</summary>
		public void Done()
		{
			var titles = _starts.Length - 1;
			_starts[titles] = _length;
			var k = _ids.Count;
			_offsets = new int[k + 1];
			for (var id = 0; id < k; id++)
			{
				_offsets[id + 1] = _offsets[id] + _counts[id];
			}

			_members = new int[_length];
			var next = _offsets[..k];
			for (var t = 0; t < titles; t++)
			{
				for (var j = _starts[t]; j < _starts[t + 1]; j++)
				{
					_members[next[_flat[j]]++] = t;
				}
			}

			_dense = new ulong[]?[k];
			for (var id = 0; id < k; id++)
			{
				if (_counts[id] >= 2 * _words)
				{
					var dense = new ulong[_words];
					for (var j = _offsets[id]; j < _offsets[id + 1]; j++)
					{
						Bitset.Set(dense, _members[j]);
					}

					_dense[id] = dense;
				}
			}

			_flat = [];
			_counts = [];
		}

		public bool TryGet(TKey key, out int id) => _ids.TryGetValue(key, out id);

		public void AndInto(ulong[] bits, int id)
		{
			if (_dense[id] is { } dense)
			{
				Bitset.And(bits, dense);
				return;
			}

			var kept = new ulong[_words];
			for (var j = _offsets[id]; j < _offsets[id + 1]; j++)
			{
				var i = _members[j];
				if (Bitset.Has(bits, i))
				{
					Bitset.Set(kept, i);
				}
			}

			Array.Copy(kept, bits, _words);
		}

		public void OrInto(ulong[] bits, int id)
		{
			if (_dense[id] is { } dense)
			{
				for (var w = 0; w < bits.Length; w++)
				{
					bits[w] |= dense[w];
				}

				return;
			}

			for (var j = _offsets[id]; j < _offsets[id + 1]; j++)
			{
				Bitset.Set(bits, _members[j]);
			}
		}

		public void AndNotInto(ulong[] bits, int id)
		{
			if (_dense[id] is { } dense)
			{
				for (var w = 0; w < bits.Length; w++)
				{
					bits[w] &= ~dense[w];
				}

				return;
			}

			for (var j = _offsets[id]; j < _offsets[id + 1]; j++)
			{
				Bitset.Clear(bits, _members[j]);
			}
		}
	}
}


/// <summary>Nixie 1.9: a set of title positions as bits, 64 a word.</summary>
internal static class Bitset
{
	public static int Words(int n) => (n + 63) >> 6;

	public static void Set(ulong[] bits, int i) => bits[i >> 6] |= 1UL << (i & 63);

	public static void Clear(ulong[] bits, int i) => bits[i >> 6] &= ~(1UL << (i & 63));

	public static bool Has(ulong[] bits, int i) => (bits[i >> 6] & (1UL << (i & 63))) != 0;

	public static void And(ulong[] bits, ulong[] other)
	{
		for (var w = 0; w < bits.Length; w++)
		{
			bits[w] &= other[w];
		}
	}

	public static int Count(ulong[] bits)
	{
		var count = 0;
		foreach (var word in bits)
		{
			count += BitOperations.PopCount(word);
		}

		return count;
	}

	public static int CountAnd(ulong[] bits, ulong[] other)
	{
		var count = 0;
		for (var w = 0; w < bits.Length; w++)
		{
			count += BitOperations.PopCount(bits[w] & other[w]);
		}

		return count;
	}

	/// <summary>The positions set, lowest first.</summary>
	public static IEnumerable<int> Each(ulong[] bits)
	{
		for (var w = 0; w < bits.Length; w++)
		{
			var word = bits[w];
			while (word != 0)
			{
				var bit = BitOperations.TrailingZeroCount(word);
				yield return (w << 6) + bit;
				word &= word - 1;
			}
		}
	}
}
