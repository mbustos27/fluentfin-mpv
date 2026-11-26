using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FluentFin.Core.ViewModels;

/// <summary>fluentfin-mpv: where a page's data is (docs/research/ux-review.md §3.11).</summary>
public enum LoadStatus
{
	/// <summary>Nothing asked for yet.</summary>
	Idle,

	Loading,

	/// <summary>Loaded and there is something to show (<see cref="LoadState.HasProblem"/>: not all of it).</summary>
	Loaded,

	/// <summary>Loaded, and the server has nothing to show here.</summary>
	Empty,

	/// <summary>Nothing could be loaded.</summary>
	Failed,
}

/// <summary>How a load ended, with the words the page shows for it.</summary>
public readonly record struct LoadOutcome(LoadStatus Status, string Title = "", string Message = "")
{
	public static LoadOutcome Loaded { get; } = new(LoadStatus.Loaded);

	/// <summary>Something is shown, but part of the page could not be loaded.</summary>
	public static LoadOutcome Partly(string title, string message) => new(LoadStatus.Loaded, title, message);

	public static LoadOutcome Empty(string title, string message) => new(LoadStatus.Empty, title, message);

	public static LoadOutcome Failed(string title, string message) => new(LoadStatus.Failed, title, message);

	/// <summary>
	/// 1.5.1 fixes (BH-150-M2): the server no longer takes this sign-in (<see cref="SignInState.Ended"/>): the page asks for a new
	/// sign-in, in place of what it had (a failure, an empty page) or above what it shows (Home from its cache).
	/// </summary>
	public bool SignInNeeded { get; init; }

	/// <summary>1.5.1 fixes (BH-150-M2): <paramref name="outcome"/> once the sign-in has ended: the same place on the page, the sign-in's words.</summary>
	public static LoadOutcome SignedOut(LoadOutcome outcome) => outcome.Status switch
	{
		LoadStatus.Loaded => new(LoadStatus.Loaded, SignInState.Title(), SignInState.Message()) { SignInNeeded = true },
		_ => new(LoadStatus.Failed, SignInState.Title(), SignInState.Message()) { SignInNeeded = true },
	};
}

/// <summary>
/// fluentfin-mpv: the loading, empty and error state of a page or a part of one, with a Retry that runs the same load again.
/// Never blank, never stuck: a load that throws ends <see cref="LoadStatus.Failed"/>, and a load overtaken by a newer one
/// (a Retry, another navigation) is not shown at all.
/// </summary>
public sealed partial class LoadState : ObservableObject
{
	private readonly string _what;
	private Func<Task<LoadOutcome>>? _load;
	private int _run;

