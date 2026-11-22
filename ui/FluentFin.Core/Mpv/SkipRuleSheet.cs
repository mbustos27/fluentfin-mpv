using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FluentFin.Core.Mpv;

/// <summary>
/// 1.6 skip: the player's "Skip a part every episode" sheet (Shift+S, or the bar's skip-parts button). The viewer gives a
/// part of the episode playing, by its times here (pre-filled from their last forward seek, or marked with the position
/// now), says whether it sits a fixed time from the episode's start or from its end, and whether it gets a Skip button or
/// is skipped by itself. <see cref="Build"/> turns that into <c>add_skip_rule</c> (docs/BRIDGE.md §4) for the show playing.
/// Pure: no bridge, no clock.
/// </summary>
public sealed partial class SkipRuleSheet : ObservableObject
{
	/// <summary>Where the part starts in this episode, as typed ("0:41").</summary>
	[ObservableProperty]
	public partial string StartText { get; set; } = "0:00";

	/// <summary>Where it ends in this episode.</summary>
	[ObservableProperty]
	public partial string EndText { get; set; } = "0:30";

	/// <summary>True: the part sits a fixed time before each episode's end (promos after the credits); false: from the start.</summary>
	[ObservableProperty]
	public partial bool FromEnd { get; set; }

	/// <summary>True: skipped by itself; false: a Skip button (the default, so the viewer sees where it lands).</summary>
	[ObservableProperty]
	public partial bool SkipsByItself { get; set; }

	/// <summary>What is wrong with the times, in words; empty when nothing is.</summary>
	[ObservableProperty]
	public partial string Error { get; set; } = "";

	/// <summary>The episode's length in seconds (0: not known yet).</summary>
	public double DurationSeconds { get; private set; }

	/// <summary>
	/// Fills the sheet as it opens: the last forward seek in this episode if there is one (the usual moment: the viewer just
	/// jumped over the part), else 30 s from now. A part in the episode's second half is measured from its end.
	/// </summary>
	public void Prefill((double From, double To)? lastJump, double positionSeconds, double durationSeconds, string ruleAction)
	{
		DurationSeconds = Math.Max(0, durationSeconds);
		double start, end;
		if (lastJump is { } jump && jump.To > jump.From)
		{
			(start, end) = (jump.From, jump.To);
		}
		else
		{
			start = Math.Max(0, positionSeconds);
			end = start + 30;
		}

		if (DurationSeconds > 0)
		{
			end = Math.Min(end, DurationSeconds);
			start = Math.Min(start, Math.Max(0, end - 1));
		}

		StartText = SkipRuleText.Time(start);
		EndText = SkipRuleText.Time(end);
		FromEnd = DurationSeconds > 0 && start >= DurationSeconds / 2;
		SkipsByItself = ruleAction == SkipRuleWords.Always;
		Error = "";
	}

	/// <summary>"Mark start": the part starts where playback is now.</summary>
	public void MarkStart(double positionSeconds)
	{
		StartText = SkipRuleText.Time(positionSeconds);
		Error = "";
	}

	/// <summary>"Mark end": the part ends where playback is now.</summary>
	public void MarkEnd(double positionSeconds)
	{
		EndText = SkipRuleText.Time(positionSeconds);
		Error = "";
	}

	/// <summary>What the sheet will save, in words ("The first 0:41, a Skip button"); empty while the times are wrong.</summary>
	public string Summary
	{
		get
		{
			if (Times() is not { } times)
			{
				return "";
			}

			var (anchor, startS, endS) = Anchored(times.Start, times.End);
			return $"{SkipRuleText.Where(anchor, startS, endS)} of every episode, {(SkipsByItself ? "skipped by itself" : "with a Skip button")}";
		}
	}

	partial void OnStartTextChanged(string value) => OnPropertyChanged(nameof(Summary));

	partial void OnEndTextChanged(string value) => OnPropertyChanged(nameof(Summary));

	partial void OnFromEndChanged(bool value) => OnPropertyChanged(nameof(Summary));

	partial void OnSkipsByItselfChanged(bool value) => OnPropertyChanged(nameof(Summary));

	/// <summary>
	/// <c>add_skip_rule</c>'s arguments for the show playing, or null with <see cref="Error"/> saying what to fix.
	/// </summary>
	public JsonObject? Build()
	{
		var start = SkipRuleText.ParseTime(StartText);
		var end = SkipRuleText.ParseTime(EndText);
		Error = start is null ? "Type the start as minutes and seconds, like 0:41."
			: end is null ? "Type the end as minutes and seconds, like 1:43."
			: end <= start ? "The end has to come after the start."
			: end - start > SkipRuleWords.MaxSeconds ? "A part can be at most 10 minutes long."
			: DurationSeconds > 0 && start >= DurationSeconds ? "That starts after the episode ends."
			: FromEnd && DurationSeconds <= 0 ? "The episode's length isn't known yet. Try again in a moment."
			: "";
		if (Error.Length > 0 || start is not { } s || end is not { } e)
		{
			return null;
		}

		var (anchor, startS, endS) = Anchored(s, e);
		return BridgeSkipCommands.AddRule(anchor, startS, endS, SkipsByItself ? SkipRuleWords.Always : SkipRuleWords.Ask);
	}

	private (double Start, double End)? Times() =>
		SkipRuleText.ParseTime(StartText) is { } s && SkipRuleText.ParseTime(EndText) is { } e && e > s ? (s, e) : null;

	/// <summary>The times in this episode, measured as the rule keeps them: from the start, or back from the end.</summary>
	private (string Anchor, double StartS, double EndS) Anchored(double start, double end)
	{
		if (!FromEnd || DurationSeconds <= 0)
		{
			return (SkipRuleWords.Start, start, end);
		}

		end = Math.Min(end, DurationSeconds);
		var endS = DurationSeconds - end < 1 ? 0 : DurationSeconds - end;
		return (SkipRuleWords.End, DurationSeconds - start, endS);
	}
}
