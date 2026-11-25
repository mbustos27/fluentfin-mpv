using System.Diagnostics;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.SmartCollections.Anchors;

/// <summary>Nixie 1.8 (P1-2): what a people pass asks: every library's people (<see cref="Whole"/>), or only these titles' by id.</summary>
/// <param name="Whole">The index has no people yet: every library's people, page by page.</param>
/// <param name="Ask">Otherwise the titles to ask by id: those the index before didn't have, and those it still had to ask.</param>
internal sealed record PeoplePlan(bool Whole, IReadOnlyList<Guid> Ask)
{
	/// <summary>Nothing to ask: every title's people are lent from the index before.</summary>
	public bool Nothing => !Whole && Ask.Count == 0;
}

/// <summary>
/// Nixie 1.8 (P1-2): what a people pass made: the index with the people that came, those not yet come still to ask by id
/// (<see cref="IndexSnapshot.PeopleToAsk"/>), and whether all it asked came.
/// </summary>
internal sealed record PeopleRun(IndexSnapshot Index, bool Complete, int Asked, int Came);

/// <summary>
/// Nixie 1.8 (P1-2): the index's people pass, back for "More from" and Spotlight: each title's directors and its first three
/// actors billed (the report's billing gate; the index kept five before 1.7.3), and no more. 1.7.1's pass read every
/// library's people at every whole read: about twice the titles' time on the server the owner uses (682 s of 1,198), and
/// 1.7.3 stopped it. Here:
/// <list type="bullet">
/// <item>it runs after the titles are in and shown (rule 1), in the background;</item>
/// <item>a title the index before had keeps its people (<see cref="Lend"/>): only new titles are asked, by id, a hundred a
/// request (<see cref="ByIdsAsync"/>);</item>
/// <item>every library's people, page by page (two at a time, lean: <see cref="WholeAsync"/>), only for an index that has
/// none.</item>
/// </list>
/// Review (Major): what comes is never thrown away. A pass cut short (a page that failed every try, R, a sign-in elsewhere,
/// the app closing, a request the server refused) keeps the people that came, and the rest go on the by-id list (<see cref="IndexSnapshot.PeopleToAsk"/>);
/// a long pass hands what came so far to the caller every <see cref="KeepEvery"/> to keep. On the server the owner uses the
/// 1.7.1 pass took 682 s, and starting it from zero at every interruption would have asked it again and again. The log says
/// counts and durations only (rule 10), never a title or a name.
/// <para>
/// <c>IndexRead.PeopleAsync</c> runs it; <c>LibraryIndex</c> lends and plans before a read's titles are shown
/// (<c>WithLentPeople</c>) and runs it behind them; <see cref="IndexEntry.PeopleOf"/> is <see cref="Billed"/>.
/// </para>
/// </summary>
internal sealed class PeoplePass(ILogger? log)
{
	/// <summary>Titles a request by id asks for.</summary>
	public const int ByIdsChunk = 100;

	/// <summary>How often a long pass hands what came so far to be kept (a close or R loses at most this much).</summary>
	public static readonly TimeSpan DefaultKeepEvery = TimeSpan.FromMinutes(1);

	/// <summary>How often this pass hands what came so far to be kept (tests: at every page or request).</summary>
	public TimeSpan KeepEvery { get; init; } = DefaultKeepEvery;

	/// <summary>Asks the server for these titles' people (lean: the id and People); null when it didn't answer.</summary>
	public delegate Task<IReadOnlyList<BaseItemDto>?> AskByIds(IReadOnlyList<Guid> ids, CancellationToken cancellationToken);

	/// <summary>Keeps the index with what came so far (the pass goes on after it).</summary>
	public delegate Task Keep(IndexSnapshot soFar);

	/// <summary>The size of the whole pass's pages after it (smaller after a page ran out of time; review M3).</summary>
	public int PageSizeNow { get; private set; } = IndexPages.PageSize;

