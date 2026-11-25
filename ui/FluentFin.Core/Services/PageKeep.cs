namespace FluentFin.Core.Services;

/// <summary>
/// Nixie 1.7.4 (the owner, 2026-11-23: "going back to the collection from a movie in the collection makes u wait for the
/// collection to load again"; rule 1): the view models of the last pages left, so Back and Forward show a page as it was, at
/// once. One mechanism for every page: the navigation service keeps the view model of a page it leaves
/// (<see cref="Contracts.ViewModels.IKeptOnBack"/>), keyed by the page's entry in the frame's history, and hands it to the page
/// Back or Forward makes for that entry, in place of a new view model that would load again.
/// <para>
/// What is kept, and for how long: at most <see cref="Depth"/> pages (Back's reach; the oldest goes first), each only while its
/// entry is still in the history (<see cref="Retain"/>), and only for the server and user that loaded it (<see cref="Take"/>
/// gives nothing to another session). The navigation service lets go of everything on a sign-in or a sign-out (Switch user, a
/// server change), a new shell and a theme switch (<see cref="Clear"/>). A page kept while something may have changed
/// (<see cref="Take"/>'s stale: the change count moved, or it was kept longer than <see cref="FreshFor"/>) is shown as it was and
/// refreshed behind.
/// </para>
/// <para>
/// An entry is matched by identity first (the frame's entries keep their wrapper while it is held here), else by its page and
/// parameter, the newest kept first. Thread-safe: a sign-out may come from any thread.
/// </para>
/// </summary>
/// <typeparam name="TEntry">The frame's history entry (PageStackEntry in the app; a record in the tests).</typeparam>
public sealed class PageKeep<TEntry>(Func<TEntry, Type?> pageOf, Func<TEntry, object?> parameterOf, TimeProvider? time = null)
	where TEntry : class
{
	/// <summary>How many pages are kept: Back's usual reach (a collection, its film, a person, their film).</summary>
	public const int Depth = 4;

	/// <summary>A page kept longer than this is refreshed behind when it comes back, as Home's saved page is (HomeCache).</summary>
	public static readonly TimeSpan FreshFor = HomeCache.FreshFor;

	private sealed record Kept(TEntry Entry, Type? Page, object? Parameter, object ViewModel, string Session, int Changes, long Mark, long At);

	private readonly TimeProvider _time = time ?? TimeProvider.System;
	private readonly Lock _lock = new();
	private readonly List<Kept> _kept = []; // the oldest first

	/// <summary>How many pages are kept now.</summary>
	public int Count
	{
		get
		{
			lock (_lock)
			{
				return _kept.Count;
			}
		}
	}

	/// <summary>The view models kept now, the oldest first (the tests, and what a drop lets go of).</summary>
	public IReadOnlyList<object> ViewModels
	{
		get
		{
			lock (_lock)
			{
				return [.. _kept.Select(x => x.ViewModel)];
			}
		}
	}

	/// <summary>
	/// A page was left with <paramref name="viewModel"/> loaded: kept for <paramref name="entry"/>, its entry in the history now,
	/// with the session it was loaded for, the change count then and <paramref name="mark"/> (the caller's: where its log of
	/// changes was, to give the page what it missed). The oldest goes past <see cref="Depth"/>.
	/// </summary>
	/// <returns>The view models let go to stay within <see cref="Depth"/>.</returns>
	public IReadOnlyList<object> Keep(TEntry entry, object viewModel, string session, int changes, long mark = 0)
	{
		lock (_lock)
		{
			_kept.RemoveAll(x => ReferenceEquals(x.Entry, entry) || ReferenceEquals(x.ViewModel, viewModel));
			_kept.Add(new Kept(entry, pageOf(entry), parameterOf(entry), viewModel, session, changes, mark, _time.GetTimestamp()));
			List<object> dropped = [];
			while (_kept.Count > Depth)
			{
				dropped.Add(_kept[0].ViewModel);
				_kept.RemoveAt(0);
			}

			return dropped;
		}
	}

	/// <summary>
	/// The view model kept for <paramref name="entry"/>, the page Back or Forward is about to show, taken out of the keep (it is
	/// the page's again): with whether it is stale (the change count moved since it was kept, or it was kept longer than
	/// <see cref="FreshFor"/>). Null when none is kept for it, or it was loaded for another session (never another user's or
	/// server's page): the page loads as before. <c>Mark</c> is the one it was kept with.
	/// </summary>
	public (object ViewModel, bool Stale, long Mark)? Take(TEntry entry, string session, int changes)
	{
		lock (_lock)
		{
			var at = Find(entry);
			if (at < 0)
			{
				return null;
			}

			var kept = _kept[at];
			_kept.RemoveAt(at);
			if (kept.Session != session)
			{
				return null;
			}

			return (kept.ViewModel, kept.Changes != changes || _time.GetElapsedTime(kept.At) > FreshFor, kept.Mark);
		}
	}

	/// <summary>Whether a view model is kept for <paramref name="entry"/> (the next Take would find one).</summary>
	public bool Has(TEntry entry)
	{
		lock (_lock)
		{
			return Find(entry) >= 0;
		}
	}

	/// <summary>
	/// Lets go of what is kept for entries no longer in <paramref name="history"/> (Back's and Forward's entries): a page R loaded
	/// again, a season switched in place, a history cleared.
	/// </summary>
	/// <returns>The view models let go.</returns>
	public IReadOnlyList<object> Retain(IEnumerable<TEntry> history)
	{
		var entries = history.ToList();
		lock (_lock)
		{
			List<object> dropped = [];
			for (var i = _kept.Count - 1; i >= 0; i--)
			{
				var kept = _kept[i];
				if (!entries.Any(e => ReferenceEquals(e, kept.Entry) || SamePlace(kept, e)))
				{
					dropped.Add(kept.ViewModel);
					_kept.RemoveAt(i);
				}
			}

			return dropped;
		}
	}

	/// <summary>Lets go of everything kept (a sign-in or sign-out, a new shell, a theme switch).</summary>
	/// <returns>The view models let go.</returns>
	public IReadOnlyList<object> Clear()
	{
		lock (_lock)
		{
			var all = _kept.Select(x => x.ViewModel).ToList();
			_kept.Clear();
			return all;
		}
	}

	// under _lock: the entry itself, else the newest kept for its page and parameter; -1 for none
	private int Find(TEntry entry)
	{
		for (var i = _kept.Count - 1; i >= 0; i--)
		{
			if (ReferenceEquals(_kept[i].Entry, entry))
			{
				return i;
			}
		}

		for (var i = _kept.Count - 1; i >= 0; i--)
		{
			if (SamePlace(_kept[i], entry))
			{
				return i;
			}
		}

		return -1;
	}

	private bool SamePlace(Kept kept, TEntry entry) => kept.Page == pageOf(entry) && Equals(kept.Parameter, parameterOf(entry));
}
