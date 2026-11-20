using System.Collections.Concurrent;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

/// <summary>
/// Nixie 1.3.0, the Editorial theme's Home (Themes\Editorial\Views\EditorialHomePage): a hero tile beside a numbered "Up next"
/// list, then a big-type genre strip that picks the shelf under it. Additive: nothing here changes what the shared Home shows
/// or asks for. The genre strip reads the movie and show libraries Home already lists (one request per library, kept for the
/// session), after the rest of Home has loaded.
/// </summary>
public partial class HomeViewModel
{
	/// <summary>Editorial: the genre strip and the shelf it picks (filled by <see cref="EditorialLoadGenres"/>).</summary>
	public EditorialGenreStrip EditorialGenres { get; } = new();

	/// <summary>
	/// Editorial: the genres of the movie and show libraries Home shows ("New in ..." rows), counted, and the shelf of the
	/// genre picked last (this session), else the largest. Asks the server once per library and session (<see cref="EditorialGenreSource"/>).
	/// </summary>
	public async Task EditorialLoadGenres()
	{
		var libraries = RecentItems.Select(r => r.Library)
			.Where(l => l.CollectionType is BaseItemDto_CollectionType.Movies or BaseItemDto_CollectionType.Tvshows)
			.ToList();
		if (libraries.Count == 0)
		{
			return;
		}

		EditorialGenres.IsLoading = EditorialGenres.Genres.Count == 0;
		try
		{
			var titles = await EditorialGenreSource.Titles(JellyfinClient, libraries);
			EditorialGenres.SetTitles(titles, EditorialGenreSource.LastPicked(JellyfinClient.UserId));
		}
		finally
		{
			EditorialGenres.IsLoading = false;
		}
	}

	/// <summary>Editorial: the genre strip's pick, remembered for this session (Back to Home shows the same shelf).</summary>
	public void EditorialPickGenre(string genre)
	{
		EditorialGenres.Select(genre);
		EditorialGenreSource.RememberPick(JellyfinClient.UserId, genre);
	}
}

/// <summary>Editorial: one line of Home's numbered "Up next" list.</summary>
/// <param name="Item">The card's item (its menu, Play).</param>
/// <param name="Number">"01".</param>
/// <param name="Title">The series' or the film's name.</param>
/// <param name="Detail">"S3 E4 · The Lamp Room · 31 min left", "S2 E1 · Thaw · Next episode", "1 h 12 m left".</param>
/// <param name="Progress">0 to 100, 0 for an item not started.</param>
public sealed record EditorialUpNextEntry(BaseItemViewModel Item, string Number, string Title, string Detail, double Progress, bool IsResume)
{
	public bool HasProgress => Progress > 0;

	/// <summary>
	/// What Narrator reads for the line, what it shows: "Iron Lighthouses S3 E4 The Lamp Room, 31 min left". Nixie 1.4.0 (the UX
	/// review's X12): the line opens its item, as Home's next-up item does in every theme (in 1.3.0 it played it).
	/// </summary>
	public string Name => $"{ItemLabels.ActionTarget(Item.Dto)}, {(IsResume ? EditorialHome.TimeLeft(Item.Dto) is { Length: > 0 } left ? left : "in progress" : "next episode")}";
}

/// <summary>Editorial: the pure parts of the Home view (tested in EditorialThemeTests).</summary>
public static class EditorialHome
{
	/// <summary>The most lines "Up next" shows beside the hero (1920 px); a narrow window shows three across.</summary>
	public const int UpNextLines = 5;

	/// <summary>How many next episodes the lines on screen keep a place for (the task "next episode, one click").</summary>
	public const int NextShown = 2;

