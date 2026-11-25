using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Journal;
using FluentFin.Core.Mpv;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using ReactiveUI;

namespace FluentFin.Core.Services;

/// <summary>sg-cw: what a play the mpv player just ended does to the item, by the server's rules (<see cref="ResumeRules"/>).</summary>
public enum PlayGuess
{
	/// <summary>Not known (no runtime, or no position seen): nothing is guessed, the server's answer is waited for.</summary>
	Unknown,

	/// <summary>A resume point (by default 5 to 90 % of an item of 5 minutes or more): in Continue Watching, at its new place.</summary>
	Resumable,

	/// <summary>Watched (past 90 %, to the end, or played past 5 % of a short item): out of Continue Watching and Next Up.</summary>
	Watched,

	/// <summary>Stopped before 5 %: no resume point (the server clears the one it had).</summary>
	NoResumePoint,
}

/// <summary>
/// sg-cw: the server's rules for a resume point (its ServerConfiguration, which an ordinary user may read: QA servers 10.11.11
/// and 12.4.0): under <see cref="MinPercent"/> no resume point, over <see cref="MaxPercent"/> watched, and an item shorter than
/// <see cref="MinDurationTicks"/> watched once started past <see cref="MinPercent"/>.
/// </summary>
public sealed record ResumeRules(double MinPercent, double MaxPercent, long MinDurationTicks)
{
	/// <summary>Jellyfin's defaults: 5 %, 90 %, 5 minutes.</summary>
	public static ResumeRules Default { get; } = new(5, 90, TimeSpan.FromMinutes(5).Ticks);

	public static ResumeRules From(ServerConfiguration? configuration) => configuration is null
		? Default
		: new(configuration.MinResumePct ?? Default.MinPercent, configuration.MaxResumePct ?? Default.MaxPercent,
			configuration.MinResumeDurationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds).Ticks : Default.MinDurationTicks);
}

/// <summary>
/// sg-cw: a play the mpv player just ended, as the UI saw it from the backend's events (docs/BRIDGE.md §5): where it stopped
/// (the latest <c>position</c> of its play session). Once the backend is done with its Stopped report and the server shows the
/// play (or the wait for it is over), <see cref="Outcome"/> is the report's and <see cref="ServerData"/> the item's user data as
/// the server has it. The server and user are FluentFin's when the play session showed up (the ones Home shows); the rules,
/// the server's when it could be read (else Jellyfin's defaults).
/// </summary>
public sealed record RecentPlay(Guid ItemId, string PlaySessionId, long? PositionTicks, long? RuntimeTicks, string? EndedReason, string? Outcome,
	string? ServerUrl, Guid UserId, DateTimeOffset At, UserItemDataDto? ServerData = null, ResumeRules? Rules = null)
{
	/// <summary>How far the server's resume point may be from where the UI saw the play stop (position events come every ~0.5 s).</summary>
	internal static readonly long PositionTolerance = TimeSpan.FromSeconds(10).Ticks;

	/// <summary>The backend is done with its Stopped report and the server was asked what it made of it: what it answers now is what shows.</summary>
	public bool Reported => Outcome is not null;

	/// <summary>How far in it stopped (0-100), when known.</summary>
	public double? Percent => PositionTicks is { } position && RuntimeTicks is > 0 and var runtime ? Math.Clamp(100.0 * position / runtime, 0, 100) : null;

	/// <summary>What the server will make of it, by its rules (<see cref="Rules"/>; its answer wins all the same).</summary>
	public PlayGuess Guess
	{
		get
		{
			var rules = Rules ?? ResumeRules.Default;
			if (string.Equals(EndedReason, "eof", StringComparison.Ordinal))
			{
				return PlayGuess.Watched;
			}

			if (Percent is not { } percent || RuntimeTicks is not { } runtime)
			{
				return PlayGuess.Unknown;
			}

			if (percent < rules.MinPercent)
			{
				return PlayGuess.NoResumePoint;
			}

			if (percent > rules.MaxPercent || runtime < rules.MinDurationTicks)
			{
				return PlayGuess.Watched;
			}

			return PlayGuess.Resumable;
		}
	}

	/// <summary>The server's user data of the item shows this play (it has caught up with the Stopped report).</summary>
	public bool ShownBy(UserItemDataDto? data) => Guess switch
	{
		PlayGuess.Resumable => data?.PlaybackPositionTicks is { } ticks && PositionTicks is { } position && Math.Abs(ticks - position) <= PositionTolerance,
		PlayGuess.Watched => data is { Played: true, PlaybackPositionTicks: null or 0 },
		PlayGuess.NoResumePoint => data is { PlaybackPositionTicks: null or 0 },
		_ => data is not null,
	};

	/// <summary>A play of this server and user (Home shows only its own).</summary>
	public bool IsOf(string? serverUrl, Guid userId) =>
		userId == UserId && MpvPlaybackService.SameServer(ServerUrl, serverUrl);
}

