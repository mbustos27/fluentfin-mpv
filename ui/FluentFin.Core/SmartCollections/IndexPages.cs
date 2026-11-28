using System.Diagnostics;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.SmartCollections;

/// <summary>
/// Nixie 1.7.1 (the index on a big server): one pass of the library index (its titles, or their people) over every library,
/// two requests at a time. 1.6 read one page after another, sized to take 2 s each, so a server that answers slowly whatever
/// the size was asked a few hundred titles at a time: on the owner's (34,001 titles in 23 libraries) the whole index took
/// 1,198 s, and it was read again at every start. Here each library's first page comes first (small, with the library's
/// count, so the first rows and "12,000 of 25,736 films" come early), then every other page at its offset, at most
/// <see cref="AtOnce"/> at once, <see cref="PageSize"/> titles each. Only the first page asks the server to count.
/// <para>
/// A page that fails or takes longer than <see cref="PageTimeout"/> is asked again after a pause, as two halves, at most
/// <see cref="Tries"/> times in all; then the pass stops and says so (null). Titles added while the pass runs are read on
/// past the count; a title the server's order moved across two pages meanwhile comes once (by its id).
/// </para>
/// <para>
/// Review M3: a page that ran out of time halves the size of every page after it in the pass (<see cref="PageSizeNow"/>), and
/// the next read starts at that size. 1,000 people a page on the owner's server (about 26 ms a person, the two in flight served
/// one after the other) was ~52 s a page: every page ran out, was asked again as halves, and the abandoned one ran on.
/// Re-review (Major A): never below <see cref="MinCarriedSize"/>; doubled again after <see cref="FastPagesToGrow"/> pages in a
/// row well inside the time; a library's first page (the one that counts) never changes it.
/// </para>
/// </summary>
internal sealed class IndexPages
{
	/// <summary>A library's first page: its first rows, and its count.</summary>
	public const int FirstPageSize = 200;

	/// <summary>Every other page. On the throwaway 25,736-film server a request's cost is mostly the request: 500 titles came in 150 ms, 1,000 in 160 ms, 2,000 in 200 ms.</summary>
	public const int PageSize = 1000;

	/// <summary>The most requests at once: gentle on a shared server, and the whole pass in half the time.</summary>
	public const int AtOnce = 2;

	public const int MinPageSize = 50;

	/// <summary>Re-review (Major A): the smallest size a timeout leaves for the pages after it (a page's own halves go lower).</summary>
	public const int MinCarriedSize = 250;

	/// <summary>Re-review (Major A): this many pages in a row at the size now, each within a quarter of the time, double it (to <see cref="PageSize"/>).</summary>
	public const int FastPagesToGrow = 3;

	public const int Tries = 3;

	public static readonly TimeSpan DefaultPageTimeout = TimeSpan.FromSeconds(45);

	public delegate Task<BaseItemDtoQueryResult?> Ask(BaseItemDto library, int start, int limit, bool withTotal, CancellationToken cancellationToken);

	private readonly IReadOnlyList<BaseItemDto> _libraries;
	private readonly Ask _ask;
	private readonly string _pass;
	private readonly ILogger? _log;
	private readonly Lock _lock = new();
	private readonly SortedDictionary<(int Library, int Start), Stored> _pages = [];

	// 1.8 (BH-180-m2): a page as kept: its titles' ids always (the tail's checks), its items only when they are kept
	private sealed record Stored(IReadOnlyList<BaseItemDto>? Items, Guid?[] Ids);
	private readonly int?[] _totals;
	private readonly int[] _read;
	private readonly List<TimeSpan> _took = [];
	private int _requests;

	/// <param name="pageSize">Review M3: the size the pages after each library's first start at (a read before ran out of time at a bigger one).</param>
	public IndexPages(IReadOnlyList<BaseItemDto> libraries, Ask ask, string pass, ILogger? log = null, int pageSize = PageSize)
	{
		_libraries = libraries;
		_ask = ask;
		_pass = pass;
		_log = log;
		_totals = new int?[libraries.Count];
		_read = new int[libraries.Count];
		_next = new int[libraries.Count];
		_end = new int[libraries.Count];
		_size = Math.Clamp(pageSize, MinCarriedSize, PageSize);
	}

