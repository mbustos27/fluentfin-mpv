using FluentFin.Controls;
using Microsoft.UI.Xaml;

namespace FluentFin.Tests;

/// <summary>
/// 1.5 (the top bar review's T5): when the tabs are rebuilt (the libraries arriving, the window folding them, the lit tab
/// changing with the page), the tab that had the focus gets it back without the box unless it was keyboard focus; and a tab
/// the window focuses itself before any key or click (at launch, after a page change) drops the box. Before, Home wore the
/// focus box over its lit underline at launch and kept it after a page change though no key had been pressed. The drawn
/// result is checked off-screen in four steps (then-t5-launch.ps1: launch, resized, another page, back on Home).
/// </summary>
public sealed class TabRefocusTests
{
	[Theory]
	[InlineData(FocusState.Keyboard, 1234L, FocusState.Keyboard)] // the user's keys put it there: the box stays
	[InlineData(FocusState.Keyboard, 0L, FocusState.Pointer)]     // the window's own first focus, nothing pressed yet
	[InlineData(FocusState.Programmatic, 1234L, FocusState.Pointer)]
	[InlineData(FocusState.Programmatic, 0L, FocusState.Pointer)]
	[InlineData(FocusState.Pointer, 0L, FocusState.Pointer)]
	public void ARebuiltTab_GetsItsFocusBack_WithTheBoxOnlyForTheUsersKeyboardFocus(FocusState had, long userActedAt, FocusState gets) =>
		Assert.Equal(gets, TopTabStrip.RefocusState(had, userActedAt));

	[Theory]
	[InlineData(FocusState.Programmatic, 0L, true)]   // the window's first focus, or a page change's, nothing pressed yet: no box
	[InlineData(FocusState.Programmatic, 1234L, false)] // after a key or click: as WinUI decides
	[InlineData(FocusState.Keyboard, 0L, true)]        // the window's first focus is keyboard focus: no box before any key
	[InlineData(FocusState.Keyboard, 1234L, false)]    // after a key: the keyboard is in use, the box shows
	[InlineData(FocusState.Pointer, 0L, false)]
	public void TheLaunchFocus_DropsTheBox_OnlyBeforeAnyKeyOrClick(FocusState state, long userActedAt, bool drops) =>
		Assert.Equal(drops, TopTabStrip.DropsLaunchBox(state, userActedAt));

	// the coordinator (from the updater stream's runs): Home kept its box after a page change. A focus that lands on a tab
	// within PageChangeFocusMs of a page arriving, with no key or click since, is the page change's
	[Theory]
	[InlineData(FocusState.Programmatic, 5_000L, 10_000L, 10_400L, true)]   // a key earlier (an Esc), then a page change: dropped
	[InlineData(FocusState.Keyboard, 5_000L, 10_000L, 10_400L, true)]
	[InlineData(FocusState.Programmatic, 10_200L, 10_000L, 10_400L, false)] // a key after the page came: the user's
	[InlineData(FocusState.Programmatic, 5_000L, 10_000L, 12_000L, false)]  // long after the page came: as WinUI decides
	[InlineData(FocusState.Pointer, 5_000L, 10_000L, 10_400L, false)]
	// the review of 1.5 (m5): the key that changed the page (Backspace, Esc, Alt+Left) is the user's: a keyboard Back that
	// lands on a tab keeps its box
	[InlineData(FocusState.Programmatic, 9_900L, 10_000L, 10_200L, false)]  // Backspace 100 ms before the page came
	[InlineData(FocusState.Keyboard, 8_600L, 10_000L, 10_200L, false)]      // a slow page: 1.4 s
	[InlineData(FocusState.Programmatic, 8_400L, 10_000L, 10_200L, true)]   // 1.6 s before: not what changed the page
	public void AFocusAPageChangePutsOnATab_DropsTheBox(FocusState state, long userActedAt, long arrivedAt, long now, bool drops) =>
		Assert.Equal(drops, TopTabStrip.DropsLaunchBox(state, userActedAt, arrivedAt, now));

	// the GotFocus hook decides with ArrivalFocus's own clocks, and refocuses only while the decision still holds
	[Fact]
	public void TheGotFocusHook_DecidesWithTheArrivalClocks()
	{
		var code = File.ReadAllText(FindUp(Path.Combine("ui", "FluentFin", "Controls", "TopTabStrip.xaml.cs")));
		var hook = code[code.IndexOf("private void OnTabsGotFocus", StringComparison.Ordinal)..];
		hook = hook[..hook.IndexOf("\n\t}", StringComparison.Ordinal)];

		Assert.Contains("DropsLaunchBox(tab.FocusState, Services.ArrivalFocus.UserActedAt, Services.ArrivalFocus.ArrivedAt, Environment.TickCount64)", hook);
		Assert.Contains("tab.Focus(FocusState.Pointer)", hook);
		Assert.Equal(2, hook.Split("DropsBoxNow(tab)").Length - 1); // decided as it gets the focus and again before it refocuses
		Assert.Contains("TabsPanel.GotFocus += OnTabsGotFocus;", code);
	}

	[Fact]
	public void TheRebuild_UsesIt()
	{
		var code = File.ReadAllText(FindUp(Path.Combine("ui", "FluentFin", "Controls", "TopTabStrip.xaml.cs")));

		Assert.Contains("?.Focus(RefocusState(state, Services.ArrivalFocus.UserActedAt));", code);
		Assert.DoesNotContain("?.Focus(FocusState.Keyboard);", code);
	}

	private static string FindUp(string relative)
	{
		for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
		{
			if (File.Exists(Path.Combine(dir.FullName, relative)))
			{
				return Path.Combine(dir.FullName, relative);
			}
		}

		throw new FileNotFoundException(relative);
	}
}
