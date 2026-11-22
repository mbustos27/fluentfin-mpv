using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FluentFin.Core.Mpv;

/// <summary>
/// 1.6 skip: skip rules in FluentFin's player controls: the "Skip this every episode?" toast for a <c>skip_suggestion</c>
/// (Yes, Not now, Never for this show; Escape is Not now), and the "Skip a part every episode" sheet (Shift+S, or the bar's
/// skip-parts button), pre-filled from the viewer's last forward seek (docs/BRIDGE.md §4, §5).
/// </summary>
public sealed partial class MpvControlsViewModel
{
	/// <summary>The toast goes by itself after this long, as a Not now (so the same skip can ask again later).</summary>
	public static readonly TimeSpan SuggestionFor = TimeSpan.FromSeconds(45);

	/// <summary>A forward jump this long (seconds) is one the sheet offers to mark: the shortest bumper, the longest ad block.</summary>
	public const double MarkJumpMin = 5, MarkJumpMax = SkipRuleWords.MaxSeconds;

	private BridgeSkipSuggestion? _suggestion;
	private DateTimeOffset _suggestionUntil;
	private (double From, double To)? _lastJump;
	private long? _jumpFrom;

	/// <summary>The item before this one: a question about it still shows (an end skip is asked as its episode ends).</summary>
	private string? _previousItemId;

	/// <summary>
	/// Sends a command and says whether the backend took it (true) or not; the page's. Without it, <see cref="SaveSkipRule"/>
	/// and the toast's answers go through the plain send and count as taken.
	/// </summary>
	public Func<string, JsonObject?, Task<bool>>? Request { get; set; }

	/// <summary>Settings' "When a show has a skip rule": the sheet's default ("ask": a Skip button; "always").</summary>
	public string SkipRuleAction { get; set; } = SkipRuleWords.Ask;

	/// <summary>The sheet's state; the view shows it in a flyout when <see cref="SkipSheetRequested"/> is raised.</summary>
	public SkipRuleSheet SkipSheet { get; } = new();

	/// <summary>Shift+S or the bar's button: the view opens the sheet.</summary>
	public event EventHandler? SkipSheetRequested;

	/// <summary>Skip rules are per show: the button and Shift+S work on episodes only (a film says so).</summary>
	public bool CanMarkSkipPart => _isEpisode;

	/// <summary>"Skip this every episode?" shows (bottom right, with the prompts), also while the bars are hidden.</summary>
	[ObservableProperty]
	public partial bool IsSuggestionVisible { get; set; }

	/// <summary>The toast's second line: where the part is in this episode ("0:41–1:43 in every episode of Shadows").</summary>
	[ObservableProperty]
	public partial string SuggestionDetail { get; set; } = "";

	/// <summary>The toast's name for Narrator: the question and the detail.</summary>
	public string SuggestionName => $"{SuggestionQuestion} {SuggestionDetail}";

	public const string SuggestionQuestion = "Skip this every episode?";

	partial void OnIsSuggestionVisibleChanged(bool value) => OnPropertyChanged(nameof(AnythingVisible));

	partial void OnSuggestionDetailChanged(string value) => OnPropertyChanged(nameof(SuggestionName));

	/// <summary>
	/// The <c>skip_suggestion</c> event: the toast shows, for the item playing or the one just before it. A skip of the promos
	/// after an episode is asked as that episode ends, and the question can arrive (or still be up) once the next one plays
	/// (review, Major 2: it was dropped for naming the item before).
	/// </summary>
	public void OnSkipSuggestion(BridgeSkipSuggestion suggestion)
	{
		if (!IsThisItem(suggestion.ItemId) && !SameId(suggestion.ItemId, _previousItemId))
		{
			return;
		}

		if (_suggestion is { } shown && shown.Id != suggestion.Id)
		{
			// a second question in the same episode takes the toast's place: the first is answered Not now, so the backend
			// can ask it again later rather than keep it as asked forever
			_ = SendChecked(Bridge.Commands.AnswerSkipSuggestion, BridgeSkipCommands.Answer(shown.Id, SkipRuleWords.NotNow));
		}

		_suggestion = suggestion;
		_suggestionUntil = _now() + SuggestionFor;
		var show = string.IsNullOrWhiteSpace(suggestion.SeriesName) ? "this show" : suggestion.SeriesName.Trim();
		SuggestionDetail = $"{SkipRuleText.Span(suggestion.StartTicks, suggestion.EndTicks)} in every episode of {show}";
		IsSuggestionVisible = true;
	}

