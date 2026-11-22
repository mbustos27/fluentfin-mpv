using System.Reactive.Linq;
using System.Text.Json.Nodes;
using FluentFin.Core.Mpv;
using FluentFin.Core.Settings;
using FluentFin.Core.Shortcuts;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.6 skip: skip rules in the player and in Settings (docs/BRIDGE.md §4 skip_rules, add_skip_rule, forget_skip_rules,
/// answer_skip_suggestion; §5 skip_suggestion, segments with source "rule"): the pill's words and tooltip by source, the
/// "Skip this every episode?" toast and its three answers (and Escape, and its time), the mark sheet (Shift+S), and the
/// Settings list. What the user sees is asserted: the text, the visibility, Narrator's name.
/// </summary>
public sealed class SkipRulesTests
{
	private const string Item = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee1";
	private const string Other = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee2";

	private DateTimeOffset _now = new(2026, 11, 21, 20, 0, 0, TimeSpan.Zero);
	private readonly List<(string Command, JsonObject? Args)> _sent = [];
	private readonly MpvControlsViewModel _vm;
	private bool _takes = true;

	public SkipRulesTests()
	{
		_vm = new MpvControlsViewModel((command, args) =>
		{
			_sent.Add((command, args?.DeepClone() as JsonObject));
			return Task.CompletedTask;
		}, () => _now)
		{
			Request = (command, args) =>
			{
				_sent.Add((command, args?.DeepClone() as JsonObject));
				return Task.FromResult(_takes);
			},
		};
	}

	private static long Ticks(double seconds) => TimeSpan.FromSeconds(seconds).Ticks;

	private void Wait(double seconds)
	{
		_now += TimeSpan.FromSeconds(seconds);
		_vm.Tick();
	}

	private void Playing(string type = "Episode", string itemId = Item, double position = 5)
	{
		_vm.OnEvent(new BridgeEvent(Bridge.Events.Item, JsonNode.Parse($$"""
			{"itemId": "{{itemId}}", "name": "Pilot", "type": "{{type}}", "seriesName": "Shadows", "seasonNumber": 1, "episodeNumber": 1, "runTimeTicks": {{Ticks(1400)}}}
			""")));
		_vm.OnState(new BridgeState { State = Bridge.States.Playing, ItemId = itemId, DurationTicks = Ticks(1400) });
		Position(position);
	}

	private void Position(double seconds) => _vm.OnPosition(new BridgePosition(Ticks(seconds), Ticks(1400)));

	private void Offer(string type, string source, string? origin = null) =>
		_vm.OnEvent(new BridgeEvent(Bridge.Events.Segments, JsonNode.Parse($$"""
			{"itemId": "{{Item}}", "segments": [{"type": "{{type}}", "startTicks": {{Ticks(0)}}, "endTicks": {{Ticks(41)}}, "action": "ask",
			  "source": "{{source}}"{{(origin is null ? "" : $", \"ruleId\": \"r0123456789ab\", \"origin\": \"{origin}\", \"anchor\": \"start\"")}}}],
			 "current": 0}
			""")));

	private void Suggest(string itemId = Item, string? seriesName = "Shadows", string id = "c0123456789ab") =>
		_vm.OnEvent(new BridgeEvent(Bridge.Events.SkipSuggestion, JsonNode.Parse($$"""
			{"id": "{{id}}", "itemId": "{{itemId}}", "seriesId": "abababababababababababababababab",
			 {{(seriesName is null ? "" : $"\"seriesName\": \"{seriesName}\",")}} "anchor": "start", "startTicks": {{Ticks(3)}}, "endTicks": {{Ticks(42)}}}
			""")));

	// -- the pill ------------------------------------------------------------------------------------------------------

	[Theory]
	[InlineData("Commercial", "rule", "marked", "Skip extra", "Your rule for this show")]
	[InlineData("Commercial", "rule", "learned", "Skip extra", "Learned from your skips")]
	[InlineData("Intro", "rule", "marked", "Skip intro", "Your rule for this show")]
	[InlineData("Outro", "rule", "learned", "Skip credits", "Learned from your skips")]
	[InlineData("Commercial", "chapters", null, "Skip commercial", "from chapters")]
	[InlineData("Intro", "server", null, "Skip intro", null)]
	public void ThePill_SaysWhatTheRuleIs_AndWhereItCameFrom(string type, string source, string? origin, string text, string? note)
	{
		Playing();
		Offer(type, source, origin);

		Assert.True(_vm.IsSkipVisible);
		Assert.Equal(text, _vm.SkipText);
		Assert.Equal(note, _vm.SkipSourceNote);
	}

	[Fact]
	public void ARulesSegment_ParsesItsRuleFields_AndOldOnesHaveNone()
	{
		Playing();
		var e = new BridgeEvent(Bridge.Events.Segments, JsonNode.Parse("""
			{"itemId": "x", "segments": [
			  {"type": "Commercial", "startTicks": 1, "endTicks": 2, "action": "always", "source": "rule", "ruleId": "r1", "origin": "learned", "anchor": "end"},
			  {"type": "Intro", "startTicks": 3, "endTicks": 4, "action": "ask"}], "current": null}
			"""));
		var segments = e.DataAs<BridgeSegments>()!.Segments!;
		Assert.Equal(("r1", "learned", "end"), (segments[0].RuleId, segments[0].Origin, segments[0].Anchor));
		Assert.True(segments[0].IsExtra);
		Assert.Equal("Skip Extra", segments[0].SkipLabel);
		Assert.Null(segments[1].RuleId);
		Assert.False(segments[1].IsExtra);
	}

	[Fact]
	public void S_SkipsARulesPart_AsAnyOffer_AndNothingWithoutOne()
	{
		Playing();
		Assert.True(_vm.HandleKey(MpvControlsViewModel.Keys.S));
		Assert.DoesNotContain(_sent, x => x.Command == Bridge.Commands.SkipSegment);
		Offer("Commercial", "rule", "marked");
		Assert.True(_vm.HandleKey(MpvControlsViewModel.Keys.S));
		Assert.Contains(_sent, x => x.Command == Bridge.Commands.SkipSegment);
		Assert.False(_vm.IsSkipVisible);
	}

	// -- the toast ---------------------------------------------------------------------------------------------------

	[Fact]
	public void TheToast_ShowsTheSpanAndTheShow_AlsoWithTheBarsHidden()
	{
		Playing();
		Wait(5);
		Assert.False(_vm.ControlsVisible);

		Suggest();

		Assert.True(_vm.IsSuggestionVisible);
		Assert.True(_vm.AnythingVisible, "the window the controls draw in shows");
		Assert.Equal("0:03–0:42 in every episode of Shadows", _vm.SuggestionDetail);
		Assert.Equal("Skip this every episode? 0:03–0:42 in every episode of Shadows", _vm.SuggestionName);
	}

	[Fact]
	public void TheToast_WithNoShowName_SaysThisShow()
	{
		Playing();
		Suggest(seriesName: null);
		Assert.EndsWith("in every episode of this show", _vm.SuggestionDetail);
	}

	[Fact]
	public void ASecondQuestion_TakesThePlace_AndTheFirstIsNotNow()
	{
		Playing();
		Suggest(id: "c000000000001");
		Suggest(id: "c000000000002");
		Assert.True(_vm.IsSuggestionVisible);
		var sent = Assert.Single(_sent, x => x.Command == Bridge.Commands.AnswerSkipSuggestion);
		Assert.Equal(("c000000000001", "not_now"), ((string)sent.Args!["id"]!, (string)sent.Args!["answer"]!));
		// the same one again (a repeated event) answers nothing
		Suggest(id: "c000000000002");
		Assert.Single(_sent, x => x.Command == Bridge.Commands.AnswerSkipSuggestion);
	}

	[Fact]
	public void TheToast_IsForTheItemPlaying()
	{
		Playing();
		Suggest(itemId: Other);
		Assert.False(_vm.IsSuggestionVisible);
	}

	[Theory]
	[InlineData("yes", "Skipping it in every episode")]
	[InlineData("not_now", null)]
	[InlineData("never", "Won't ask about this show again")]
	public async Task EachAnswer_GoesToTheBackend_AndTheToastGoes(string answer, string? toast)
	{
		Playing();
		Suggest();

		await _vm.AnswerSuggestion(answer);

		Assert.False(_vm.IsSuggestionVisible);
		var sent = _sent.Single(x => x.Command == Bridge.Commands.AnswerSkipSuggestion);
		Assert.Equal("c0123456789ab", (string)sent.Args!["id"]!);
		Assert.Equal(answer, (string)sent.Args!["answer"]!);
		Assert.Equal(toast is not null, _vm.ToastVisible);
		if (toast is not null)
		{
			Assert.Equal(toast, _vm.ToastText);
		}

		// answered once: a second press does nothing
		await _vm.AnswerSuggestion(answer);
		Assert.Single(_sent, x => x.Command == Bridge.Commands.AnswerSkipSuggestion);
	}

	[Fact]
	public async Task AYesTheBackendRefused_SaysSo()
	{
		Playing();
		Suggest();
		_takes = false;
		await _vm.AnswerSuggestion(SkipRuleWords.Yes);
		Assert.Equal("Couldn't save that. Try Shift+S.", _vm.ToastText);
	}

	// 1.6 integration (the coordinator's Esc order; skip review: Esc answered the toast before closing Cinematic's panel):
	// the panel the viewer is looking at closes first, and only the next Escape is Not now (EscapeOrderTests has the rest)
	[Fact]
	public void Escape_ClosesAnOpenPanelFirst_ThenIsNotNow()
	{
		Playing();
		Suggest();
		var panelOpen = true;
		_vm.ClosePanelsRequested += (_, e) =>
		{
			if (panelOpen)
			{
				panelOpen = false;
				e.Handled = true;
			}
		};

		Assert.True(_vm.ClosePanels());

		Assert.False(panelOpen, "the panel closed");
		Assert.True(_vm.IsSuggestionVisible, "one Escape, one thing closed: the question waits");
		Assert.DoesNotContain(_sent, x => x.Command == Bridge.Commands.AnswerSkipSuggestion);
		// no panel now: Escape is Not now
		Assert.True(_vm.ClosePanels());
		Assert.False(_vm.IsSuggestionVisible);
		Assert.Equal("not_now", (string)_sent.Single(x => x.Command == Bridge.Commands.AnswerSkipSuggestion).Args!["answer"]!);
		// and then Escape goes on to the page (full screen)
		Assert.False(_vm.ClosePanels());
	}

	[Fact]
	public void TheToast_GoesAfterItsTime_AsNotNow()
	{
		Playing();
		Suggest();
		Wait(MpvControlsViewModel.SuggestionFor.TotalSeconds - 1);
		Assert.True(_vm.IsSuggestionVisible);
		Assert.False(_vm.ControlsVisible, "the bars went meanwhile");
		Wait(2);
		Assert.False(_vm.IsSuggestionVisible);
		Assert.False(_vm.ControlsVisible, "its time running out wakes nothing");
		Assert.Equal("not_now", (string)_sent.Single(x => x.Command == Bridge.Commands.AnswerSkipSuggestion).Args!["answer"]!);
	}

	/// <summary>
	/// Review (Major 2): a skip of the promos after an episode is asked as that episode ends; the next one starts at once.
	/// The question stays up into the next episode (nothing is answered for it), and its Yes still reaches the backend.
	/// </summary>
	[Fact]
	public async Task TheToast_StaysIntoTheNextEpisode_AndItsYesStillGoes()
	{
		Playing();
		Suggest();
		Playing(itemId: Other);
		Assert.True(_vm.IsSuggestionVisible);
		Assert.DoesNotContain(_sent, x => x.Command == Bridge.Commands.AnswerSkipSuggestion);

		await _vm.AnswerSuggestion(SkipRuleWords.Yes);
		Assert.Equal("yes", (string)_sent.Single(x => x.Command == Bridge.Commands.AnswerSkipSuggestion).Args!["answer"]!);
	}

	/// <summary>Review (Major 2): the question can arrive after the next episode began, naming the one before: it shows.</summary>
	[Fact]
	public void AQuestionAboutTheEpisodeJustEnded_Shows()
	{
		Playing();
		Playing(itemId: Other);
		Suggest(itemId: Item);
		Assert.True(_vm.IsSuggestionVisible);
		Assert.EndsWith("in every episode of Shadows", _vm.SuggestionDetail);
	}

	[Fact]
	public void AQuestionAboutAnOlderEpisode_DoesNotShow()
	{
		Playing(itemId: "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee3");
		Playing();
		Playing(itemId: Other);
		Suggest(itemId: "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee3");
		Assert.False(_vm.IsSuggestionVisible);
	}

	// -- the mark sheet ------------------------------------------------------------------------------------------------

	[Fact]
	public void ShiftS_OpensTheSheet_PrefilledFromTheLastForwardSeek()
	{
		Playing(position: 2);
		Position(2.5);
		Position(41);           // the viewer jumped over the bumper
		Position(41.5);
		var opened = 0;
		_vm.SkipSheetRequested += (_, _) => opened++;

		Assert.Equal(MpvControlsAction.MarkSkipPart, MpvControlsViewModel.ActionFor(MpvControlsViewModel.Keys.S, false, true, false));
		Assert.True(_vm.HandleKey(MpvControlsViewModel.Keys.S, shift: true));

		Assert.Equal(1, opened);
		Assert.Equal("0:03", _vm.SkipSheet.StartText);
		Assert.Equal("0:41", _vm.SkipSheet.EndText);
		Assert.False(_vm.SkipSheet.FromEnd);
		Assert.False(_vm.SkipSheet.SkipsByItself, "a Skip button by default");
		Assert.Equal("0:03 to 0:41 from the start of every episode, with a Skip button", _vm.SkipSheet.Summary);
	}

	[Fact]
	public void TheFirstPositionOfAnItem_IsNoSeek()
	{
		Playing(position: 600);  // a resume
		_vm.OpenSkipSheet();
		Assert.Equal("10:00", _vm.SkipSheet.StartText);
		Assert.Equal("10:30", _vm.SkipSheet.EndText);
	}

	[Fact]
	public void OnAFilm_ShiftS_SaysWhyNot()
	{
		Playing(type: "Movie");
		var opened = 0;
		_vm.SkipSheetRequested += (_, _) => opened++;
		_vm.HandleKey(MpvControlsViewModel.Keys.S, shift: true);
		Assert.Equal(0, opened);
		Assert.False(_vm.CanMarkSkipPart);
		Assert.Equal("Skip parts work on episodes of a show", _vm.ToastText);
	}

	[Fact]
	public async Task Save_SendsTheRule_ForTheShowPlaying_AndSaysSo()
	{
		Playing(position: 1340);
		_vm.SkipRuleAction = SkipRuleWords.Always;
		_vm.OpenSkipSheet();
		_vm.SkipSheet.StartText = "22:18";
		_vm.SkipSheet.EndText = "23:20";
		Assert.True(_vm.SkipSheet.FromEnd, "a part in the second half is measured from the end");
		Assert.True(_vm.SkipSheet.SkipsByItself, "Settings' choice");

		Assert.True(await _vm.SaveSkipRule());

		var args = _sent.Single(x => x.Command == Bridge.Commands.AddSkipRule).Args!;
		Assert.Equal("end", (string)args["anchor"]!);
		Assert.Equal(62.0, (double)args["startS"]!);   // 23:20 is the end: 1400 - 1338
		Assert.Equal(0.0, (double)args["endS"]!);
		Assert.Equal("always", (string)args["action"]!);
		Assert.False(args.ContainsKey("seriesId"), "the show playing: the backend knows it");
		Assert.Equal("Skipping it in every episode", _vm.ToastText);
	}

	[Fact]
	public async Task Save_TheBackendRefused_KeepsTheSheetOpen_WithTheReason()
	{
		Playing();
		_vm.OpenSkipSheet();
		_takes = false;
		Assert.False(await _vm.SaveSkipRule());
		Assert.Equal("Couldn't save the rule. Try again in a moment.", _vm.SkipSheet.Error);
	}

	[Theory]
	[InlineData("", "0:41", "Type the start as minutes and seconds, like 0:41.")]
	[InlineData("0:10", "nope", "Type the end as minutes and seconds, like 1:43.")]
	[InlineData("0:41", "0:10", "The end has to come after the start.")]
	[InlineData("0:00", "10:01", "A part can be at most 10 minutes long.")]
	[InlineData("23:30", "23:40", "That starts after the episode ends.")]
	public void TheSheet_SaysWhatToFix(string start, string end, string error)
	{
		var sheet = new SkipRuleSheet();
		sheet.Prefill(null, 0, 1400, "ask");
		(sheet.StartText, sheet.EndText) = (start, end);
		Assert.Null(sheet.Build());
		Assert.Equal(error, sheet.Error);
	}

	[Fact]
	public void TheSheet_MarksStartAndEnd_FromThePosition()
	{
		var sheet = new SkipRuleSheet();
		sheet.Prefill(null, 0, 1400, "ask");
		sheet.MarkStart(12.4);
		sheet.MarkEnd(53.6);
		Assert.Equal(("0:12", "0:54"), (sheet.StartText, sheet.EndText));
		var args = sheet.Build()!;
		Assert.Equal(("start", 12.0, 54.0, "ask"), ((string)args["anchor"]!, (double)args["startS"]!, (double)args["endS"]!, (string)args["action"]!));
	}

	[Theory]
	[InlineData("41", 41.0)]
	[InlineData("0:41", 41.0)]
	[InlineData("1:02:03", 3723.0)]
	[InlineData(" 1:05 ", 65.0)]
	[InlineData("1:60", null)]
	[InlineData("-1", null)]
	[InlineData("a:10", null)]
	[InlineData("1:2:3:4", null)]
	[InlineData(null, null)]
	public void Times_AreTyped_AsMinutesAndSeconds(string? text, double? seconds) => Assert.Equal(seconds, SkipRuleText.ParseTime(text));

	[Fact]
	public void ShiftS_IsInTheShortcutSheet_AndIsThePlayers()
	{
		var entry = ShortcutCatalog.Sections.SelectMany(s => s.Entries).Single(x => x.Keys == "Shift+S");
		Assert.Equal(ShortcutScope.Player, entry.Scope);
		Assert.Null(AppShortcuts.For(MpvControlsViewModel.Keys.S, false, true, false, inTextInput: false));
	}

	// -- Settings' list ------------------------------------------------------------------------------------------------

	private static BridgeSkipRulesList Listing() => new([
		new BridgeSkipShow("abababababababababababababababab", "Shadows",
			[new BridgeSkipRule("r1", "start", 0, 41, null, "marked"), new BridgeSkipRule("r2", "end", 62, 0, "always", "learned")],
			[new BridgeLearnedIntro("intro-abababababababababababababababab-2", 2, 104, 132, false),
			 new BridgeLearnedIntro("intro-abababababababababababababababab-3", 3, 40, 70, true)],
			false),
		new BridgeSkipShow("cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd", null, [], [], true),
	]);

	[Fact]
	public void TheList_ShowsEachShow_ThenItsRulesAndIntros_InWords()
	{
		var rows = SkipRulesList.RowsOf(Listing());

		Assert.Equal(
		[
			"Shadows | 2 rules · 2 learned intros | Forget everything for Shadows",
			"The first 0:41 | As in Settings · Marked by you | Forget The first 0:41 of Shadows",
			"The last 1:02 | Skips by itself · Learned from your skips | Forget The last 1:02 of Shadows",
			"Season 2 intro | 1:44 to 2:12 · Learned from your skips | Forget the season 2 intro of Shadows",
			"Season 3 intro | Moves between episodes, so it's not offered | Forget the season 3 intro of Shadows",
			"A show | No suggestions (you said never) | Forget everything for A show",
		], rows.Select(x => $"{x.Title} | {x.Detail} | {x.ForgetName}"));
		Assert.Equal([true, false, false, false, false, true], rows.Select(x => x.IsShow));
	}

	[Fact]
	public void TheList_OfNothing_IsEmpty() => Assert.Empty(SkipRulesList.RowsOf(new BridgeSkipRulesList([])));

	private static Mock<IMpvBackend> Backend(Func<string, JsonObject?, JsonNode?> answer)
	{
		var backend = new Mock<IMpvBackend>();
		backend.SetupGet(x => x.StatusChanged).Returns(Observable.Never<MpvBackendStatus>());
		backend.Setup(x => x.SendAsync(It.IsAny<string>(), It.IsAny<JsonObject?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
			.Returns<string, JsonObject?, TimeSpan?, CancellationToken>((command, args, _, _) => Task.FromResult(answer(command, args)));
		return backend;
	}

	[Fact]
	public async Task TheList_IsReadFromTheBackend_ForThisAccount_AndForgetReadsItAgain()
	{
		var sent = new List<(string, JsonObject?)>();
		var shows = new List<BridgeSkipShow>(Listing().Shows!);
		var backend = Backend((command, args) =>
		{
			sent.Add((command, args?.DeepClone() as JsonObject));
			if (command == Bridge.Commands.ForgetSkipRules)
			{
				shows.Clear();
				return JsonNode.Parse("""{"forgotten": 2}""");
			}

			return System.Text.Json.JsonSerializer.SerializeToNode(new BridgeSkipRulesList(shows), Bridge.JsonOptions);
		});
		var list = new SkipRulesList(backend.Object, () => ("S1", "0123456789abcdef0123456789abcdef"));

		await list.LoadAsync();

		backend.Verify(x => x.StartAsync(false, It.IsAny<CancellationToken>()), Times.Once);
		Assert.Equal(6, list.Rows.Count);
		Assert.True(list.HasRows);
		Assert.True(list.CanForgetAll);
		Assert.Equal("", list.Status);
		var (command, args) = sent[0];
		Assert.Equal("skip_rules", command);
		Assert.Equal(["serverId", "userId"], args!.Select(x => x.Key).Order());

		await list.ForgetAsync(list.Rows[1]);
		Assert.Equal("r1", (string)sent[1].Item2!["ruleId"]!);
		Assert.Empty(list.Rows);
		Assert.Equal(SkipRulesList.Empty, list.Status);
		Assert.False(list.CanForgetAll);
	}

	[Fact]
	public async Task TheList_ForgetsAShow_ByItsId()
	{
		JsonObject? forgot = null;
		var backend = Backend((command, args) =>
		{
			if (command == Bridge.Commands.ForgetSkipRules)
			{
				forgot = args?.DeepClone() as JsonObject;
			}

			return System.Text.Json.JsonSerializer.SerializeToNode(Listing(), Bridge.JsonOptions);
		});
		var list = new SkipRulesList(backend.Object, () => ("S1", "0123456789abcdef0123456789abcdef"));
		await list.LoadAsync();
		await list.ForgetAsync(list.Rows[0]);
		Assert.Equal("abababababababababababababababab", (string)forgot!["seriesId"]!);
		Assert.False(forgot.ContainsKey("ruleId"));
	}

	[Fact]
	public async Task TheList_SaysWhatWentWrong()
	{
		var down = new Mock<IMpvBackend>();
		down.Setup(x => x.StartAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).ThrowsAsync(new BridgeException("no backend"));
		var list = new SkipRulesList(down.Object, () => ("S1", "0123456789abcdef0123456789abcdef"));
		await list.LoadAsync();
		Assert.Equal(SkipRulesList.Unreachable, list.Status);
		Assert.False(list.IsBusy);

		var signedOut = new SkipRulesList(down.Object, () => (null, null));
		await signedOut.LoadAsync();
		Assert.Equal(SkipRulesList.NoAccount, signedOut.Status);
		down.Verify(x => x.StartAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once, "no start without an account");
	}

	[Fact]
	public void Settings_Defaults_AreASkipButton()
	{
		Assert.Equal("ask", SettingKeys.AdsAndPreviewsAction.Default.Value);
		Assert.Equal("ask", SettingKeys.SkipRuleAction.Default.Value);
		Assert.Equal("ask", BridgeSkipCommands.SegmentAction(null));
		Assert.Equal("ask", BridgeSkipCommands.RuleAction("bogus"));
		Assert.Equal("off", BridgeSkipCommands.SegmentAction("off"));
	}
}
