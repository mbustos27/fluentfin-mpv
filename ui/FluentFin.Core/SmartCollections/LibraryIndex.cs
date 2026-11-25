using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.SmartCollections;

/// <summary>
/// Nixie 1.6 (smart collections phase 1): every film and show of the user's movie and show libraries, in one lean index
/// the Nixie collections are built from on this PC. Read page by page (<see cref="LibraryPageReader"/>, the 1.4.1 big-library
/// paging: a 25,800-film library never goes in one request), off the UI thread and cancellable; then the people in a pass of
/// their own (they cost about 3.5 times the index), then the played episodes (no show carries a last-played date). Kept per
/// server and user like <see cref="HomeCache"/>, in memory and in the data folder; read again when the server reports a
/// library change, on R, and once a day; forgotten when the user signs out or the saved login is removed.
/// <para>
/// Nixie 1.7.1 (the index on a big server: 1,198 s on the owner's 34,001 titles, at every start): the saved index shows at
/// once and is read again in the background only when it is stale: a day old, R, an index without its people or of an older
/// format, or a library change the server's check (<see cref="IJellyfinClient.GetIndexCheck"/>: one request of one title)
/// confirms, at most once an hour. Pages come two at a time (<see cref="IndexPages"/>); a first read shows its rows as its
/// pages come (<see cref="IndexSnapshot.Reading"/>, <see cref="Progress"/>) and is in, and saved, before its people pass.
/// </para>
/// <para>
/// 1.7.1 review (M2): a read again is saved before its people pass too, the people it had lent to its titles
/// (<see cref="IndexSnapshot.PeopleDue"/>); an index whose people are lent or missing reads only its people again; a library
/// change the check confirms reads only the titles (the people lent); and when a read last began is kept on this PC, so the
/// once-an-hour limit holds across restarts. M3: the page sizes a slow server's timeouts left are kept with it.
/// </para>
/// <para>
/// Nixie 1.7.3: no people pass, and no read for people (the owner's answer to the 1.7.3 plan: nothing published reads them);
/// a title the server lists in two libraries is one entry with both (P0-2), in a read and in a saved index that had it twice;
/// the saved index's revision 3, inside version 2 (an older one shows at once and is read again behind it); and played
/// episodes that didn't come are asked again on their own, at most once an hour (<see cref="IndexSnapshot.PlaysDue"/>).
/// </para>
/// </summary>
public sealed class LibraryIndex : IDisposable
{
	public static readonly TimeSpan FreshFor = TimeSpan.FromDays(1);

	// 1.7.1 (the index on a big server)
	/// <summary>A library change, an index of an older format, played episodes that didn't come: read again at most this often (R at once).</summary>
	public static readonly TimeSpan ChangesReadAfter = TimeSpan.FromHours(1);

	/// <summary>The server's check, at most this often.</summary>
	public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(5);

	/// <summary>BH-171-m5: how long the server's check may take (it waited the client's 100 s, and outlived a sign-out).</summary>
	public static readonly TimeSpan DefaultCheckTimeout = TimeSpan.FromSeconds(15);

	/// <summary>A first read's rows, made again from its pages at most this often.</summary>
	public static readonly TimeSpan PartialEvery = TimeSpan.FromSeconds(4);
	// end 1.7.1

	private const int FormatVersion = 2; // 1.7 keyless: 2 adds the Tmdb, Imdb and Tvdb ids (an older saved index is read again)
	private const int OldestFormat = 1; // 1.7.1: a 1.6 index shows at once (without the outside ids) and is read again behind it

	// 1.7.3: a revision inside version 2, so that 1.7.2 (which skips what it doesn't know) still reads the file after a
	// downgrade. 3 adds every library of a title (LibraryIds), the critics' rating and the tag count; a file of an older
	// revision shows at once and is read again behind it, at most once an hour
	private const int FormatRevision = 3;

	private readonly IJellyfinClient _client;
	private readonly HomeCache _homeCache;
	private readonly UserFiles _files;
	private readonly ILogger _logger;
	private readonly TimeProvider _time;
	private readonly IDisposable _subscription;
	private readonly Lock _lock = new();
	private string? _memoryKey;
	private IndexSnapshot? _memory;
	private bool _stale;
	private string? _readingKey;
	private Task? _reading; // 1.7.1: the whole read
	private Task<IndexSnapshot?>? _firstIn; // 1.7.1: the read's first whole index (1.7.3: its only one, with no people pass after it)
	private CancellationTokenSource? _readingCancel;
	// 1.7.1: why the saved index is read again, and how often
	private bool _checkDue = true; // the server's check, once at the start and after each library change
	private bool _oldFormat;
	// 1.7.5 (fx1; stream I's review of 1.7.3): a read of every title began in this run while the index was of an older format:
	// one that fails is tried again after the hour (the flag stays until a read is in), not at every visit
	private bool _upgradeBegun;
	private DateTimeOffset _lastCheck = DateTimeOffset.MinValue;
	private IndexSnapshot? _soFar; // a first read's titles so far
	private ReadKind _readingKind;

