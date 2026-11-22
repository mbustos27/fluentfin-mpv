using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FluentFin.Core.Mpv;

/// <summary>
/// 1.6 player: the Quality menu and the Playback stats of the player's controls (docs/BRIDGE.md §4 <c>set_quality</c>,
/// <c>get_stats</c>; §5 <c>stream</c>). The quality is the item's own (the next item starts with Settings' default, which
/// the page sends before each play); the stats panel asks the backend about once a second while it is open
/// (<see cref="PlaybackStatsPoller"/>) and never while it is closed.
/// </summary>
public sealed partial class MpvControlsViewModel
{
	/// <summary>Set by the page: one bridge command whose answer is wanted (<c>get_stats</c>); null: none can be asked.</summary>
	public Func<string, JsonObject?, Task<JsonNode?>>? Query { get; set; }

	/// <summary>Set by the page: the app's version for the Copy text's first line.</summary>
	public string? AppVersionText { get; set; }

	/// <summary>Settings &gt; Playback, "Show the Playback stats button" (off by default). The I key works either way.</summary>
	[ObservableProperty]
	public partial bool ShowStatsButton { get; set; }

	// -- quality -------------------------------------------------------------------------------------------------------

	/// <summary>The item's latest <c>stream</c> event: how it is served and the quality chosen for it.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(QualityHeader), nameof(CurrentQuality), nameof(QualityName), nameof(QualityToolTip))]
	public partial BridgeStream? Stream { get; set; }

	/// <summary>The Quality menu's header: what plays now ("Direct play · 1080p · 12 Mbps"); empty until the backend says.</summary>
	public string QualityHeader => QualityChoices.Header(Stream);

	/// <summary>The choice in effect for this item.</summary>
	public QualityChoice CurrentQuality => QualityChoice.From(Stream?.Quality);

	/// <summary>The menu's entries for this item: Auto, Original and the caps that suit its picture, the one in effect marked.</summary>
	public IReadOnlyList<QualityMenuItem> QualityMenu => QualityChoices.Menu(Stream);

	/// <summary>The Quality button's Narrator name: "Quality, Auto".</summary>
	public string QualityName => $"Quality, {CurrentQuality.Label}";

	/// <summary>"Quality: Auto. Direct play · 1080p · 12 Mbps".</summary>
	public string QualityToolTip => QualityHeader is { Length: > 0 } header ? $"Quality: {CurrentQuality.Label}. {header}" : $"Quality: {CurrentQuality.Label}";

	/// <summary>The <c>stream</c> event (an older backend never sends one: the menu then has no header and reads Auto).</summary>
	public void OnStream(BridgeStream stream)
	{
		if (IsThisItem(stream.ItemId))
		{
			Stream = stream;
		}
	}

	/// <summary>
	/// The Quality menu's pick: sent (the item restarts where it is, as the same viewing); the same choice again sends nothing.
	/// Shown briefly as asked; the menu's mark follows the backend's <c>stream</c> event, so a refused cap never reads as taken.
	/// </summary>
	public Task SetQuality(QualityChoice choice)
	{
		NotifyActivity();
		if (choice == CurrentQuality)
		{
			return Task.CompletedTask;
		}

		ShowToast($"Quality: {choice.Label}");
		return _send(Bridge.Commands.SetQuality, choice.ToJson());
	}

	// -- Playback stats --------------------------------------------------------------------------------------------------

	private PlaybackStatsPoller? _stats;

	private PlaybackStatsPoller Stats
	{
		get
		{
			if (_stats is null)
			{
				_stats = new PlaybackStatsPoller(AskStats, _now);
				_stats.RowsChanged += (_, _) =>
				{
					StatsRows = _stats.Rows;
					ShowStatsRows(_stats.Rows);
				};
			}

			return _stats;
		}
	}

	/// <summary>The Playback stats panel is open.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(StatsButtonName))]
	public partial bool IsStatsOpen { get; set; }

	/// <summary>The panel's rows, about once a second while it is open.</summary>
	[ObservableProperty]
	public partial IReadOnlyList<PlaybackStatsRow> StatsRows { get; set; } = PlaybackStats.Waiting;

	/// <summary>
	/// The rows as the panel shows them: one stable element per row, its value changed in place each second (a new list each
	/// second rebuilt every row, and Narrator lost its place in the panel), rebuilt only when the rows themselves change.
	/// </summary>
	public ObservableCollection<PlaybackStatsRowView> StatsRowViews { get; } = [.. PlaybackStats.Waiting.Select(r => new PlaybackStatsRowView(r))];

	private void ShowStatsRows(IReadOnlyList<PlaybackStatsRow> rows)
	{
		if (rows.Count == StatsRowViews.Count && rows.Select(r => r.Label).SequenceEqual(StatsRowViews.Select(v => v.Label)))
		{
			for (var i = 0; i < rows.Count; i++)
			{
				StatsRowViews[i].Value = rows[i].Value;
			}

			return;
		}

		StatsRowViews.Clear();
		foreach (var row in rows)
		{
			StatsRowViews.Add(new PlaybackStatsRowView(row));
		}
	}

	/// <summary>How many times the backend was asked for the stats, ever (none while the panel is closed).</summary>
	public int StatsQuestions => _stats?.Asked ?? 0;

	/// <summary>The stats button's Narrator name, which says what a press does.</summary>
	public string StatsButtonName => IsStatsOpen ? "Hide playback stats" : "Show playback stats";

	partial void OnIsStatsOpenChanged(bool value) => Stats.SetOpen(value);

	/// <summary>The stats button, the I key: opens or closes the panel.</summary>
	public void ToggleStats() => SetStatsOpen(!IsStatsOpen);

	/// <summary>Opens or closes the Playback stats panel (its own Close, Esc, the button, I).</summary>
	public void SetStatsOpen(bool open)
	{
		NotifyActivity();
		IsStatsOpen = open;
	}

	/// <summary>The panel's Copy text: the rows as plain text, with no token, server credentials or api key in it.</summary>
	public string StatsCopyText() => PlaybackStats.CopyText(StatsRows, AppVersionText, SessionInfo.AccessToken, SessionInfo.BaseUrl);

	/// <summary>
	/// 1.6.1 (the 1.6 review's queue): whether the window is minimized (the player controls set it; null: never). The open
	/// panel asks nothing then: nobody sees its numbers, and it asked mpv once a second for them.
	/// </summary>
	public Func<bool>? WindowMinimized { get; set; }

	/// <summary>The heartbeat's part: the open panel asks again once a second has passed, unless the window is minimized.</summary>
	private void TickStats()
	{
		if (_stats is { IsOpen: true } stats && WindowMinimized?.Invoke() != true)
		{
			_ = stats.Poll();
		}
	}

	private Task<JsonNode?> AskStats() =>
		Query is { } query ? query(Bridge.Commands.GetStats, null) : Task.FromResult<JsonNode?>(null);
}