	/// <summary>
	/// The hero tile's item: what the user was watching last (the first of Continue Watching: "Tonight", with Resume), else the
	/// shared hero's first slide (new to the user), else the first of Next Up. Null when Home has nothing yet.
	/// </summary>
	public static BaseItemViewModel? HeroItem(IReadOnlyList<BaseItemViewModel> continueItems, IReadOnlyList<HeroSlide> slides, IReadOnlyList<BaseItemViewModel> nextUp)
	{
		if (continueItems.Count > 0)
		{
			return continueItems[0];
		}

		if (slides.Count > 0)
		{
			return BaseItemViewModel.FromDto(slides[0].Dto);
		}

		return nextUp.Count > 0 ? nextUp[0] : null;
	}

	/// <summary>
	/// "Up next": what is in progress, then the next episodes (Continue Watching, then Next Up, as the server orders them),
	/// without the hero's item, numbered from 01. All of them (the page shows the first <see cref="UpNextLines"/>).
	/// </summary>
	public static IReadOnlyList<EditorialUpNextEntry> UpNext(IEnumerable<BaseItemViewModel> continueItems, IEnumerable<BaseItemViewModel> nextUp, BaseItemViewModel? hero, int lines = UpNextLines)
	{
		var heroId = hero?.Dto.Id;
		var seen = new HashSet<Guid>();
		var entries = new List<EditorialUpNextEntry>();
		void Add(BaseItemViewModel item, bool resume)
		{
			if (item.Dto.Id is { } id && (id == heroId || !seen.Add(id)))
			{
				return;
			}

			var dto = item.Dto;
			var title = dto.Type == BaseItemDto_Type.Episode && !string.IsNullOrWhiteSpace(dto.SeriesName) ? dto.SeriesName.Trim() : dto.Name?.Trim() ?? "";
			var parts = new List<string>();
			if (dto.Type == BaseItemDto_Type.Episode)
			{
				parts.Add(ItemLabels.EpisodeTitle(dto));
			}

			parts.Add(resume ? TimeLeft(dto) : dto.Type == BaseItemDto_Type.Episode ? "Next episode" : "");
			var progress = resume ? dto.UserData?.PlayedPercentage ?? 0 : 0;
			entries.Add(new EditorialUpNextEntry(item, Number(entries.Count + 1), title, string.Join(" · ", parts.Where(p => p.Length > 0)), Math.Clamp(progress, 0, 100), resume));
		}

		// the lines on screen always hold next episodes when there are any (a long Continue Watching would push them all
		// off the list): the first of what is in progress, then up to NextShown next episodes, then the rest of both
		var resuming = continueItems.ToList();
		var next = nextUp.ToList();
		var first = Math.Max(0, lines - Math.Min(NextShown, next.Count));
		foreach (var item in resuming.Take(first))
		{
			Add(item, resume: true);
		}

		foreach (var item in next.Take(NextShown))
		{
			Add(item, resume: false);
		}

		foreach (var item in resuming.Skip(first))
		{
			Add(item, resume: true);
		}

		foreach (var item in next.Skip(NextShown))
		{
			Add(item, resume: false);
		}

		return entries;
	}

	/// <summary>"01" .. "99", then the number itself.</summary>
	public static string Number(int n) => n.ToString(n < 100 ? "00" : "0", CultureInfo.InvariantCulture);

	/// <summary>"31 min left", "1 h 12 m left" (what is left of the runtime after the resume point); "" when unknown.</summary>
	public static string TimeLeft(BaseItemDto? dto)
	{
		if (dto?.RunTimeTicks is not { } runtime || runtime <= 0)
		{
			return "";
		}

		var left = TimeSpan.FromTicks(Math.Max(0, runtime - (dto.UserData?.PlaybackPositionTicks ?? 0)));
		var minutes = (int)Math.Ceiling(left.TotalMinutes);
		return minutes switch
		{
			<= 0 => "",
			< 60 => $"{minutes} min left",
			_ => $"{minutes / 60} h {minutes % 60} m left",
		};
	}