	/// <param name="what">What is loaded, for the default failure text ("this movie", "Home").</param>
	public LoadState(string what = "this page")
	{
		_what = what;
		RetryCommand = new AsyncRelayCommand(RetryAsync, () => _load is not null && Status != LoadStatus.Loading);
	}

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsLoaded), nameof(IsEmpty), nameof(IsFailed), nameof(HasMessage), nameof(HasProblem), nameof(RetryText))]
	public partial LoadStatus Status { get; private set; }

	/// <summary>1.5.1 fixes (BH-150-M2): the page asks for a new sign-in; its button is Sign in again (<see cref="LoadOutcome.SignedOut"/>).</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(RetryText))]
	public partial bool SignInNeeded { get; private set; }

	/// <summary>What happened (empty while loading or loaded without a problem).</summary>
	[ObservableProperty]
	public partial string Title { get; private set; } = "";

	/// <summary>What to do about it.</summary>
	[ObservableProperty]
	public partial string Message { get; private set; } = "";

	public bool IsLoading => Status == LoadStatus.Loading;

	public bool IsLoaded => Status == LoadStatus.Loaded;

	public bool IsEmpty => Status == LoadStatus.Empty;

	public bool IsFailed => Status == LoadStatus.Failed;

	/// <summary>The page shows a message in place of its content: nothing could be loaded, or there is nothing.</summary>
	public bool HasMessage => Status is LoadStatus.Empty or LoadStatus.Failed;

	/// <summary>Loaded, but not all of it: the page shows its content with a warning above it.</summary>
	public bool HasProblem => Status == LoadStatus.Loaded && !string.IsNullOrEmpty(Title);

	/// <summary>The Retry button's text: an empty page is refreshed, a failed one tried again, an ended sign-in signed in again.</summary>
	public string RetryText => SignInNeeded ? SignInState.ButtonText : Status == LoadStatus.Empty ? "Refresh" : "Try again";

	public IAsyncRelayCommand RetryCommand { get; }

	partial void OnStatusChanged(LoadStatus value) => RetryCommand.NotifyCanExecuteChanged();

	/// <summary>
	/// Runs <paramref name="load"/> and shows how it ended; Retry runs it again. A load that throws is a failure, in the words
	/// of what threw (<see cref="LoadMessages.Failed"/>). Only the newest run is shown: one that finishes after a newer run
	/// began changes nothing.
	/// </summary>
	public async Task RunAsync(Func<Task<LoadOutcome>> load)
	{
		_load = load;
		var run = ++_run;
		Set(new LoadOutcome(LoadStatus.Loading));

		LoadOutcome outcome;
		try
		{
			// 1.7.5: what the server answered to this load's requests (Services.AnswerTrace): a page that got null from the client
			// on a 5xx says the server's words (LoadMessages.CheckServer), not the connection's
			Services.AnswerTrace.Begin();
			outcome = await load();
		}
		// 1.7.3 (rule 11): the server's words only for the server's failures (an HTTP error, no answer in time: the HTTP client's
		// own timeout is a cancellation too, so it is let in here); anything else is Nixie's own (it said "Check that the
		// server is running" for every throw, 1.7.1's Collections page among them)
		catch (Exception ex) when (ex is not OperationCanceledException || LoadMessages.IsTimeout(ex))
		{
			outcome = LoadMessages.Failed(_what, ex);
		}
		catch (OperationCanceledException)
		{
			if (run == _run)
			{
				Set(new LoadOutcome(LoadStatus.Idle));
			}

			return;
		}

		if (run == _run)
		{
			// 1.5.1 fixes (BH-150-M2): the server refused the token (checked: SignInWatch) while this loaded, so what failed or came
			// back empty (or the cache Home showed) is not the server's answer: the page asks for a new sign-in
			var problem = outcome.Status is LoadStatus.Failed or LoadStatus.Empty || !string.IsNullOrEmpty(outcome.Title);
			Set(problem && SignInState.Ended ? LoadOutcome.SignedOut(outcome) : outcome);
		}
	}

	/// <summary>
	/// Nixie 1.7.4: <paramref name="load"/> run behind what the page shows (Back showed a kept page, and something may have
	/// changed since): no loading state at any point. A load that ends Loaded is shown as RunAsync shows it (a part that failed
	/// says so above the content); one that fails or throws leaves the page as it is (rule 1 and 11: the copy shown
	/// stays); an empty answer is the server's and is shown. A newer run (a Retry, R's page) wins, as with
	/// <see cref="RunAsync"/>; Retry still runs the page's own load.
	/// </summary>
	/// <returns>Whether the load ended Loaded or Empty and is the newest (what it changed is shown).</returns>
	public Task<bool> RefreshBehindAsync(Func<Task<LoadOutcome>> load) => RefreshBehindAsync(_ => load());

	/// <summary>
	/// 1.8.1 (BH-175-q2): <see cref="RefreshBehindAsync(Func{Task{LoadOutcome}})"/> for a load that writes what came into the page
	/// before it ends: it is given <c>newest</c>, true while no newer run (a later Back's refresh, a Retry, R's page) has begun,
	/// and writes only while it is. An older refresh whose answers came last overwrote a newer one's (Back, Forward and Back
	/// while the server was slow): only its own <see cref="Set"/> was checked.
	/// </summary>
	public async Task<bool> RefreshBehindAsync(Func<Func<bool>, Task<LoadOutcome>> load)
	{
		var run = ++_run;
		bool Newest() => run == _run;
		LoadOutcome outcome;
		try
		{
			outcome = await load(Newest);
		}
		catch (Exception ex) when (ex is not OperationCanceledException || LoadMessages.IsTimeout(ex))
		{
			outcome = LoadMessages.Failed(_what, ex);
		}
		catch (OperationCanceledException)
		{
			return false;
		}

		// (a sign-in the server no longer takes: what came back is not its answer, and the page keeps its copy; the title bar asks
		// for the sign-in, as it does everywhere)
		var problem = outcome.Status == LoadStatus.Empty || !string.IsNullOrEmpty(outcome.Title);
		if (run != _run || outcome.Status is not (LoadStatus.Loaded or LoadStatus.Empty) || (problem && SignInState.Ended))
		{
			return false;
		}

		Set(outcome);
		return true;
	}

	/// <summary>Forgets the shown state (the page was left): a load still running ends unseen.</summary>
	public void Reset()
	{
		_run++;
		Set(new LoadOutcome(LoadStatus.Idle));
	}

	private async Task RetryAsync()
	{
		// 1.5.1 fixes (BH-150-M2): Sign in again is the app's sign-in (SignInState.SignInAgain). Signed in again in place (a saved
		// sign-in), or by another page's button meanwhile, the page loads again; on the sign-in page, this page is gone.
		if (SignInNeeded && SignInState.Ended && SignInState.SignInAgain is { } signIn)
		{
			await signIn();
			if (SignInState.Ended)
			{
				return;
			}
		}

		if (_load is { } load)
		{
			await RunAsync(load);
		}
	}

	private void Set(LoadOutcome outcome)
	{
		Title = outcome.Title;
		Message = outcome.Message;
		SignInNeeded = outcome.SignInNeeded;
		Status = outcome.Status;
	}
}

