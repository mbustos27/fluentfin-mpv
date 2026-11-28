using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.SmartCollections.Builders;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.SmartCollections;

/// <summary>
/// Nixie 1.9 (rule 5): what pauses background work: standby, the display off, the window minimised. The app sets each reason
/// as Windows reports it (<c>FluentFin\Services\BackgroundPauseWatch.cs</c>); a pass waits at its next request while any holds,
/// and goes on where it was when none does.
/// </summary>
public sealed class PauseGate
{
	/// <summary>The app's.</summary>
	public static PauseGate App { get; } = new();

	private readonly Lock _lock = new();
	private readonly SortedSet<string> _reasons = new(StringComparer.Ordinal);
	private TaskCompletionSource _going = Going();

	private static TaskCompletionSource Going()
	{
		var going = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		going.SetResult();
		return going;
	}

	/// <summary>Some reason holds.</summary>
	public bool Paused
	{
		get
		{
			lock (_lock)
			{
				return _reasons.Count > 0;
			}
		}
	}

	/// <summary>The reasons that hold ("minimised, standby"); "" for none.</summary>
	public string Why
	{
		get
		{
			lock (_lock)
			{
				return string.Join(", ", _reasons);
			}
		}
	}

	/// <summary>A reason ("standby") begins or ends.</summary>
	public void Set(string reason, bool paused)
	{
		TaskCompletionSource? release = null;
		bool changed, now;
		lock (_lock)
		{
			var was = _reasons.Count > 0;
			changed = paused ? _reasons.Add(reason) : _reasons.Remove(reason);
			now = _reasons.Count > 0;
			if (!was && now)
			{
				_going = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			}
			else if (was && !now)
			{
				release = _going;
			}
		}

		release?.TrySetResult();
		if (changed)
		{
			Changed?.Invoke(now);
		}
	}

	/// <summary>Done at once when nothing holds; else when the last reason ends (or <paramref name="cancellationToken"/>).</summary>
	public Task WhileGoing(CancellationToken cancellationToken)
	{
		Task going;
		lock (_lock)
		{
			going = _going.Task;
		}

		return going.IsCompleted ? Task.CompletedTask : going.WaitAsync(cancellationToken);
	}

	/// <summary>Paused (true) or going again (false).</summary>
	public event Action<bool>? Changed;
}

/// <summary>
/// Nixie 1.9 (Collections 2, step 1b, the survey's a′): the summaries' pass. The index keeps no summary; this reads each
/// title's <c>Overview</c> and <c>Taglines</c> once, lean (<see cref="IJellyfinClient.GetLibraryTextPage"/>), matches them
/// against the categories' word lists (<see cref="TextWords"/>: whole words, case-folded) as they come, and keeps only which
/// categories each title's summary has the words of (<see cref="TextMatches"/>), per server and user on this PC
/// (<c>LibraryIndex\Summaries</c>). The builders keep a category's titles by them (<see cref="MicroGenreMatcher.Match"/>).
/// <list type="bullet">
/// <item>Only the kinds the word lists are for (the shipped catalogue's: shows), every library of them once, a page at a time;
/// then only the titles new since, by id, a hundred a request (and those with no summary again after a week).</item>
/// <item>Rule 1: after Home's first Made by Nixie row, or Home's rows with none to show (<see cref="FirstRowShown"/>; two
/// minutes at most, counted once a run), and never beside the index's own read (its titles, then its people). Settings
/// turning the categories on reads at once. Saved matches are shown at once.</item>
/// <item>Rule 5: standby, the display off and a minimised window pause it at its next request (<see cref="PauseGate"/>); a
/// request that fails ends the pass, and it is tried again no sooner than <see cref="RetryAfter"/>.</item>
/// <item>Rule 9: Settings turning the categories off (<see cref="Switched"/>) stops it, and its matches are deleted, every
/// user's; nothing it began lands after.</item>
/// <item>Rule 4: a title with no summary is unknown, never "not workplace"; a category waits until the pass has read its kind.</item>
/// <item>Rule 10: the log says counts and durations only, never a title or a summary.</item>
/// <item>A sign-out forgets that user's matches (<see cref="UserFiles"/>); a sign-in elsewhere stops the pass for the one
/// before (what came is kept for them).</item>
/// </list>
/// </summary>
public sealed class TextPass : IDisposable
{
	/// <summary>The first fact of a category's reason line when its titles were matched from their summaries.</summary>
	public const string ReasonFact = "From each title's summary";

	/// <summary>Titles a request by id asks for.</summary>
	public const int ByIdsChunk = 100;

	/// <summary>Titles with no summary are asked again this long after they last were.</summary>
	public static readonly TimeSpan UnknownAskedAfter = TimeSpan.FromDays(7);

