namespace FluentFin.Behaviors;

/// <summary>
/// Nixie 1.8.1 (1.7.5's Left: Back ignored with the search box's list open, about 1 samesearch cell in 17; the review's race):
/// which of the suggestions pipeline's updates reach the search box. A drop (Enter, a page change, a sign-out) always does: it
/// closes the list. An answer does only when no drop came since its query was asked: each query is stamped with the drop count
/// when it is asked, on the box's thread, so an answer for a query asked before Enter never fills the list after it, whenever
/// it arrives. (1.8.1's first version stamped the answer when it arrived: one arriving between Enter's count and its drop
/// counted as after it.) Every call is on the box's thread.
/// </summary>
internal sealed class SuggestionGate
{
	private readonly Dictionary<string, int> _askedAt = new(StringComparer.Ordinal);
	private int _drops;

	/// <summary>A query is asked (typed, or tried again): stamped with the drops so far.</summary>
	public void Asked(string query) => _askedAt[query] = _drops;

	/// <summary>The search is dropped: every query asked before now is stale.</summary>
	public void Dropped()
	{
		_drops++;
		_askedAt.Clear();
	}

	/// <summary>Whether the update reaches the box: a drop, or an answer to a query asked since the last drop.</summary>
	public bool Shows(bool dropped, string query) => dropped || (_askedAt.TryGetValue(query, out var at) && at == _drops);
}
