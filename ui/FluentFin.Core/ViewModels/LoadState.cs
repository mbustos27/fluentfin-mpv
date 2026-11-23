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
	/// Runs <paramref name="load"/> and shows how it ended; Retry runs it again. A load that throws is a failure with the
	/// default text. Only the newest run is shown: one that finishes after a newer run began changes nothing.
	/// </summary>
	public async Task RunAsync(Func<Task<LoadOutcome>> load)
	{
		_load = load;
		var run = ++_run;
		Set(new LoadOutcome(LoadStatus.Loading));

		LoadOutcome outcome;
		try
		{
			outcome = await load();
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			outcome = LoadOutcome.Failed($"Couldn't load {_what}", LoadMessages.CheckServer());
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
	public static string CheckServer() =>
		$"{AppIdentity.Name} couldn't get it from {ServerName()} just now. Check this PC's connection, then select Try again.";

	public static LoadOutcome CouldNotLoad(string what) => LoadOutcome.Failed($"Couldn't load {what}", CheckServer());

	/// <summary>
	/// 1.6.1 (BH-151-m2): what an action's failure says (a removal, a Save): <paramref name="otherwise"/>, unless the server no
	/// longer takes this sign-in (<see cref="SignInState.Ended"/>): then <paramref name="what"/> and the ended sign-in, not "check
	/// the server", which can't help.
	/// </summary>
	public static string ActionFailed(string what, string otherwise) =>
		SignInState.Ended ? $"{what}: your sign-in to {ServerName()} ended. Sign in again, then try again." : otherwise;
}