	// review M3: the size of the pages still to ask, and where each library's next page starts and its pages end
	private int _size;
	private int _fastPages; // re-review (Major A): full pages in a row well inside the time
	private readonly int[] _next;
	private readonly int[] _end;

	/// <summary>Review M3: the page size now: smaller than it began after a page ran out of time (the next read starts here).</summary>
	public int PageSizeNow
	{
		get
		{
			lock (_lock)
			{
				return _size;
			}
		}
	}

	/// <summary>How long one page may take (tests shorten it).</summary>
	internal TimeSpan PageTimeout { get; init; } = DefaultPageTimeout;

	/// <summary>The pause before try <c>n + 1</c> (tests shorten it).</summary>
	internal Func<int, CancellationToken, Task> Pause { get; init; } = (tried, cancellationToken) => Task.Delay(TimeSpan.FromSeconds(tried), cancellationToken);

	// 1.9 (stream text)
	/// <summary>
	/// Nixie 1.9: awaited before each request, outside its <see cref="PageTimeout"/> (the summaries' pass waits here while the PC
	/// is on standby, and stops here when its switch is off): a wait is never a page that ran out of time.
	/// </summary>
	internal Func<CancellationToken, Task>? BeforeAsk { get; init; }

	/// <summary>Nixie 1.9: the requests asked so far (every try).</summary>
	internal int Requests => Volatile.Read(ref _requests);
	// end 1.9 (stream text)

	/// <summary>A page came in (any order, off the UI thread): what is read so far, of how many (null until every count is in).</summary>
	public event Action<int, int?>? PageIn;

	/// <summary>
	/// 1.8 (BH-180-m2): a page's items, as they came (any order, off the UI thread), before <see cref="PageIn"/>: what a pass
	/// that doesn't keep them (<see cref="KeepItems"/>) takes from them.
	/// </summary>
	public event Action<int, int, IReadOnlyList<BaseItemDto>>? PageCame;

	/// <summary>
	/// 1.8 (BH-180-m2): whether the pages' items are kept until the pass ends (<see cref="Items"/>: the titles). The people pass
	/// keeps none: each page's whole cast and crew, image tags and blurhashes, was held until the end (891 MB above the titles
	/// at 25,000 titles with real casts); it takes the names it needs at <see cref="PageCame"/> and lets the page go.
	/// </summary>
	public bool KeepItems { get; init; } = true;

	/// <summary>
	/// Every title read so far, each library's in the server's order, each title once, where it came first. 1.7.3 (P0-2):
	/// with every library it came in (<c>Libraries[0]</c> the first): a title the server lists in two libraries (3,167 of
	/// 34,006 on the server the owner uses) counted under the first one only, and the other library's presets missed it.
	/// </summary>
	public IReadOnlyList<(IReadOnlyList<int> Libraries, BaseItemDto Item)> Items()
	{
		lock (_lock)
		{
			var at = new Dictionary<Guid, int>();
			var all = new List<(List<int> Libraries, BaseItemDto Item)>();
			foreach (var ((library, _), page) in _pages)
			{
				foreach (var item in page.Items ?? throw new InvalidOperationException("the pages' items weren't kept (KeepItems)"))
				{
					if (item.Id is not { } id || !at.TryGetValue(id, out var first))
					{
						if (item.Id is { } key)
						{
							at[key] = all.Count;
						}

						all.Add(([library], item));
					}
					else if (!all[first].Libraries.Contains(library))
					{
						all[first].Libraries.Add(library); // 1.7.3: merged, not dropped (one the order moved across two pages is the same library)
					}
				}
			}

			return [.. all.Select(x => ((IReadOnlyList<int>)x.Libraries, x.Item))];
		}
	}

