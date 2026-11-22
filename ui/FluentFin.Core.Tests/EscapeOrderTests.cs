using System.Text.Json.Nodes;
using FluentFin.Core.Mpv;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.6 (the integration of the skip and player streams): Escape in the player closes the innermost thing first, one thing
/// per press (docs/SHORTCUTS.md, Esc; <see cref="MpvControlsViewModel.ClosePanels"/>): an open menu or flyout (it closes
/// itself), then the Playback stats panel, then a theme's panel (Cinematic's tabs panel), then "Skip this every episode?" as
/// Not now, and only then the page's own rule (false: leave full screen; in a window, show the controls). Escape never answers
/// the question while a panel the viewer is looking at is open. What the viewer sees is asserted at each press.
/// </summary>
public sealed class EscapeOrderTests
{
	private const string Item = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee1";

	private DateTimeOffset _now = new(2026, 11, 21, 20, 0, 0, TimeSpan.Zero);
	private readonly List<(string Command, JsonObject? Args)> _sent = [];
	private readonly MpvControlsViewModel _vm;

	/// <summary>
	/// A theme's panel (Cinematic's tabs panel) as CinematicPlayerPanel is: it holds the bars through MenuOpened while open, and
	/// closes on ClosePanelsRequested.
	/// </summary>
	private bool _themePanelOpen;

	private void OpenThemePanel()
	{
		_themePanelOpen = true;
		_vm.MenuOpened(true);
	}