	/// <summary>The hero's line over the title: "Continue watching · Film", "Continue watching · S1 E3", "New · Series", "Up next · S2 E1".</summary>
	public static string Kicker(BaseItemDto? dto, bool resume, bool nextUp)
	{
		var what = dto?.Type switch
		{
			BaseItemDto_Type.Movie => "Film",
			BaseItemDto_Type.Series => "Series",
			BaseItemDto_Type.Episode => ItemLabels.EpisodeCode(dto) is { Length: > 0 } code ? code : "Episode",
			_ => "",
		};
		var why = resume ? "Continue watching" : nextUp ? "Up next" : "New";
		return what.Length > 0 ? $"{why} · {what}" : why;
	}

	/// <summary>
	/// The hero's facts: "1997 · R · 1h 58m · Thriller" (an episode: its show's code first), and, apart, what is left to watch
	/// (<see cref="TimeLeft"/>) for the bold end of the line.
	/// </summary>
	public static string HeroMeta(BaseItemDto? dto, int genres = 1)
	{
		if (dto is null)
		{
			return "";
		}

		var parts = new List<string>();
		if (dto.ProductionYear is > 0 and var year)
		{
			parts.Add(year.ToString(CultureInfo.InvariantCulture));
		}

		if (!string.IsNullOrWhiteSpace(dto.OfficialRating))
		{
			parts.Add(dto.OfficialRating.Trim());
		}

		if (dto.RunTimeTicks is > 0 and var ticks && dto.Type is not BaseItemDto_Type.Series)
		{
			parts.Add(ItemChips.Runtime(ticks));
		}

		if (dto.Genres?.Where(g => !string.IsNullOrWhiteSpace(g)).Take(Math.Max(1, genres)).Select(g => g.Trim()).ToList() is { Count: > 0 } named)
		{
			parts.Add(string.Join(", ", named));
		}

		return string.Join(" · ", parts);
	}
}

/// <summary>Editorial: the words of its cards (EditorialPoster, the stills), apart so they can be tested.</summary>
public static class EditorialCards
{
	/// <summary>A poster's title: the item's name (an episode's: its show's).</summary>
	public static string Title(BaseItemDto? dto) =>
		(dto?.Type == BaseItemDto_Type.Episode && !string.IsNullOrWhiteSpace(dto.SeriesName) ? dto.SeriesName : dto?.Name)?.Trim() ?? "";

	/// <summary>The line under a poster: "2022 · 2h 11m" (a film), "3 seasons" (a show), "S1 E3 · Sawdust" (an episode), "8 episodes" (a season).</summary>
	public static string PosterMeta(BaseItemDto? dto)
	{
		if (dto is null)
		{
			return "";
		}

		string Year() => dto.ProductionYear is > 0 and var y ? y.ToString(CultureInfo.InvariantCulture) : "";
		return dto.Type switch
		{
			BaseItemDto_Type.Movie => string.Join(" · ", new[] { Year(), dto.RunTimeTicks is > 0 and var t ? ItemChips.Runtime(t) : "" }.Where(x => x.Length > 0)),
			BaseItemDto_Type.Series => dto.ChildCount is > 0 and var seasons ? (seasons == 1 ? "1 season" : $"{seasons} seasons") : Year(),
			BaseItemDto_Type.Season => dto.ChildCount is > 0 and var episodes ? (episodes == 1 ? "1 episode" : $"{episodes} episodes") : Year(),
			BaseItemDto_Type.Episode => ItemLabels.EpisodeTitle(dto),
			_ => Year(),
		};
	}

	/// <summary>The share of the item watched, 0 to 100 (0 when it is not in progress).</summary>
	public static double Progress(BaseItemDto? dto) =>
		dto?.UserData is { Played: not true, PlayedPercentage: double percent and > 0 and < 100 } ? percent : 0;