	/// <summary>
	/// What to ask for <paramref name="titles"/>' people, given the index before (<paramref name="previous"/>) and the titles
	/// it still had to ask (<paramref name="previousToAsk"/>): every library when it had no people (or a whole pass was due and
	/// nothing of it came), else the new titles and those still to ask, by id, however many (review, Major: more than 2,000 new
	/// titles read every library's people again).
	/// </summary>
	public static PeoplePlan PlanFor(IndexSnapshot titles, IndexSnapshot? previous, IReadOnlyCollection<Guid>? previousToAsk = null)
	{
		if (previous is null || !previous.HasPeople || previous.PeopleDue)
		{
			return new PeoplePlan(true, []);
		}

		var had = new HashSet<Guid>(previous.Entries.Count);
		foreach (var entry in previous.Entries)
		{
			if (entry is not null)
			{
				had.Add(entry.Id);
			}
		}

		var still = previousToAsk is { Count: > 0 } ? new HashSet<Guid>(previousToAsk) : null;
		var ask = new List<Guid>();
		foreach (var entry in titles.Entries)
		{
			if (entry is not null && (!had.Contains(entry.Id) || still?.Contains(entry.Id) == true))
			{
				ask.Add(entry.Id);
			}
		}

		return new PeoplePlan(false, ask);
	}

	/// <summary>
	/// The titles with the people the index before had for them (a title it didn't have, none yet); the index says it has
	/// people when the one before did. The titles' own people stay where the one before had no entry for them.
	/// </summary>
	public static IndexSnapshot Lend(IndexSnapshot titles, IndexSnapshot previous)
	{
		if (!previous.HasPeople)
		{
			return titles;
		}

		var before = new Dictionary<Guid, IndexPerson[]>(previous.Entries.Count);
		foreach (var entry in previous.Entries)
		{
			if (entry is not null)
			{
				before.TryAdd(entry.Id, entry.People);
			}
		}

		return titles with { Entries = [.. titles.Entries.Select(x => x is not null && before.TryGetValue(x.Id, out var people) ? x with { People = people } : x)], HasPeople = true };
	}

	/// <summary>The titles with the people read (<paramref name="people"/>, by title id); those not read keep theirs.</summary>
	public static IndexSnapshot With(IndexSnapshot titles, IReadOnlyDictionary<Guid, IndexPerson[]> people) =>
		titles with { Entries = [.. titles.Entries.Select(x => x is not null && people.TryGetValue(x.Id, out var read) ? x with { People = read } : x)], HasPeople = true, PeopleDue = false };

	/// <summary>
	/// The people the index keeps of a title: its directors, then its first three actors billed, in the server's order (guest
	/// stars, writers and the rest never; a blank name never). None for a title with no people.
	/// </summary>
	public static IndexPerson[] Billed(BaseItemDto dto)
	{
		if (dto.People is not { Count: > 0 } people)
		{
			return [];
		}

		// review (Major 2): names alone (a person is one name, as the server keeps them): the server's person ids doubled what
		// the people add to the saved index, and nothing reads them
		var directors = people.Where(p => p?.Type == BaseItemPerson_Type.Director && !string.IsNullOrWhiteSpace(p.Name)).Select(p => new IndexPerson(p.Name!.Trim(), null, "Director"));
		var actors = people.Where(p => p?.Type == BaseItemPerson_Type.Actor && !string.IsNullOrWhiteSpace(p.Name)).Take(IndexAnchorSource.Billed).Select(p => new IndexPerson(p.Name!.Trim(), null, "Actor"));
		return [.. directors, .. actors];
	}