	public EscapeOrderTests()
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
				return Task.FromResult(true);
			},
			Query = (_, _) => Task.FromResult<JsonNode?>(null),
		};
		_vm.ClosePanelsRequested += (_, e) =>
		{
			if (_themePanelOpen && !e.Handled)
			{
				_themePanelOpen = false;
				_vm.MenuOpened(false);
				e.Handled = true;
			}
		};
		_vm.OnEvent(new BridgeEvent(Bridge.Events.Item, JsonNode.Parse($$"""
			{"itemId": "{{Item}}", "name": "Pilot", "type": "Episode", "seriesName": "Shadows", "seasonNumber": 1, "episodeNumber": 2, "runTimeTicks": {{Ticks(1400)}}}
			""")));
		_vm.OnState(new BridgeState { State = Bridge.States.Playing, ItemId = Item, DurationTicks = Ticks(1400) });
		_vm.OnPosition(new BridgePosition(Ticks(95), Ticks(1400)));
	}

	private static long Ticks(double seconds) => TimeSpan.FromSeconds(seconds).Ticks;

	private void Ask() =>
		_vm.OnEvent(new BridgeEvent(Bridge.Events.SkipSuggestion, JsonNode.Parse($$"""
			{"id": "c0123456789ab", "itemId": "{{Item}}", "seriesId": "abababababababababababababababab", "seriesName": "Shadows",
			 "anchor": "start", "startTicks": {{Ticks(60)}}, "endTicks": {{Ticks(90)}}}
			""")));

	private bool Answered => _sent.Any(x => x.Command == Bridge.Commands.AnswerSkipSuggestion);

	private string Seen() =>
		$"stats {_vm.IsStatsOpen}, panel {_themePanelOpen}, question {_vm.IsSuggestionVisible}, answered {Answered}";

	[Fact]
	public void EveryLayerOpen_EachEscapeClosesOne_TheInnermostFirst_ThenThePageHasIt()
	{
		Ask();
		_vm.ToggleStats();
		OpenThemePanel();
		_vm.FlyoutOpened(true);
		Assert.True(_vm.IsSuggestionVisible && _vm.IsStatsOpen && _themePanelOpen, Seen());

		// 1. the menu: it takes its own Escape (WinUI closes it); if the press reaches the player too, nothing under it goes
		Assert.True(_vm.ClosePanels());
		Assert.True(_vm.IsStatsOpen && _themePanelOpen && _vm.IsSuggestionVisible && !Answered, Seen());
		_vm.FlyoutOpened(false);

		// 2. the Playback stats panel
		Assert.True(_vm.ClosePanels());
		Assert.False(_vm.IsStatsOpen, Seen());
		Assert.Equal("Show playback stats", _vm.StatsButtonName);
		Assert.True(_themePanelOpen && _vm.IsSuggestionVisible && !Answered, Seen());

		// 3. the theme's panel
		Assert.True(_vm.ClosePanels());
		Assert.False(_themePanelOpen, Seen());
		Assert.True(_vm.IsSuggestionVisible && !Answered, Seen());

		// 4. only now: "Skip this every episode?" is answered Not now
		Assert.True(_vm.ClosePanels());
		Assert.False(_vm.IsSuggestionVisible, Seen());
		Assert.Equal("not_now", (string)_sent.Single(x => x.Command == Bridge.Commands.AnswerSkipSuggestion).Args!["answer"]!);

		// 5. nothing left: the page's own Escape (leave full screen; in a window, show the controls)
		Assert.False(_vm.ClosePanels());
		Assert.Single(_sent, x => x.Command == Bridge.Commands.AnswerSkipSuggestion);
	}

	[Fact]
	public void TheStatsOpen_EscapeClosesThem_TheQuestionStays()
	{
		Ask();
		_vm.ToggleStats();

		Assert.True(_vm.ClosePanels());

		Assert.False(_vm.IsStatsOpen);
		Assert.True(_vm.IsSuggestionVisible, "Esc never answers the question while a panel the viewer is looking at is open");
		Assert.False(Answered);
	}

	[Fact]
	public void TheThemesPanelOpen_EscapeClosesIt_TheQuestionStays()
	{
		Ask();
		OpenThemePanel();

		Assert.True(_vm.ClosePanels());

		Assert.False(_themePanelOpen);
		Assert.True(_vm.IsSuggestionVisible);
		Assert.False(Answered);
	}

	// the keys matrix's first run of section 11 (Cinematic): the panel holds the bars as a menu does (MenuOpened), and an Escape
	// rung on "a menu holds the bars" left Escape dead under the panel and the stats panel. Holding the bars is not a flyout.
	[Fact]
	public void APanelHoldingTheBars_IsNoFlyout_EscapeStillClosesTheStatsThenIt()
	{
		_vm.ToggleStats();
		OpenThemePanel();
		Assert.Equal("a menu is open", _vm.HeldBy);   // the bars are held, as Cinematic's panel holds them

		Assert.True(_vm.ClosePanels());
		Assert.False(_vm.IsStatsOpen, Seen());
		Assert.True(_themePanelOpen, Seen());
		Assert.True(_vm.ClosePanels());
		Assert.False(_themePanelOpen, Seen());
		Assert.False(_vm.ClosePanels(), "nothing open: the page's Escape (leave full screen)");
	}

	[Fact]
	public void AMenuOpen_EscapeLeavesTheStatsAndTheQuestion_AndAfterItClosesTheLadderGoesOn()
	{
		Ask();
		_vm.ToggleStats();
		_vm.FlyoutOpened(true);

		// a held or repeated Escape while the menu is still closing changes nothing under it
		Assert.True(_vm.ClosePanels());
		Assert.True(_vm.ClosePanels());
		Assert.True(_vm.IsStatsOpen && _vm.IsSuggestionVisible && !Answered, Seen());

		// the way out: the menu closed (by Esc or a click), the next Escapes go on
		_vm.FlyoutOpened(false);
		Assert.True(_vm.ClosePanels());
		Assert.False(_vm.IsStatsOpen);
		Assert.True(_vm.ClosePanels());
		Assert.False(_vm.IsSuggestionVisible);
		Assert.False(_vm.ClosePanels());
	}

	[Fact]
	public void TwoMenusOpenedAndClosed_TheCountComesBackToNothing()
	{
		// the volume flyout, then the Quality menu (one replaces the other: the second opens before the first's Closed)
		_vm.FlyoutOpened(true);
		_vm.FlyoutOpened(true);
		_vm.FlyoutOpened(false);
		Assert.True(_vm.ClosePanels(), "one is still open");
		_vm.FlyoutOpened(false);
		// a stray Closed never makes the count negative (which would let Escape through under an open menu later)
		_vm.FlyoutOpened(false);
		Assert.False(_vm.ClosePanels());
		_vm.FlyoutOpened(true);
		Assert.True(_vm.ClosePanels());
	}

	[Fact]
	public void NothingOpen_EscapeIsThePages_FromTheFirstPress()
	{
		// keyboard first: Escape right after the player opened, before any click or any panel
		Assert.False(_vm.ClosePanels());
		Assert.False(_vm.IsStatsOpen);
		Assert.Empty(_sent.Where(x => x.Command == Bridge.Commands.AnswerSkipSuggestion));
	}

	[Fact]
	public void TheQuestionAlone_EscapeIsNotNow_WithNoToastOfItsOwn()
	{
		Ask();
		Assert.True(_vm.ClosePanels());
		Assert.False(_vm.IsSuggestionVisible);
		Assert.Equal("", _vm.ToastText);
		Assert.False(_vm.ClosePanels());
	}
}