	/// <summary>
	/// The toast's Yes, Not now or Never for this show. Never throws. <paramref name="byUser"/> false (its time ran out, its item
	/// ended): the bars stay as they are.
	/// </summary>
	public async Task AnswerSuggestion(string answer, bool byUser = true)
	{
		if (byUser)
		{
			NotifyActivity();
		}

		if (_suggestion is not { } suggestion)
		{
			return;
		}

		_suggestion = null;
		IsSuggestionVisible = false;
		var taken = await SendChecked(Bridge.Commands.AnswerSkipSuggestion, BridgeSkipCommands.Answer(suggestion.Id, answer));
		if (answer == SkipRuleWords.Yes)
		{
			ShowToast(taken ? "Skipping it in every episode" : "Couldn't save that. Try Shift+S.");
		}
		else if (answer == SkipRuleWords.Never && taken)
		{
			ShowToast("Won't ask about this show again");
		}
	}

	/// <summary>Escape (<paramref name="byUser"/>) or the toast's time running out: Not now. False when no toast shows.</summary>
	private bool DismissSuggestion(bool byUser = true)
	{
		if (!IsSuggestionVisible)
		{
			return false;
		}

		_ = AnswerSuggestion(SkipRuleWords.NotNow, byUser);
		return true;
	}

	/// <summary>From <see cref="Tick"/>: the toast's time.</summary>
	private void TickSuggestion(DateTimeOffset now)
	{
		if (IsSuggestionVisible && now >= _suggestionUntil)
		{
			DismissSuggestion(byUser: false);
		}
	}

	/// <summary>
	/// A new item: the last one's seek goes. Its question stays until answered or its time runs out: it is about the show,
	/// and an end skip's question comes just as its episode ends (review, Major 2). The backend keeps it open through this one
	/// item.
	/// </summary>
	private void ResetSkipRulesForItem(string? previousItemId)
	{
		_lastJump = null;
		_jumpFrom = null;
		_previousItemId = previousItemId;
	}

	private static bool SameId(string? a, string? b) =>
		a is not null && b is not null && string.Equals(a.Replace("-", ""), b.Replace("-", ""), StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// From <see cref="OnPosition"/> (about every 0.5 s): a forward jump between two reports of this item is one the sheet can
	/// offer. The first report of an item is no jump (a resume lands there).
	/// </summary>
	private void NoteJump(long toTicks)
	{
		if (_jumpFrom is long fromTicks)
		{
			var jump = TimeSpan.FromTicks(toTicks - fromTicks).TotalSeconds;
			if (jump is >= MarkJumpMin and <= MarkJumpMax)
			{
				_lastJump = (TimeSpan.FromTicks(fromTicks).TotalSeconds, TimeSpan.FromTicks(toTicks).TotalSeconds);
			}
		}

		_jumpFrom = toTicks;
	}

	/// <summary>Shift+S or the bar's button: fills the sheet and asks the view to open it; on a film, says why not.</summary>
	public void OpenSkipSheet()
	{
		NotifyActivity();
		if (!CanMarkSkipPart)
		{
			ShowToast("Skip parts work on episodes of a show");
			return;
		}

		SkipSheet.Prefill(_lastJump, TimeSpan.FromTicks(PositionTicks).TotalSeconds, TimeSpan.FromTicks(DurationTicks).TotalSeconds, SkipRuleAction);
		SkipSheetRequested?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>The sheet's Save: true when the backend took the rule (the sheet closes); false keeps it open with the reason.</summary>
	public async Task<bool> SaveSkipRule()
	{
		NotifyActivity();
		if (SkipSheet.Build() is not { } args)
		{
			return false;
		}

		if (!await SendChecked(Bridge.Commands.AddSkipRule, args))
		{
			SkipSheet.Error = "Couldn't save the rule. Try again in a moment.";
			return false;
		}

		ShowToast("Skipping it in every episode");
		return true;
	}

	private async Task<bool> SendChecked(string command, JsonObject args)
	{
		try
		{
			if (Request is { } request)
			{
				return await request(command, args);
			}

			await _send(command, args);
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}
}