	/// <summary>The longest the pass waits for Home's first Made by Nixie row (Home may show none).</summary>
	public static readonly TimeSpan FirstRowLongest = TimeSpan.FromMinutes(2);

	/// <summary>A pass that stopped (a request failed, the server offline) is tried again no sooner than this (rule 5).</summary>
	public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(15);

	/// <summary>How long one request by id may take (a library's pages: <see cref="PageTimeout"/>).</summary>
	public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

	/// <summary>The kind whose Settings switch turns the categories (and this pass) on and off.</summary>
	public static SmartCollectionKind Kind => SmartCollectionKind.MicroGenre;

	/// <summary>The app's pass (set at start); null in tests.</summary>
	public static TextPass? App { get; set; }

	// rule 1: Home's first screen of this run: its first Made by Nixie row, or (1.9's pre-release fix H) a Home with none to show
	private static readonly TaskCompletionSource s_firstRow = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>
	/// Home showed its first Made by Nixie row, or its rows from the whole index with none among them (HomeViewModel: its rows
	/// off, or none made): the pass may begin.
	/// </summary>
	public static void FirstRowShown() => s_firstRow.TrySetResult();

	/// <summary>1.9 (the index's load, the review's item 3): Home's first row of this run, for the library index's day's read.</summary>
	internal static Task FirstRow => s_firstRow.Task;

	private const int FileVersion = 1;
	private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

	private readonly IJellyfinClient _client;
	private readonly LibraryIndex _index;
	private readonly HomeCache _homeCache;
	private readonly UserFiles _files;
	private readonly ILogger _logger;
	private readonly TimeProvider _time;
	private readonly PauseGate _pause;
	private readonly bool _shared;
	private readonly Lock _lock = new();
	private (string Key, TextMatches Matches)? _memory;
	private Task _running = Task.CompletedTask;
	private string? _runningKey;
	private CancellationTokenSource _cancel = new();
	private bool _again;
	private bool _forced;
	private string? _signedOut;
	private (string Key, DateTimeOffset At)? _stopped;
	private string? _refused; // the review's item 7: the server refused this user's pass (401, 403): none until the next sign-in
	private bool _forgotten; // rule 9: the matches were deleted since the categories were last on
	private bool _disposed;

	public TextPass(IJellyfinClient client, LibraryIndex index, HomeCache homeCache, KnownFolders folders, ILogger<TextPass> logger)
		: this(client, index, homeCache, Path.Combine(folders.ApplicationData, "LibraryIndex", "Summaries"), logger, TimeProvider.System, PauseGate.App, shared: true)
	{
	}

	/// <param name="shared">The app's: what it finds is <see cref="TextMatches.Shared"/> (a test's keeps it to itself).</param>
	internal TextPass(IJellyfinClient client, LibraryIndex index, HomeCache homeCache, string folder, ILogger logger, TimeProvider time, PauseGate pause, bool shared)
	{
		_client = client;
		_index = index;
		_homeCache = homeCache;
		_logger = logger;
		_time = time;
		_pause = pause;
		_shared = shared;
		_files = new UserFiles(folder, homeCache, logger);
		_files.Deleted += Dropped;
		homeCache.Forgetting += (url, user) =>
		{
			var key = HomeCache.FileKey(url, user);
			bool said;
			lock (_lock)
			{
				_signedOut = key;
				if (_runningKey == key)
				{
					_cancel.Cancel();
				}

				if (_memory is { } memory && memory.Key == key)
				{
					_memory = null; // at once: the file goes after (UserFiles), and nothing may read the matches meanwhile
				}

				said = Unshare(key); // in the lock a publish takes too (the review's item 5)
			}

			if (said)
			{
				TextMatches.Say();
			}
		};
		homeCache.SigningIn += (url, user) =>
		{
			var key = HomeCache.FileKey(url, user);
			var said = false;
			lock (_lock)
			{
				_signedOut = null;
				_refused = null; // a new sign-in: the server may take the pass again (review item 7)
				_stopped = null;
				if (_runningKey is { } running && running != key)
				{
					_cancel.Cancel(); // the one before's requests would go to the new server (what came is kept for them)
				}

				if (_memory is { } memory && memory.Key != key)
				{
					_memory = null;
				}

				if (TextMatches.Shared.Key is { } before && before != key)
				{
					said = Unshare(before);
				}
			}

			if (said)
			{
				TextMatches.Say();
			}
		};
		index.Changed += OnIndexChanged;
	}

	/// <summary>Whether the categories are on (Settings); tests give their own.</summary>
	internal Func<bool> Wanted { get; init; } = () => !CollectionSettings.KindsOff(ViewModels.SmartCollectionServices.Settings).Contains(Kind);

