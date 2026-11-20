using System.Diagnostics;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.Services;

/// <summary>
/// Nixie 1.4.1 (big libraries): a library's titles read a page at a time, by sort name, one request after another. A whole
/// library in one request took the server over 100 s on 25,842 movies (the client gave up at 100 s, and the library page
/// stayed empty); a page of a hundred comes back at once, so the first cards show, and the rest follows page by page.
/// <para>
/// The first page is <see cref="FirstPageSize"/> titles, with the library's size. Each page after it is sized from how
/// long the one before took: a page faster than <see cref="PageTarget"/> makes the next one larger (up to twice), one
/// slower than <see cref="SlowPage"/> halves it, and between them a larger one is tried, kept growing only while the time
/// does not grow with it (<see cref="Grow"/>); between <see cref="MinPageSize"/> and <see cref="MaxPageSize"/>, so no
/// request asks for more than <see cref="MaxPageSize"/>. The end is an empty page, or a short one once the first page's
/// count is reached (titles added meanwhile are read too). A page that fails or takes longer than
/// <see cref="PageTimeout"/> is asked again, half as large, after a pause, <see cref="Tries"/> times in all; then the
/// reader stops (<see cref="HasFailed"/>) and what came stays.
/// </para>
/// </summary>
public sealed class LibraryPageReader(IJellyfinClient client, BaseItemDto library)
{
	/// <summary>The first page: a screen of cards at the smallest size, at once.</summary>
	public const int FirstPageSize = 100;

	public const int MinPageSize = 50;

	/// <summary>The most titles any one request asks for.</summary>
	public const int MaxPageSize = 1000;

	/// <summary>How long a page should take: pages grow or shrink toward it.</summary>
	public static readonly TimeSpan PageTarget = TimeSpan.FromSeconds(2);

	/// <summary>A page slower than this halves the next one.</summary>
	public static readonly TimeSpan SlowPage = TimeSpan.FromSeconds(6);

	/// <summary>How many times a page is asked before the reader stops.</summary>
	public const int Tries = 3;

	/// <summary>How long one page may take before it is asked again (the client's own limit is 100 s).</summary>
	public static readonly TimeSpan DefaultPageTimeout = TimeSpan.FromSeconds(45);

	/// <summary>How long one page may take here (<see cref="DefaultPageTimeout"/>; tests shorten it).</summary>
	internal TimeSpan PageTimeout { get; init; } = DefaultPageTimeout;

	/// <summary>The pause before try <c>n + 1</c>: 1 s, then 2 s (tests shorten it).</summary>
	internal Func<int, CancellationToken, Task> Pause { get; init; } = (tried, cancellationToken) => Task.Delay(TimeSpan.FromSeconds(tried), cancellationToken);

	/// <summary>Where each page, each try again and the end go (the app's log: a page's time is what a big server costs).</summary>
	public ILogger? Log { get; init; }

	private readonly Stopwatch _reading = new();
	private Guid? _lastFirst;

	/// <summary>Where the next page starts (the titles read so far, as the server counts them).</summary>
	public int Next { get; private set; }

	/// <summary>The library's size as the server counted it with the first page; null before it, or when it did not say.</summary>
	public int? Total { get; private set; }

	/// <summary>The next page's size.</summary>
	public int Size { get; private set; } = FirstPageSize;

	/// <summary>Every page is in.</summary>
	public bool IsDone { get; private set; }

	/// <summary>A page failed every try: the reader stopped with the library part read.</summary>
	public bool HasFailed { get; private set; }

	/// <summary>The requests made, tried again ones too.</summary>
	public int Requests { get; private set; }

	/// <summary>
	/// The first page and the library's size; null when it failed (asked once: the page offers Retry). Throws
	/// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
	/// </summary>
	public async Task<IReadOnlyList<BaseItemDto>?> ReadFirstAsync(CancellationToken cancellationToken = default)
	{
		_reading.Restart();
		var (answer, took) = await AskAsync(0, FirstPageSize, withTotal: true, cancellationToken).ConfigureAwait(false);
		cancellationToken.ThrowIfCancellationRequested();
		if (answer?.Items is not { } items)
		{
			HasFailed = true;
			Log?.LogWarning("Library {Library}: the first page failed or took over {Seconds} s", library.Name, PageTimeout.TotalSeconds);
			return null;
		}

		Total = answer.TotalRecordCount is >= 0 and var total && (total > 0 || items.Count == 0) ? total : null;
		Took(items.Count, FirstPageSize, took, items);
		return items;
	}