	// review M2, M3: per user, when a read last began and the page sizes it left; kept on this PC beside the index
	private readonly UserFiles _readsFiles;
	private readonly Dictionary<string, Reads> _reads = [];
	private readonly Dictionary<string, Task> _readsLoading = []; // re-review (Minor): a second load waits for the first

	// BH-171-m5: the check asked with an index read when the check didn't answer (its Check is null): what later checks are
	// compared with, rather than a change every time
	private (string Key, string Check)? _adoptedCheck;
	private CancellationTokenSource _checkCancel = new();
	private Task _checking = Task.CompletedTask;

	// BH-171-m7: the user signed out (their key): nothing is read for them until the next sign-in
	private string? _signedOut;

	// fix round (BH-173-M1): Revision, the index revision of the last read that finished (0: before 1.7.3, or none finished)
	private sealed record Reads(DateTimeOffset LastRead, int TitlesPage, int Revision = 0);

	/// <summary>
	/// What a read reads: every title, dated anew; the titles of a library change (the index keeps the day's date); 1.7.3, the
	/// played episodes only (the people-only read went with the people pass); 1.7.5 (BH-171-m5), a library change as the titles
	/// saved since the last read, else every title (<see cref="Titles"/>).
	/// </summary>
	private enum ReadKind
	{
		Whole,
		Titles,
		Plays,
		Added,
	}

	public LibraryIndex(IJellyfinClient client, HomeCache homeCache, KnownFolders folders, IObservable<IOutboundWebSocketMessage> serverMessages, ILogger<LibraryIndex> logger)
		: this(client, homeCache, Path.Combine(folders.ApplicationData, "LibraryIndex"), serverMessages, logger, TimeProvider.System)
	{
	}

	internal LibraryIndex(IJellyfinClient client, HomeCache homeCache, string folder, IObservable<IOutboundWebSocketMessage> serverMessages, ILogger logger, TimeProvider time)
	{
		_client = client;
		_homeCache = homeCache;
		_logger = logger;
		_time = time;
		_files = new UserFiles(folder, homeCache, logger);
		_files.Deleted += Drop;
		_readsFiles = new UserFiles(Path.Combine(folder, "Reads"), homeCache, logger); // review M2: forgotten with the index
		// BH-171-m7: a sign-out ends the index's asking for that user (a late ask started a read with the ended sign-in), and
		// the next sign-in starts it again
		homeCache.Forgetting += (url, user) =>
		{
			lock (_lock)
			{
				_signedOut = HomeCache.FileKey(url, user);
			}
		};
		homeCache.SigningIn += (_, _) =>
		{
			lock (_lock)
			{
				_signedOut = null;
			}
		};
		_subscription = serverMessages.Subscribe(message =>
		{
			if (message is LibraryChangedMessage)
			{
				MarkChanged(); // 1.7.1: checked, not read at once
			}
		});
	}

	/// <summary>
	/// A new index came in (from the server, or the saved one); raised off the UI thread. 1.7.1: on a first read, also the
	/// titles read so far as its pages come (<see cref="IndexSnapshot.Reading"/> says how far).
	/// </summary>
	public event EventHandler<IndexSnapshot>? Changed;

	/// <summary>Nixie 1.7.1: a first read's progress, at each page (off the UI thread).</summary>
	public event EventHandler<IndexProgress>? Progress;

	/// <summary>Tests: awaited before the note of the last read is read from this PC (a test holds it there).</summary>
	internal Func<Task>? ReadingNote { get; init; }

	/// <summary>BH-171-m5: how long the server's check may take (tests shorten it).</summary>
	internal TimeSpan CheckTimeout { get; init; } = DefaultCheckTimeout;

	/// <summary>BH-171-m5: the server's check running now (tests wait for it); done when there is none.</summary>
	internal Task Checking
	{
		get
		{
			lock (_lock)
			{
				return _checking;
			}
		}
	}

	// BH-171-m7: the signed-in user signed out, and nobody has signed in since
	private bool SignedOut
	{
		get
		{
			lock (_lock)
			{
				return _signedOut is { } key && key == CurrentKey;
			}
		}
	}

	/// <summary>Nixie 1.7.1: the read running now (tests wait for it); done when there is none.</summary>
	internal Task Settled
	{
		get
		{
			lock (_lock)
			{
				return _reading ?? Task.CompletedTask;
			}
		}
	}

