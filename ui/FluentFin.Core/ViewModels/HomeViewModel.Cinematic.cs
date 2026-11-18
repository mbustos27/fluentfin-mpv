using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

// Nixie 1.3.0, the Cinematic theme: additive members its views read (FluentFin\Themes\Cinematic). Nothing here changes what
// the shared Home loads or shows.
public partial class HomeViewModel
{
	private UpNextShelf? _upNext;

	/// <summary>
	/// Cinematic's one "Up next" shelf: Continue Watching, then Next Up (which already leaves out what Continue Watching
	/// shows), the same item models in the same order, kept in step with both rows.
	/// </summary>
	public UpNextShelf UpNext => _upNext ??= new UpNextShelf(this);
}

/// <summary>
/// Continue Watching followed by Next Up as one list (the Cinematic theme's "Up next" shelf). It follows the two rows of
/// <see cref="HomeViewModel"/>: a card that leaves a row leaves this list as a Remove (so the shelf keeps the keyboard where it
/// was), a refresh that changes nothing changes nothing here, and the item models are the rows' own (their badges, their
/// progress and their menus stay one object).
/// </summary>
public sealed partial class UpNextShelf : ObservableObject
{
	private readonly HomeViewModel _home;
	private ObservableCollection<BaseItemViewModel>? _continue;
	private ObservableCollection<BaseItemViewModel>? _nextUp;

	public UpNextShelf(HomeViewModel home)
	{
		_home = home;
		home.PropertyChanged += OnHomeChanged;
		Watch();
		Sync();
	}

	public ObservableCollection<BaseItemViewModel> Items { get; } = [];

	/// <summary>True while either row is still on its way (the shelf shows its placeholders).</summary>
	[ObservableProperty]
	public partial bool IsLoading { get; set; } = true;

	[ObservableProperty]
	public partial bool HasItems { get; set; }

	/// <summary>The words beside the shelf's title: "Continue watching and new episodes · 9".</summary>
	[ObservableProperty]
	public partial string Summary { get; set; } = "";

	/// <summary>Whether <paramref name="item"/> came from Next Up (an episode not started), not from Continue Watching.</summary>
	public bool IsNextUp(BaseItemViewModel item) => _nextUp?.Contains(item) == true && _continue?.Contains(item) != true;

	private void OnHomeChanged(object? sender, PropertyChangedEventArgs e)
	{
		switch (e.PropertyName)
		{
			case nameof(HomeViewModel.ContinueItems) or nameof(HomeViewModel.NextUpItems):
				Watch();
				Sync();
				break;
			case nameof(HomeViewModel.IsContinueLoading) or nameof(HomeViewModel.IsNextUpLoading):
				Sync();
				break;
		}
	}

	private void Watch()
	{
		if (!ReferenceEquals(_continue, _home.ContinueItems))
		{
			if (_continue is not null)
			{
				_continue.CollectionChanged -= OnRowChanged;
			}

			_continue = _home.ContinueItems;
			_continue.CollectionChanged += OnRowChanged;
		}

		if (!ReferenceEquals(_nextUp, _home.NextUpItems))
		{
			if (_nextUp is not null)
			{
				_nextUp.CollectionChanged -= OnRowChanged;
			}

			_nextUp = _home.NextUpItems;
			_nextUp.CollectionChanged += OnRowChanged;
		}
	}

	private void OnRowChanged(object? sender, NotifyCollectionChangedEventArgs e) => Sync();

	private void Sync()
	{
		var target = Merge(_home.ContinueItems, _home.NextUpItems);
		Apply(Items, target);
		IsLoading = _home.IsContinueLoading || _home.IsNextUpLoading;
		HasItems = Items.Count > 0;
		Summary = CinematicLabels.UpNextSummary(Items.Count);
	}