/// <summary>fluentfin-mpv: the words of the loading and error states, in one place so every page says it the same way.</summary>
public static class LoadMessages
{
	/// <summary>The server FluentFin is signed in to, as the user knows it: its saved name, else its address.</summary>
	public static string ServerName()
	{
		if (!string.IsNullOrWhiteSpace(SessionInfo.ServerName))
		{
			return SessionInfo.ServerName;
		}

		return Uri.TryCreate(SessionInfo.BaseUrl, UriKind.Absolute, out var url) && !string.IsNullOrEmpty(url.Host)
			? url.IsDefaultPort ? url.Host : $"{url.Host}:{url.Port}"
			: "the server";
	}

	/// <summary>
	/// What to do when the server did not give the app what it asked for. 1.1.4 (states): the app's own name (it said
	/// "FluentFin"), and the button's words ("Try again" below it). 1.7.2 (the owner: "always remember im not a jellyfin
	/// server admin"): what the user can do on this PC, never a step on the server (it said "Check that the server is running").
	/// </summary>
	/// <remarks>
	/// 1.7.5 (stream B's review of 1.7.3): in a load whose server answered with an error of its own (a 5xx, and every request got
	/// an answer: <see cref="Services.AnswerTrace.ServersFault"/>), the server's words (<see cref="ServerHadAProblem"/>): the client
	/// answers null for a 5xx, so the page's "couldn't load" said the connection's.
	/// </remarks>
	public static string CheckServer() => Services.AnswerTrace.Current is { ServersFault: true }
		? ServerHadAProblem()
		: $"{AppIdentity.Name} couldn't get it from {ServerName()} just now. Check this PC's connection, then select Try again.";

	public static LoadOutcome CouldNotLoad(string what) => LoadOutcome.Failed($"Couldn't load {what}", CheckServer());