	/// <summary>Nixie 1.7.1: during a first read, its titles so far (a page opened meanwhile shows them); null otherwise.</summary>
	public IndexSnapshot? ReadingSoFar
	{
		get
		{
			lock (_lock)
			{
				return _soFar is not null && (_memoryKey != CurrentKey || _memory is null) && _readingKey == CurrentKey && _reading is { IsCompleted: false } ? _soFar : null;
			}
		}
	}

	/// <summary>How long the last read took, for the log and the time-budget test.</summary>
	public TimeSpan LastReadTook { get; private set; }

	private string CurrentKey => HomeCache.FileKey(_client.BaseUrl, _client.UserId);

	/// <summary>The index in memory for the signed-in user; null until it is read.</summary>
	public IndexSnapshot? Current
	{
		get
		{
			lock (_lock)
			{
				return _memoryKey == CurrentKey ? _memory : null;
			}
		}
	}

	/// <summary>R: the next <see cref="GetAsync"/> reads the index again (the old one shows meanwhile).</summary>
	public void MarkStale()
	{
		lock (_lock)
		{
			// review (Minor): R during a whole read of this user is answered by that read; it was kept and read it all again
			// at the next visit. 1.7.3: a library change's read too (with no people pass it reads all a whole read does)
			if (_reading is { IsCompleted: false } && _readingKey == CurrentKey && _readingKind is ReadKind.Whole or ReadKind.Titles)
			{
				return;
			}

			_stale = true;
		}
	}

	/// <summary>
	/// Nixie 1.7.1: the server reported a library change: the next <see cref="GetAsync"/> asks the server's check, and reads
	/// the index again only when it changed (1.7.0 read the whole library at once, on a server whose library changes often).
	/// </summary>
	public void MarkChanged()
	{
		lock (_lock)
		{
			_checkDue = true;
		}
	}