/// <summary>
/// sg-cw: the plays the mpv player just ended, so what shows them is up to date the moment the player closes. The UI never
/// reports playback (the backend does, docs/BRIDGE.md §6); this only listens to the backend's events:
/// <list type="bullet">
/// <item><c>ended</c>: <see cref="Changes"/> says where the play stopped (not yet <see cref="RecentPlay.Reported"/>): Home moves the
/// item at once (<see cref="ViewModels.HomeViewModel"/>);</item>
/// <item><c>reported</c>, <c>sent</c>: the item is read from the server, again after <see cref="CatchUpRetries"/> while its user data
/// does not show the play yet (a server that has not caught up), then <see cref="Changes"/> says it is reported, with the
/// server's user data, which every surface showing the item gets too (<see cref="ItemUserData.Publish"/>: cards, the series
/// and season pages' Play and "Up next"). Another outcome (the server never got it): reported at once, nothing read.</item>
/// </list>
/// Home's view model is made after the player is left (the Back navigation runs the new page first), so the plays of the
/// last <see cref="KeptFor"/> are kept for it (<see cref="Recent"/>). Thread-safe: events come from the backend's reader thread.
/// </summary>
public sealed class RecentPlays : IDisposable
{
	public static readonly TimeSpan KeptFor = TimeSpan.FromMinutes(1);

	private const int MaxSessions = 32;

	private readonly IJellyfinClient _client;
	private readonly TimeProvider _time;
	private readonly IScheduler? _ui;
	private readonly ILogger<RecentPlays>? _logger;
	private readonly Subject<RecentPlay> _changes = new();
	private readonly Lock _lock = new();
	private readonly Lock _publishing = new(); // reader threads and catch-up reads: one OnNext at a time
	private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
	private readonly List<RecentPlay> _recent = [];
	/// <summary>1.7.5 (BH-151-m8): plays whose Stopped report the server refused (the sign-in ended mid-playback), by session.</summary>
	private readonly Dictionary<string, RecentPlay> _owed = new(StringComparer.Ordinal);
	private readonly IDisposable _subscription;
	private Session? _current;
	private Task _catchingUp = Task.CompletedTask;

	public RecentPlays(IMpvBackend backend, IJellyfinClient client, ILogger<RecentPlays> logger)
		: this(backend.Events, client, TimeProvider.System, RxSchedulers.MainThreadScheduler, logger)
	{
	}

	/// <param name="ui">Where <see cref="ItemUserData.Publish"/> is raised (the UI thread); null: not raised.</param>
	internal RecentPlays(IObservable<BridgeEvent> events, IJellyfinClient client, TimeProvider time, IScheduler? ui, ILogger<RecentPlays>? logger = null)
	{
		_client = client;
		_time = time;
		_ui = ui;
		_logger = logger;
		_subscription = events.Subscribe(Handle);
		SignInState.Changed += OnSignInChanged;
		ItemUserData.Changed += OnUserDataChanged;
	}