	/// <summary>
	/// 1.7.3 (rule 11): the server answered with an error of its own (5xx): the fault is on its side, so its owner is named,
	/// and what the user can do here comes first.
	/// </summary>
	public static string ServerHadAProblem()
	{
		var name = ServerName();
		return $"{(name == "the server" ? "The server" : name)} had a problem answering just now. Select Try again; if it keeps happening, its owner may need to know.";
	}

	/// <summary>
	/// 1.7.3 (rule 11): the failure was Nixie's own (a bug, a file it couldn't read): no word about the server, which answered
	/// or was never asked; what the user can do on this PC.
	/// </summary>
	public static string CouldNotShow() =>
		$"{AppIdentity.Name} couldn't show this page. Select Try again; if it keeps happening, restart {AppIdentity.Name}, or use Report a problem in Settings.";

	/// <summary>
	/// 1.7.3 fix round (BH-173-q2): the server's own no (403): its account rules, which only its owner changes; never "check
	/// this PC's connection", which answered.
	/// </summary>
	public static string NotAllowed()
	{
		var name = ServerName();
		return $"{(name == "the server" ? "The server" : name)} doesn't let your account open this. Only the server's owner can change that.";
	}

	/// <summary>1.7.3: where a load's failure came from, which says whose words it gets (<see cref="Failed"/>).</summary>
	public enum Cause
	{
		/// <summary>
		/// The server didn't answer, didn't answer in time, or turned the sign-in or the pace down (401, 407, 408, 429):
		/// <see cref="CheckServer"/>.
		/// </summary>
		Server,

		/// <summary>
		/// The server answered with a 5xx error, its own fault: <see cref="ServerHadAProblem"/>. The re-review: a 404 or 410 too
		/// (an address it doesn't have).
		/// </summary>
		ServerFault,

		/// <summary>
		/// Anything else: Nixie's own failure, <see cref="CouldNotShow"/>. Fix round (BH-173-q2): a 4xx not below (a 400: a
		/// request Nixie made that the server couldn't take) too.
		/// </summary>
		Local,

		/// <summary>Fix round (BH-173-q2): 403, the account may not: <see cref="NotAllowed"/>.</summary>
		NotAllowed,
	}

	/// <summary>
	/// 1.7.3: whose failure <paramref name="ex"/> is, looking through what wraps it: an HTTP error (Jellyfin's API, Flurl,
	/// HttpClient, a socket) or a timeout is the server's (a 5xx its own fault); anything else is Nixie's.
	/// </summary>
	public static Cause CauseOf(Exception ex)
	{
		var seen = 0;
		var pending = new Stack<Exception>();
		pending.Push(ex);
		while (pending.Count > 0 && seen++ < 32)
		{
			var e = pending.Pop();
			int? status = e switch
			{
				Microsoft.Kiota.Abstractions.ApiException api => api.ResponseStatusCode,
				Flurl.Http.FlurlHttpTimeoutException => 0,
				Flurl.Http.FlurlHttpException flurl => flurl.StatusCode ?? 0,
				HttpRequestException http => (int?)http.StatusCode ?? 0,
				HttpIOException or System.Net.Sockets.SocketException or System.Net.WebSockets.WebSocketException or TimeoutException => 0,
				_ => null,
			};

			// 1.7.5 (stream B's review of 1.7.3): a captive portal or a proxy's page answering 200 with HTML for the server: Kiota
			// has no reader for text/html (an InvalidOperationException), or the page came labelled JSON (a JsonException while
			// the answer was read). The connection, not Nixie's own ("couldn't show this page… restart Nixie"); a JsonException
			// from reading a file of Nixie's own stays Nixie's
			status ??= IsAnAnswerThatWasntTheServers(e) ? 0 : null;

			if (status is { } code)
			{
				// fix round (BH-173-q2): a 403 or a 404 isn't the connection (every 4xx but 401 got its words). The re-review: a 404
				// is the server's (an address an older server doesn't have, a reverse proxy's path), never "removed": every page asks
				// for its own item through calls that answer null, so a 404 that reaches here is never the item's
				return code switch
				{
					511 => Cause.Server, // 1.7.5: a captive portal's "sign in to this network first": the connection
					>= 500 and <= 599 or 404 or 410 => Cause.ServerFault,
					403 => Cause.NotAllowed,
					401 or 407 or 408 or 429 => Cause.Server,
					>= 400 and <= 499 => Cause.Local,
					_ => Cause.Server,
				};
			}

			if (e is AggregateException aggregate)
			{
				foreach (var inner in aggregate.InnerExceptions.Reverse())
				{
					pending.Push(inner);
				}
			}
			else if (e.InnerException is { } inner)
			{
				pending.Push(inner);
			}
		}

		return Cause.Local;
	}