	/// <summary>
	/// The index for the signed-in user: in memory, else the saved one, else read from the server now (null when that
	/// failed). 1.7.1: a saved one is read again in the background only when it is stale (<see cref="ReadAgainIfDue"/>), and
	/// <see cref="Changed"/> says when it is in.
	/// </summary>
	public async Task<IndexSnapshot?> GetAsync(CancellationToken cancellationToken = default)
	{
		var key = CurrentKey;
		if (SignedOut)
		{
			return null; // BH-171-m7
		}

		if (await SavedAsync().ConfigureAwait(false) is { } known)
		{
			return known;
		}

		return await Read(key).WaitAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Nixie 1.7.1: the index in memory, else the saved one (read from this PC, never from the server), else null; a stale
	/// one is read again in the background. What a page shows at once.
	/// </summary>
	public async Task<IndexSnapshot?> SavedAsync()
	{
		var key = CurrentKey;
		IndexSnapshot? known;
		lock (_lock)
		{
			known = _memoryKey == key ? _memory : null;
		}

		if (SignedOut)
		{
			return null; // BH-171-m7
		}

		if (known is null)
		{
			var watch = Stopwatch.StartNew();
			var since = _files.Generation;
			await LoadReads(key).ConfigureAwait(false); // review M2: when a read last began, before deciding on another
			if (await Load(key).ConfigureAwait(false) is not { } saved)
			{
				return null;
			}

			var loaded = false;
			lock (_lock)
			{
				// BH-171-m6: another user signed in, or this one out, while the file was read: it is not theirs to have
				if (key != CurrentKey || _files.Generation != since)
				{
					return null;
				}

				if (_memoryKey != key || _memory is null)
				{
					(_memoryKey, _memory) = (key, saved);
					loaded = true;
				}

				known = _memory;
			}

			if (loaded)
			{
				_logger.LogInformation("Library index: the saved one ({Titles} titles, read {Hours:0.0} h ago) in {Ms} ms from this PC",
					saved.Entries.Count, (_time.GetUtcNow() - saved.ReadAt).TotalHours, watch.ElapsedMilliseconds);
				Changed?.Invoke(this, saved);
			}
		}

		ReadAgainIfDue(key, known);
		return known;
	}

	/// <summary>R: the index read again now; the one in memory stays until the new one is in.</summary>
	public Task<IndexSnapshot?> RefreshAsync()
	{
		MarkStale();
		return Read(CurrentKey, ReadKind.Whole);
	}

	/// <summary>
	/// Nixie 1.7.1: the index read again in the background when it is stale: R; a day old; of an older format or revision,
	/// or (1.7.3) without the played episodes its read asked for (at most once an hour); or a library change the server's
	/// check confirms (the check at most every five minutes, the read at most once an hour). Nothing when a read is running.
	/// 1.7.3: an index without its people is no longer a reason (the people pass stopped).
	/// </summary>
	private void ReadAgainIfDue(string key, IndexSnapshot known)
	{
		var now = _time.GetUtcNow();
		ReadKind? due;
		var check = false;
		lock (_lock)
		{
			if (_reading is { IsCompleted: false } && _readingKey == key)
			{
				return;
			}

			// 1.7.3: the played episodes alone when only they are missing (BH-171-M1's other half). Fix round (BH-173-M1): an
			// index of an older format is read again at once on the first start after the update, whatever the older version's
			// note of its last read says (it held 1.7.2's index, and its Acclaimed, for up to an hour); a read this version began
			// within the hour, and finished, still holds it
			due = _stale ? ReadKind.Whole
				: _oldFormat && !_upgradeBegun && !ReadLately(key, now, thisVersion: true) ? ReadKind.Whole
				: ReadLately(key, now) ? null
				: now - known.ReadAt > FreshFor || _oldFormat ? ReadKind.Whole
				// 1.7.5 (fx1; stream I's review of 1.7.3): the played episodes alone only when no check is due. Taken before the
				// check, a read of them (at most once an hour) won every hour while they kept failing, and the check's change
				// waited for the day's read; the check now comes first, and the played episodes after it (PlaysAfterCheck)
				: known.PlaysDue && !(_checkDue && now - _lastCheck >= CheckEvery) ? ReadKind.Plays
				: null;
			if (due is null && _checkDue && now - _lastCheck >= CheckEvery)
			{
				(check, _checkDue, _lastCheck) = (true, false, now);
			}
		}

		if (due is { } kind)
		{
			_ = Read(key, kind);
		}
		else if (check)
		{
			_ = CheckAsync(key, known);
		}
	}

	// whether a read of this user began within the hour (review M2: also before a restart). Re-review (Minor): a note dated
	// after now (the clock moved back) is not "lately": it held every automatic read until the clock passed it
	// fix round (BH-173-M1): thisVersion, only when the last read that finished was of this version (or a later one)
	private bool ReadLately(string key, DateTimeOffset now, bool thisVersion = false) =>
		_reads.TryGetValue(key, out var reads) && (!thisVersion || reads.Revision >= FormatRevision)
		&& now - reads.LastRead is var age && age >= TimeSpan.Zero && age < ChangesReadAfter;

	// the server's check against the one the index was read with: a change reads it again, at most once an hour
	private Task CheckAsync(string key, IndexSnapshot known)
	{
		lock (_lock)
		{
			return _checking = Check(_checkCancel.Token); // BH-171-m5: a sign-out ends it
		}

		async Task Check(CancellationToken signedIn)
		{
			var titles = false;
			try
			{
				titles = await CheckOnce(key, known, signedIn).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				_logger.LogInformation("Library index: the check failed: {Error}", ex.GetType().Name);
			}

			if (!titles && !signedIn.IsCancellationRequested)
			{
				PlaysAfterCheck(key);
			}
		}
	}

	// 1.7.5 (fx1): a check that began no read of the titles: the played episodes that didn't come are asked now, if they are
	// due and no read began within the hour (ReadAgainIfDue left them for the check)
	private void PlaysAfterCheck(string key)
	{
		lock (_lock)
		{
			if (key != CurrentKey || _memoryKey != key || _memory is not { PlaysDue: true } || ReadLately(key, _time.GetUtcNow())
				|| _reading is { IsCompleted: false } && _readingKey == key)
			{
				return;
			}
		}

		_ = Read(key, ReadKind.Plays);
	}

	// true when it began a read of the titles (a change the check confirmed)
	private async Task<bool> CheckOnce(string key, IndexSnapshot known, CancellationToken signedIn)
	{
		var watch = Stopwatch.StartNew();
		string? now;
		using (var limit = CancellationTokenSource.CreateLinkedTokenSource(signedIn))
		{
			limit.CancelAfter(CheckTimeout); // BH-171-m5: 15 s, not the client's 100
			now = await _client.GetIndexCheck(limit.Token).ConfigureAwait(false);
		}

		if (now is null || key != CurrentKey || signedIn.IsCancellationRequested)
		{
			_logger.LogInformation("Library index: the server's check in {Ms} ms: no answer, nothing decided on it", watch.ElapsedMilliseconds);
			return false;
		}

		string? against;
		lock (_lock)
		{
			against = known.Check ?? (_adoptedCheck is { } adopted && adopted.Key == key ? adopted.Check : null);
			if (against is null)
			{
				// BH-171-m5: the index was read when the check didn't answer: nothing to compare with, so not a change (it read
				// every title at the next start); this answer is what later checks are compared with
				_adoptedCheck = (key, now);
			}
		}

		var changed = against is not null && now != against;
		_logger.LogInformation("Library index: the server's check in {Ms} ms: {Outcome}", watch.ElapsedMilliseconds,
			against is null ? "not known before, kept to compare with" : changed ? "the libraries changed" : "unchanged");
		if (!changed)
		{
			return false;
		}

		lock (_lock)
		{
			if (_time.GetUtcNow() - known.ReadAt < ChangesReadAfter || ReadLately(key, _time.GetUtcNow()))
			{
				_checkDue = true; // read lately: checked again later
				return false;
			}

			// check of 69002e14: what the next check is compared with if this read's own check doesn't answer (the older answer
			// made a change of one already read)
			_adoptedCheck = (key, now);
		}

		// review (Minor): the titles, the index keeping the day's date. 1.7.5 (fx1, BH-171-m5): only those saved since the last
		// read, when the counts say that's all that changed (else every title, as before)
		var (was, count) = (IndexRead.CountOf(against), IndexRead.CountOf(now));
		_ = Read(key, IndexRead.MayBeAdded(known, was, count) ? ReadKind.Added : ReadKind.Titles, (was, count, now));
		return true;
	}

	// one read at a time per user; a read for another user cancels the one running
	private Task<IndexSnapshot?> Read(string key, ReadKind kind = ReadKind.Whole, (int? Was, int? Now, string Check)? added = null)
	{
		Reads reads;
		string url;
		Guid user;
		long since;
		Task<IndexSnapshot?> first;
		lock (_lock)
		{
			if (_signedOut is { } gone && gone == key)
			{
				return Task.FromResult<IndexSnapshot?>(null); // BH-171-m7: signed out: nothing asked with the ended sign-in
			}

			if (key != CurrentKey)
			{
				// check of 69002e14: another user signed in since this was asked (m6's switch): it cancelled their read and read
				// a whole index with their sign-in, to throw it away
				return Task.FromResult<IndexSnapshot?>(null);
			}

			if (_reading is { IsCompleted: false } && _readingKey == key && _firstIn is { } running)
			{
				return running;
			}

			var previous = _memoryKey == key ? _memory : null;
			if (previous is null)
			{
				kind = ReadKind.Whole; // nothing to keep the date of, or to read the played episodes of
			}

			_readingCancel?.Cancel();
			_readingCancel = new CancellationTokenSource();
			_readingKey = key;
			_readingKind = kind;
			_stale = false;
			// 1.7.5 (fx1; stream I's review of 1.7.3): the older format is forgotten when a read of every title is in
			// (ReadFromServer), not when one begins: 1.7.3 cleared it here, so a failed upgrade read wasn't tried again until a
			// restart (1.7.2's index, without LibraryIds, stayed till then or the day's read)
			if (kind != ReadKind.Plays && _oldFormat)
			{
				_upgradeBegun = true;
			}

			if (kind != ReadKind.Plays)
			{
				_checkDue = false; // its titles answer the check (the played episodes alone don't)
			}

			var cancellation = _readingCancel.Token;
			url = _client.BaseUrl;
			user = _client.UserId;
			since = _files.Generation; // 1.6.1: a sign-out after this refuses the read's write-back
			// the re-review of the fix round: the note keeps the revision of the read that last finished (Revision is stamped when
			// this one is in, ThisVersionsReadIsIn): an upgrade read that fails or is closed part way doesn't hold the next start
			reads = (_reads.GetValueOrDefault(key) ?? new Reads(DateTimeOffset.MinValue, IndexPages.PageSize)) with { LastRead = _time.GetUtcNow() };
			_reads[key] = reads;
			var firstIn = new TaskCompletionSource<IndexSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
			_firstIn = first = firstIn.Task;
			var begun = reads;
			_reading = Task.Run(() => ReadFromServer(key, kind, url, user, since, previous, begun, firstIn, cancellation, added), CancellationToken.None);
		}

		return first;
	}

	// fix round's re-review (BH-173-M1): a read of this version is in (its index is of this revision): the note says so, and
	// from now on it holds an index of an older format for the hour (a 1.7.2 note, or one of a read that never finished, doesn't)
	private Task ThisVersionsReadIsIn(string key, string url, Guid user, long since)
	{
		Reads stamped;
		lock (_lock)
		{
			if (!_reads.TryGetValue(key, out var reads) || reads.Revision >= FormatRevision)
			{
				return Task.CompletedTask;
			}

			_reads[key] = stamped = reads with { Revision = FormatRevision };
		}

		return KeepReads(key, stamped, url, user, since);
	}

	// review M2, M3: when a read last began and the page sizes it left, on this PC (forgotten with the index on sign-out)
	private Task KeepReads(string key, Reads reads, string url, Guid user, long since) =>
		_readsFiles.Write(key, new JsonObject
		{
			["LastRead"] = reads.LastRead.ToString("O"),
			["TitlesPage"] = reads.TitlesPage, // 1.7.3: no PeoplePage (1.7.2 reads its default if it comes back)
			["Revision"] = reads.Revision, // fix round (BH-173-M1): which version began it
		}, _homeCache.LoginKey(url, user), since);

	// re-review (Minor): one load per user; a second start-up ask waits for it (it went on without the note, and read)
	private Task LoadReads(string key)
	{
		lock (_lock)
		{
			if (!_readsLoading.TryGetValue(key, out var loading))
			{
				_readsLoading[key] = loading = LoadReadsOnce(key);
			}

			return loading;
		}
	}

	private async Task LoadReadsOnce(string key)
	{
		await Task.Yield(); // not under the caller's lock
		if (ReadingNote is { } hold)
		{
			await hold();
		}
		if (await _readsFiles.Read(key).ConfigureAwait(false) is not { } saved)
		{
			return;
		}

		try
		{
			var kept = new Reads(DateTimeOffset.Parse((string)saved["LastRead"]!, System.Globalization.CultureInfo.InvariantCulture),
				(int?)saved["TitlesPage"] ?? IndexPages.PageSize, (int?)saved["Revision"] ?? 0);
			lock (_lock)
			{
				if (!_reads.TryGetValue(key, out var now) || now.LastRead < kept.LastRead)
				{
					_reads[key] = kept;
				}
			}
		}
		catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException)
		{
			_logger.LogInformation("Library index: ignoring a damaged note of its last read");
		}
	}

