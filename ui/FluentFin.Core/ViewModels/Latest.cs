namespace FluentFin.Core.ViewModels;

/// <summary>
/// 1.2.0 (items review, item 3): of overlapping refreshes of one thing (a page's item, its episode list, its Play label),
/// only the newest one's answer is shown: each takes a ticket, and an answer whose ticket is no longer the latest is dropped.
/// Before, whichever answer arrived last won, so a slow older one could replace a newer one.
/// </summary>
public sealed class Latest
{
	private int _ticket;

	public int Next() => Interlocked.Increment(ref _ticket);

	public bool Is(int ticket) => Volatile.Read(ref _ticket) == ticket;
}
