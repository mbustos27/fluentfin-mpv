using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.SmartCollections;

/// <summary>A film or a show in the index.</summary>
public enum IndexKind
{
	Movie,
	Series,
}

/// <summary>One person of a title, as the index keeps them: directors, and the first five actors billed.</summary>
public sealed record IndexPerson(string Name, Guid? Id, string Kind);

/// <summary>
/// Nixie 1.6: one title of the library index, lean: what the builders read and a card needs (no overview, no paths). Tags
/// keep only those the builders read (the keyword allowlist and the seasonal ones), so the saved index stays small on a
/// 25,800-film library.
/// </summary>
public sealed record IndexEntry
{
	public required Guid Id { get; init; }
	public required string Name { get; init; }
	public string? SortName { get; init; }
	public IndexKind Kind { get; init; }
	public Guid LibraryId { get; init; }
	public int? Year { get; init; }
	public string? PremiereDate { get; init; }
	public string? TmdbCollection { get; init; }

	// 1.7 keyless: the ids outside lists are matched by, on this PC (plan §3): TMDb's, IMDb's and TheTVDB's
	public string? Tmdb { get; init; }
	public string? Imdb { get; init; }
	public string? Tvdb { get; init; }
	// end 1.7 keyless
	public string[] Tags { get; init; } = [];
	public string[] Genres { get; init; } = [];
	public string[] Studios { get; init; } = [];
	public string[] Countries { get; init; } = [];
	public double? Rating { get; init; }
	public string? OfficialRating { get; init; }
	public long? RunTimeTicks { get; init; }
	public bool Played { get; init; }
	public bool Favorite { get; init; }
	public int? Unplayed { get; init; }
	public string? PrimaryTag { get; init; }
	public string? PrimaryBlurHash { get; init; }
	public DateTimeOffset? Added { get; init; }
	public IndexPerson[] People { get; init; } = [];

	/// <summary>The title's name for its order in a list: the sort name the server keeps, else the name.</summary>
	public string OrderName => string.IsNullOrEmpty(SortName) ? Name : SortName;

	/// <summary>What the index keeps of one title the server sent; null for anything but a film or a show.</summary>
	public static IndexEntry? From(BaseItemDto dto, Guid libraryId)
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
			OfficialRating = dto.OfficialRating,
			RunTimeTicks = dto.RunTimeTicks,
			Played = dto.UserData?.Played == true,
			Favorite = dto.UserData?.IsFavorite == true,
			Unplayed = dto.UserData?.UnplayedItemCount,
			PrimaryTag = tag,
			PrimaryBlurHash = blur,
			Added = dto.DateCreated,
		};
	}

	/// <summary>The people the index keeps from a title's People: directors, and the first five actors billed.</summary>
	public static IndexPerson[] PeopleOf(BaseItemDto dto)
	{
		if (dto.People is not { Count: > 0 } people)
		{
			return [];
		}

		var directors = people.Where(p => p.Type == BaseItemPerson_Type.Director && !string.IsNullOrWhiteSpace(p.Name)).Select(p => new IndexPerson(p.Name!.Trim(), p.Id, "Director"));
		var actors = people.Where(p => p.Type == BaseItemPerson_Type.Actor && !string.IsNullOrWhiteSpace(p.Name)).Take(5).Select(p => new IndexPerson(p.Name!.Trim(), p.Id, "Actor"));
		return [.. directors, .. actors];
	}

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
/// pass (when it came through) and the played-episodes pass.
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
	/// </summary>
	public bool PeopleDue { get; init; }

	/// <summary>
	/// BH-171-m6: whose index this is (the server and user, <see cref="Services.HomeCache.FileKey"/>): what is made of it is
	/// made for them, whoever is signed in by then.
	/// </summary>
	public string? Key { get; init; }
	// end 1.7.1
}

/// <summary>Nixie 1.7.1: how far a first read of the index has got: titles read, of how many (null until every library's count is in).</summary>
public sealed record IndexProgress(int Read, int? Total, IndexKind? Only)
{
	/// <summary>"12,000 of 25,736 films", "300 of 1,200 titles", "12,000 films": what the reading line says.</summary>
	public string Text
	{
		get
		{
			var noun = Only switch { IndexKind.Movie => "films", IndexKind.Series => "shows", _ => "titles" };
			var read = Read.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
			return Total is { } total ? $"{read} of {total.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} {noun}" : $"{read} {noun}";
		}
	}
}
