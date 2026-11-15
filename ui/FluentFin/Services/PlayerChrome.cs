using CommunityToolkit.Mvvm.ComponentModel;

namespace FluentFin.Services;

/// <summary>
/// fluentfin-mpv: the app chrome around the player page (docs/research/ux-review.md V9). While a player page is open the
/// navigation pane and the title bar's search are hidden, windowed too, so the video gets the whole window below the title
/// bar (back and caption buttons stay). Each open player page holds a lease: a page replacing another (a remote Play while
/// playing) never shows the pane in between, whatever order the two pages are told in.
/// </summary>
public sealed partial class PlayerChrome(Action<Action>? post = null) : ObservableObject
{
	private int _leases;

	/// <summary>
	/// Runs the chrome's return later (the app: the next turn of the UI thread), so a player page that replaces another is
	/// counted before the pane would show: the frame tells the old page it was left before the new one arrives.
	/// </summary>
	private readonly Action<Action> _post = post ?? (action => action());

	/// <summary>A player page is open.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsNavigationVisible))]
	public partial bool IsPlayerOpen { get; private set; }

	/// <summary>The navigation pane and the search box are shown.</summary>
	public bool IsNavigationVisible => !IsPlayerOpen;

	/// <summary>Hides the chrome until the returned lease is disposed (once; disposing again does nothing).</summary>
	public IDisposable Enter()
	{
		_leases++;
		IsPlayerOpen = true;
		return new Lease(this);
	}

	private void Exit()
	{
		if (--_leases > 0)
		{
			return;
		}

		_leases = 0;
		_post(() =>
		{
			if (_leases == 0)
			{
				IsPlayerOpen = false;
			}
		});
	}

	private sealed class Lease(PlayerChrome chrome) : IDisposable
	{
		private PlayerChrome? _chrome = chrome;

		public void Dispose()
		{
			_chrome?.Exit();
			_chrome = null;
		}
	}
}

/// <summary>
/// fluentfin-mpv: one player page's hold on <see cref="PlayerChrome"/>. Taken when the page is navigated to, given back when
/// it is navigated from, and also when it is taken out of the window without a navigation (Log out, Switch user remove the
/// shell with the page on it): the next shell must not start with its pane hidden. A page that comes back into the window
/// (a move unloads and loads it again) takes it again, unless it was navigated from.
/// </summary>
public sealed class PlayerChromeHold(PlayerChrome chrome)
{
	private IDisposable? _lease;
	private bool _navigatedFrom;

	public bool IsHeld => _lease is not null;

	public void NavigatedTo() => Take();

	public void NavigatedFrom()
	{
		_navigatedFrom = true;
		Release();
	}

	public void Loaded()
	{
		if (!_navigatedFrom)
		{
			Take();
		}
	}

	public void Unloaded() => Release();

	private void Take() => _lease ??= chrome.Enter();

	private void Release()
	{
		_lease?.Dispose();
		_lease = null;
	}
}
