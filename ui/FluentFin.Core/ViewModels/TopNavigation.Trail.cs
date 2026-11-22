using CommunityToolkit.Mvvm.ComponentModel;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.ViewModels;

// 1.7 upnav
/// <summary>
/// Nixie 1.7: the item page's way up, beside the eyebrow (<see cref="Eyebrow"/>, the library crumb): its series and season
/// crumbs (<see cref="ItemTrail"/>), shown by lc:TrailEyebrow in every theme's item header.
/// </summary>
public sealed partial class TopNavigation
{
	/// <summary>
	/// The shown item page's series and season crumbs (<see cref="ItemTrail"/>); <see cref="ItemTrail.None"/> on any other page.
	/// Set as the page is shown from the item it was opened with; one that does not carry its ids gets them from the server
	/// once (<see cref="ItemOf"/>, <see cref="SeasonsOf"/>), kept for this sign-in.
	/// </summary>
	[ObservableProperty]
	public partial ItemTrail Trail { get; private set; } = ItemTrail.None;

	/// <summary>
	/// An item by its id (the shell sets it: the one the shown page's view model fetches, TrailItems.FromShownPage); null when
	/// it cannot be read. The token is cancelled when the page is left.
	/// </summary>
	public Func<Guid, CancellationToken, Task<BaseItemDto?>>? ItemOf { get; set; }

	/// <summary>A series' seasons asked of the server by the series' id (the shell sets it); null when they cannot be read.</summary>
	public Func<Guid, CancellationToken, Task<IReadOnlyList<BaseItemDto>?>>? SeasonsOf { get; set; }

	// review item 3: the shown page's lookup, cancelled when another page is shown or the user signs out
	private CancellationTokenSource? _trailCancel;

	// the trails that took a lookup, by item: Back to the page, or the page opened again, asks nothing (forgotten by Reset)
	private readonly Dictionary<Guid, ItemTrail> _trails = [];

	private int _trailFor;

	// review item 4: the trail a crumb was opened from, until the next page is shown (OpenCrumb)
	private ItemTrail? _openedFrom;

	// review item 2: the sign-in the kept trails belong to (Reset starts a new one): a lookup that ends after it keeps nothing
	private int _trailSession;

	/// <summary>The trail's last lookup (tests wait on it).</summary>
	internal Task TrailLookup { get; private set; } = Task.CompletedTask;

	private void ShowTrail(string pageKey, object? parameter)
	{
		var version = ++_trailFor;
		TrailLookup = Task.CompletedTask;
		CancelTrailLookup();
		_openedFrom = null;
		if (!ItemPages.Contains(pageKey) || parameter is not BaseItemDto item)
		{
			Trail = ItemTrail.None;
			return;
		}

		if (!ItemTrail.NeedsLookup(item) || item.Id is not { } id)
		{
			SetTrail(pageKey, ItemTrail.From(item));
			return;
		}

		if (_trails.TryGetValue(id, out var known))
		{
			SetTrail(pageKey, known);
			return;
		}

		// what the item carries meanwhile (an episode's series without its season)
		SetTrail(pageKey, ItemTrail.From(item));
		if (ItemOf is not { } itemOf || SeasonsOf is not { } seasonsOf)
		{
			return;
		}

		var session = _trailSession;
		var cancel = (_trailCancel = new CancellationTokenSource()).Token;
		TrailLookup = Lookup();

		async Task Lookup()
		{
			ItemTrail? trail;
			try
			{
				trail = await ItemTrail.ResolveAsync(item, i => itemOf(i, cancel), s => seasonsOf(s, cancel));
			}
			catch (OperationCanceledException)
			{
				return; // the page was left
			}
			catch (Exception ex)
			{
				logger.LogDebug("Top bar: no series or season for the trail: {Error}", ex.Message);
				return;
			}

			// review item 1: no answer (a timeout, an error: the client answers null) is not kept; the page shows what the item
			// carries, and asks again the next time it is shown
			if (trail is null)
			{
				logger.LogInformation("Top bar: no answer for the trail of {Page}: asked again next time", pageKey[(pageKey.LastIndexOf('.') + 1)..]);
				return;
			}

			// signed out (or in as someone else) meanwhile: not this sign-in's to keep, nor to show
			if (session != _trailSession)
			{
				return;
			}

			_trails[id] = trail;
			// a page shown since: its own trail stays
			if (version == _trailFor)
			{
				SetTrail(pageKey, trail);
			}
		}
	}

	// the item page's crumbs, said in the log by kind only, no names (the harness waits on these lines; Information: the log file
	// takes nothing below it)
	private void SetTrail(string pageKey, ItemTrail trail)
	{
		Trail = trail;
		logger.LogInformation("Top bar: the trail of {Page}: {Crumbs}", pageKey[(pageKey.LastIndexOf('.') + 1)..],
			trail.IsEmpty ? "none" : trail.Season is null ? "series" : trail.Series is null ? "season" : "series, season");
	}

	/// <summary>
	/// A crumb of the trail the eyebrow shows (<paramref name="shown"/>) was pressed: its page opens (the series or season page
	/// of the theme shown, GlobalCommands.PageFor). Once only (review item 4): a second press before the page it opened is
	/// shown (a double click, Enter held), or a press on a trail the bar has moved on from, opens nothing.
	/// </summary>
	/// <returns>Whether the page was opened.</returns>
	public bool OpenCrumb(ItemTrail shown, TrailCrumb crumb)
	{
		if (!ReferenceEquals(shown, Trail) || ReferenceEquals(shown, _openedFrom)
			|| !(ReferenceEquals(crumb, shown.Series) || ReferenceEquals(crumb, shown.Season))
			|| GlobalCommands.PageFor(crumb.Target.Type) is not { } page)
		{
			return false;
		}

		// marked before the navigation: the page it opens may be shown (and the mark cleared) before NavigateTo returns
		_openedFrom = shown;
		if (navigation.NavigateTo(page.FullName!, crumb.Target))
		{
			return true;
		}

		_openedFrom = null;
		return false;
	}

	private void CancelTrailLookup()
	{
		_trailCancel?.Cancel();
		_trailCancel = null;
	}

	private void ForgetTrails()
	{
		CancelTrailLookup();
		++_trailSession;
		_trails.Clear();
		++_trailFor;
		TrailLookup = Task.CompletedTask;
		Trail = ItemTrail.None;
	}
}
// end 1.7 upnav