	/// <summary>Every page of every library; false when a page failed every try (what came stays in <see cref="Items"/>).</summary>
	public async Task<bool> ReadAsync(CancellationToken cancellationToken)
	{
		var watch = Stopwatch.StartNew();
		using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		var failed = false;
		var options = new ParallelOptions { MaxDegreeOfParallelism = AtOnce, CancellationToken = stop.Token };

		async ValueTask Run(Func<Task<bool>> page)
		{
			if (!await page().ConfigureAwait(false))
			{
				failed = true;
				await stop.CancelAsync().ConfigureAwait(false);
			}
		}

		try
		{
			// the first page of each library, with its count
			await Parallel.ForEachAsync(Enumerable.Range(0, _libraries.Count), options,
				(library, token) => Run(() => Page(library, 0, FirstPageSize, withTotal: true, tried: 0, token))).ConfigureAwait(false);
			// then the rest, by offset, of every library at once (review M3: each page as big as the size is when it is asked)
			for (var library = 0; library < _libraries.Count; library++)
			{
				var first = FirstCount(library);
				lock (_lock)
				{
					(_next[library], _end[library]) = first >= FirstPageSize && _totals[library] is { } total ? (first, total) : (0, 0);
				}
			}

			async Task Worker()
			{
				try
				{
					while (NextPage() is { } page)
					{
						await Run(() => Page(page.Library, page.Start, page.Limit, withTotal: false, tried: 0, stop.Token)).ConfigureAwait(false);
						if (failed)
						{
							return;
						}
					}
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					await stop.CancelAsync().ConfigureAwait(false); // as Parallel.ForEachAsync: the others stop too
					throw;
				}
			}

			await Task.WhenAll(Enumerable.Range(0, AtOnce).Select(_ => Task.Run(Worker, CancellationToken.None))).ConfigureAwait(false);
			// titles added meanwhile (or a server that gave no count): on from the end, a page at a time, until a short one
			for (var library = 0; library < _libraries.Count && !failed; library++)
			{
				while (Tail(library) is { } next)
				{
					if (!await Page(library, next.Start, next.Limit, withTotal: false, tried: 0, stop.Token).ConfigureAwait(false))
					{
						failed = true;
						break;
					}
				}
			}
		}
		catch (OperationCanceledException) when (failed && !cancellationToken.IsCancellationRequested)
		{
			// a page failed every try: the others stopped
		}

		Log(watch.Elapsed, failed);
		return !failed;
	}

	// review M3: the next page to ask: the next library's next offset, at the size now; null when every page was asked
	private (int Library, int Start, int Limit)? NextPage()
	{
		lock (_lock)
		{
			for (var library = 0; library < _libraries.Count; library++)
			{
				if (_next[library] < _end[library])
				{
					// a whole page even at the end: one shorter than it asked says the library is all in (no page after it)
					var start = _next[library];
					_next[library] += _size;
					return (library, start, _size);
				}
			}

			return null;
		}
	}

	private int FirstCount(int library)
	{
		lock (_lock)
		{
			return _pages.TryGetValue((library, 0), out var first) ? first.Ids.Length : 0;
		}
	}

	// the page after a library's last one, when that one was full (or the server gave no count); null when it is all in
	private (int Start, int Limit)? Tail(int library)
	{
		lock (_lock)
		{
			var mine = _pages.Where(p => p.Key.Library == library).ToList();
			if (mine.Count == 0)
			{
				return null;
			}

			var (key, page) = mine[^1];
			var ids = page.Ids;
			var asked = key.Start == 0 ? FirstPageSize : _asked.GetValueOrDefault(key, _size);
			var end = key.Start + ids.Length;
			var expected = _totals[library];
			if (ids.Length == 0 || ids.Length < asked || (expected is { } count && end < count))
			{
				return null;
			}

			// a server that leaves StartIndex out answers the first titles again: stop rather than read them for ever
			var first = ids[0];
			return mine.Take(mine.Count - 1).Any(p => p.Value.Ids.Contains(first)) ? null : (end, _size);
		}
	}

	private readonly Dictionary<(int, int), int> _asked = [];