	/// <summary>Continue Watching, then Next Up; an item already in the list (by id) is not added twice.</summary>
	public static List<BaseItemViewModel> Merge(IEnumerable<BaseItemViewModel> continueWatching, IEnumerable<BaseItemViewModel> nextUp)
	{
		var seen = new HashSet<Guid>();
		var list = new List<BaseItemViewModel>();
		foreach (var item in continueWatching.Concat(nextUp))
		{
			if (item.Dto?.Id is { } id && !seen.Add(id))
			{
				continue;
			}

			list.Add(item);
		}

		return list;
	}

	/// <summary>
	/// Makes <paramref name="shown"/> hold <paramref name="target"/> with the fewest changes: what left is removed, what came
	/// is inserted, what moved is moved; an unchanged list is not touched at all.
	/// </summary>
	public static void Apply(ObservableCollection<BaseItemViewModel> shown, IReadOnlyList<BaseItemViewModel> target)
	{
		var wanted = new HashSet<BaseItemViewModel>(target, ReferenceEqualityComparer.Instance);
		for (var i = shown.Count - 1; i >= 0; i--)
		{
			if (!wanted.Contains(shown[i]))
			{
				shown.RemoveAt(i);
			}
		}

		for (var i = 0; i < target.Count; i++)
		{
			if (i < shown.Count && ReferenceEquals(shown[i], target[i]))
			{
				continue;
			}

			var at = -1;
			for (var j = i + 1; j < shown.Count; j++)
			{
				if (ReferenceEquals(shown[j], target[i]))
				{
					at = j;
					break;
				}
			}

			if (at >= 0)
			{
				shown.Move(at, i);
			}
			else
			{
				shown.Insert(i, target[i]);
			}
		}
	}
}

