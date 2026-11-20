using System.Diagnostics;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Services;

/// <summary>
/// Nixie 1.4.1 (big libraries): a library's titles read a page at a time, by sort name, one request after another. A whole
/// library in one request took the server over 100 s on 25,842 movies (the client gave up at 100 s, and the library page
/// stayed empty); a page of a hundred comes back at once, so the first cards show, and the rest follows page by page.
/// <para>
/// The first page is <see cref="FirstPageSize"/> titles, with the library's size. Each page after it is sized from how
/// long the one before took, aiming at <see cref="PageTarget"/> (at most twice or half the last, between
/// <see cref="MinPageSize"/> and <see cref="MaxPageSize"/>): a fast server is read in a few large pages, a slow one in many
/// small ones, and no request asks for more than <see cref="MaxPageSize"/>. A page that fails or takes longer than
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

	/// <summary>How many times a page is asked before the reader stops.</summary>
	public const int Tries = 3;

	/// <summary>How long one page may take before it is asked again (the client's own limit is 100 s).</summary>
	public static readonly TimeSpan DefaultPageTimeout = TimeSpan.FromSeconds(45);

	/// <summary>How long one page may take here (<see cref="DefaultPageTimeout"/>; tests shorten it).</summary>
	internal TimeSpan PageTimeout { get; init; } = DefaultPageTimeout;

	/// <summary>The pause before try <c>n + 1</c>: 1 s, then 2 s (tests shorten it).</summary>
	internal Func<int, CancellationToken, Task> Pause { get; init; } = (tried, cancellationToken) => Task.Delay(TimeSpan.FromSeconds(tried), cancellationToken);

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
		var (answer, took) = await AskAsync(0, FirstPageSize, withTotal: true, cancellationToken).ConfigureAwait(false);
		cancellationToken.ThrowIfCancellationRequested();
		if (answer?.Items is not { } items)
		{
			HasFailed = true;
			return null;
		}

		Total = answer.TotalRecordCount is >= 0 and var total && (total > 0 || items.Count == 0) ? total : null;
		Took(items.Count, FirstPageSize, took);
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
				Took(items.Count, size, took);
				return items;
			}

			// failed or too slow: smaller, after a pause
			Size = Math.Max(MinPageSize, size / 2);
			if (tried >= Tries)
			{
				HasFailed = true;
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
	/// The next page's size from the last one's: <paramref name="size"/> titles took <paramref name="took"/>; toward
	/// <see cref="PageTarget"/>, at most twice or half as many, between <see cref="MinPageSize"/> and <see cref="MaxPageSize"/>.
	/// </summary>
	public static int NextSize(int size, TimeSpan took)
	{
		var seconds = Math.Max(took.TotalSeconds, 0.001);
		var aimed = size * PageTarget.TotalSeconds / seconds;
		var next = (int)Math.Round(Math.Clamp(aimed, size / 2.0, size * 2.0));
		return Math.Clamp(next, MinPageSize, MaxPageSize);
	}

	private void Took(int count, int asked, TimeSpan took)
	{
		Next += count;
		Size = NextSize(asked, took);
		// done: nothing came, or the server's count is reached; without a count, a short page is the last (with one, a short
		// page goes on: the next page from where it ended, until the count or an empty page)
		IsDone = count == 0 || (Total is { } total ? Next >= total : count < asked);
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