	private async Task<bool> Page(int library, int start, int limit, bool withTotal, int tried, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var (answer, took, ranOut) = await AskTimed(library, start, limit, withTotal, cancellationToken).ConfigureAwait(false);
		cancellationToken.ThrowIfCancellationRequested();
		if (answer?.Items is { } items)
		{
			int read;
			int? total;
			int? grew = null;
			lock (_lock)
			{
				_pages[(library, start)] = new Stored(KeepItems ? items : null, [.. items.Select(x => x.Id)]); // 1.8 (BH-180-m2)
				_asked[(library, start)] = limit;
				if (withTotal)
				{
					_totals[library] = answer.TotalRecordCount is >= 0 and var count && (count > 0 || items.Count == 0) ? count : null;
				}

				_read[library] += items.Count;
				_took.Add(took);
				if (!withTotal)
				{
					// re-review (Major A): a server that is quick again gets its bigger pages back. Check of 69002e14: only a full page
					// says so (a small library's only page, a big one's last, is quick whatever the server)
					_fastPages = limit >= _size && items.Count >= limit && took < PageTimeout / 4 ? _fastPages + 1 : 0;
					if (_fastPages >= FastPagesToGrow && _size < PageSize)
					{
						(_size, _fastPages) = (Math.Min(PageSize, _size * 2), 0);
						grew = _size;
					}
				}
				read = _read.Sum();
				total = _totals.All(t => t is not null) ? Math.Max(read, _totals.Sum(t => t!.Value)) : null;
			}

			_log?.LogInformation("Library index: {Pass} page of library {Library} from {Start}: {Count} titles in {Ms} ms", _pass, library + 1, start, items.Count, (int)took.TotalMilliseconds);
			if (grew is { } bigger)
			{
				_log?.LogInformation("Library index: {Pass} pages of {Size} titles from now on (the server is quick again)", _pass, bigger);
			}

			PageCame?.Invoke(library, start, items); // 1.8 (BH-180-m2): before PageIn, so what PageIn keeps has it
			PageIn?.Invoke(read, total);
			return true;
		}

		tried++;
		_log?.LogWarning("Library index: {Pass} page of library {Library} from {Start} ({Limit} titles) failed or took over {Seconds} s (try {Try} of {Tries})", _pass, library + 1, start, limit, PageTimeout.TotalSeconds, tried, Tries);
		if (ranOut && !withTotal)
		{
			// review M3: every page after it is smaller too (not each asked at the size that ran out, then again as halves).
			// Re-review (Major A): not below MinCarriedSize, and not for a first page, which alone asks the server to count
			int now;
			lock (_lock)
			{
				now = _size = Math.Max(MinCarriedSize, Math.Min(_size, limit / 2));
				_fastPages = 0;
			}

			_log?.LogInformation("Library index: {Pass} pages of {Size} titles from now on", _pass, now);
		}
		if (tried >= Tries)
		{
			return false;
		}

		await Pause(tried, cancellationToken).ConfigureAwait(false);
		if (!withTotal && limit >= 2 * MinPageSize)
		{
			// smaller: the two halves
			var half = limit / 2;
			return await Page(library, start, half, false, tried, cancellationToken).ConfigureAwait(false)
				&& await Page(library, start + half, limit - half, false, tried, cancellationToken).ConfigureAwait(false);
		}

		return await Page(library, start, limit, withTotal, tried, cancellationToken).ConfigureAwait(false);
	}

	private async Task<(BaseItemDtoQueryResult? Answer, TimeSpan Took, bool RanOut)> AskTimed(int library, int start, int limit, bool withTotal, CancellationToken cancellationToken)
	{
		if (BeforeAsk is { } before)
		{
			await before(cancellationToken).ConfigureAwait(false); // 1.9 (stream text): before the page's time starts
		}

		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(PageTimeout);
		Interlocked.Increment(ref _requests);
		var started = Stopwatch.GetTimestamp();
		try
		{
			var answer = await _ask(_libraries[library], start, limit, withTotal, timeout.Token).ConfigureAwait(false);
			// re-review (Major A): the app's client answers null when its time runs out (it never throws): ran out all the same
			return (answer, Stopwatch.GetElapsedTime(started), answer is null && timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return (null, Stopwatch.GetElapsedTime(started), true); // the page's own time ran out
		}
	}

	private void Log(TimeSpan elapsed, bool failed)
	{
		if (_log is null)
		{
			return;
		}

		lock (_lock)
		{
			var took = _took.Order().ToList();
			var median = took.Count > 0 ? took[took.Count / 2] : TimeSpan.Zero;
			var slowest = took.Count > 0 ? took[^1] : TimeSpan.Zero;
			_log.LogInformation("Library index: {Pass} pass {Outcome}: {Titles} titles in {Requests} requests ({AtOnce} at a time), {Seconds:0.0} s; a page took {Median} ms (median), {Slowest} ms at most",
				_pass, failed ? "stopped" : "done", _read.Sum(), _requests, AtOnce, elapsed.TotalSeconds, (int)median.TotalMilliseconds, (int)slowest.TotalMilliseconds);
		}
	}
}
