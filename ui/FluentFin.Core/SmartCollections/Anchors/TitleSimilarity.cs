namespace FluentFin.Core.SmartCollections.Anchors;

/// <summary>
/// Nixie 1.8 (P1-2, "More like"; the report's "Because you watched"): how alike two titles are, on this PC, weighted by
/// rarity. Each feature a title has (a genre, a keyword, a studio, a country, its decade, its director, a top-3 billed
/// actor; each in its own namespace) weighs its inverse document frequency, log(N / n), among the titles of its kind (films
/// and shows apart), and two titles' similarity is the cosine of those weights. "Drama" or "United States" weighs almost
/// nothing; a keyword or a director a lot (Jellyfin's own similar titles add 10 a shared genre, tag, studio or person,
/// which rewards the generic). No matrix of title against title: one title's likes are one pass over its kind.
/// </summary>
public sealed class TitleSimilarity
{
	/// <summary>What a feature is: its namespace.</summary>
	public enum FeatureKind
	{
		Genre,
		Keyword,
		Studio,
		Country,
		Decade,
		Director,
		Actor,
	}

	/// <summary>A shared feature counts as rare when at most this share of the titles of its kind has it.</summary>
	public const double RareShare = 0.1;

	private static readonly double RareIdf = Math.Log(1 / RareShare);

	private readonly Dictionary<(FeatureKind Kind, string Text), int> _ids = new(new KeyComparer());
	private readonly List<(FeatureKind Kind, string Label)> _features = [];
	private readonly Dictionary<Guid, int> _at = [];
	private readonly IReadOnlyList<IndexEntry> _titles;
	private readonly int[][] _of;
	private readonly double[] _norm;
	private readonly Dictionary<IndexKind, double[]> _idf = [];
	private bool[] _hook = [];

	private TitleSimilarity(IReadOnlyList<IndexEntry> titles, IAnchorSource source)
	{
		_titles = titles;
		_of = new int[titles.Count][];
		_norm = new double[titles.Count];
		var buffer = new List<int>(24);
		for (var i = 0; i < titles.Count; i++)
		{
			var title = titles[i];
			_of[i] = [];
			if (title is null)
			{
				continue;
			}

			_at.TryAdd(title.Id, i);
			buffer.Clear();
			Collect(title, source, buffer);
			buffer.Sort();
			var unique = new int[buffer.Count];
			var n = 0;
			foreach (var id in buffer)
			{
				if (n == 0 || unique[n - 1] != id)
				{
					unique[n++] = id;
				}
			}

			_of[i] = n == unique.Length ? unique : unique[..n];
		}

		// each feature's weight among the titles of each kind
		var counts = new Dictionary<IndexKind, (int Titles, int[] Df)>();
		for (var i = 0; i < titles.Count; i++)
		{
			if (titles[i] is not { } title)
			{
				continue;
			}

			if (!counts.TryGetValue(title.Kind, out var kind))
			{
				kind = (0, new int[_features.Count]);
			}

			foreach (var id in _of[i])
			{
				kind.Df[id]++;
			}

			counts[title.Kind] = (kind.Titles + 1, kind.Df);
		}

		foreach (var (kind, (n, df)) in counts)
		{
			var idf = new double[_features.Count];
			for (var id = 0; id < idf.Length; id++)
			{
				idf[id] = df[id] > 0 ? Math.Log((double)n / df[id]) : 0;
			}

			_idf[kind] = idf;
		}

		_hook = [.. _features.Select(x => x.Kind is FeatureKind.Keyword or FeatureKind.Director or FeatureKind.Actor)];
		for (var i = 0; i < titles.Count; i++)
		{
			if (titles[i] is { } title && _idf.TryGetValue(title.Kind, out var idf))
			{
				var sum = 0.0;
				foreach (var id in _of[i])
				{
					sum += idf[id] * idf[id];
				}

				_norm[i] = Math.Sqrt(sum);
			}
		}
	}

	/// <summary>The similarity of <paramref name="titles"/> (one entry a title), with the people <paramref name="source"/> bills.</summary>
	public static TitleSimilarity Of(IReadOnlyList<IndexEntry> titles, IAnchorSource source) => new(titles, source);

	/// <summary>How alike two titles are (0 to 1; 0 across kinds, or for a title this doesn't have).</summary>
	public double Of(IndexEntry a, IndexEntry b) =>
		_at.TryGetValue(a.Id, out var i) && _at.TryGetValue(b.Id, out var j) && a.Kind == b.Kind ? Cosine(i, j, out _) : 0;

	/// <summary>
	/// The titles of <paramref name="anchor"/>'s kind like it, most alike first, that <paramref name="keep"/> keeps and that
	/// share a rare feature with it (a keyword, a person, or anything at most <see cref="RareShare"/> of its kind has):
	/// two dramas of the same decade aren't alike.
	/// </summary>
	public IReadOnlyList<(IndexEntry Title, double Similarity)> Like(IndexEntry anchor, Func<IndexEntry, bool> keep)
	{
		if (!_at.TryGetValue(anchor.Id, out var a) || _norm[a] <= 0)
		{
			return [];
		}

		var found = new List<(IndexEntry Title, double Similarity)>();
		for (var i = 0; i < _titles.Count; i++)
		{
			var title = _titles[i];
			if (i == a || title is null || title.Kind != anchor.Kind || _norm[i] <= 0)
			{
				continue;
			}

			var similarity = Cosine(a, i, out var rare);
			if (similarity > 0 && rare && keep(title))
			{
				found.Add((title, similarity));
			}
		}

		found.Sort((x, y) => y.Similarity.CompareTo(x.Similarity) is var bySimilarity and not 0 ? bySimilarity
			: StringComparer.OrdinalIgnoreCase.Compare(x.Title.OrderName, y.Title.OrderName) is var byName and not 0 ? byName
			: x.Title.Id.CompareTo(y.Title.Id));
		return found;
	}