	/// <summary>
	/// Every library's people, page by page (<see cref="IndexPages"/>, two at a time): those that came, and whether every page
	/// did. Cut short (a page that failed every try, or the read cancelled) it still returns what came. What came so far goes
	/// to <paramref name="keep"/> every <see cref="KeepEvery"/>.
	/// </summary>
	public async Task<(IReadOnlyDictionary<Guid, IndexPerson[]> Came, bool Complete)> WholeAsync(IReadOnlyList<BaseItemDto> libraries, IndexPages.Ask ask, int pageSize,
		Func<IReadOnlyDictionary<Guid, IndexPerson[]>, Task>? keep, CancellationToken cancellationToken)
	{
		var watch = Stopwatch.StartNew();
		// 1.8 (BH-180-m2): each page's directors and first three billed, as names, taken as it comes, and the page let go (a
		// page's whole cast and crew, with image tags and blurhashes, was held until the pass ended); the one map, kept whole
		var came = new System.Collections.Concurrent.ConcurrentDictionary<Guid, IndexPerson[]>();
		var pages = new IndexPages(libraries, ask, "people", log, pageSize) { KeepItems = false };
		pages.PageCame += (_, _, items) =>
		{
			foreach (var item in items)
			{
				if (item?.Id is { } id)
				{
					came.TryAdd(id, Billed(item));
				}
			}
		};
		var keeps = new List<Task>();
		var lastKeep = 0L;
		var keeping = 0;
		var again = 0;

		// one keep at a time, at most every KeepEvery; pages that come while one runs are kept by it after, so the last progress
		// before a stall is never left out (pages come two at a time, on the pages' own threads)
		async Task KeepLoop()
		{
			while (true)
			{
				while (Interlocked.Exchange(ref again, 0) == 1)
				{
					Interlocked.Exchange(ref lastKeep, watch.Elapsed.Ticks);
					await KeepNow(keep!, came).ConfigureAwait(false);
				}

				Volatile.Write(ref keeping, 0);
				if (Volatile.Read(ref again) == 0 || Interlocked.CompareExchange(ref keeping, 1, 0) != 0)
				{
					return;
				}
			}
		}

		if (keep is not null)
		{
			pages.PageIn += (_, _) =>
			{
				if (watch.Elapsed.Ticks - Interlocked.Read(ref lastKeep) < KeepEvery.Ticks)
				{
					return;
				}

				Volatile.Write(ref again, 1);
				if (Interlocked.CompareExchange(ref keeping, 1, 0) == 0)
				{
					lock (keeps)
					{
						keeps.Add(KeepLoop());
					}
				}
			};
		}

		bool complete;
		try
		{
			complete = await pages.ReadAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			complete = false; // cut short: what came is still the caller's to keep
		}
		catch (Services.IndexRefusedException ex)
		{
			// re-review (Minor): the server refused a page (401, 403): as a cut, what came is kept (the sign-in watch knows)
			log?.LogInformation("Library index: the people pass was refused (HTTP {Status})", ex.Status);
			complete = false;
		}
		finally
		{
			PageSizeNow = pages.PageSizeNow;
		}

		Task[] pending;
		lock (keeps)
		{
			pending = [.. keeps];
		}

		await Task.WhenAll(pending).ConfigureAwait(false); // nothing kept after the pass has said what came
		var people = came;
		if (complete)
		{
			log?.LogInformation("Library index: the people pass read {Titles} titles' people in {Seconds:0.0} s", people.Count, watch.Elapsed.TotalSeconds);
		}
		else
		{
			log?.LogInformation("Library index: the people pass stopped after {Seconds:0.0} s with {Titles} titles' people; they are kept, the rest asked by id later",
				watch.Elapsed.TotalSeconds, people.Count);
		}
		return (people, complete);
	}

	private async Task KeepNow(Func<IReadOnlyDictionary<Guid, IndexPerson[]>, Task> keep, IReadOnlyDictionary<Guid, IndexPerson[]> came)
	{
		try
		{
			await keep(came).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			log?.LogInformation("Library index: what the people pass brought so far wasn't kept: {Error}", ex.GetType().Name);
		}
	}

