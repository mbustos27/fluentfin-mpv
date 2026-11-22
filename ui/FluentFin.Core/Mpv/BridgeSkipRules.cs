using System.Globalization;
using System.Text.Json.Nodes;

namespace FluentFin.Core.Mpv;

// 1.6 skip: skip rules over the bridge (docs/BRIDGE.md §4 skip_rules, add_skip_rule, forget_skip_rules,
// answer_skip_suggestion, set_skip_options' 1.6 fields; §5 skip_suggestion and segments with source "rule").

/// <summary>1.6 skip: the protocol's words for skip rules.</summary>
public static class SkipRuleWords
{
	public const string Start = "start";
	public const string End = "end";
	public const string Ask = "ask";
	public const string Always = "always";
	public const string Off = "off";
	public const string Marked = "marked";
	public const string Learned = "learned";
	public const string Yes = "yes";
	public const string NotNow = "not_now";
	public const string Never = "never";

	/// <summary>The longest part a rule may cover, as the backend checks it (docs/BRIDGE.md §4 add_skip_rule).</summary>
	public const double MaxSeconds = 600;
}

/// <summary>1.6 skip: the <c>skip_suggestion</c> event: the same skip in two episodes of a show; asked once.</summary>
public sealed record BridgeSkipSuggestion(string Id, string? ItemId, string? SeriesId, string? SeriesName, string Anchor, long StartTicks, long EndTicks);

/// <summary>1.6 skip: <c>skip_rules</c>' result.</summary>
public sealed record BridgeSkipRulesList(IReadOnlyList<BridgeSkipShow>? Shows);

/// <summary>1.6 skip: one show of <c>skip_rules</c>, with its rules, its learned intros and whether it was refused.</summary>
public sealed record BridgeSkipShow(string SeriesId, string? Name, IReadOnlyList<BridgeSkipRule>? Rules, IReadOnlyList<BridgeLearnedIntro>? LearnedIntros, bool Refused);

/// <summary>
/// 1.6 skip: one rule. <see cref="Anchor"/> "start": seconds from the episode's start; "end": seconds before its end
/// (<see cref="StartS"/> &gt; <see cref="EndS"/>). <see cref="Action"/> null follows Settings.
/// </summary>
public sealed record BridgeSkipRule(string Id, string Anchor, double StartS, double EndS, string? Action, string Origin, long Created = 0);

/// <summary>1.6 skip: a one-seek learned intro of a season; <see cref="Moves"/>: not offered any more (it is never in one place).</summary>
public sealed record BridgeLearnedIntro(string Id, int? Season, double StartS, double EndS, bool Moves);

/// <summary>1.6 skip: the arguments of the skip rules' commands.</summary>
public static class BridgeSkipCommands
{
	/// <summary>
	/// <c>set_skip_options</c> with the 1.6 fields: the two switches (sent as <c>learnIntros</c> too, for a backend that does
	/// not know <c>learnSkips</c>), what the server's ads and previews do, and what a rule with no choice of its own does.
	/// Nothing else: no server, user, token or path.
	/// </summary>
	public static JsonObject SkipOptions(bool onlineLookup, bool learnSkips, string adsAndPreviews, string ruleAction) => new()
	{
		["onlineLookup"] = onlineLookup,
		["learnIntros"] = learnSkips,
		["learnSkips"] = learnSkips,
		["segmentActions"] = new JsonObject
		{
			["Commercial"] = SegmentAction(adsAndPreviews),
			["Preview"] = SegmentAction(adsAndPreviews),
		},
		["ruleAction"] = RuleAction(ruleAction),
	};

	/// <summary>"off", "ask" or "always"; anything else is the default, "ask".</summary>
	public static string SegmentAction(string? value) => value is SkipRuleWords.Off or SkipRuleWords.Always ? value : SkipRuleWords.Ask;

	/// <summary>"ask" or "always"; anything else is "ask" (a Skip button).</summary>
	public static string RuleAction(string? value) => value == SkipRuleWords.Always ? value : SkipRuleWords.Ask;