	/// <summary>The small badges after a title: "4K", "HDR10", "5.1" (plain stereo and mono say nothing, so they are left out).</summary>
	public static IReadOnlyList<string> Badges(BaseItemDto? dto) =>
		[.. ItemChips.For(dto)
			.Where(c => c.Name.StartsWith("Resolution ", StringComparison.Ordinal) || c.Name.StartsWith("Video range ", StringComparison.Ordinal)
				|| (c.Name.StartsWith("Audio ", StringComparison.Ordinal) && !c.Text.EndsWith(" Stereo", StringComparison.Ordinal) && !c.Text.EndsWith(" Mono", StringComparison.Ordinal)))
			.Select(c => c.Name.StartsWith("Audio ", StringComparison.Ordinal) ? AudioBadge(c.Text) : c.Text)];

	// "English 5.1" -> "5.1", "Dolby Atmos" stays
	private static string AudioBadge(string text) =>
		text.Split(' ').LastOrDefault(w => w.Length > 0 && char.IsDigit(w[0])) ?? text;

	/// <summary>"With Ines Calder, Abe Morrow, Kit Yuen · Director Sam Okonkwo" (the top-billed three); "" when the item lists nobody.</summary>
	public static string People(BaseItemDto? dto)
	{
		var actors = (dto?.People ?? []).Where(p => p.Type == BaseItemPerson_Type.Actor && !string.IsNullOrWhiteSpace(p.Name)).Take(3).Select(p => p.Name!.Trim()).ToList();
		var directors = ItemMeta.Directors(dto).Take(2).ToList();
		var parts = new List<string>();
		if (actors.Count > 0)
		{
			parts.Add("With " + string.Join(", ", actors));
		}

		if (directors.Count > 0)
		{
			parts.Add((directors.Count == 1 ? "Director " : "Directors ") + string.Join(", ", directors));
		}

		return string.Join(" · ", parts);
	}

	/// <summary>An episode still's big number: "03" (its number in the season), "" without one.</summary>
	public static string StillNumber(BaseItemDto? dto) => dto?.IndexNumber is { } n and >= 0 ? EditorialHome.Number(n) : "";

	/// <summary>The line under an episode still: "52 min · 28 Mar 2022 · 44 min left", "49 min · 14 Mar 2022 · Watched".</summary>
	public static string StillMeta(BaseItemDto? dto)
	{
		if (dto is null)
		{
			return "";
		}

		var parts = new List<string>();
		if (dto.RunTimeTicks is > 0 and var ticks)
		{
			parts.Add($"{Math.Max(1, (int)Math.Round(TimeSpan.FromTicks(ticks).TotalMinutes))} min");
		}

		if (dto.PremiereDate is { } aired)
		{
			parts.Add(aired.ToString("d MMM yyyy", CultureInfo.InvariantCulture));
		}

		if (Watched(dto))
		{
			parts.Add("Watched");
		}
		else if (Progress(dto) > 0 && EditorialHome.TimeLeft(dto) is { Length: > 0 } left)
		{
			parts.Add(left);
		}

		return string.Join(" · ", parts);
	}

	/// <summary>Watched through (a show with episodes left is not).</summary>
	public static bool Watched(BaseItemDto? dto) => dto?.UserData is { Played: true } data && (data.UnplayedItemCount ?? 0) == 0;
}

/// <summary>Editorial: which of the genre strip's tabs fit its width.</summary>
public static class EditorialGenreFit
{
	/// <summary>
	/// The tabs shown, in order: as many as fit <paramref name="available"/> from the first (most titles), and the picked one
	/// (<paramref name="selected"/>) always: when it would not fit it takes the place of the last ones that do. At least one.
	/// </summary>
	public static IReadOnlyList<int> Fit(IReadOnlyList<double> widths, double spacing, double available, int selected)
	{
		var shown = new List<int>();
		var used = 0.0;
		for (var i = 0; i < widths.Count; i++)
		{
			var need = widths[i] + (shown.Count > 0 ? spacing : 0);
			if (used + need > available && shown.Count > 0)
			{
				break;
			}

			shown.Add(i);
			used += need;
		}

		if (selected >= 0 && selected < widths.Count && !shown.Contains(selected))
		{
			// drop from the end until the picked one fits after what stays
			while (shown.Count > 0 && used + spacing + widths[selected] > available)
			{
				var last = shown[^1];
				shown.RemoveAt(shown.Count - 1);
				used -= widths[last] + (shown.Count > 0 ? spacing : 0);
			}

			shown.Add(selected);
		}

		return shown;
	}
}

