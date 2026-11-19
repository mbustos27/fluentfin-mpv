using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core;

/// <summary>
/// 1.2.0: what an item page's Play button says. <see cref="Label"/> is the visible text, <see cref="Name"/> what
/// Narrator reads and the tooltip shows (it starts with the label), <see cref="Progress"/> the resume bar (0-100) or null.
/// </summary>
/// <param name="EpisodeId">Nixie 1.4.0 (the UX review's X12, C-m10): the episode a series' or a season's Play starts while it
/// is up next (not when every episode is watched): the episode lists mark it "Up next" in every theme.</param>
public sealed record PlayTarget(string Label, string Name, double? Progress, Guid? EpisodeId = null)
{
	/// <summary>Before the episodes are known, or when they could not be loaded: plain "Play".</summary>
	public static PlayTarget Plain { get; } = new("Play", "Play", null);

	public bool HasProgress => Progress is not null;

	public double ProgressValue => Progress ?? 0;
}

/// <summary>
/// 1.2.0: Play on a series or a season names what it starts (Seaglass UX review, fix 12): "Resume S1 E4" with its
/// progress, "Play S2 E1" for the next episode not watched, "Play from the start" once all are watched. It starts what both
/// players start: <see cref="Mpv.MpvQueue"/> and FluentFin's playlist (PlaylistViewModel.AutoSelect) begin a season or a
/// series at the series' first episode not played, at its resume point, and at the first episode when all are played
/// (a season's Play too, so it can name an episode of another season). ItemPlayTests holds the two to the same episode.
/// </summary>
public static class ItemPlay
{
	/// <summary>Where a season's or a series' Play starts in the series' episodes (in play order): the first not played, else the first.</summary>
	public static int StartIndex(IReadOnlyList<BaseItemDto> episodes)
	{
		for (var i = 0; i < episodes.Count; i++)
		{
			if (episodes[i].UserData?.Played is false or null)
			{
				return i;
			}
		}

		return 0;
	}

	/// <param name="item">The page's item.</param>
	/// <param name="seriesEpisodes">For a series or a season: the series' episodes in play order
	/// (<c>IJellyfinClient.GetSeriesEpisodes</c>); null while unknown.</param>
	public static PlayTarget For(BaseItemDto? item, IReadOnlyList<BaseItemDto>? seriesEpisodes)
	{
		if (item?.Type is not (BaseItemDto_Type.Series or BaseItemDto_Type.Season))
		{
			// a movie or an episode starts at itself: "Resume at 12:34" or "Play", as before
			var label = ItemLabels.PlayLabel(item);
			return new PlayTarget(label, label, ItemLabels.ResumePosition(item) is null ? null : Percent(item));
		}

		var episodes = seriesEpisodes?.Where(x => x.Id is not null).ToList();
		if (episodes is not { Count: > 0 })
		{
			return PlayTarget.Plain;
		}

		var start = StartIndex(episodes);
		var episode = episodes[start];
		var title = ItemLabels.EpisodeSpoken(episode);
		var code = ItemLabels.EpisodeCode(episode);
		// an episode without a number is named by its title
		var which = code.Length > 0 ? code : episode.Name?.Trim() ?? "";

		if (episode.UserData?.Played == true)
		{
			// every episode is watched: Play starts the first one again
			return new PlayTarget("Play from the start", title.Length > 0 ? $"Play from the start, {title}" : "Play from the start", null);
		}

		if (ItemLabels.ResumePosition(episode) is not null)
		{
			var percent = Percent(episode);
			var label = Join("Resume", which);
			var name = string.Join(", ", new[] { Join("Resume", title), percent is { } p ? ItemLabels.WatchedPercent(p) : "" }.Where(x => x.Length > 0));
			return new PlayTarget(label, name, percent, episode.Id);
		}

		return new PlayTarget(Join("Play", which), Join("Play", title), null, episode.Id);
	}

	/// <summary>How far into the item its resume point is (0-100), from the server's percentage or the ticks; null when unknown.</summary>
	private static double? Percent(BaseItemDto? item)
	{
		if (item?.UserData?.PlayedPercentage is > 0 and var percent)
		{
			return Math.Clamp(percent, 0, 100);
		}

		if (item?.UserData?.PlaybackPositionTicks is > 0 and var position && item.RunTimeTicks is > 0 and var runtime)
		{
			return Math.Clamp(100.0 * position / runtime, 0, 100);
		}

		return null;
	}

	private static string Join(string first, string second) => second.Length > 0 ? $"{first} {second}" : first;
}