	/// <summary>The catalogue whose word lists are matched; tests give their own.</summary>
	internal Func<MicroGenreCatalogue> Catalogue { get; init; } = () => MicroGenres.Shipped;

	/// <summary>Rule 1: done when the first screen is (Home's first Made by Nixie row, or <see cref="FirstRowLongest"/>); tests give their own.</summary>
	internal Func<CancellationToken, Task>? AfterFirstScreen { get; init; }

	/// <summary>1.9 (fix H): Home's first screen of this run (<see cref="FirstRowShown"/>); tests give their own.</summary>
	internal Task FirstScreen { get; init; } = s_firstRow.Task;

	/// <summary>How often a long pass keeps what came so far (in memory and saved; a close loses at most this much).</summary>
	internal TimeSpan KeepEvery { get; init; } = TimeSpan.FromMinutes(1);

	/// <summary>The pass running now (tests wait for it); done when there is none.</summary>
	internal Task Running
	{
		get
		{
			lock (_lock)
			{
				return _running;
			}
		}
	}

	/// <summary>What the pass found for the signed-in user; <see cref="TextMatches.Empty"/> before it is read.</summary>
	public TextMatches Current
	{
		get
		{
			lock (_lock)
			{
				return _memory is { } memory && memory.Key == CurrentKey ? memory.Matches : TextMatches.Empty;
			}
		}
	}

	private string CurrentKey => HomeCache.FileKey(_client.BaseUrl ?? "", _client.UserId);

	/// <summary>
	/// Settings turned the categories on or off. Off (rule 9): the pass stops, and every user's matches are deleted. On: the
	/// pass now, for the signed-in user's index.
	/// </summary>
	public void Switched()
	{
		if (!Wanted())
		{
			_ = ForgetAllAsync();
			return;
		}

		lock (_lock)
		{
			_forgotten = false;
		}

		Schedule(CurrentKey, force: true);
	}

	// the index came in (saved, read, its people): the pass, for its user. A first read's pages so far say nothing yet
	private void OnIndexChanged(object? sender, IndexSnapshot snapshot)
	{
		if (snapshot.Reading is null && snapshot.Key is { } key)
		{
			Schedule(key, force: false);
		}
	}

	// one pass at a time; a pass for another user stops the one before
	private void Schedule(string key, bool force)
	{
		lock (_lock)
		{
			if (_disposed || _signedOut == key || key != CurrentKey)
			{
				return;
			}

			_forced |= force;
			// 1.9 (the review of the ui stream, Minor 3): a pass cancelled (the categories off) but not ended yet is no pass to
			// wait for: "on" right after "off" was taken for "again after this one", and the cancelled loop ended without it
			if (_running is { IsCompleted: false } && _runningKey == key && !_cancel.IsCancellationRequested)
			{
				_again = true; // after this one: the index it reads may have changed meanwhile
				return;
			}

			_cancel.Cancel();
			_cancel = new CancellationTokenSource();
			_runningKey = key;
			_again = false;
			var token = _cancel.Token;
			_running = Task.Run(() => Loop(key, token), CancellationToken.None);
		}
	}

	private async Task Loop(string key, CancellationToken cancellationToken)
	{
		while (true)
		{
			await RunOnce(key, cancellationToken).ConfigureAwait(false);
			lock (_lock)
			{
				if (!_again || cancellationToken.IsCancellationRequested)
				{
					return;
				}

				_again = false;
			}
		}
	}