	/// <summary>
	/// What <paramref name="anchor"/> shares with <paramref name="titles"/>, the weightiest first, at most
	/// <paramref name="most"/>: "space", "anime", "Megumi Hayashibara". A feature weighs its rarity times how many of them share
	/// it, times its namespace's <see cref="HookWeight"/>: a keyword or a person is a hook, a genre less, a country or a
	/// decade only context (labels naming a genre or a release date are the ones viewers like least).
	/// </summary>
	public IReadOnlyList<(FeatureKind Kind, string Label)> Shared(IndexEntry anchor, IEnumerable<IndexEntry> titles, int most)
	{
		if (!_at.TryGetValue(anchor.Id, out var a) || !_idf.TryGetValue(anchor.Kind, out var idf))
		{
			return [];
		}

		var weight = new Dictionary<int, double>();
		foreach (var title in titles)
		{
			if (title is null || !_at.TryGetValue(title.Id, out var t))
			{
				continue;
			}

			foreach (var id in _of[a])
			{
				if (Array.BinarySearch(_of[t], id) >= 0)
				{
					weight[id] = weight.GetValueOrDefault(id) + (idf[id] * HookWeight(_features[id].Kind));
				}
			}
		}

		return [.. weight.Where(x => x.Value > 0)
			.OrderByDescending(x => x.Value).ThenBy(x => _features[x.Key].Label, StringComparer.OrdinalIgnoreCase)
			.Select(x => _features[x.Key])
			.Take(most)];
	}

	/// <summary>How much a shared feature of a namespace counts toward naming it in a reason line.</summary>
	public static double HookWeight(FeatureKind kind) => kind switch
	{
		FeatureKind.Keyword or FeatureKind.Director => 2,
		FeatureKind.Actor => 1.5,
		FeatureKind.Studio => 1.2,
		FeatureKind.Genre => 1,
		_ => 0.7,
	};

	// the cosine of two titles' weights (both of one kind), and whether a feature they share is rare
	private double Cosine(int a, int b, out bool rare)
	{
		rare = false;
		if (_norm[a] <= 0 || _norm[b] <= 0 || !_idf.TryGetValue(_titles[a].Kind, out var idf))
		{
			return 0;
		}

		var fa = _of[a];
		var fb = _of[b];
		int i = 0, j = 0;
		var dot = 0.0;
		while (i < fa.Length && j < fb.Length)
		{
			var x = fa[i];
			var y = fb[j];
			if (x == y)
			{
				var w = idf[x];
				dot += w * w;
				rare |= _hook[x] || w >= RareIdf;
				i++;
				j++;
			}
			else if (x < y)
			{
				i++;
			}
			else
			{
				j++;
			}
		}

		return dot / (_norm[a] * _norm[b]);
	}

	// a title's features into the buffer: each namespaced (a genre and a keyword of one name are two), with the words a
	// reason line uses, made once a feature
	private void Collect(IndexEntry title, IAnchorSource source, List<int> into)
	{
		foreach (var genre in title.Genres ?? [])
		{
			Add(FeatureKind.Genre, genre, into);
		}

		foreach (var tag in title.Tags ?? [])
		{
			Add(FeatureKind.Keyword, tag, into);
		}

		foreach (var studio in title.Studios ?? [])
		{
			Add(FeatureKind.Studio, studio, into);
		}

		foreach (var country in title.Countries ?? [])
		{
			Add(FeatureKind.Country, country, into);
		}

		if (title.Year is >= 1900 and < 2100 and var year)
		{
			Add(FeatureKind.Decade, Decades[(year / 10) - 190], into);
		}

		var billed = source.BilledOf(title);
		for (var k = 0; k < billed.Count; k++)
		{
			Add(billed[k].Kind == "Director" ? FeatureKind.Director : FeatureKind.Actor, billed[k].Name, into);
		}
	}

	private static readonly string[] Decades = [.. Enumerable.Range(190, 21).Select(d => (d * 10).ToString(System.Globalization.CultureInfo.InvariantCulture))];

	private void Add(FeatureKind kind, string? text, List<int> into)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
		}

		var key = (kind, text.Trim());
		if (!_ids.TryGetValue(key, out var id))
		{
			_ids[key] = id = _features.Count;
			_features.Add((kind, LabelOf(kind, key.Item2)));
		}

		into.Add(id);
	}

	private static string LabelOf(FeatureKind kind, string text) => kind switch
	{
		FeatureKind.Genre => Lower(text),
		FeatureKind.Keyword => Lower(Keywords.NameOf(text) ?? text),
		FeatureKind.Decade => $"the {text}s",
		_ => text,
	};

	/// <summary>A word for the middle of a sentence: the first letter lower case when nothing after it is upper case ("Heists" "heists", "World War II" as it is).</summary>
	public static string Lower(string word) =>
		word.Length > 1 && char.IsUpper(word[0]) && !word.AsSpan(1).ContainsAnyInRange('A', 'Z') ? char.ToLowerInvariant(word[0]) + word[1..] : word;

	// a feature's key: its namespace and its text, the text compared without case (no lower-case copy a feature)
	private sealed class KeyComparer : IEqualityComparer<(FeatureKind Kind, string Text)>
	{
		public bool Equals((FeatureKind Kind, string Text) x, (FeatureKind Kind, string Text) y) => x.Kind == y.Kind && string.Equals(x.Text, y.Text, StringComparison.OrdinalIgnoreCase);

		public int GetHashCode((FeatureKind Kind, string Text) key) => HashCode.Combine(key.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(key.Text));
	}
}
