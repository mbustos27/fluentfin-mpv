using System.Net;
using FluentFin.Core.Mpv;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.Services;

/// <summary>
/// Nixie 1.5.1 (BH-150-M2): the signed-in client's watch on its own token, a handler under every API request. A 401 to a
/// request that carried this sign-in's token is checked once with <c>GET /Users/Me</c> (Jellyfin also answers 401 for
/// other reasons: Quick Connect turned off on the server); a 401 there too means the server no longer takes the token, and
/// <see cref="SignInState.MarkEnded"/> runs before the caller sees its 401, so its page already asks for a new sign-in.
/// A burst of 401s (Home's rows) shares one check; a check that fails to get an answer ends nothing. A request with an
/// older token (one from before a new sign-in) is not looked at.
/// </summary>
public sealed class SignInWatch(Func<string> baseUrl, Func<string> token, ILogger logger) : DelegatingHandler
{
	private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);
	private readonly object _gate = new();
	private Task<bool>? _checking;

	/// <summary>Tests: the checks made so far.</summary>
	internal int Checks { get; private set; }

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		var response = await base.SendAsync(request, cancellationToken);
		if (response.StatusCode != HttpStatusCode.Unauthorized || SignInState.Ended || Authorization(request) is not { } authorization ||
			!Carries(authorization, token()))
		{
			return response;
		}

		Task<bool> check;
		lock (_gate)
		{
			check = _checking ??= StillRefusedAsync(authorization);
		}

		var refused = await check;
		lock (_gate)
		{
			if (ReferenceEquals(_checking, check))
			{
				_checking = null;
			}
		}

		if (refused && SignInState.MarkEnded())
		{
			logger.LogWarning("[sign-in] {Server} no longer accepts this sign-in (401 to {Path}, and to /Users/Me): the pages ask to sign in again",
				BridgeRedaction.ServerForLog(baseUrl()), request.RequestUri?.AbsolutePath);
		}

		return response;
	}

	private async Task<bool> StillRefusedAsync(string authorization)
	{
		Checks++;
		using var timeout = new CancellationTokenSource(CheckTimeout);
		try
		{
			using var check = new HttpRequestMessage(HttpMethod.Get, baseUrl().TrimEnd('/') + "/Users/Me");
			check.Headers.TryAddWithoutValidation("Authorization", authorization);
			check.Headers.TryAddWithoutValidation("Accept", "application/json");
			using var answer = await base.SendAsync(check, timeout.Token);
			return answer.StatusCode == HttpStatusCode.Unauthorized;
		}
		catch (Exception ex)
		{
			logger.LogInformation("[sign-in] a 401 could not be checked ({Error}); the sign-in is taken as still good", ex.GetType().Name);
			return false;
		}
	}

	private static string? Authorization(HttpRequestMessage request) =>
		request.Headers.TryGetValues("Authorization", out var values) ? values.FirstOrDefault() : null;

	/// <summary>Whether the header carries <paramref name="token"/> (Jellyfin's <c>Token="..."</c>).</summary>
	internal static bool Carries(string authorization, string token) =>
		!string.IsNullOrEmpty(token) && authorization.Contains($"Token=\"{token}\"", StringComparison.Ordinal);
}