	private async Task ReadFromServer(string key, ReadKind kind, string url, Guid user, long since, IndexSnapshot? previous, Reads reads,
		TaskCompletionSource<IndexSnapshot?> firstIn, CancellationToken cancellationToken, (int? Was, int? Now, string Check)? counts = null)
	{
		var watch = Stopwatch.StartNew();
		bool Current() => !cancellationToken.IsCancellationRequested && key == CurrentKey;
		var read = new IndexRead(_client, _time, _logger) { TitlesPageSize = reads.TitlesPage, CheckTimeout = CheckTimeout };
		try
		{
			await KeepReads(key, reads, url, user, since).ConfigureAwait(false); // review M2: a restart within the hour begins no other

			if (kind == ReadKind.Plays && previous is not null)
			{
				// 1.7.3 (BH-171-M1's other half): only the played episodes are missing (they didn't come with the read): the
				// titles stay, and the index keeps its date
				var plays = await read.PlaysAsync(previous, cancellationToken).ConfigureAwait(false);
				if (!Current())
				{
					firstIn.TrySetResult(null);
					return;
				}

				if (plays is null)
				{
					_logger.LogInformation("Library index: the played episodes didn't come again after {Seconds:0.0} s: asked again within the hour", watch.Elapsed.TotalSeconds);
					firstIn.TrySetResult(previous);
					return;
				}

				var played = previous with { SeriesPlays = plays, PlaysDue = false };
				LastReadTook = watch.Elapsed;
				_logger.LogInformation("Library index: the played episodes are in: {Shows} shows, {Seconds:0.0} s", plays.Count, watch.Elapsed.TotalSeconds);
				await Publish(key, played, url, user, since).ConfigureAwait(false);
				firstIn.TrySetResult(played);
				return;
			}

			// 1.7.5 (fx1, BH-171-m5): a library change the check confirmed: the titles saved since the last read, merged in
			// (the index keeps the day's date and its played episodes); every title when that can't be all that changed
			if (kind == ReadKind.Added && previous is not null && counts is { } check)
			{
				var (merged, why) = await read.AddedAsync(previous, check.Was, check.Now, check.Check, cancellationToken).ConfigureAwait(false);
				if (!Current())
				{
					firstIn.TrySetResult(null);
					return;
				}

				if (merged is not null)
				{
					merged = merged with { Key = key };
					LastReadTook = watch.Elapsed;
					_logger.LogInformation("Library index: a library change read as the titles saved since the last read: {Titles} titles, {Seconds:0.0} s",
						merged.Entries.Count, watch.Elapsed.TotalSeconds);
					await Publish(key, merged, url, user, since).ConfigureAwait(false);
					firstIn.TrySetResult(merged);
					return;
				}

				_logger.LogInformation("Library index: a library change read as every title ({Why}), after {Seconds:0.0} s", why, watch.Elapsed.TotalSeconds);
				kind = EveryTitle(key);
			}
			else if (kind == ReadKind.Added)
			{
				kind = EveryTitle(key);
			}

			if (previous is null)
			{
				// a first read: its rows as its pages come, and how far it has got
				read.Progress += progress => Progress?.Invoke(this, progress);
				read.Partial += partial =>
				{
					if (Current())
					{
						partial = partial with { Key = key }; // BH-171-m6: whose it is
						lock (_lock)
						{
							_soFar = partial;
						}

						Changed?.Invoke(this, partial);
					}
				};
			}

			var titles = await read.TitlesAsync(cancellationToken).ConfigureAwait(false);
			if (titles is null || !Current())
			{
				firstIn.TrySetResult(null);
				return;
			}

			titles = titles with { Key = key }; // BH-171-m6: whose it is
			if (previous is not null)
			{
				// 1.7.3: the people it had are not lent any more (the people pass stopped): the new index has none
				if (read.PlaysFailed)
				{
					// BH-171-M1: the played episodes didn't come: the plays it had stay (Unfinished series went until the next read),
					// and (1.7.3) they are asked again within the hour (PlaysDue)
					titles = titles with { SeriesPlays = previous.SeriesPlays };
				}

				if (kind == ReadKind.Titles)
				{
					// re-review (Major B): a library change's read is not the day's: the index keeps the day's date, so the day's
					// whole read still comes on a server whose library changes every day
					titles = titles with { ReadAt = previous.ReadAt };
				}
			}

			LastReadTook = watch.Elapsed;
			_logger.LogInformation("Library index: {Titles} titles in {Libraries} libraries, played episodes {Plays}, {Seconds:0.0} s",
				titles.Entries.Count, titles.Libraries.Count, titles.PlaysDue ? "to ask again" : "in", watch.Elapsed.TotalSeconds);
			var saving = Publish(key, titles, url, user, since); // shown at once (Changed), then saved
			lock (_lock)
			{
				_oldFormat = false; // 1.7.5 (fx1): every title is in, of this format
			}

			await saving.ConfigureAwait(false);
			await ThisVersionsReadIsIn(key, url, user, since).ConfigureAwait(false);
			firstIn.TrySetResult(titles);
		}
		catch (OperationCanceledException)
		{
			firstIn.TrySetResult(null);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Library index: the read failed");
			firstIn.TrySetResult(null);
		}
		finally
		{
			// review M3: the page size this read left (smaller after a page ran out of time), for the next one
			if (read.TitlesPageSize != reads.TitlesPage)
			{
				Reads kept;
				lock (_lock)
				{
					kept = (_reads.GetValueOrDefault(key) ?? reads) with { TitlesPage = read.TitlesPageSize };
					_reads[key] = kept;
				}

				await KeepReads(key, kept, url, user, since).ConfigureAwait(false);
			}
		}
	}