	/// <summary>1.7.5 (BH-151-m8): how long a refused play's place is kept for a new sign-in (a later one doesn't write an old place).</summary>
	internal static readonly TimeSpan OwedFor = TimeSpan.FromHours(12);

	/// <summary>Completes when the places sent after the last new sign-in are written (tests).</summary>
	internal Task SendingOwed { get; private set; } = Task.CompletedTask;

	/// <summary>How long to wait before reading the item again while the server does not show the play (then its answer wins).</summary>
	internal IReadOnlyList<TimeSpan> CatchUpRetries { get; set; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

	/// <summary>A play ended (where it stopped), and again once it is reported (what the server has). From any thread.</summary>
	public IObservable<RecentPlay> Changes => _changes.AsObservable();

	/// <summary>Completes when the reads after the reports so far are done (tests).</summary>
	internal Task CatchingUp
	{
		get
		{
			lock (_lock)
			{
				return _catchingUp;
			}
		}
	}

	/// <summary>The plays of this server and user that ended in the last <see cref="KeptFor"/>, the latest of each item, oldest first.</summary>
	public IReadOnlyList<RecentPlay> Recent(string? serverUrl, Guid userId)
	{
		var since = _time.GetUtcNow() - KeptFor;
		lock (_lock)
		{
			return [.. _recent.Where(x => x.At >= since && x.IsOf(serverUrl, userId))
				.GroupBy(x => x.ItemId).Select(g => g.Last()).OrderBy(x => x.At)];
		}
	}

	public void Dispose()
	{
		SignInState.Changed -= OnSignInChanged;
		ItemUserData.Changed -= OnUserDataChanged;
		_subscription.Dispose();
		_changes.Dispose();
	}

	// never throws: it shares the backend's event stream with the player page, which a throw here would cut off
	private void Handle(BridgeEvent e)
	{
		try
		{
			Follow(e);
		}
		catch (Exception ex)
		{
			_logger?.LogWarning(ex, "The mpv event {Event} could not be followed for the rows the play changes", e.Name);
		}
	}

	private void Follow(BridgeEvent e)
	{
		RecentPlay? changed;
		try
		{
			lock (_lock)
			{
				changed = e.Name switch
				{
					Bridge.Events.Hello => Forget(),
					Bridge.Events.State => OnState(e),
					Bridge.Events.Position => OnPosition(e),
					Bridge.Events.Ended => OnEnded(e),
					Bridge.Events.Reported => OnReported(e),
					Bridge.Events.Error => OnError(e),
					_ => null,
				};
			}
		}
		catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
		{
			return; // an event this version cannot read changes nothing
		}

		if (changed is null)
		{
			return;
		}

		if (changed.Outcome == BridgeReported.Sent)
		{
			// the server took the report: reported once it shows it (CatchUpAsync)
			var catchUp = Task.Run(() => CatchUpAsync(changed));
			lock (_lock)
			{
				_catchingUp = _catchingUp.IsCompleted ? catchUp : Task.WhenAll(_catchingUp, catchUp);
			}
			return;
		}

		Emit(changed);
	}

	private void Emit(RecentPlay play)
	{
		lock (_lock)
		{
			var since = _time.GetUtcNow() - KeptFor;
			_recent.RemoveAll(x => x.At < since || x.PlaySessionId == play.PlaySessionId);
			_recent.Add(play);
		}

		lock (_publishing)
		{
			_changes.OnNext(play);
		}
	}

	// a new backend process: the sessions of the old one never get their ended or reported
	private RecentPlay? Forget()
	{
		_sessions.Clear();
		_current = null;
		return null;
	}

	private RecentPlay? OnState(BridgeEvent e)
	{
		if (e.DataAs<BridgeState>() is not { IsActive: true, PlaySessionId: { Length: > 0 } playSessionId } state || !Guid.TryParse(state.ItemId, out var itemId))
		{
			return null;
		}

		if (!_sessions.TryGetValue(playSessionId, out var session))
		{
			if (_sessions.Count >= MaxSessions)
			{
				_sessions.Remove(_sessions.Values.MinBy(x => x.Order)!.PlaySessionId);
			}

			session = _sessions[playSessionId] = new Session(playSessionId, itemId, _client.BaseUrl, _client.UserId) { Rules = Task.Run(ReadRulesAsync) };
			// 1.7.5 (the review of BH-151-m8): the item plays again: a place still owed from an earlier play is older than this one
			DropOwed(itemId);
		}

		if (state.DurationTicks is > 0)
		{
			session.RuntimeTicks = state.DurationTicks;
		}

		// an opening snapshot always says 0 (docs/BRIDGE.md §6): only a playing or paused one says where it is
		if (state.State is Bridge.States.Playing or Bridge.States.Paused && state.PositionTicks > 0 && session.EndedReason is null)
		{
			session.PositionTicks = state.PositionTicks;
		}

		_current = session;
		return null;
	}

	private RecentPlay? OnPosition(BridgeEvent e)
	{
		if (_current is not { EndedReason: null } session || e.Data is not JsonObject data)
		{
			return null;
		}

		if (data["durationTicks"]?.GetValueKind() == JsonValueKind.Number && data["durationTicks"]!.GetValue<long>() is > 0 and var duration)
		{
			session.RuntimeTicks = duration;
		}

		if (data["positionTicks"]?.GetValueKind() == JsonValueKind.Number && data["positionTicks"]!.GetValue<long>() is >= 0 and var position)
		{
			session.PositionTicks = position;
		}

		return null;
	}

	private RecentPlay? OnEnded(BridgeEvent e)
	{
		if (e.DataAs<BridgeEnded>() is not { } ended || !Guid.TryParse(ended.ItemId, out var itemId))
		{
			return null;
		}

		// the newest session of that item that has not ended
		if (_sessions.Values.Where(x => x.ItemId == itemId && x.EndedReason is null).MaxBy(x => x.Order) is not { } session)
		{
			return null;
		}

		session.EndedReason = ended.Reason ?? "";
		return session.Play(null, _time.GetUtcNow());
	}

	private RecentPlay? OnReported(BridgeEvent e)
	{
		if (e.DataAs<BridgeReported>() is not { PlaySessionId: { Length: > 0 } playSessionId } reported || !_sessions.Remove(playSessionId, out var session))
		{
			return null; // a session no state showed: nothing seen of it
		}

		if (ReferenceEquals(_current, session))
		{
			_current = null;
		}

		var play = session.Play(reported.Outcome ?? "", _time.GetUtcNow());
		if (session.Refused && reported.Outcome == BridgeReported.Failed && play.PositionTicks is not null)
		{
			// 1.7.5 (BH-151-m8): its place is sent after the user signs in again (SendOwedAsync)
			if (_owed.Count >= MaxSessions)
			{
				_owed.Remove(_owed.Values.MinBy(x => x.At)!.PlaySessionId);
			}

			_owed[play.PlaySessionId] = play;

			// 1.7.5 (the review): signed in again while it still played (the player's own Sign in again, a saved sign-in, then
			// Back): the stop was refused after the new sign-in came, so it is sent now, not at some later sign-in
			if (!SignInState.Ended && SignInState.Generation != session.RefusedGeneration)
			{
				StartSendingLocked();
			}
		}

		return play;
	}

	/// <summary>1.7.5 (the review of BH-151-m8): the places owed for <paramref name="itemId"/> are dropped (a newer play or mark).</summary>
	private void DropOwed(Guid itemId)
	{
		foreach (var key in _owed.Where(x => x.Value.ItemId == itemId).Select(x => x.Key).ToList())
		{
			_owed.Remove(key);
		}
	}

	// 1.7.5 (the review of BH-151-m8): a Mark watched or unwatched (or the server's newer answer) of an item whose place is owed:
	// the place would undo it (a watched mark or a resume point back), so it is dropped
	private void OnUserDataChanged(object? sender, ItemUserDataChangedEventArgs e)
	{
		if (e.Failed)
		{
			return;
		}

		lock (_lock)
		{
			DropOwed(e.ItemId);
		}
	}

	// 1.7.5 (BH-151-m8): the server refused the playing item's progress or Stopped report (auth_failed, docs/BRIDGE.md §5: once
	// per play session, 401 or 403): its Stopped may be refused too, and then its place is owed to the next sign-in
	private RecentPlay? OnError(BridgeEvent e)
	{
		if (e.DataAs<BridgeError>() is { Code: "auth_failed" } && _current is { } session)
		{
			session.Refused = true;
			session.RefusedGeneration = SignInState.Generation; // 1.7.5: the sign-in the refusal came under
		}

		return null;
	}

	// 1.7.5 (BH-151-m8): a new sign-in (Sign in again): the places of the plays it ended are sent, for the same server and user
	private void OnSignInChanged()
	{
		if (SignInState.Ended)
		{
			return;
		}

		lock (_lock)
		{
			if (_owed.Count == 0)
			{
				return;
			}

			StartSendingLocked();
		}
	}

	// under _lock
	private void StartSendingLocked()
	{
		var sending = Task.Run(SendOwedAsync);
		SendingOwed = SendingOwed.IsCompleted ? sending : Task.WhenAll(SendingOwed, sending);
	}

	/// <summary>
	/// 1.7.5 (BH-151-m8): after Sign in again, each play whose Stopped report the ended sign-in refused gets the place the player
	/// last saw, by the server's rules (<see cref="RecentPlay.Guess"/>): watched, its resume point, or none; with the time it
	/// stopped as its last play. A play of the item since (a later last play on the server) is never overwritten. It writes the
	/// item's user data (as Mark as watched does), not a playback report: the backend is the one reporter.
	/// </summary>
	private async Task SendOwedAsync()
	{
		List<RecentPlay> due;
		var since = _time.GetUtcNow() - OwedFor;
		lock (_lock)
		{
			due = [.. _owed.Values.Where(x => x.At >= since && x.IsOf(_client.BaseUrl, _client.UserId)).OrderBy(x => x.At)];
			foreach (var play in _owed.Values.Where(x => x.At < since || due.Contains(x)).ToList())
			{
				_owed.Remove(play.PlaySessionId);
			}
		}

		foreach (var play in due.GroupBy(x => x.ItemId).Select(g => g.Last()))
		{
			try
			{
				var watch = System.Diagnostics.Stopwatch.StartNew();
				var now = (await _client.GetItem(play.ItemId))?.UserData;
				if (now is null)
				{
					// not read: its watched state isn't known, so nothing is written over it
					_logger?.LogInformation("[plays] {ItemId} couldn't be read after the new sign-in: its refused stop's place isn't sent", play.ItemId);
					continue;
				}

				if (now.LastPlayedDate is { } last && last > play.At)
				{
					_logger?.LogInformation("[plays] {ItemId} was played again since its refused stop: its place is the server's", play.ItemId);
					continue;
				}

				var (played, position) = play.Guess switch
				{
					PlayGuess.Watched => (true, 0L),
					PlayGuess.Resumable => (now.Played ?? false, play.PositionTicks ?? 0),
					PlayGuess.NoResumePoint => (now.Played ?? false, 0L),
					_ => (false, -1L),
				};
				if (position < 0)
				{
					continue; // not known where it stopped: nothing guessed
				}

				var answer = await _client.SetPlace(play.ItemId, played, position, play.At);
				_logger?.LogInformation("[plays] the place of {ItemId}, whose stop the ended sign-in refused, {Result} after the new sign-in in {Ms} ms ({Guess})",
					play.ItemId, answer is null ? "couldn't be sent" : "was sent", watch.ElapsedMilliseconds, play.Guess);
				if (answer is not null && _ui is not null)
				{
					_ui.Schedule(() =>
					{
						if (play.IsOf(_client.BaseUrl, _client.UserId))
						{
							ItemUserData.Publish(play.ItemId, answer);
						}
					});
				}
			}
			catch (Exception ex)
			{
				_logger?.LogInformation("[plays] the place of {ItemId} couldn't be sent after the new sign-in: {Error}", play.ItemId, ex.GetType().Name);
			}
		}
	}

	// the server's resume rules, read as the play starts (a setting changed meanwhile counts); Jellyfin's defaults when they cannot be
	private async Task<ResumeRules> ReadRulesAsync()
	{
		try
		{
			return ResumeRules.From(await _client.GetConfiguration());
		}
		catch (Exception)
		{
			return ResumeRules.Default;
		}
	}

	// the server took the Stopped report: its user data of the item, read again while it does not show the play yet (at most
	// CatchUpRetries more times), then the play is reported with it, and every surface showing the item gets it (UI thread)
	private async Task CatchUpAsync(RecentPlay play)
	{
		UserItemDataDto? data = null;
		try
		{
			for (var attempt = 0; ; attempt++)
			{
				if (!play.IsOf(_client.BaseUrl, _client.UserId))
				{
					break; // signed in elsewhere since: not this server's to read
				}

				data = (await _client.GetItem(play.ItemId))?.UserData;
				if (play.ShownBy(data) || attempt >= CatchUpRetries.Count)
				{
					break;
				}

				_logger?.LogInformation("The server does not show the play of {ItemId} yet; asking again in {Delay}", play.ItemId, CatchUpRetries[attempt]);
				await Task.Delay(CatchUpRetries[attempt], _time);
			}
		}
		catch (Exception ex)
		{
			_logger?.LogInformation("The played item {ItemId} could not be read again after its stop report: {Error}", play.ItemId, ex.GetType().Name);
		}

		play = play with { ServerData = data };
		Emit(play);
		if (data is null || _ui is null)
		{
			return;
		}

		var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_ui.Schedule(() =>
		{
			try
			{
				if (play.IsOf(_client.BaseUrl, _client.UserId))
				{
					ItemUserData.Publish(play.ItemId, data);
				}
			}
			catch (Exception ex)
			{
				_logger?.LogWarning(ex, "Showing the played item {ItemId} as the server has it failed", play.ItemId);
			}
			finally
			{
				done.SetResult();
			}
		});
		await done.Task;
	}

	private sealed class Session(string playSessionId, Guid itemId, string? serverUrl, Guid userId)
	{
		private static long s_orders;

		public string PlaySessionId { get; } = playSessionId;

		public Guid ItemId { get; } = itemId;

		public long Order { get; } = Interlocked.Increment(ref s_orders);

		public long? PositionTicks { get; set; }

		public long? RuntimeTicks { get; set; }

		public string? EndedReason { get; set; }

		/// <summary>1.7.5 (BH-151-m8): the server refused its reports (<c>auth_failed</c>).</summary>
		public bool Refused { get; set; }

		/// <summary>1.7.5 (the review): <see cref="SignInState.Generation"/> when its reports were refused.</summary>
		public int RefusedGeneration { get; set; }

		public Task<ResumeRules>? Rules { get; init; }

		public RecentPlay Play(string? outcome, DateTimeOffset at) =>
			new(ItemId, PlaySessionId, PositionTicks, RuntimeTicks, EndedReason, outcome, serverUrl, userId, at,
				Rules: Rules is { IsCompletedSuccessfully: true } rules ? rules.Result : null);
	}
}