/// <summary>One genre of the strip: its name and how many titles of the libraries have it.</summary>
public sealed record EditorialGenre(string Name, int Count)
{
	/// <summary>Narrator: "Sci-fi, 31 titles".</summary>
	public string AutomationName => Count == 1 ? $"{Name}, 1 title" : $"{Name}, {Count} titles";
}

/// <summary>
/// Editorial: the genre strip's state: the genres (most titles first), the one picked, and its shelf (newest first). The
/// titles are the libraries' own (<see cref="EditorialGenreSource"/>), so a pick is instant: no request.
/// </summary>
public sealed partial class EditorialGenreStrip : ObservableObject
{
	private IReadOnlyList<BaseItemDto> _titles = [];

	/// <summary>Every genre, most titles first (then A to Z).</summary>
	[ObservableProperty]
	public partial IReadOnlyList<EditorialGenre> Genres { get; set; } = [];

	[ObservableProperty]
	public partial string? Selected { get; set; }

	/// <summary>The picked genre's titles, newest first (added to the server), at most <see cref="ShelfSize"/>.</summary>
	[ObservableProperty]
	public partial IReadOnlyList<BaseItemViewModel> Items { get; set; } = [];

	/// <summary>"31 films and shows · newest first".</summary>
	[ObservableProperty]
	public partial string Caption { get; set; } = "";

	[ObservableProperty]
	public partial bool IsLoading { get; set; }

	public bool HasGenres => Genres.Count > 0;

	partial void OnGenresChanged(IReadOnlyList<EditorialGenre> value) => OnPropertyChanged(nameof(HasGenres));

	/// <summary>The most titles a genre's shelf holds (its count says how many there are).</summary>
	public const int ShelfSize = 40;

	/// <summary>The libraries' titles (a refresh keeps the pick when its genre is still there).</summary>
	public void SetTitles(IReadOnlyList<BaseItemDto> titles, string? remembered = null)
	{
		_titles = titles;
		Genres = Count(titles);
		var pick = Selected ?? remembered;
		Select(Genres.Any(g => g.Name == pick) ? pick! : Genres.FirstOrDefault()?.Name);
	}

	public void Select(string? genre)
	{
		if (genre is null || Genres.All(g => g.Name != genre))
		{
			Selected = null;
			Items = [];
			Caption = "";
			return;
		}

		Selected = genre;
		var shelf = Shelf(_titles, genre);
		Items = [.. shelf.Take(ShelfSize).Select(BaseItemViewModel.FromDto)];
		Caption = CaptionFor(shelf);
	}

	/// <summary>The genres of <paramref name="titles"/> with how many titles have each (a title counts once per genre), most first, then A to Z.</summary>
	public static IReadOnlyList<EditorialGenre> Count(IEnumerable<BaseItemDto> titles) =>
		[.. titles.SelectMany(t => (t.Genres ?? []).Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
			.GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
			.Select(g => new EditorialGenre(g.First(), g.Count()))
			.OrderByDescending(g => g.Count)
			.ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)];