	// 1.7.5 (fx1, BH-171-m5): a library change's read goes on as every title: R meanwhile is answered by it (MarkStale), as
	// by 1.7.3's change read; R during the titles added since alone is read whole after it
	private ReadKind EveryTitle(string key)
	{
		lock (_lock)
		{
			if (_readingKey == key && _readingKind == ReadKind.Added)
			{
				_readingKind = ReadKind.Titles;
				_stale = false; // R during the titles added since: this read of every title answers it
			}
		}

		return ReadKind.Titles;
	}

	// in memory and said (at once), then saved (in the background: a 25,000-title index takes a moment to write)
	private Task Publish(string key, IndexSnapshot snapshot, string url, Guid user, long since)
	{
		lock (_lock)
		{
			(_memoryKey, _memory) = (key, snapshot);
			_soFar = null;
		}

		Changed?.Invoke(this, snapshot);
		var login = _homeCache.LoginKey(url, user);
		return Task.Run(() => _files.Write(key, ToJson(snapshot), login, since));
	}

	/// <summary>
	/// The whole read: the media libraries, their index pages and the played episodes (1.7.3: no people pass). Null when the
	/// libraries or an index page could not be read; a failed played pass leaves Unfinished series out.
	/// </summary>
	internal static Task<IndexSnapshot?> ReadIndex(IJellyfinClient client, TimeProvider time, CancellationToken cancellationToken, ILogger? log = null) =>
		new IndexRead(client, time, log).TitlesAsync(cancellationToken);

