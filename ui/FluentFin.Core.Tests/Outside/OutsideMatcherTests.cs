using FluentFin.Core.Outside;
using FluentFin.Core.SmartCollections;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Tests.Outside;

/// <summary>1.7 keyless: the index keeps the ids lists are matched by, and the matcher finds the library's titles on this PC.</summary>
public sealed class OutsideMatcherTests
{
	private static IndexEntry Film(string name, string? tmdb = null, string? imdb = null) =>
		new() { Id = Guid.NewGuid(), Name = name, Kind = IndexKind.Movie, Tmdb = tmdb, Imdb = imdb };

	private static IndexEntry Show(string name, string? tmdb = null, string? imdb = null, string? tvdb = null) =>
		new() { Id = Guid.NewGuid(), Name = name, Kind = IndexKind.Series, Tmdb = tmdb, Imdb = imdb, Tvdb = tvdb };

	private static IndexSnapshot Index(params IndexEntry[] entries) => IndexSnapshot.Empty with { Entries = entries };

	[Fact]
	public void The_index_keeps_a_titles_TMDb_IMDb_and_TheTVDB_ids()
	{
		var dto = new BaseItemDto
		{
			Id = Guid.NewGuid(),
			Name = "Heat",
			Type = BaseItemDto_Type.Movie,
			ProviderIds = new BaseItemDto_ProviderIds { AdditionalData = new Dictionary<string, object> { ["Tmdb"] = "949", ["imdb"] = " tt0113277 ", ["Tvdb"] = "" } },
		};

		var entry = IndexEntry.From(dto, Guid.NewGuid())!;

		Assert.Equal("949", entry.Tmdb);
		Assert.Equal("tt0113277", entry.Imdb);
		Assert.Null(entry.Tvdb);
	}

	[Fact]
	public void A_TMDb_id_matches_a_title_of_its_own_kind_only()
	{
		var heat = Film("Heat", tmdb: "1399");
		var thrones = Show("Game of Thrones", tmdb: "1399");
		var matcher = new OutsideMatcher(Index(heat, thrones));

		Assert.Same(heat, matcher.Match(new OutsideRef(Tmdb: "1399", Kind: IndexKind.Movie)));
		Assert.Same(thrones, matcher.Match(new OutsideRef(Tmdb: "1399", Kind: IndexKind.Series)));
		Assert.Same(heat, matcher.Match(new OutsideRef(Tmdb: "1399"))); // kind unknown: a film first
	}

	[Fact]
	public void IMDb_and_TheTVDB_ids_match_when_TMDb_has_none()
	{
		var heat = Film("Heat", imdb: "tt0113277");
		var show = Show("The Wire", tvdb: "79126");
		var matcher = new OutsideMatcher(Index(heat, show));

		Assert.Same(heat, matcher.Match(new OutsideRef(Tmdb: "999999", Imdb: "TT0113277")));
		Assert.Same(show, matcher.Match(new OutsideRef(Tvdb: "79126", Kind: IndexKind.Series)));
		Assert.Null(matcher.Match(new OutsideRef(Tvdb: "79126", Kind: IndexKind.Movie)));
		Assert.Null(matcher.Match(new OutsideRef(Imdb: "tt0113277", Kind: IndexKind.Series)));
		Assert.Null(matcher.Match(new OutsideRef()));
	}

	[Fact]
	public void A_list_matches_in_its_own_order_each_title_once()
	{
		var a = Film("A", tmdb: "1", imdb: "tt1");
		var b = Film("B", tmdb: "2");
		var matcher = new OutsideMatcher(Index(a, b, Film("C")));

		var found = matcher.MatchAll([new(Tmdb: "2"), new(Tmdb: "404"), new(Imdb: "tt1"), new(Tmdb: "1")]);

		Assert.Equal([b, a], found);
	}

	[Fact]
	public void An_empty_index_or_list_matches_nothing()
	{
		Assert.Empty(new OutsideMatcher(IndexSnapshot.Empty).MatchAll([new(Tmdb: "1")]));
		Assert.Empty(new OutsideMatcher(Index(Film("A", tmdb: "1"))).MatchAll([]));
	}
}
