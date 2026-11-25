using System.Net;

namespace FluentFin.Core.Services;

/// <summary>
/// Nixie 1.7.5 (stream B's review of 1.7.3): what the server answered to the requests one piece of work made (a page's load,
/// a toggle), so the words for a failure can be the right side's. <c>JellyfinClient</c> answers null for most failures (it
/// logs and swallows them), so a page whose server answered 500 said "Check this PC's connection"; only a throw that reached
/// <see cref="ViewModels.LoadState.RunAsync"/> got the server's words. <see cref="AnswerWatch"/>, under every API request,
/// notes each answer in the trace of the work it belongs to (an <see cref="AsyncLocal{T}"/>: it follows the work's awaits,
/// and two pages loading at once each have their own).
/// </summary>
public sealed class AnswerTrace
{
	private static readonly AsyncLocal<AnswerTrace?> s_current = new();
	private int _serverErrors;
	private int _noAnswers;

	/// <summary>The trace of the work this runs in, or null (none was begun).</summary>
	public static AnswerTrace? Current => s_current.Value;

	/// <summary>
	/// Begins a trace for the calling async method and what it awaits (its caller's flow doesn't see it: an async method's
	/// changes to an <see cref="AsyncLocal{T}"/> end with it).
	/// </summary>
	public static AnswerTrace Begin() => s_current.Value = new AnswerTrace();

	/// <summary>The server answered with an error of its own (a 5xx).</summary>
	public bool SawServerError => Volatile.Read(ref _serverErrors) > 0;

	/// <summary>A request got no answer, no answer in time, or an answer that wasn't the server's (a captive portal's page).</summary>
	public bool SawNoAnswer => Volatile.Read(ref _noAnswers) > 0;

	/// <summary>
	/// The failure was the server's own: it answered with a 5xx, and every request got an answer from it (a connection
	/// that failed too says the connection's words, the first thing the user can try).
	/// </summary>
	public bool ServersFault => SawServerError && !SawNoAnswer;

	internal void Answered(HttpStatusCode status, string? mediaType)
	{
		if (status == HttpStatusCode.NetworkAuthenticationRequired)
		{
			// 511: a captive portal (a hotel's or a café's Wi-Fi) wants its own sign-in first: the connection, not the server
			Interlocked.Increment(ref _noAnswers);
		}
		else if ((int)status is >= 500 and <= 599)
		{
			Interlocked.Increment(ref _serverErrors);
		}
		else if ((int)status is >= 200 and <= 299 && IsPage(mediaType))
		{
			// a captive portal or a proxy's sign-in page answering for the server: the connection, not the server
			Interlocked.Increment(ref _noAnswers);
		}
	}

	internal void NoAnswer() => Interlocked.Increment(ref _noAnswers);

	private static bool IsPage(string? mediaType) => mediaType is not null &&
		(mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) || mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Nixie 1.7.5: the handler under every API request that writes its answer into the work's <see cref="AnswerTrace"/>.</summary>
public sealed class AnswerWatch : DelegatingHandler
{
	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		var trace = AnswerTrace.Current;
		HttpResponseMessage response;
		try
		{
			response = await base.SendAsync(request, cancellationToken);
		}
		catch (Exception) when (trace is not null)
		{
			// no answer, or none in time (a page that stops its own load shows no words, so its cancellation counts too)
			trace.NoAnswer();
			throw;
		}

		trace?.Answered(response.StatusCode, response.Content?.Headers.ContentType?.MediaType);
		return response;
	}
}