/// <summary>Nixie 1.3.0: the words the Cinematic theme's views show (the shelves, the hero, the episode stills).</summary>
public static class CinematicLabels
{
	public static string UpNextSummary(int count) =>
		count <= 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"Continue watching and new episodes · {count}");

	/// <summary>"44 min left", "1 h 12 min left"; null when the item has no resume point or no length.</summary>
	public static string? TimeLeft(BaseItemDto? dto)
	{
		if (dto?.UserData?.PlaybackPositionTicks is not (> 0 and var position) || dto.RunTimeTicks is not (> 0 and var length) || position >= length)
		{
			return null;
		}

		var minutes = (int)Math.Ceiling(TimeSpan.FromTicks(length - position).TotalMinutes);
		return minutes < 60
			? string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, minutes)} min left")
			: minutes % 60 == 0
				? string.Create(CultureInfo.InvariantCulture, $"{minutes / 60} h left")
				: string.Create(CultureInfo.InvariantCulture, $"{minutes / 60} h {minutes % 60} min left");
	}

	/// <summary>How far through the item is, 0 to 100 (0 when not started).</summary>
	public static double Progress(BaseItemDto? dto)
	{
		if (dto?.UserData?.PlayedPercentage is > 0 and var percent)
		{
			return Math.Clamp(percent, 0, 100);
		}

		if (dto?.UserData?.PlaybackPositionTicks is > 0 and var position && dto.RunTimeTicks is > 0 and var length)
		{
			return Math.Clamp(100.0 * position / length, 0, 100);
		}

		return 0;
	}

	/// <summary>The big name on a still: the show's for an episode, else the item's.</summary>
	public static string TitleArtText(BaseItemDto? dto) =>
		dto is null ? "" : dto.Type == BaseItemDto_Type.Episode && !string.IsNullOrWhiteSpace(dto.SeriesName) ? dto.SeriesName.Trim() : (dto.Name ?? "").Trim();

	/// <summary>
	/// The line under an Up next still: "S1 E3 · Sawdust · 44 min left" for an episode under way, "S2 E1 · The Thaw · Next
	/// episode" for one not started, "1997 · 1 h 12 min left" for a film.
	/// </summary>
	public static string UpNextLine(BaseItemDto? dto)
	{
		if (dto is null)
		{
			return "";
		}

		var parts = new List<string>();
		if (dto.Type == BaseItemDto_Type.Episode)
		{
			if (ItemLabels.EpisodeCode(dto) is { Length: > 0 } code)
			{
				parts.Add(code);
			}

			if (!string.IsNullOrWhiteSpace(dto.Name))
			{
				parts.Add(dto.Name.Trim());
			}

			parts.Add(TimeLeft(dto) ?? "Next episode");
		}
		else
		{
			if (dto.ProductionYear is > 0 and var year)
			{
				parts.Add(year.ToString(CultureInfo.InvariantCulture));
			}

			if (TimeLeft(dto) is { } left)
			{
				parts.Add(left);
			}
		}

		return string.Join(" · ", parts);
	}

	/// <summary>The small capitals over the hero's title: "Continue watching · Film", "New · Series".</summary>
	public static string Kicker(BaseItemDto? dto, bool isResume)
	{
		var kind = dto?.Type switch
		{
			BaseItemDto_Type.Movie => "Film",
			BaseItemDto_Type.Series or BaseItemDto_Type.Season or BaseItemDto_Type.Episode => "Series",
			_ => "",
		};
		var what = isResume ? "Continue watching" : dto?.Type == BaseItemDto_Type.Episode ? "Next up" : "New";
		return kind.Length == 0 ? what : $"{what} · {kind}";
	}

	/// <summary>
	/// The facts as plain text, "1997 · R · ★ 8.4 · 1h 58m · Thriller" (an episode's start with "S1 E3"; a show's with its
	/// years), and the picture and sound as small badges ("4K", "HDR10", "5.1"): Cinematic's hero and item pages.
	/// </summary>
	public static string MetaFacts(BaseItemDto? dto)
	{
		if (dto is null)
		{
			return "";
		}

		var parts = new List<string>();
		if (dto.Type == BaseItemDto_Type.Episode && ItemLabels.EpisodeCode(dto) is { Length: > 0 } code)
		{
			parts.Add(code);
		}

		foreach (var chip in ItemChips.For(dto).Where(c => !IsBadge(c)))
		{
			parts.Add(chip.Text);
		}

		if (dto.Type is BaseItemDto_Type.Series && SeriesYears(dto) is { Length: > 0 } years && parts.Count > 0 && parts[0] == $"{dto.ProductionYear}")
		{
			parts[0] = years;
		}

		if (dto.Genres?.Where(g => !string.IsNullOrWhiteSpace(g)).Take(2).ToList() is { Count: > 0 } genres)
		{
			parts.Add(string.Join(", ", genres.Select(g => g.Trim())));
		}

		return string.Join(" · ", parts);
	}

	public static IReadOnlyList<string> MetaBadges(BaseItemDto? dto) =>
		[.. ItemChips.For(dto).Where(IsBadge).Where(c => !(c.Text.EndsWith(" Stereo", StringComparison.Ordinal) || c.Text.EndsWith(" Mono", StringComparison.Ordinal))).Select(c => c.Text)];

	private static bool IsBadge(ItemChip chip) =>
		chip.Name.StartsWith("Resolution ", StringComparison.Ordinal) || chip.Name.StartsWith("Video range ", StringComparison.Ordinal) || chip.Name.StartsWith("Audio ", StringComparison.Ordinal);

	private static string SeriesYears(BaseItemDto dto)
	{
		if (dto.ProductionYear is not { } start)
		{
			return "";
		}

		if (string.Equals(dto.Status, "Continuing", StringComparison.OrdinalIgnoreCase))
		{
			return string.Create(CultureInfo.InvariantCulture, $"{start}–present");
		}

		return dto.EndDate?.Year is { } end && end != start ? string.Create(CultureInfo.InvariantCulture, $"{start}–{end}") : start.ToString(CultureInfo.InvariantCulture);
	}

	/// <summary>The item page's facts at the right of the art: "Starring" (three names at most) and who made it ("Directed by" a film or an episode, "Created by" a show), those the item has.</summary>
	public static IReadOnlyList<(string Label, string Names)> Credits(BaseItemDto? dto)
	{
		var credits = new List<(string, string)>();
		if (dto?.People is not { Count: > 0 } people)
		{
			return credits;
		}

		static string Names(IEnumerable<BaseItemPerson> who, int max) =>
			string.Join(", ", who.Select(p => p.Name?.Trim()).Where(n => !string.IsNullOrEmpty(n)).Distinct().Take(max));

		var cast = Names(people.Where(p => p.Type == BaseItemPerson_Type.Actor), 3);
		if (cast.Length > 0)
		{
			credits.Add(("Starring", cast));
		}

		var series = dto.Type is BaseItemDto_Type.Series or BaseItemDto_Type.Season;
		var makers = Names(people.Where(p => series ? p.Type is BaseItemPerson_Type.Creator or BaseItemPerson_Type.Writer : p.Type == BaseItemPerson_Type.Director), 2);
		if (makers.Length > 0)
		{
			credits.Add((series ? "Created by" : "Directed by", makers));
		}

		return credits;
	}

	/// <summary>An item page's kicker: "Series · 3 seasons", "Film", "Season", "Episode · S1 E3" (the title art names the show).</summary>
	public static string ItemKicker(BaseItemDto? dto, int seasons = 0) => dto?.Type switch
	{
		BaseItemDto_Type.Series => seasons > 1 ? string.Create(CultureInfo.InvariantCulture, $"Series · {seasons} seasons") : seasons == 1 ? "Series · 1 season" : "Series",
		BaseItemDto_Type.Season => "Season",
		BaseItemDto_Type.Episode => ItemLabels.EpisodeCode(dto) is { Length: > 0 } code ? $"Episode · {code}" : "Episode",
		BaseItemDto_Type.Movie => "Film",
		_ => "",
	};

	/// <summary>An episode still's title: "3. Sawdust" ("Sawdust" with no number).</summary>
	public static string EpisodeTitle(BaseItemDto? episode)
	{
		var name = episode?.Name?.Trim() ?? "";
		return episode?.IndexNumber is { } number ? string.Create(CultureInfo.InvariantCulture, $"{number}. {name}").TrimEnd(' ', '.') : name;
	}

	/// <summary>The episode badge on a still: "E3" ("E3–E4" for a double); empty without a number.</summary>
	public static string EpisodeBadge(BaseItemDto? episode) =>
		episode?.IndexNumber is not { } number ? ""
		: episode.IndexNumberEnd is { } end && end > number ? string.Create(CultureInfo.InvariantCulture, $"E{number}–E{end}")
		: string.Create(CultureInfo.InvariantCulture, $"E{number}");

	/// <summary>"52 min · 44 min left · Aired 21 Mar 2022", "49 min · Watched": an episode still's second line.</summary>
	public static string EpisodeMeta(BaseItemDto? episode)
	{
		if (episode is null)
		{
			return "";
		}

		var parts = new List<string>();
		if (episode.RunTimeTicks is > 0 and var ticks)
		{
			parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, (int)Math.Round(TimeSpan.FromTicks(ticks).TotalMinutes))} min"));
		}

		if (episode.UserData?.Played == true)
		{
			parts.Add("Watched");
			return string.Join(" · ", parts);
		}

		if (TimeLeft(episode) is { } left)
		{
			parts.Add(left);
		}

		if (episode.PremiereDate is { } aired)
		{
			parts.Add("Aired " + aired.ToString("d MMM yyyy", CultureInfo.InvariantCulture));
		}

		return string.Join(" · ", parts);
	}

	/// <summary>"8 episodes · 6 h 12 min · 2 watched": the season tabs' summary; empty with no episodes.</summary>
	public static string SeasonSummary(IReadOnlyCollection<BaseItemDto>? episodes)
	{
		if (episodes is not { Count: > 0 })
		{
			return "";
		}

		var parts = new List<string> { episodes.Count == 1 ? "1 episode" : string.Create(CultureInfo.InvariantCulture, $"{episodes.Count} episodes") };
		var minutes = (int)Math.Round(episodes.Sum(e => TimeSpan.FromTicks(e.RunTimeTicks ?? 0).TotalMinutes));
		if (minutes > 0)
		{
			parts.Add(minutes < 60 ? string.Create(CultureInfo.InvariantCulture, $"{minutes} min")
				: minutes % 60 == 0 ? string.Create(CultureInfo.InvariantCulture, $"{minutes / 60} h")
				: string.Create(CultureInfo.InvariantCulture, $"{minutes / 60} h {minutes % 60} min"));
		}

		var watched = episodes.Count(e => e.UserData?.Played == true);
		if (watched > 0)
		{
			parts.Add(string.Create(CultureInfo.InvariantCulture, $"{watched} watched"));
		}

		return string.Join(" · ", parts);
	}

	/// <summary>
	/// The typeset title art drawn when an item has no Logo image: one of four faces, picked from the title so the same
	/// title always looks the same (a stable hash: string.GetHashCode differs between runs).
	/// </summary>
	public static TitleArtStyle StyleFor(string? title)
	{
		var hash = 2166136261u;
		foreach (var ch in title ?? "")
		{
			hash = (hash ^ char.ToUpperInvariant(ch)) * 16777619u;
		}

		return (TitleArtStyle)(hash % 4);
	}
}