	/// <summary>
	/// 1.7.5: <paramref name="e"/> came from reading an answer that wasn't the server's JSON: Kiota's refusal of its content type
	/// (the JSON reader's "expected a application/json content type", or the registry's "Content type text/html does not have a
	/// factory registered to be parsed"), or a <see cref="System.Text.Json.JsonException"/> thrown while Kiota (or Flurl) read an
	/// answer, or in a load whose trace saw such a page (<see cref="Services.AnswerTrace.SawNoAnswer"/>).
	/// </summary>
	internal static bool IsAnAnswerThatWasntTheServers(Exception e) => e switch
	{
		InvalidOperationException when e.Message.Contains("does not have a factory registered to be parsed", StringComparison.Ordinal) => true,
		ArgumentException { ParamName: "contentType" } => ThrownReadingAnAnswer(e),
		System.Text.Json.JsonException => Services.AnswerTrace.Current is { SawNoAnswer: true } || ThrownReadingAnAnswer(e),
		_ => false,
	};

	private static bool ThrownReadingAnAnswer(Exception e)
	{
		try
		{
			return new System.Diagnostics.StackTrace(e, false).GetFrames().Any(frame =>
				frame.GetMethod()?.DeclaringType?.Assembly.GetName().Name is { } assembly &&
				(assembly.StartsWith("Microsoft.Kiota", StringComparison.Ordinal) || assembly.StartsWith("Flurl", StringComparison.Ordinal) ||
				 assembly.StartsWith("Jellyfin.Sdk", StringComparison.Ordinal)));
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>
	/// 1.7.3: a cancellation that is a timeout (the HTTP client's own limit: a <see cref="TimeoutException"/> inside), not a
	/// page that stopped its load.
	/// </summary>
	public static bool IsTimeout(Exception ex)
	{
		for (var (e, depth) = (ex, 0); e is not null && depth < 32; (e, depth) = (e.InnerException, depth + 1))
		{
			if (e is TimeoutException)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>1.7.3 (rule 11): a load of <paramref name="what"/> that threw <paramref name="ex"/>, in its cause's words (<see cref="CauseOf"/>).</summary>
	public static LoadOutcome Failed(string what, Exception ex) => LoadOutcome.Failed($"Couldn't load {what}", CauseOf(ex) switch
	{
		Cause.Server => CheckServer(),
		Cause.ServerFault => ServerHadAProblem(),
		Cause.NotAllowed => NotAllowed(), // fix round (BH-173-q2)
		_ => CouldNotShow(),
	});

	/// <summary>
	/// 1.6.1 (BH-151-m2): what an action's failure says (a removal, a Save): <paramref name="otherwise"/>, unless the server no
	/// longer takes this sign-in (<see cref="SignInState.Ended"/>): then <paramref name="what"/> and the ended sign-in, not "check
	/// the server", which can't help.
	/// </summary>
	public static string ActionFailed(string what, string otherwise) =>
		SignInState.Ended ? $"{what}: your sign-in to {ServerName()} ended. Sign in again, then try again." : otherwise;
}