	/// <summary>
	/// These titles' people, a hundred a request, one request at a time: those that came (a title asked whose request was
	/// answered without it, one the server no longer has, comes with none and isn't asked again), and whether every request
	/// was answered. Cut short (a request unanswered, or the read cancelled) it still returns what came. What came so far goes
	/// to <paramref name="keep"/> every <see cref="KeepEvery"/>.
	/// </summary>
	public async Task<(IReadOnlyDictionary<Guid, IndexPerson[]> Came, bool Complete)> ByIdsAsync(IReadOnlyList<Guid> ids, AskByIds ask,
		Func<IReadOnlyDictionary<Guid, IndexPerson[]>, Task>? keep, CancellationToken cancellationToken)
	{
		var watch = Stopwatch.StartNew();
		var people = new Dictionary<Guid, IndexPerson[]>(ids.Count);
		var requests = 0;
		var complete = true;
		var lastKeep = TimeSpan.Zero;
		try
		{
			foreach (var chunk in ids.Distinct().Chunk(ByIdsChunk))
			{
				cancellationToken.ThrowIfCancellationRequested();
				requests++;
				if (await ask(chunk, cancellationToken).ConfigureAwait(false) is not { } items)
				{
					complete = false;
					break;
				}

				foreach (var item in items)
				{
					if (item?.Id is { } id)
					{
						people.TryAdd(id, Billed(item));
					}
				}

				// asked and answered: a title the answer left out (the server no longer has it) keeps none, and is done
				foreach (var id in chunk)
				{
					people.TryAdd(id, []);
				}

				if (keep is not null && watch.Elapsed - lastKeep >= KeepEvery)
				{
					lastKeep = watch.Elapsed;
					await KeepNow(keep, people).ConfigureAwait(false);
				}
			}
		}
		catch (OperationCanceledException)
		{
			complete = false; // cut short: what came is still the caller's to keep
		}
		catch (Services.IndexRefusedException ex)
		{
			// re-review (Minor): the server refused a request (401, 403): as a cut, what came is kept
			log?.LogInformation("Library index: the people by id were refused (HTTP {Status})", ex.Status);
			complete = false;
		}

		if (complete)
		{
			log?.LogInformation("Library index: the people of {Titles} titles by id in {Requests} requests, {Seconds:0.0} s", people.Count, requests, watch.Elapsed.TotalSeconds);
		}
		else
		{
			log?.LogInformation("Library index: the people by id stopped at {Titles} of {Asked} titles ({Requests} requests, {Seconds:0.0} s); the rest asked again later",
				people.Count, ids.Count, requests, watch.Elapsed.TotalSeconds);
		}
		return (people, complete);
	}

	/// <summary>
	/// The people pass for <paramref name="titles"/> (read with <paramref name="libraries"/>), the index before lending what it
	/// had: the index with the people that came (<see cref="IndexSnapshot.HasPeople"/>, no longer due) and those still to ask
	/// by id; never null. A whole pass of which nothing came leaves the index as it was (the whole pass still due).
	/// <paramref name="keep"/> keeps what came so far during a long pass.
	/// </summary>
	public async Task<PeopleRun> RunAsync(IndexSnapshot titles, IndexSnapshot? previous, IReadOnlyCollection<Guid>? previousToAsk, IReadOnlyList<BaseItemDto> libraries,
		IndexPages.Ask wholePage, AskByIds byIds, int pageSize, CancellationToken cancellationToken, Keep? keep = null)
	{
		var plan = PlanFor(titles, previous, previousToAsk);
		var lent = previous is not null ? Lend(titles, previous) : titles;
		if (plan.Nothing)
		{
			log?.LogInformation("Library index: every title's people lent from the index before; none asked");
			return new PeopleRun(lent with { HasPeople = true, PeopleDue = false, PeopleToAsk = [] }, true, 0, 0);
		}

		IReadOnlyCollection<Guid> asked = plan.Whole ? [.. titles.Entries.Where(x => x is not null).Select(x => x.Id).Distinct()] : plan.Ask;
		Func<IReadOnlyDictionary<Guid, IndexPerson[]>, Task>? soFar = keep is null ? null : came => keep(WithWhatCame(lent, came, asked, complete: false));
		var (people, complete) = plan.Whole
			? await WholeAsync(libraries, wholePage, pageSize, soFar, cancellationToken).ConfigureAwait(false)
			: await ByIdsAsync(plan.Ask, byIds, soFar, cancellationToken).ConfigureAwait(false);
		if (plan.Whole && !complete && people.Count == 0)
		{
			return new PeopleRun(lent with { PeopleDue = true, PeopleToAsk = [] }, false, asked.Count, 0); // nothing came: the whole pass again
		}

		return new PeopleRun(WithWhatCame(lent, people, asked, complete), complete, asked.Count, people.Count);
	}

	/// <summary>
	/// The index with the people that came, and those asked that didn't still to ask by id (none when every page or request
	/// was answered: a title no answer had is gone from the server).
	/// </summary>
	internal static IndexSnapshot WithWhatCame(IndexSnapshot lent, IReadOnlyDictionary<Guid, IndexPerson[]> came, IReadOnlyCollection<Guid> asked, bool complete) =>
		With(lent, came) with { PeopleToAsk = complete ? [] : [.. asked.Where(id => !came.ContainsKey(id))] };
}