/// <summary>
/// Nixie 1.3.0: which picture a Cinematic 16:9 still shows, and where it is on the server (relative, as
/// <see cref="ItemArtwork"/>'s paths). A film or a show: its Thumb, else its backdrop, else its Primary. An episode on a
/// shelf of shows (Up next): its show's Thumb or backdrop (the title art on it is the show's name), else its own picture.
/// An episode on its season's shelf: its own picture first.
/// </summary>
public static class CinematicArtwork
{
	public static string? StillPath(BaseItemDto? dto, int maxWidth, bool episodeOwnFirst = false)
	{
		if (dto?.Id is not { } id)
		{
			return null;
		}

		var candidates = new List<(Guid? Owner, string Kind, string? Tag)>();
		var own = Tag(dto, "Primary");
		if (dto.Type == BaseItemDto_Type.Episode)
		{
			var parentThumb = (dto.ParentThumbItemId, "Thumb", dto.ParentThumbImageTag);
			var parentBackdrop = (dto.ParentBackdropItemId ?? dto.SeriesId, "Backdrop", dto.ParentBackdropImageTags?.FirstOrDefault(t => !string.IsNullOrEmpty(t)));
			if (episodeOwnFirst)
			{
				candidates.Add((id, "Primary", own));
				candidates.Add(parentThumb);
				candidates.Add(parentBackdrop);
			}
			else
			{
				candidates.Add(parentThumb);
				candidates.Add(parentBackdrop);
				candidates.Add((id, "Primary", own));
			}
		}
		else
		{
			candidates.Add((id, "Thumb", Tag(dto, "Thumb")));
			candidates.Add((id, "Backdrop", dto.BackdropImageTags?.FirstOrDefault(t => !string.IsNullOrEmpty(t))));
			candidates.Add((dto.ParentThumbItemId, "Thumb", dto.ParentThumbImageTag));
			candidates.Add((dto.ParentBackdropItemId, "Backdrop", dto.ParentBackdropImageTags?.FirstOrDefault(t => !string.IsNullOrEmpty(t))));
			candidates.Add((id, "Primary", own));
		}

		foreach (var (owner, kind, tag) in candidates)
		{
			if (owner is { } item && !string.IsNullOrEmpty(tag))
			{
				return string.Create(CultureInfo.InvariantCulture, $"/Items/{item:D}/Images/{kind}?tag={Uri.EscapeDataString(tag)}&maxWidth={maxWidth}");
			}
		}

		return null;
	}

	private static string? Tag(BaseItemDto dto, string kind) =>
		dto.ImageTags?.AdditionalData.TryGetValue(kind, out var tag) == true && $"{tag}" is { Length: > 0 } text ? text : null;
}

/// <summary>The faces of the typeset title art (Cinematic): a wide tracked caps, condensed bold caps, a serif italic, light caps.</summary>
public enum TitleArtStyle
{
	Wide,
	Condensed,
	Serif,
	Light,
}
