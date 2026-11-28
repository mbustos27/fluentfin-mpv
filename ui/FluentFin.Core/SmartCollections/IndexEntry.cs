using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.SmartCollections;

/// <summary>A film or a show in the index.</summary>
public enum IndexKind
{
	Movie,
	Series,
}

/// <summary>One person of a title, as the index keeps them: directors, and (1.8) the first three actors billed.</summary>
/// <param name="Id">
/// The server's id of the person; 1.8 (review, Major 2): no longer kept (null), as nothing reads it and it was half of what the
/// people added to the saved index (344 bytes a title on 25,738 films). Left out of the saved index when null.
/// </param>
public sealed record IndexPerson(string Name, [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? Id,
	string Kind);

/// <summary>
/// Nixie 1.6: one title of the library index, lean: what the builders read and a card needs (no overview, no paths). Tags
/// keep only those the builders read (the keyword allowlist and the seasonal ones; 1.9, and the categories' matchable ones,
/// <see cref="Keywords.Matchable"/>: the saved index's revision 4), so the saved index stays small on a 25,800-film library.
/// </summary>
public sealed record IndexEntry
{
	public required Guid Id { get; init; }
	public required string Name { get; init; }
	public string? SortName { get; init; }
	public IndexKind Kind { get; init; }
	public Guid LibraryId { get; init; }
	// 1.7.3 (P0-2): every library the server lists this title in, LibraryId first; empty in an index saved before 1.7.3
	// (then LibraryId alone). Stream I fills it; read it through InLibrary. BH-173-q3: a damaged saved index's null is empty
	// (InLibrary and the load's Once threw on it); so for every list below
	// 1.7.5 (fx1): saved only for a title in another library than LibraryId (SavedLibraryIds); one saved since loads empty
	[System.Text.Json.Serialization.JsonIgnore]
	public Guid[] LibraryIds { get; init => field = value ?? []; } = [];

	/// <summary>
	/// 1.7.5 (fx1; stream I's review of 1.7.3): what the saved index keeps of <see cref="LibraryIds"/>, under that name: the
	/// libraries of a title in more than its <see cref="LibraryId"/>'s, else nothing (about 1.8 MB on 34,000 titles, nearly all
	/// in one library each). Loaded without it, a title's <see cref="LibraryIds"/> is empty: <see cref="LibraryId"/> alone, as
	/// 1.7.3 reads an index saved before it.
	/// </summary>
	[System.Text.Json.Serialization.JsonPropertyName("LibraryIds")]
	[System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
	[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
	public Guid[]? SavedLibraryIds
	{
		get => Array.Exists(LibraryIds, x => x != LibraryId) ? LibraryIds : null;
		init => LibraryIds = value ?? [];
	}
	public int? Year { get; init; }
	public string? PremiereDate { get; init; }
	public string? TmdbCollection { get; init; }

	// 1.7 keyless: the ids outside lists are matched by, on this PC (plan §3): TMDb's, IMDb's and TheTVDB's
	public string? Tmdb { get; init; }
	public string? Imdb { get; init; }
	public string? Tvdb { get; init; }
	// end 1.7 keyless
	public string[] Tags { get; init => field = value ?? []; } = [];
	public string[] Genres { get; init => field = value ?? []; } = [];
	public string[] Studios { get; init => field = value ?? []; } = [];
	public string[] Countries { get; init => field = value ?? []; } = [];
	public double? Rating { get; init; }
	// 1.7.3 (P0-4): the critics' rating the server keeps (0-100), and how many tags the title had before the allowlist
	// (Tags keeps only the builders' own): both feed the confidence of a title's score
	public float? Critic { get; init; }
	public int TagCount { get; init; }
	public string? OfficialRating { get; init; }
	public long? RunTimeTicks { get; init; }
	public bool Played { get; init; }

	// 1.8 (P1-2, stream a): how often the user played the title, and when last (the anchors: rewatched, the latest first).
	// Saved only when known, so an index of 34,000 titles nobody played stays as small as before
	/// <summary>1.8: how many times the user played the title (the server's play count); 0 when never or not known.</summary>
	[System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
	public int PlayCount { get; init; }

	/// <summary>1.8: when the user last played the title; null when never or not known (an index saved before 1.8).</summary>
	[System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
	public DateTimeOffset? LastPlayed { get; init; }
	// end 1.8

	public bool Favorite { get; init; }
	public int? Unplayed { get; init; }
	public string? PrimaryTag { get; init; }
	public string? PrimaryBlurHash { get; init; }
	public DateTimeOffset? Added { get; init; }
	public IndexPerson[] People { get; init => field = value ?? []; } = [];

	/// <summary>The title's name for its order in a list: the sort name the server keeps, else the name.</summary>
	public string OrderName => string.IsNullOrEmpty(SortName) ? Name : SortName;

	/// <summary>1.7.3: whether the server lists this title in <paramref name="libraryId"/> (any of its libraries, not only the first).</summary>
	public bool InLibrary(Guid libraryId) => LibraryId == libraryId || Array.IndexOf(LibraryIds, libraryId) >= 0;

	/// <summary>
	/// What the index keeps of one title the server sent; null for anything but a film or a show. 1.7.3 (P0-2):
	/// <paramref name="alsoIn"/>, the other libraries the server lists it in.
	/// </summary>
	public static IndexEntry? From(BaseItemDto dto, Guid libraryId, IEnumerable<Guid>? alsoIn = null)
	{
		if (dto.Id is not { } id || dto.Type is not (BaseItemDto_Type.Movie or BaseItemDto_Type.Series))
		{
			return null;
		}

		string? tag = null;
		if (dto.ImageTags?.AdditionalData is { } tags && tags.TryGetValue("Primary", out var primary))
		{
			tag = primary as string;
		}

		string? blur = null;
		if (tag is not null && dto.ImageBlurHashes?.Primary?.AdditionalData is { } hashes && hashes.TryGetValue(tag, out var hash))
		{
			blur = hash as string;
		}

		return new IndexEntry
		{
			Id = id,
			Name = dto.Name ?? "",
			SortName = dto.SortName,
			Kind = dto.Type == BaseItemDto_Type.Movie ? IndexKind.Movie : IndexKind.Series,
			LibraryId = libraryId,
			LibraryIds = alsoIn is null ? [libraryId] : [.. alsoIn.Prepend(libraryId).Distinct()], // 1.7.3 (P0-2)
			Year = dto.ProductionYear,
			PremiereDate = dto.PremiereDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
			TmdbCollection = ProviderId(dto, "TmdbCollection"),
			Tmdb = ProviderId(dto, "Tmdb"), // 1.7 keyless
			Imdb = ProviderId(dto, "Imdb"), // 1.7 keyless
			Tvdb = ProviderId(dto, "Tvdb"), // 1.7 keyless
			Tags = Keywords.Kept(dto.Tags),
			Genres = Clean(dto.Genres),
			Studios = Clean(dto.Studios?.Select(x => x.Name)),
			Countries = Clean(dto.ProductionLocations),
			Rating = dto.CommunityRating,
			Critic = dto.CriticRating, // 1.7.3 (P0-4)
			TagCount = dto.Tags?.Count ?? 0, // 1.7.3 (P0-4)
			OfficialRating = dto.OfficialRating,
			RunTimeTicks = dto.RunTimeTicks,
			Played = dto.UserData?.Played == true,
			PlayCount = Math.Max(dto.UserData?.PlayCount ?? 0, 0), // 1.8 (P1-2)
			LastPlayed = dto.UserData?.LastPlayedDate, // 1.8 (P1-2)
			Favorite = dto.UserData?.IsFavorite == true,
			Unplayed = dto.UserData?.UnplayedItemCount,
			PrimaryTag = tag,
			PrimaryBlurHash = blur,
			Added = dto.DateCreated,
		};
	}

	/// <summary>
	/// The people the index keeps from a title's People: directors, and the first three actors billed. 1.8 (P1-2): the people
	/// pass is back for "More from" and Spotlight (<see cref="Anchors.PeoplePass"/>), with the top three billed only (the
	/// report's billing gate; it kept five before 1.7.3).
	/// </summary>
	public static IndexPerson[] PeopleOf(BaseItemDto dto) => Anchors.PeoplePass.Billed(dto);

	/// <summary>A provider id from the item's ProviderIds ("Tmdb", "TmdbCollection"), any case; null when it has none.</summary>
	public static string? ProviderId(BaseItemDto? dto, string provider)
	{
		if (dto?.ProviderIds?.AdditionalData is not { } ids)
		{
			return null;
		}

		foreach (var (key, value) in ids)
		{
			if (string.Equals(key, provider, StringComparison.OrdinalIgnoreCase) && value is string s && !string.IsNullOrWhiteSpace(s))
			{
				return s.Trim();
			}
		}

		return null;
	}

	private static string[] Clean(IEnumerable<string?>? values) =>
		values is null ? [] : [.. values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];

	/// <summary>A card's item record for this title (the Home rows and the mosaic; a Nixie collection's page reads fresh records).</summary>
	public BaseItemDto ToDto()
	{
		var dto = new BaseItemDto
		{
			Id = Id,
			Name = Name,
			SortName = SortName,
			Type = Kind == IndexKind.Movie ? BaseItemDto_Type.Movie : BaseItemDto_Type.Series,
			ProductionYear = Year,
			CommunityRating = Rating is { } r ? (float)r : null,
			OfficialRating = OfficialRating,
			RunTimeTicks = RunTimeTicks,
			Genres = [.. Genres],
			UserData = new UserItemDataDto { Played = Played, IsFavorite = Favorite, UnplayedItemCount = Unplayed },
		};
		if (PrimaryTag is not null)
		{
			dto.ImageTags = new BaseItemDto_ImageTags { AdditionalData = new Dictionary<string, object> { ["Primary"] = PrimaryTag } };
			if (PrimaryBlurHash is not null)
			{
				dto.ImageBlurHashes = new BaseItemDto_ImageBlurHashes
				{
					Primary = new BaseItemDto_ImageBlurHashes_Primary { AdditionalData = new Dictionary<string, object> { [PrimaryTag] = PrimaryBlurHash } },
				};
			}
		}

		return dto;
	}
}

/// <summary>A show's played episodes, from the played-episodes pass: how many, and when the last one was played.</summary>
public sealed record SeriesPlays(int Played, DateTimeOffset LastPlayed);

/// <summary>A library the index read.</summary>
public sealed record IndexLibrary(Guid Id, string Name, IndexKind Kind);

/// <summary>
/// Nixie 1.6: the library index of one server and user: every film and show of their movie and show libraries, the people
/// pass (when it came through) and the played-episodes pass. 1.7.3: a read has no people pass. 1.8 (P1-2): it has again,
/// after the titles, with the top three billed (<see cref="Anchors.PeoplePass"/>).
/// </summary>
public sealed record IndexSnapshot(
	IReadOnlyList<IndexLibrary> Libraries,
	IReadOnlyList<IndexEntry> Entries,
	IReadOnlyDictionary<Guid, SeriesPlays> SeriesPlays,
	bool HasPeople,
	DateTimeOffset ReadAt)
{
	public static IndexSnapshot Empty { get; } = new([], [], new Dictionary<Guid, SeriesPlays>(), false, DateTimeOffset.MinValue);

	// 1.7.1 (the index on a big server)
	/// <summary>What the server's check said when this index was read (<see cref="Contracts.Services.IJellyfinClient.GetIndexCheck"/>); null when not known.</summary>
	public string? Check { get; init; }

	/// <summary>A first read on its way: the titles read so far, and how far it has got; null for a whole index.</summary>
	public IndexProgress? Reading { get; init; }

	/// <summary>
	/// Review M2: this index's own people pass is still to come (the people it has are lent from the index before it, for the
	/// titles that one had). Saved, so that a read closed before its people pass reads only the people at a later start.
	/// 1.7.3: only an index saved before 1.7.3 said so (the people pass stopped). 1.8 (P1-2): every library's people are due
	/// (an index read first, or one with more new titles than a pass by id is worth); the people pass asks them after the
	/// titles, and again within the hour if it didn't finish.
	/// </summary>
	public bool PeopleDue { get; init; }

	// 1.8 (P1-2, stream a)
	/// <summary>
	/// 1.8: the titles whose people are still to ask, by id (new since the index before, and those whose request didn't come);
	/// the people pass asks them after the titles, and again within the hour if they didn't come. Saved. Empty when every
	/// title's people are in, or a whole people pass is due (<see cref="PeopleDue"/>).
	/// </summary>
	public IReadOnlyList<Guid> PeopleToAsk { get; init => field = value ?? []; } = [];

	/// <summary>
	/// 1.8 re-review (Minor): the share of titles whose people must be in before a slot made of people (More from, a
	/// Spotlight of a person) is made: a people step cut short, or a minute's keep of a long one, has the people of a part of
	/// the titles only, and a slot made of them would change as the rest came.
	/// </summary>
	public const double PeopleCoveredShare = 0.98;

	/// <summary>
	/// 1.8 re-review (Minor): the people of at least <see cref="PeopleCoveredShare"/> of the titles are in (none due, few still
	/// to ask): what a slot made of people waits for. <see cref="HasPeople"/> alone says only that some are.
	/// </summary>
	public bool PeopleCovered => HasPeople && !PeopleDue && PeopleToAsk.Count <= (1 - PeopleCoveredShare) * Entries.Count;
	// end 1.8

	/// <summary>
	/// 1.7.3 (BH-171-M1's other half): the played episodes didn't come with this index's read (a first read then has none,
	/// a read again keeps the ones it had): they are asked again on their own, at most once an hour. Saved.
	/// </summary>
	public bool PlaysDue { get; init; }

	/// <summary>
	/// BH-171-m6: whose index this is (the server and user, <see cref="Services.HomeCache.FileKey"/>): what is made of it is
	/// made for them, whoever is signed in by then.
	/// </summary>
	public string? Key { get; init; }
	// end 1.7.1

	/// <summary>
	/// 1.7.3 fix round (BH-173-M1): the index keeps each title's critics' rating and tag count (the saved index's revision 3, and
	/// every read). False for an index saved before 1.7.3, whose titles all read as having neither: Acclaimed then ranks by
	/// the rating alone (<see cref="RatingBacking"/>) until the index is read again.
	/// </summary>
	public bool BackingKnown { get; init; } = true;

	/// <summary>
	/// 1.9 (Collections 2): the index keeps each title's matchable keywords (<see cref="Keywords.Matchable"/>: the saved index's
	/// revision 4, and every read). False for an index saved before 1.9 (its tags the allowlist's and the seasonal ones only):
	/// it shows at once and keeps until its day's read brings revision 4 (none is made for the keywords alone), and meanwhile a
	/// category whose rule names another keyword waits
	/// (<see cref="Builders.MicroGenreVerdict.WaitsForKeywords"/>: missing data is not a decision, rule 4).
	/// </summary>
	public bool KeywordsKnown { get; init; } = true;

	// 1.9 (the index's load on a server that isn't the user's)
	/// <summary>
	/// 1.9: when the read that last brought the titles up to date began (a read of every title, or of what changed since); the
	/// next read of what changed asks from here (less <see cref="IndexRead.SavedSinceMargin"/>). <see cref="ReadAt"/> stays the
	/// date of the last read of every title. An index saved before 1.9's has none: its <see cref="ReadAt"/>.
	/// </summary>
	public DateTimeOffset TitlesAt { get => field == default ? ReadAt : field; init; }

	/// <summary>
	/// 1.9: a read of what changed found a library with fewer titles than the index has of it (titles removed, which a read of
	/// what changed can't name): the index isn't complete until the next read of every title, at most once a day. Saved.
	/// </summary>
	public bool RemovalsDue { get; init; }

	/// <summary>
	/// 1.9 (the review of wip/190-index, item 6): when the read that last brought the user's marks up to date began (a read of every
	/// title, or of the marks that came whole); the next read of the marks asks from here. Advances only when that read succeeds.
	/// An index saved before it has none: its <see cref="TitlesAt"/>.
	/// </summary>
	public DateTimeOffset MarksAt { get => field == default ? TitlesAt : field; init; }
	// end 1.9
}

/// <summary>Nixie 1.7.1: how far a first read of the index has got: titles read, of how many (null until every library's count is in).</summary>
public sealed record IndexProgress(int Read, int? Total, IndexKind? Only)
{
	/// <summary>
	/// 1.7.3 (BH-171-q1): <see cref="Total"/> is the server's check's count, until every library's first page and its count are
	/// in (84 s on a server of 23 libraries at 3-10 s a request, with no total meanwhile). The check counts each title once, in
	/// every library, so it is near the total and not the total.
	/// </summary>
	public bool About { get; init; }

	/// <summary>"12,000 of 25,736 films", "200 of about 25,736 titles", "300 of 1,200 titles", "12,000 films": what the reading line says.</summary>
	/// 1.7.5 (fx1; stream I's review of 1.7.3): "titles" while the total is only "about". The check counts every film and show of
	/// the user once, in libraries the index skips too, so "of about 34,006 films" could count shows and was followed by "of
	/// 37,173 films" once each library's own count was in (a title in two libraries counted in both).
	public string Text
	{
		get
		{
			var noun = About ? "titles" : Only switch { IndexKind.Movie => "films", IndexKind.Series => "shows", _ => "titles" };
			var read = Read.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
			return Total is { } total ? $"{read} of {(About ? "about " : "")}{total.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} {noun}" : $"{read} {noun}";
		}
	}
}