	private async Task RunOnce(string key, CancellationToken cancellationToken)
	{
		var url = _client.BaseUrl ?? "";
		var user = _client.UserId;
		var since = _files.Generation; // a sign-out (or the categories off) after this refuses the pass's writes
		var asked = false; // 1.9 (fix H): Settings turned the categories on
		try
		{
			if (!Wanted())
			{
				await ForgetAllAsync().ConfigureAwait(false);
				return;
			}

			lock (_lock)
			{
				_forgotten = false; // on: the next off deletes again
			}

			// saved data first: the matches kept on this PC are shown at once, before the server is asked anything
			await KeptAsync(key).ConfigureAwait(false);

			// rule 5: a pass that stopped is tried again no sooner than RetryAfter (Settings turning it on asks at once)
			lock (_lock)
			{
				if (_refused == key)
				{
					return; // the server refused it (the review's item 7): not until the next sign-in, Settings or not
				}

				if (!_forced && _stopped is { } stopped && stopped.Key == key && _time.GetUtcNow() - stopped.At < RetryAfter)
				{
					return;
				}

				asked = _forced;
				_forced = false;
			}

			// rule 1: after Home's first screen (its first Made by Nixie row, or a Home with none); and never beside the index's own
			// read (its titles, then its people). 1.9 (the pre-release fix H, BH-190-m1): Settings turning the categories on asks
			// now: the user is past Home's first screen
			if (!asked)
			{
				await (AfterFirstScreen ?? DefaultFirstScreen)(cancellationToken).ConfigureAwait(false);
			}

			while (_index.Settled is { IsCompleted: false } reading)
			{
				await reading.WaitAsync(cancellationToken).ConfigureAwait(false);
			}

			cancellationToken.ThrowIfCancellationRequested();
			if (!Wanted())
			{
				await ForgetAllAsync().ConfigureAwait(false);
				return;
			}

			var index = _index.Current;
			var words = TextWords.Of(Catalogue());
			if (index is null || index.Key != key || index.Reading is not null || words.IsEmpty)
			{
				return;
			}

			var kept = await KeptAsync(key).ConfigureAwait(false);
			var pageSize = kept?.PageSize is > 0 and var carried ? carried : IndexPages.PageSize; // the size the last whole read left
			if (kept is not null && kept.Fingerprint != words.Fingerprint)
			{
				_logger.LogInformation("Summaries: the categories' word lists changed: every title's summary is read again");
				kept = null;
			}

			var plan = Plan(index, words, kept, _time.GetUtcNow());
			if (plan.Ask.Count == 0 && !plan.Whole)
			{
				if (kept is not null && plan.Pruned > 0)
				{
					await Keep(key, Merge(key, words, kept, new Dictionary<Guid, string[]?>(), plan, false, _time.GetUtcNow(), kept.PageSize), url, user, since,
						publish: true).ConfigureAwait(false);
				}

				return;
			}

			await Read(key, url, user, since, index, words, kept, plan, pageSize, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// R doesn't reach here; a sign-out, a sign-in elsewhere, the categories off or the app closing did
		}
		catch (IndexRefusedException ex)
		{
			_logger.LogInformation("Summaries: the server refused the pass (HTTP {Status}): nothing asked again until the next sign-in", ex.Status);
			lock (_lock)
			{
				_refused = key; // the review's item 7
			}
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_logger.LogWarning("Summaries: the pass failed: {Error}", ex.GetType().Name); // no text or title (rule 10)
			Stopped(key);
		}
	}

	private void Stopped(string key)
	{
		lock (_lock)
		{
			_stopped = (key, _time.GetUtcNow());
		}
	}

	// 1.9 (the pre-release fix H, BH-190-m1): FirstRowLongest counted once, from the first pass's wait: each pass counted its own
	// two minutes, so with no Made by Nixie row on Home every pass (a library change's top-up too) waited two minutes first
	private DateTimeOffset? _firstWaitFrom;

	private async Task DefaultFirstScreen(CancellationToken cancellationToken)
	{
		if (!FirstScreen.IsCompleted)
		{
			DateTimeOffset from;
			lock (_lock)
			{
				from = _firstWaitFrom ??= _time.GetUtcNow();
			}

			var left = FirstRowLongest - (_time.GetUtcNow() - from);
			if (left > TimeSpan.Zero)
			{
				using var longest = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				await Task.WhenAny(FirstScreen, Task.Delay(left, _time, longest.Token)).ConfigureAwait(false);
				await longest.CancelAsync().ConfigureAwait(false); // the row came: no timer left behind
			}
		}

		cancellationToken.ThrowIfCancellationRequested();
	}

	/// <summary>What a pass asks: every library of its kinds (<see cref="Whole"/>), or these titles by id.</summary>
	internal sealed record TextPlan(bool Whole, IReadOnlyList<Guid> Ask, IReadOnlySet<Guid> InIndex, bool AsksUnknown, int Pruned);

	/// <summary>
	/// What to ask of <paramref name="index"/>'s titles of the word lists' kinds, given what was kept. Every library only when
	/// nothing was (a first pass, or word lists changed). Once any title's summary is kept, never again (the review of
	/// wip/190-text, Major 1: a pass cut short at its second page read the first again): by id the titles not read yet (those a
	/// pass cut short didn't reach, and new ones), and those with no summary when they were last asked
	/// <see cref="UnknownAskedAfter"/> ago.
	/// </summary>
	internal static TextPlan Plan(IndexSnapshot index, TextWords words, TextMatches? kept, DateTimeOffset now)
	{
		var inIndex = new HashSet<Guid>();
		foreach (var entry in index.Entries)
		{
			if (entry is not null && words.Kinds.Contains(entry.Kind))
			{
				inIndex.Add(entry.Id);
			}
		}

		var asksUnknown = kept is null || now - kept.UnknownAskedAt >= UnknownAskedAfter || now < kept.UnknownAskedAt;
		var ask = new List<Guid>();
		foreach (var id in inIndex)
		{
			if (kept is null || !kept.Asked(id) || (asksUnknown && kept.IsUnknown(id)))
			{
				ask.Add(id);
			}
		}

		var pruned = kept is null ? 0 : kept.Titles.Keys.Count(id => !inIndex.Contains(id));
		var whole = inIndex.Count > 0 && (kept is null || kept.Count == 0);
		return new TextPlan(whole, whole ? [] : ask, inIndex, asksUnknown, pruned);
	}

	// what a read has asked so far, and the page size it leaves (a page that ran out of time halves it: IndexPages)
	private sealed class ReadState(int pageSize)
	{
		public int Requests;
		public int PageSize = pageSize;
	}

	private async Task Read(string key, string url, Guid user, long since, IndexSnapshot index, TextWords words, TextMatches? kept, TextPlan plan, int pageSize,
		CancellationToken cancellationToken)
	{
		var watch = Stopwatch.StartNew();
		var came = new ConcurrentDictionary<Guid, string[]?>(); // pages come two at a time, on the pages' own threads
		var state = new ReadState(pageSize);
		var complete = false;
		TextMatches SoFar(bool done) => Merge(key, words, kept, came, plan, done, _time.GetUtcNow(), Volatile.Read(ref state.PageSize));

		try
		{
			// a long pass keeps what came so far every KeepEvery (in memory and saved, no page rebuilt for it)
			using var keeping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			var keeper = KeepWhileReading(() => Keep(key, SoFar(false), url, user, since, publish: false), keeping.Token);
			try
			{
				complete = plan.Whole
					? await WholeAsync(index, words, came, state, cancellationToken).ConfigureAwait(false)
					: await ByIdsAsync(plan.Ask, words, came, state, cancellationToken).ConfigureAwait(false);
			}
			finally
			{
				await keeping.CancelAsync().ConfigureAwait(false);
				await keeper.ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) when (!Wanted())
		{
			await ForgetAllAsync().ConfigureAwait(false); // rule 9: off meanwhile
			return;
		}
		catch (OperationCanceledException)
		{
			// a sign-in elsewhere, the app closing: what came is kept for this user (a sign-out's write is refused)
			await Keep(key, SoFar(false), url, user, since, publish: false).ConfigureAwait(false);
			throw;
		}
		catch (IndexRefusedException ex)
		{
			// the review's item 7: the server refused (401, 403): what came is kept, and nothing is asked again until the next
			// sign-in (asked again every 15 minutes, it would only be refused again; the sign-in watch knows)
			_logger.LogInformation("Summaries: the server refused the pass (HTTP {Status}): nothing asked again until the next sign-in", ex.Status);
			lock (_lock)
			{
				_refused = key;
			}

			complete = false;
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			// a request threw: as a request that didn't come, what came is kept
			_logger.LogInformation("Summaries: a request failed ({Error})", ex.GetType().Name);
			complete = false;
		}

		var made = SoFar(complete);
		var matched = made.Titles.Count(x => x.Value is { Length: > 0 });
		if (complete)
		{
			_logger.LogInformation("Summaries: {Titles} titles read ({Whole}) in {Requests} requests, {Seconds:0.0} s: {Known} with a summary, {Unknown} without; {Matched} of all {All} have a category's words",
				came.Count, plan.Whole ? "every library" : "by id", state.Requests, watch.Elapsed.TotalSeconds, came.Values.Count(x => x is not null), came.Values.Count(x => x is null), matched, made.Count);
		}
		else
		{
			_logger.LogInformation("Summaries: the pass stopped after {Seconds:0.0} s and {Requests} requests with {Titles} titles read; they are kept, the rest asked by id later",
				watch.Elapsed.TotalSeconds, state.Requests, came.Count);
			Stopped(key);
		}

		if (!came.IsEmpty || plan.Pruned > 0 || complete)
		{
			await Keep(key, made, url, user, since, publish: true).ConfigureAwait(false);
		}
	}

	private async Task KeepWhileReading(Func<Task> keep, CancellationToken cancellationToken)
	{
		try
		{
			while (true)
			{
				await Task.Delay(KeepEvery, cancellationToken).ConfigureAwait(false);
				await keep().ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
			// the read is over: what it made is kept by the read
		}
	}

	/// <summary>How long one page of every library's may take (tests shorten it): <see cref="IndexPages.DefaultPageTimeout"/>.</summary>
	internal TimeSpan PageTimeout { get; init; } = IndexPages.DefaultPageTimeout;

	/// <summary>The pause before a page's try <c>n + 1</c> (tests shorten it): the index's.</summary>
	internal Func<int, CancellationToken, Task> RetryPause { get; init; } = (tried, cancellationToken) => Task.Delay(TimeSpan.FromSeconds(tried), cancellationToken);

	// every library of the word lists' kinds, as the index's pages are read (IndexPages; the review of wip/190-text, Major 1):
	// a library's first page and its count, then pages of the size carried from the last read, two at a time; a page that
	// fails or takes over PageTimeout asked again after a pause, as two halves, three tries in all, and every page after it
	// smaller. False when a page failed every try (what came stays in came). Standby, the switch and the index's own read are
	// waited for before each request, outside its time (IndexPages.BeforeAsk)
	private async Task<bool> WholeAsync(IndexSnapshot index, TextWords words, ConcurrentDictionary<Guid, string[]?> came, ReadState state, CancellationToken cancellationToken)
	{
		await GateAsync(cancellationToken).ConfigureAwait(false);
		var views = await _client.GetUserViewsOrNull().ConfigureAwait(false); // the client's kept answer, as a rule
		if (views is null)
		{
			return false;
		}

		var ids = index.Libraries.Where(l => words.Kinds.Contains(l.Kind)).Select(l => l.Id).ToHashSet();
		List<BaseItemDto> libraries = [.. views.Where(v => v.Id is { } id && ids.Contains(id) && JellyfinClient.IndexKinds(v) is not null)];
		if (libraries.Count == 0)
		{
			return true;
		}

		var pages = new IndexPages(libraries, _client.GetLibraryTextPage, "summaries", _logger, state.PageSize)
		{
			KeepItems = false, // each page's summaries matched as it comes, and let go
			BeforeAsk = GateAsync,
			PageTimeout = PageTimeout,
			Pause = RetryPause,
		};
		pages.PageCame += (_, _, items) =>
		{
			foreach (var item in items)
			{
				if (item?.Id is { } id)
				{
					came[id] = Score(item, words);
				}
			}
		};
		try
		{
			return await pages.ReadAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			state.Requests += pages.Requests;
			Volatile.Write(ref state.PageSize, pages.PageSizeNow);
		}
	}

	// these titles by id, a hundred a request; false when a request didn't come (what came stays in came)
	private async Task<bool> ByIdsAsync(IReadOnlyList<Guid> ids, TextWords words, ConcurrentDictionary<Guid, string[]?> came, ReadState state, CancellationToken cancellationToken)
	{
		foreach (var chunk in ids.Chunk(ByIdsChunk))
		{
			await GateAsync(cancellationToken).ConfigureAwait(false);
			state.Requests++;
			if (await Timed(token => _client.GetTextByIds(chunk, token), cancellationToken).ConfigureAwait(false) is not { } items)
			{
				return false;
			}

			foreach (var item in items)
			{
				if (item?.Id is { } id)
				{
					came[id] = Score(item, words);
				}
			}

			// asked and answered without it: the server no longer has it (unknown; it goes with the index)
			foreach (var id in chunk)
			{
				came.TryAdd(id, null);
			}
		}

		return true;
	}

	// rule 5 and 9, before each request: paused (standby, the display off, minimised) waits; off stops. Rule 1: a read of the
	// index that began meanwhile (a library change, R) goes first
	private async Task GateAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		while (_index.Settled is { IsCompleted: false } reading)
		{
			await reading.WaitAsync(cancellationToken).ConfigureAwait(false);
		}

		if (_pause.Paused)
		{
			var paused = Stopwatch.StartNew();
			_logger.LogInformation("Summaries: the pass waits ({Why})", _pause.Why);
			await _pause.WhileGoing(cancellationToken).ConfigureAwait(false);
			_logger.LogInformation("Summaries: the pass goes on after {Seconds:0} s", paused.Elapsed.TotalSeconds);
		}

		cancellationToken.ThrowIfCancellationRequested();
		if (!Wanted())
		{
			throw new OperationCanceledException("the categories are off");
		}
	}

	private static async Task<T?> Timed<T>(Func<CancellationToken, Task<T?>> ask, CancellationToken cancellationToken) where T : class
	{
		using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		limit.CancelAfter(RequestTimeout);
		var answer = await ask(limit.Token).ConfigureAwait(false);
		cancellationToken.ThrowIfCancellationRequested();
		return answer;
	}

	/// <summary>
	/// The categories whose words <paramref name="item"/>'s summary (its overview and taglines) has, by id (as many different
	/// words as each one's <see cref="MicroGenreRule.TextMin"/>); null when it has no summary at all (unknown, rule 4). The text
	/// is let go here.
	/// </summary>
	internal static string[]? Score(BaseItemDto item, TextWords words)
	{
		// 1.9 (review item 2): the overview and the taglines together, so a category's minimum of different words counts both
		var found = new SortedSet<int>();
		var said = words.MatchAll([item.Overview, .. item.Taglines ?? []], found);
		return !said ? null : found.Count == 0 ? [] : [.. found.Select(i => words.Categories[i].Id)];
	}

	/// <summary>
	/// What was kept with what came (a title read again is as it came), the titles gone from the index left out. Those with no
	/// summary are dated as asked by a pass of every library (what it didn't reach is asked by id as new), or by one by id that
	/// asked them all and finished.
	/// </summary>
	internal static TextMatches Merge(string key, TextWords words, TextMatches? kept, IReadOnlyDictionary<Guid, string[]?> came, TextPlan plan, bool complete, DateTimeOffset now,
		int pageSize = 0)
	{
		var titles = new Dictionary<Guid, string[]?>((kept?.Count ?? 0) + came.Count);
		if (kept is not null)
		{
			foreach (var (id, categories) in kept.Titles)
			{
				if (plan.InIndex.Contains(id))
				{
					titles[id] = categories;
				}
			}
		}

		foreach (var (id, categories) in came)
		{
			titles[id] = categories;
		}

		var unknownAsked = plan.Whole || (complete && plan.AsksUnknown) ? now : kept?.UnknownAskedAt ?? DateTimeOffset.MinValue;
		// the review's item 4: the kinds read whole once (every title the index has now asked), kept from then on
		var whole = new HashSet<IndexKind>(kept?.CompleteFor ?? new HashSet<IndexKind>());
		if (plan.InIndex.Count > 0 && plan.InIndex.All(titles.ContainsKey))
		{
			whole.UnionWith(words.Kinds);
		}

		return new TextMatches(key, words.Fingerprint, unknownAsked, titles) { PageSize = pageSize, CompleteFor = whole };
	}

	// in memory and said (the signed-in user's only), then saved; never for a user signed out, or once the categories are off
	private async Task Keep(string key, TextMatches made, string url, Guid user, long since, bool publish)
	{
		var login = _homeCache.LoginKey(url, user);
		var wanted = Wanted();
		var shared = false;
		lock (_lock)
		{
			if (_disposed || _signedOut == key || !wanted || _files.ForgottenSince(key, login, since))
			{
				return;
			}

			if (key == CurrentKey)
			{
				_memory = (key, made);
			}
			else
			{
				publish = false;
			}

			// the review's item 5: the app's matches are set in the lock a sign-out takes too: one after this clears them, and
			// none before it lets them in (set after the lock, a late publish put a signed-out user's matches back)
			if (publish && _shared)
			{
				TextMatches.Put(made);
				shared = true;
			}
		}

		if (AfterKept is { } kept)
		{
			await kept().ConfigureAwait(false);
		}

		if (shared)
		{
			TextMatches.Say(); // the app's one word of new matches: the Collections page makes its categories again
		}

		await _files.WriteAs(key, saving => ToDocument(made, saving), Json, login, since).ConfigureAwait(false);
	}

	// the user's kept matches: in memory, else from this PC (said when loaded); null when there are none
	private async Task<TextMatches?> KeptAsync(string key)
	{
		lock (_lock)
		{
			if (_memory is { } memory && memory.Key == key)
			{
				return memory.Matches;
			}
		}

		var since = _files.Generation;
		var watch = Stopwatch.StartNew();
		var loaded = await Load(key).ConfigureAwait(false);
		lock (_lock)
		{
			if (_memory is { } memory && memory.Key == key)
			{
				return memory.Matches;
			}

			if (loaded is null || key != CurrentKey || _signedOut == key || _files.Generation != since || !Wanted())
			{
				return null;
			}

			_memory = (key, loaded);
			if (_shared)
			{
				TextMatches.Put(loaded); // in the lock (the review's item 5)
			}
		}

		_logger.LogInformation("Summaries: the saved matches ({Titles} titles) in {Ms} ms from this PC", loaded.Count, watch.ElapsedMilliseconds);
		if (_shared)
		{
			TextMatches.Say();
		}

		return loaded;
	}

	// a sign-out or a removed login (or the categories off): nothing kept in memory; the signed-in user's saved ones again
	private void Dropped()
	{
		bool said;
		lock (_lock)
		{
			_memory = null;
			said = Unshare(null);
		}

		if (said)
		{
			TextMatches.Say();
		}

		var key = CurrentKey;
		lock (_lock)
		{
			if (_disposed || _signedOut == key)
			{
				return;
			}
		}

		if (Wanted())
		{
			_ = KeptAsync(key);
		}
	}

	// rule 9: the categories off: the pass stops, and every user's matches go (memory and this PC)
	private async Task ForgetAllAsync()
	{
		bool first, said;
		lock (_lock)
		{
			_cancel.Cancel();
			first = !_forgotten;
			_forgotten = true;
			_memory = null;
			said = Unshare(null); // at once, before the files go
		}

		if (said)
		{
			TextMatches.Say();
		}

		if (!first)
		{
			return;
		}

		await _files.ForgetAll().ConfigureAwait(false); // Dropped says it
		_logger.LogInformation("Summaries: the categories are off: the pass stopped, and its matches were deleted");
	}

	// the saved matches, compact: each title's id once ("N"), and its categories as positions in Categories (about 45 bytes a
	// show; 1.0 MB at 4,975 shows as one id per match)
	private sealed record SavedDocument(int Version, string Fingerprint, string UnknownAskedAt, IReadOnlyList<string> Categories, IReadOnlyList<string> Known,
		IReadOnlyList<int[]> Matched, IReadOnlyList<string> Unknown, int PageSize, IReadOnlyList<string> CompleteFor, string? Login);

	private sealed record Saved(int Version, string? Fingerprint, string? UnknownAskedAt, List<string>? Categories, List<string>? Known, List<int[]?>? Matched, List<string>? Unknown,
		int PageSize = 0, List<string>? CompleteFor = null);

	private static SavedDocument ToDocument(TextMatches made, string? login)
	{
		var categories = new List<string>();
		var at = new Dictionary<string, int>(StringComparer.Ordinal);
		var known = new List<string>(made.Known);
		var matched = new List<int[]>(made.Known);
		var unknown = new List<string>(made.Unknown);
		foreach (var (id, mine) in made.Titles)
		{
			if (mine is null)
			{
				unknown.Add(id.ToString("N"));
				continue;
			}

			known.Add(id.ToString("N"));
			matched.Add([.. mine.Select(category =>
			{
				if (!at.TryGetValue(category, out var position))
				{
					at[category] = position = categories.Count;
					categories.Add(category);
				}

				return position;
			})]);
		}

		return new SavedDocument(FileVersion, made.Fingerprint, made.UnknownAskedAt.ToString("O"), categories, known, matched, unknown, made.PageSize,
			[.. made.CompleteFor.Select(x => x.ToString())], login);
	}

	private async Task<TextMatches?> Load(string key)
	{
		if (await _files.ReadBytes(key).ConfigureAwait(false) is not { } bytes)
		{
			return null;
		}

		try
		{
			var saved = JsonSerializer.Deserialize<Saved>(bytes, Json);
			if (saved is null || saved.Version != FileVersion || saved.Fingerprint is null)
			{
				return null;
			}

			string[] categories = [.. (saved.Categories ?? []).Select(x => string.Intern(x ?? ""))];
			var known = saved.Known ?? [];
			var matched = saved.Matched ?? [];
			var titles = new Dictionary<Guid, string[]?>(known.Count + (saved.Unknown?.Count ?? 0));
			for (var k = 0; k < known.Count; k++)
			{
				if (Guid.TryParse(known[k], out var id))
				{
					titles[id] = k < matched.Count && matched[k] is { } mine ? [.. mine.Where(x => x >= 0 && x < categories.Length).Distinct().Select(x => categories[x])] : [];
				}
			}

			foreach (var id in saved.Unknown ?? [])
			{
				if (Guid.TryParse(id, out var unknown))
				{
					titles.TryAdd(unknown, null);
				}
			}

			var unknownAsked = DateTimeOffset.TryParse(saved.UnknownAskedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var asked)
				? asked : DateTimeOffset.MinValue;
			return new TextMatches(key, saved.Fingerprint, unknownAsked, titles)
			{
				PageSize = saved.PageSize, // the review's Major 1: the size carried
				CompleteFor = new HashSet<IndexKind>((saved.CompleteFor ?? []).Select(x => Enum.TryParse<IndexKind>(x, out var kind) ? kind : (IndexKind?)null).OfType<IndexKind>()), // item 4
			};
		}
		catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or FormatException)
		{
			_logger.LogInformation("Summaries: ignoring damaged saved matches ({Error})", ex.GetType().Name);
			return null;
		}
	}

	// under _lock: the app's matches emptied when they are this one's (key; null: whoever's); whether they were
	private bool Unshare(string? key)
	{
		if (!_shared || TextMatches.Shared is not { Key: { } whose } || (key is not null && whose != key))
		{
			return false;
		}

		TextMatches.Put(TextMatches.Empty);
		return true;
	}

	/// <summary>Tests: awaited after matches are kept in memory (and set as the app's), before anyone is told.</summary>
	internal Func<Task>? AfterKept { get; init; }

	public void Dispose()
	{
		lock (_lock)
		{
			_disposed = true;
			_cancel.Cancel();
		}

		_index.Changed -= OnIndexChanged;
	}
}