	private void Drop()
	{
		lock (_lock)
		{
			// a sign-out or a removed login: nothing kept in memory for anyone until the next read
			_readingCancel?.Cancel();
			_checkCancel.Cancel(); // BH-171-m5: the check too
			_checkCancel = new CancellationTokenSource();
			_memoryKey = null;
			_memory = null;
			_soFar = null;
			_reads.Clear(); // review M2: their files go with the index's
			_upgradeBegun = false; // 1.7.5 (fx1): the next sign-in's index of an older format is read at once
			_readsLoading.Clear();
			_adoptedCheck = null;
		}

		Forgotten?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>A user signed out or a saved login was removed: what was made of an index (the presets) goes too.</summary>
	public event EventHandler? Forgotten;

	private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

	private JsonObject ToJson(IndexSnapshot snapshot) => new()
	{
		["Version"] = FormatVersion,
		["ReadAt"] = snapshot.ReadAt.ToString("O"),
		["HasPeople"] = snapshot.HasPeople,
		["Libraries"] = JsonSerializer.SerializeToNode(snapshot.Libraries, Json),
		["Entries"] = JsonSerializer.SerializeToNode(snapshot.Entries, Json),
		["SeriesPlays"] = JsonSerializer.SerializeToNode(snapshot.SeriesPlays, Json),
		["Check"] = snapshot.Check, // 1.7.1
		["PeopleDue"] = snapshot.PeopleDue, // 1.7.1 review M2
		["Revision"] = snapshot.BackingKnown ? FormatRevision : 0, // 1.7.3; fix round: an older index's data stays of its revision
		["PlaysDue"] = snapshot.PlaysDue, // 1.7.3 (BH-171-M1's other half)
	};

	// 1.7.1: the saved index read straight into its records (a 1.6.1 index of 34,001 titles is a 40 MB file)
	private sealed record Saved(int Version, string ReadAt, bool HasPeople, List<IndexLibrary>? Libraries, List<IndexEntry>? Entries, Dictionary<Guid, SeriesPlays>? SeriesPlays, string? Check,
		bool PeopleDue = false, int Revision = 0, bool PlaysDue = false);

	private async Task<IndexSnapshot?> Load(string key)
	{
		if (await _files.ReadBytes(key).ConfigureAwait(false) is not { } bytes)
		{
			return null;
		}

		try
		{
			var saved = await Task.Run(() => JsonSerializer.Deserialize<Saved>(bytes, Json)).ConfigureAwait(false);
			if (saved is null || saved.Version is < OldestFormat or > FormatVersion)
			{
				return null;
			}

			if (saved.Version < FormatVersion || saved.Revision < FormatRevision)
			{
				lock (_lock)
				{
					_oldFormat = true; // 1.7.1: shown, and read again behind it (1.7.3: an older revision too)
				}
			}

			return new IndexSnapshot(saved.Libraries ?? [], Once(saved.Entries ?? []), saved.SeriesPlays ?? [], saved.HasPeople,
				DateTimeOffset.Parse(saved.ReadAt, System.Globalization.CultureInfo.InvariantCulture))
			{
				Check = saved.Check, PeopleDue = saved.PeopleDue, PlaysDue = saved.PlaysDue, Key = key,
				BackingKnown = saved.Version == FormatVersion && saved.Revision >= FormatRevision, // fix round (BH-173-M1)
			};
		}
		catch (Exception ex)
		{
			_logger.LogInformation(ex, "Ignoring a saved library index");
			return null;
		}
	}

	// 1.7.2: a saved index keeps each title once (the first). 1.7.0 saved an index with a title twice (its read kept repeats),
	// and a builder keyed on the id threw on it, which took the Collections page down; every read since keeps each title once.
	// 1.7.3 (P0-2): a repeat's library is merged into the first's (1.7.0 kept one entry per library: dropping the repeat left
	// the title out of its other library's presets)
	private List<IndexEntry> Once(List<IndexEntry> entries)
	{
		var at = new Dictionary<Guid, int>(entries.Count);
		var once = new List<IndexEntry>(entries.Count);
		foreach (var entry in entries)
		{
			if (!at.TryGetValue(entry.Id, out var first))
			{
				at[entry.Id] = once.Count;
				once.Add(entry);
				continue;
			}

			var kept = once[first];
			Guid[] libraries = [.. LibrariesOf(kept).Concat(LibrariesOf(entry)).Distinct()];
			if (libraries.Length != kept.LibraryIds.Length)
			{
				once[first] = kept with { LibraryIds = libraries };
			}
		}

		var repeated = entries.Count - once.Count;
		if (repeated == 0)
		{
			return entries;
		}

		if (repeated == 1)
		{
			_logger.LogInformation("Library index: the saved one had 1 repeated title; each is kept once");
		}
		else
		{
			_logger.LogInformation("Library index: the saved one had {Repeated} repeated titles; each is kept once", repeated);
		}

		return once;
	}

	// 1.7.3: the libraries an entry is in, its first first (an index saved before 1.7.3 has LibraryId alone)
	private static IEnumerable<Guid> LibrariesOf(IndexEntry entry) => entry.LibraryIds.Prepend(entry.LibraryId);
	// end 1.7.2

	public void Dispose()
	{
		_subscription.Dispose();
		_readingCancel?.Cancel();
		_checkCancel.Cancel();
	}
}
