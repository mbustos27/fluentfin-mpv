namespace FluentFin.Core.Outside;

/// <summary>1.7 trending: the public lists Trakt and TMDb keep, as the Collections page names its rows.</summary>
public enum TrendingList
{
	/// <summary>Trending (Trakt's: watched right now; TMDb's: this week).</summary>
	Trending,

	/// <summary>Trakt's popular list.</summary>
	Popular,

	/// <summary>Trakt's most anticipated (titles on the most lists).</summary>
	Anticipated,

	/// <summary>Trakt's US box office this weekend (films only, ten titles).</summary>
	BoxOffice,
}

/// <summary>1.7 trending: a film or a show on a public list.</summary>
public enum ListedKind
{
	Movie,
	Series,
}

/// <summary>
/// 1.7 trending: one title of a public list, as Nixie keeps it: its place on the list and the ids that match a library's
/// ProviderIds (Tmdb, Imdb, Tvdb). Nothing in it comes from the user's library; it is the same for everyone who reads the
/// list, so it is kept apart from any user (the smart collections plan, section 3).
/// </summary>
/// <param name="Rank">1 for the list's first title.</param>
public sealed record ListedTitle(int Rank, ListedKind Kind, string? Tmdb, string? Imdb, string? Tvdb, string Title, int? Year)
{
	/// <summary>Whether it carries any id a library title can match.</summary>
	public bool HasIds => Tmdb is not null || Imdb is not null || Tvdb is not null;
}

/// <summary>Why a public list couldn't be read, in Nixie's words (never a stranger's error page).</summary>
public sealed class ListUnreadable(string message) : Exception(message);