	/// <summary>
	/// The next page; null when every page is in (<see cref="IsDone"/>) or a page failed every try (<see cref="HasFailed"/>).
	/// Throws <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
	/// </summary>
	public async Task<IReadOnlyList<BaseItemDto>?> ReadNextAsync(CancellationToken cancellationToken = default)
	{
		if (IsDone || HasFailed)
		{
			return null;
		}

		for (var tried = 1; ; tried++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var size = Size;
			var (answer, took) = await AskAsync(Next, size, withTotal: false, cancellationToken).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			if (answer?.Items is { } items)
			{
				Took(items.Count, size, took, items);
				return items;
			}

			// failed or too slow: smaller, after a pause
			Size = Math.Max(MinPageSize, size / 2);
			Log?.LogWarning("Library {Library}: the page from {Start} ({Size} titles) failed or took over {Seconds} s (try {Try} of {Tries})", library.Name, Next, size, PageTimeout.TotalSeconds, tried, Tries);
			if (tried >= Tries)
			{
				HasFailed = true;
				Log?.LogWarning("Library {Library}: stopped at {Read} of {Total} titles", library.Name, Next, Total?.ToString() ?? "?");
				return null;
			}

			await Pause(tried, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>Every page, one after another; null when one failed every try.</summary>
	public async Task<IReadOnlyList<BaseItemDto>?> ReadAllAsync(CancellationToken cancellationToken = default)
	{
		if (await ReadFirstAsync(cancellationToken).ConfigureAwait(false) is not { } first)
		{
			return null;
		}

		var all = new List<BaseItemDto>(first);
		while (await ReadNextAsync(cancellationToken).ConfigureAwait(false) is { } page)
		{
			all.AddRange(page);
		}

		return HasFailed ? null : all;
	}

	/// <summary>
	/// The next page's size from the last one's: <paramref name="size"/> titles took <paramref name="took"/>. Faster than
	/// <see cref="PageTarget"/>: larger, toward it, at most twice; slower than <see cref="SlowPage"/>: half; else the same.
	/// Between <see cref="MinPageSize"/> and <see cref="MaxPageSize"/>.
	/// </summary>
	public static int NextSize(int size, TimeSpan took)
	{
		int next;
		if (took < PageTarget)
		{
			next = (int)Math.Round(Math.Min(size * 2.0, size * PageTarget.TotalSeconds / Math.Max(took.TotalSeconds, 0.001)));
		}
		else if (took > SlowPage)
		{
			next = size / 2;
		}
		else
		{
			next = size; // a server whose requests cost the same whatever their size is not asked ever smaller pages
		}

		return Math.Clamp(next, MinPageSize, MaxPageSize);
	}

	/// <summary>
	/// The next size after a page: <see cref="NextSize"/>, but between the target and a slow page (where it would hold) a
	/// larger page is tried once, and pages go on growing while twice the titles do not take half again as long: a server
	/// whose requests cost the same whatever their size is read in large pages, not a hundred at a time.
	/// </summary>
	internal static int Grow(int size, TimeSpan took, int lastSize, TimeSpan lastTook, ref bool triedLarger)
	{
		var next = NextSize(size, took);
		if (next != size || took < PageTarget || took > SlowPage)
		{
			return next;
		}

		if (lastSize > 0 && size > lastSize && took.TotalSeconds < lastTook.TotalSeconds * 1.5)
		{
			return Math.Min(MaxPageSize, size * 2); // twice the titles, about the same time: the cost is the request's
		}

		if (!triedLarger)
		{
			triedLarger = true;
			return Math.Min(MaxPageSize, size * 2);
		}

		return size;
	}

	private int _lastSize;
	private TimeSpan _lastTook;
	private bool _triedLarger;

	private void Took(int count, int asked, TimeSpan took, IReadOnlyList<BaseItemDto> items)
	{
		var start = Next;
		Next += count;
		Size = Grow(asked, took, _lastSize, _lastTook, ref _triedLarger);
		(_lastSize, _lastTook) = (asked, took);
		// done: an empty page, or a short one with the server's count reached (or none said). The count only says how many
		// to expect: titles added since are read too (a full page at the count asks once more), and a short page under the
		// count (a title removed, a proxy's short answer) asks once more from where it ended
		IsDone = count == 0 || (count < asked && (Total is not { } expected || Next >= expected));
		// a server that leaves StartIndex out answers the first titles again: stop rather than read them for ever
		var first = items.Count > 0 ? items[0].Id : null;
		if (!IsDone && start > 0 && first is not null && first == _lastFirst)
		{
			IsDone = true;
			Log?.LogWarning("Library {Library}: the page from {Start} began with the same title as the one before; stopped", library.Name, start);
		}

		_lastFirst = first ?? _lastFirst;
		Log?.LogDebug("Library {Library}: titles {From} to {To} in {Ms} ms (next page {Size})", library.Name, start, Next, (int)took.TotalMilliseconds, Size);
		if (IsDone)
		{
			Log?.LogInformation("Library {Library}: {Read} titles in {Requests} requests, {Seconds:0.0} s", library.Name, Next, Requests, _reading.Elapsed.TotalSeconds);
		}
	}

	private async Task<(BaseItemDtoQueryResult? Answer, TimeSpan Took)> AskAsync(int start, int limit, bool withTotal, CancellationToken cancellationToken)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(PageTimeout);
		Requests++;
		var started = Stopwatch.GetTimestamp();
		var answer = await client.GetLibraryItems(library, start, limit, withTotal, timeout.Token).ConfigureAwait(false);
		return (answer, Stopwatch.GetElapsedTime(started));
	}
}