	/// <summary><c>add_skip_rule</c> for the show playing (no <c>seriesId</c>): the backend knows which show it is.</summary>
	public static JsonObject AddRule(string anchor, double startS, double endS, string? action)
	{
		var args = new JsonObject
		{
			["anchor"] = anchor,
			["startS"] = Math.Round(startS, 2),
			["endS"] = Math.Round(endS, 2),
		};
		if (action is SkipRuleWords.Ask or SkipRuleWords.Always)
		{
			args["action"] = action;
		}

		return args;
	}

	/// <summary><c>forget_skip_rules</c>: one rule, one show, or (both null) everything of the account.</summary>
	public static JsonObject Forget(string? seriesId, string? ruleId, string? serverId, string? userId)
	{
		var args = Account(serverId, userId);
		if (seriesId is not null)
		{
			args["seriesId"] = seriesId;
		}

		if (ruleId is not null)
		{
			args["ruleId"] = ruleId;
		}

		return args;
	}

	/// <summary>Whose rules (<c>skip_rules</c> before <c>connect</c>): the ids Jellyfin shows any client, no secret.</summary>
	public static JsonObject Account(string? serverId, string? userId) =>
		serverId is { Length: > 0 } && userId is { Length: > 0 } ? new JsonObject { ["serverId"] = serverId, ["userId"] = userId } : [];

	/// <summary><c>answer_skip_suggestion</c>.</summary>
	public static JsonObject Answer(string id, string answer) => new() { ["id"] = id, ["answer"] = answer };
}

/// <summary>1.6 skip: words and times for skip rules, shared by the player and Settings.</summary>
public static class SkipRuleText
{
	/// <summary>"0:41", "1:02:03"; never negative.</summary>
	public static string Time(double seconds) =>
		MpvControlsViewModel.FormatTime(TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds, MidpointRounding.AwayFromZero))));

	/// <summary>"0:41–1:43": a span in the episode playing.</summary>
	public static string Span(long startTicks, long endTicks) =>
		$"{Time(TimeSpan.FromTicks(startTicks).TotalSeconds)}–{Time(TimeSpan.FromTicks(endTicks).TotalSeconds)}";

	/// <summary>
	/// Where a rule is, in words: "0:00 to 0:41 from the start", "The last 1:02", "1:30 to 1:00 before the end".
	/// </summary>
	public static string Where(string anchor, double startS, double endS)
	{
		if (anchor == SkipRuleWords.End)
		{
			return endS < 0.5 ? $"The last {Time(startS)}" : $"{Time(startS)} to {Time(endS)} before the end";
		}

		return startS < 0.5 ? $"The first {Time(endS)}" : $"{Time(startS)} to {Time(endS)} from the start";
	}

	/// <summary>"Skip button" or "Skips by itself" (a rule with no choice of its own follows Settings).</summary>
	public static string What(string? action) => action switch
	{
		SkipRuleWords.Always => "Skips by itself",
		SkipRuleWords.Ask => "Skip button",
		_ => "As in Settings",
	};

	/// <summary>"Marked by you" or "Learned from your skips".</summary>
	public static string Origin(string origin) => origin == SkipRuleWords.Learned ? "Learned from your skips" : "Marked by you";

	/// <summary>
	/// A time typed in the mark sheet: "41", "0:41", "1:02:03" (seconds, minutes:seconds, hours:minutes:seconds); null for
	/// anything else, negative or over a day.
	/// </summary>
	public static double? ParseTime(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}

		var parts = text.Trim().Split(':');
		if (parts.Length > 3)
		{
			return null;
		}

		double total = 0;
		for (var i = 0; i < parts.Length; i++)
		{
			var last = i == parts.Length - 1;
			if (!double.TryParse(parts[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || value < 0
				|| (!last && value != Math.Floor(value)) || (i > 0 && value >= 60))
			{
				return null;
			}

			total = (total * 60) + value;
		}

		return total <= 86400 ? total : null;
	}
}
