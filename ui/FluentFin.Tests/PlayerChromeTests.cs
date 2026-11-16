using FluentFin.Services;

namespace FluentFin.Tests;

/// <summary>
/// fluentfin-mpv: the navigation pane and the search hide while a player page is open, windowed too
/// (docs/research/ux-review.md V9), and come back whichever way the page goes.
/// </summary>
public sealed class PlayerChromeTests
{
	[Fact]
	public void APlayerPage_HidesTheChrome_UntilItIsLeft()
	{
		var chrome = new PlayerChrome();
		var page = new PlayerChromeHold(chrome);
		Assert.True(chrome.IsNavigationVisible);

		page.NavigatedTo();
		page.Loaded();
		Assert.True(chrome.IsPlayerOpen);
		Assert.False(chrome.IsNavigationVisible);

		page.NavigatedFrom();
		page.Unloaded();
		Assert.False(chrome.IsPlayerOpen);
		Assert.True(chrome.IsNavigationVisible);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void APageReplacingAnother_NeverShowsThePaneInBetween(bool oldPageLeftFirst)
	{
		// a remote Play while playing: the frame tells the old page it was left, then the new one it arrived (or the other
		// way round); the pane's return waits for the UI thread's next turn, by which time the new page holds the chrome
		var later = new Queue<Action>();
		var chrome = new PlayerChrome(later.Enqueue);
		var changes = new List<bool>();
		chrome.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(PlayerChrome.IsNavigationVisible))
			{
				changes.Add(chrome.IsNavigationVisible);
			}
		};
		var oldPage = new PlayerChromeHold(chrome);
		oldPage.NavigatedTo();
		oldPage.Loaded();
		var newPage = new PlayerChromeHold(chrome);

		if (oldPageLeftFirst)
		{
			oldPage.NavigatedFrom();
			newPage.NavigatedTo();
		}
		else
		{
			newPage.NavigatedTo();
			oldPage.NavigatedFrom();
		}

		newPage.Loaded();
		oldPage.Unloaded();
		while (later.TryDequeue(out var action))
		{
			action();
		}

		Assert.False(chrome.IsNavigationVisible);
		Assert.Equal([false], changes);
	}

	[Fact]
	public void TheLastPageLeft_ShowsTheChromeOnTheNextTurn()
	{
		var later = new Queue<Action>();
		var chrome = new PlayerChrome(later.Enqueue);
		var page = new PlayerChromeHold(chrome);
		page.NavigatedTo();

		page.NavigatedFrom();
		Assert.False(chrome.IsNavigationVisible);

		later.Dequeue()();
		Assert.True(chrome.IsNavigationVisible);
	}

	[Fact]
	public void APageRemovedWithoutANavigation_GivesTheChromeBack()
	{
		// Log out / Switch user navigate the window's root frame: the page is only unloaded
		var chrome = new PlayerChrome();
		var page = new PlayerChromeHold(chrome);
		page.NavigatedTo();
		page.Loaded();

		page.Unloaded();

		Assert.True(chrome.IsNavigationVisible);
		Assert.False(page.IsHeld);
	}

	[Fact]
	public void APageMovedWithinTheWindow_TakesTheChromeAgain_ButNotOnceLeft()
	{
		var chrome = new PlayerChrome();
		var page = new PlayerChromeHold(chrome);
		page.NavigatedTo();
		page.Loaded();

		page.Unloaded();
		page.Loaded();
		Assert.False(chrome.IsNavigationVisible);

		page.NavigatedFrom();
		page.Unloaded();
		page.Loaded();
		Assert.True(chrome.IsNavigationVisible);
	}

	[Fact]
	public void ALease_CountsOnce_HoweverOftenItIsDisposed()
	{
		var chrome = new PlayerChrome();
		var first = chrome.Enter();
		var second = chrome.Enter();

		first.Dispose();
		first.Dispose();
		Assert.True(chrome.IsPlayerOpen);

		second.Dispose();
		Assert.False(chrome.IsPlayerOpen);

		// a hold taken twice is one lease
		var page = new PlayerChromeHold(chrome);
		page.NavigatedTo();
		page.Loaded();
		page.NavigatedFrom();
		Assert.False(chrome.IsPlayerOpen);
	}
}