	/// <summary>The titles of a genre, newest first (added to the server, then released), then A to Z.</summary>
	public static IReadOnlyList<BaseItemDto> Shelf(IEnumerable<BaseItemDto> titles, string genre) =>
		[.. titles.Where(t => t.Genres?.Any(g => string.Equals(g?.Trim(), genre, StringComparison.OrdinalIgnoreCase)) == true)
			.OrderByDescending(t => t.DateCreated ?? DateTimeOffset.MinValue)
			.ThenByDescending(t => t.PremiereDate ?? DateTimeOffset.MinValue)
			.ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)];

	/// <summary>"31 films and shows · newest first", "12 films · newest first", "1 show · newest first".</summary>
	public static string CaptionFor(IReadOnlyList<BaseItemDto> shelf)
	{
		var films = shelf.Count(t => t.Type == BaseItemDto_Type.Movie);
		var shows = shelf.Count(t => t.Type == BaseItemDto_Type.Series);
		var count = shelf.Count;
		var what = (films, shows) switch
		{
			( > 0, > 0) => "films and shows",
			( > 0, 0) when films == count => count == 1 ? "film" : "films",
			(0, > 0) when shows == count => count == 1 ? "show" : "shows",
			_ => count == 1 ? "title" : "titles",
		};
		return $"{count.ToString("N0", CultureInfo.CurrentCulture)} {what} · newest first";
	}
}

/// <summary>
/// Editorial: the movie and show libraries' titles for the genre strip, one request per library (the Library page's own
/// request: every title with its genres), kept for the session per server, user and library for <see cref="FreshFor"/>.
/// </summary>
public static class EditorialGenreSource
{
	public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(15);

	private static readonly ConcurrentDictionary<(string Server, Guid User, Guid Library), (DateTimeOffset At, IReadOnlyList<BaseItemDto> Items)> s_titles = new();
	private static readonly ConcurrentDictionary<Guid, string> s_picked = new();

	/// <summary>The time, replaceable by tests.</summary>
	internal static Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

	public static async Task<IReadOnlyList<BaseItemDto>> Titles(IJellyfinClient client, IReadOnlyList<BaseItemDto> libraries)
	{
		var server = client.BaseUrl ?? "";
		var user = client.UserId;
		var lists = await Task.WhenAll(libraries.Where(l => l.Id is not null).Select(async library =>
		{
			var key = (server, user, library.Id!.Value);
			if (s_titles.TryGetValue(key, out var kept) && Now() - kept.At < FreshFor)
			{
				return kept.Items;
			}

			// Nixie 1.4.1: a page at a time, as the Library page (a whole 25,842-movie library in one request timed out); a
			// big library takes a while, and Home opened again meanwhile shares the read on its way rather than starting another
			var reading = s_reading.GetOrAdd(key, _ => ReadAsync(client, library, key));
			IReadOnlyList<BaseItemDto>? items;
			try
			{
				items = await reading;
			}
			finally
			{
				s_reading.TryRemove(KeyValuePair.Create(key, reading)); // this read, done (not a newer one)
			}

			return items ?? kept.Items ?? []; // failed: what was kept, if anything
		}));

		return [.. lists.SelectMany(x => x)];
	}

	private static readonly ConcurrentDictionary<(string Server, Guid User, Guid Library), Task<IReadOnlyList<BaseItemDto>?>> s_reading = new();

	private static async Task<IReadOnlyList<BaseItemDto>?> ReadAsync(IJellyfinClient client, BaseItemDto library, (string Server, Guid User, Guid Library) key)
	{
		var items = await new Services.LibraryPageReader(client, library).ReadAllAsync();
		if (items is not null)
		{
			s_titles[key] = (Now(), items);
		}

		return items;
	}

	/// <summary>Tests: a read of this library is on its way.</summary>
	internal static bool IsReading(string server, Guid user, Guid library) => s_reading.ContainsKey((server, user, library));

	public static void RememberPick(Guid user, string genre) => s_picked[user] = genre;

	public static string? LastPicked(Guid user) => s_picked.TryGetValue(user, out var genre) ? genre : null;

	/// <summary>Tests: forget everything.</summary>
	internal static void Clear()
	{
		s_titles.Clear();
		s_picked.Clear();
		s_reading.Clear();
	}
}
