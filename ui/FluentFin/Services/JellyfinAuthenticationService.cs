using System.Net.Security;
using System.Reflection;
using System.Security.Authentication;
using FluentFin.Contracts.Services;
using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using FluentFin.Helpers;
using FluentFin.ViewModels;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Kiota.Abstractions;

namespace FluentFin.Services;

public class JellyfinAuthenticationService(IJellyfinClient jellyfinClient,
										   ITitleBarViewModel titleBarViewModel,
										   ILocalSettingsService settingsService,
										   [FromKeyedServices(NavigationRegions.InitialSetup)] INavigationService setupNavigationService,
										   ILogger<JellyfinAuthenticationService> logger,
										   ISettings? settings = null) : IJellyfinAuthenticationService // 174-sessions: the saved servers (null: tests)
{

	public static Uri GetSplashScreen(string url)
	{
		var client = GetClient(url);

		return client.Branding.Splashscreen.ToGetRequestInformation(x =>
		{
			var query = x.QueryParameters;
			query.Format = Jellyfin.Sdk.Generated.Branding.Splashscreen.ImageFormat.Jpg;
		}).URI;
	}

	// fluentfin-mpv: cancellable, so the server page can give up on an address that does not answer (a 10 s timeout)
	public static async Task<(PublicSystemInfo? Info, string? Error)> GetPublicInfo(string url, CancellationToken cancellationToken = default)
	{
		try
		{
			var client = GetClient(url);
			return (await client.System.Info.Public.GetAsync(cancellationToken: cancellationToken), null);
		}
		catch (Exception ex)
		{
			// 1.1.4 (states): the technical detail goes to the log (the address without what a pasted user:password@ or
			// ?api_key= would add); the page says what happened and what to do
			Serilog.Log.Warning(ex, "The server page's address {Url} did not answer as a Jellyfin server", BridgeRedaction.ServerForLog(url));
			var https = IsSecureConnectionFailure(ex) ? await DiagnoseHttps(url, cancellationToken) : HttpsProblem.None;
			return (null, DescribeFailure(ex, url, https, serverPage: true));
		}
	}

	/// <summary>What a failed HTTPS connection was (<see cref="DiagnoseHttps"/>).</summary>
	internal enum HttpsProblem
	{
		/// <summary>Not looked into (not an HTTPS failure, or not an https:// address).</summary>
		None,

		/// <summary>The server sent a certificate that Windows does not trust (self-signed, wrong name, expired).</summary>
		Certificate,

		/// <summary>The port answers plain HTTP: https:// was typed for an http:// port.</summary>
		PlainHttp,

		/// <summary>Something else ended the secure connection (a hang-up, no common protocol, something in between).</summary>
		Other,
	}

	internal static bool IsSecureConnectionFailure(Exception ex) =>
		ex is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } or HttpRequestException { InnerException: AuthenticationException };

	/// <summary>
	/// 1.1.4 (states): why an https:// address failed. The exception alone does not say: a certificate Windows rejects and a
	/// port that speaks plain HTTP both end in an AuthenticationException, told apart only by its English message. So the
	/// address is asked again, twice at most, a few seconds each: over HTTPS with a certificate check that records whether a
	/// certificate was presented and rejected, then over plain HTTP on the same port. Never throws.
	/// </summary>
	internal static async Task<HttpsProblem> DiagnoseHttps(string url, CancellationToken cancellationToken = default)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host))
		{
			return HttpsProblem.None;
		}

		var rejected = false;
		try
		{
			using var handler = new SocketsHttpHandler
			{
				ConnectTimeout = TimeSpan.FromSeconds(4),
				SslOptions = new SslClientAuthenticationOptions
				{
					RemoteCertificateValidationCallback = (_, _, _, errors) =>
					{
						rejected |= errors != SslPolicyErrors.None;
						return false;
					},
				},
			};
			using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
			using var _ = await client.GetAsync(new Uri($"https://{uri.Authority}/"), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
		}
		catch (Exception)
		{
			// expected: the certificate check above always says no
		}

		if (rejected)
		{
			return HttpsProblem.Certificate;
		}

		try
		{
			using var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(4), AllowAutoRedirect = false };
			using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
			using var _ = await client.GetAsync(new Uri($"http://{uri.Authority}/"), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
			return HttpsProblem.PlainHttp;
		}
		catch (Exception)
		{
			return HttpsProblem.Other;
		}
	}

	/// <summary>The address with http:// in place of https:// (same host, port and path), for "use this instead".</summary>
	internal static string PlainHttpAddress(string url) =>
		Uri.TryCreate(url, UriKind.Absolute, out var uri)
			? new UriBuilder(uri) { Scheme = Uri.UriSchemeHttp, Port = uri.Port, UserName = "", Password = "", Query = "", Fragment = "" }.Uri.ToString().TrimEnd('/')
			: url;

	/// <summary>
	/// Why a request to the server page's address or a sign-in failed, and what to do, in words the page shows.
	/// 1.1.4 (states; layout audit #11): a refused password, an account the server will not sign in, a server error and an
	/// unreachable address each say what to do next, in plain words; the exception's own text (Kiota's "unexpected status
	/// code", Windows' "actively refused it") is never shown: the callers log it. On the server page (<paramref name="serverPage"/>)
	/// nothing has asked for a password yet: a 401 or 403 there comes from something in front of the server. An HTTPS failure
	/// says what <paramref name="https"/> found.
	/// </summary>
	internal static string DescribeFailure(Exception ex, string url, HttpsProblem https = HttpsProblem.None, bool serverPage = false) => ex switch
	{
		ApiException { ResponseStatusCode: 401 or 403 } refused when serverPage =>
			$"The server at {url} refused the connection ({refused.ResponseStatusCode}). Something in front of Jellyfin, such as a reverse proxy " +
			"or a sign-in gateway, may want a sign-in of its own, which this app can't give. Use an address that reaches Jellyfin directly, or ask the server's administrator.",
		ApiException { ResponseStatusCode: 401 } => "Wrong username or password. Check both (the password is case-sensitive) and try again, or sign in with Quick Connect.",
		ApiException { ResponseStatusCode: 403 } =>
			$"{url} doesn't let this account sign in (it may be disabled, or not allowed on this device). Ask the server's administrator.",
		ApiException { ResponseStatusCode: >= 500 } api =>
			$"{url} answered with an error ({api.ResponseStatusCode}): it may be starting up or have a problem. Wait a moment and try again; if it keeps happening, its owner may need to know.",
		ApiException api =>
			$"{url} answered with an error ({api.ResponseStatusCode}). Check that this is the address of the Jellyfin server you use, then try again.",
		HttpRequestException secure when IsSecureConnectionFailure(secure) => (https, secure.InnerException) switch
		{
			(HttpsProblem.PlainHttp, _) =>
				$"{url} answers plain HTTP on this port, not HTTPS. Use {PlainHttpAddress(url)} instead, or the server's HTTPS port.",
			(HttpsProblem.Certificate, _) or (HttpsProblem.None, AuthenticationException) =>
				$"The HTTPS certificate of {url} isn't trusted by Windows (for example a self-signed certificate). " +
				"Use the server's http:// address, or add its certificate to the Windows certificate store.",
			_ =>
				$"Couldn't set up a secure (HTTPS) connection to {url}. Check that this is the server's HTTPS address and port, " +
				"or use its http:// address, then try again.",
		},
		// 1.7.3 (rule 11): what the user can do on this PC (it said "Check that the server is running", and "check the server's logs" for a 5xx)
		HttpRequestException => $"Can't reach {url}. Check that the address and port are right and that this PC can reach it (VPN, firewall), then try again.",
		// fluentfin-mpv: the server page's timeout (or the HTTP client's own)
		OperationCanceledException => $"{url} didn't answer in time. Check the address and port, and that this PC can reach the server (VPN, firewall), then try again.",
		_ => $"Something went wrong talking to {url}. Try again; if it keeps happening, the app's log has the details.",
	};

	public async Task<QuickConnectResult?> GetQuickConnectCode(SavedServer server)
	{
		var client = GetClient(server.GetServerUrl());
		try
		{
			return await client.QuickConnect.Initiate.PostAsync();
		}
		catch
		{
			return null;
		}
	}

	public async Task<QuickConnectResult?> CheckQuickConnectStatus(SavedServer server, QuickConnectResult result)
	{
		var client = GetClient(server.GetServerUrl());
		try
		{
			return await client.QuickConnect.Connect.GetAsync(x =>
			{
				x.QueryParameters.Secret = result.Secret;
			});
		}
		catch
		{
			return null;
		}
	}

	public async Task<bool> Authenticate(SavedServer server, QuickConnectResult result, bool remember)
	{
		var url = server.GetServerUrl();
		var client = GetClient(url);
		try
		{
			var auth = await client.Users.AuthenticateWithQuickConnect.PostAsync(new QuickConnectDto
			{
				Secret = result.Secret,
			});

			if (auth is not null)
			{
				SessionInfo.CurrentUserCrendentials = null;
				await FinishSignIn(url, auth);
				// Seaglass 1.1.4: as the password sign-ins do: the Account menu and the messages name the server
				titleBarViewModel.CurrentServer = server;
				// 174-signin: "Remember me" keeps it, as a password sign-in's: nothing was saved, so every start asked again. Its
				// password is an empty one, protected: 1.7.3 and earlier read it at the start (an empty array threw there).
				// 174-sessions: off, the next start signs in as nobody (the sign-in page forgets a remembered one, BS-m1)
				if (auth.User?.Name is { Length: > 0 } name)
				{
					SavedUser? keep = null;
					if (remember && auth.AccessToken is { Length: > 0 } token)
					{
						var entropy = settingsService.GetEntropyBytes();
						keep = new SavedUser { Username = name, Password = "".Protect(entropy), Token = token.Protect(entropy) };
					}

					SignedIn(server, name, keep);
				}

				setupNavigationService.NavigateTo<ShellViewModel>();
			}

			return auth is not null;
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Quick Connect sign-in to {Url} failed", BridgeRedaction.ServerForLog(url));
			return false;
		}
	}

	public string? SavedSignInProblem { get; private set; }

	public bool SavedSignInRefused { get; private set; }

	public Task<bool> Authenticate(SavedServer server, SavedUser user) => Authenticate(server, user, CancellationToken.None);

	public async Task<bool> Authenticate(SavedServer server, SavedUser user, CancellationToken cancellationToken)
	{
		SavedSignInProblem = null;
		SavedSignInRefused = false;

		// 174-signin: a saved Quick Connect sign-in goes on with its token (there is no password to send)
		if (user.Token.Length > 0)
		{
			return await Resume(server, user, cancellationToken);
		}

		var (error, refusal) = await Authenticate(server.GetServerUrl(), user.Username, user.Password.Unprotect(settingsService.GetEntropyBytes()), cancellationToken);
		var success = error is null;

		if (success)
		{
			titleBarViewModel.CurrentServer = server;
			SessionInfo.CurrentUserCrendentials = user;
			SignedInAgain(server, user);
		}
		else if (refusal == 401)
		{
			// 174-sessions (BS-M4): a saved password the server refuses is never sent again by itself. Jellyfin disables an
			// account after 3 failed sign-ins (5 for an administrator) and only the server's administrator can turn it on again;
			// every start sent it once, and so did Sign in again. Forgotten, the name kept: the sign-in page asks once
			Forget(server, user.Username);
			SavedSignInRefused = true;
			SavedSignInProblem = NoLongerWorks(server);
		}
		else if (refusal == 403)
		{
			// the review's m1: Jellyfin answers 403 (the account disabled, remote access off, outside its allowed hours) only
			// once the password is right, and those don't count toward the lockout: the password stays, for the next start
			SavedSignInProblem = NotNow(server);
		}
		else
		{
			// 174-sessions (BS-M3): the reason, naming the server (the server list opened without a word); kept for the next start
			SavedSignInProblem = $"Couldn't sign in to {NameOf(server)}. {error}";
		}

		return success;
	}

	public async Task<string?> Authenticate(SavedServer server, string username, string password, bool remember)
	{
		var (error, _) = await Authenticate(server.GetServerUrl(), username, password, CancellationToken.None);

		if (error is null)
		{
			titleBarViewModel.CurrentServer = server;

			var user = new SavedUser
			{
				Username = username,
				Password = password.Protect(settingsService.GetEntropyBytes()),
			};
			SessionInfo.CurrentUserCrendentials = user;

			// 174-signin: one saved user whatever the case of the name. 174-sessions: "Remember me" off, the next start signs in
			// as nobody (the sign-in page forgets a remembered one when its box is unticked for it, BS-m1)
			SignedIn(server, username, remember ? user : null);

			setupNavigationService.NavigateTo<ShellViewModel>();
		}

		return error;
	}

	/// <returns><c>null</c> on success, otherwise a message describing why sign-in failed; a refusal: the server said no (401, 403).</returns>
	private async Task<(string? Error, int? Refusal)> Authenticate(string url, string username, string password, CancellationToken cancellationToken)
	{
		try
		{
			var client = GetClient(url);
			var auth = await client.Users.AuthenticateByName.PostAsync(new AuthenticateUserByName
			{
				Username = username,
				Pw = password
			}, cancellationToken: cancellationToken);

			if (auth is null)
			{
				return ("The server returned no session.", null);
			}

			await FinishSignIn(url, auth);
			return (null, null);
		}
		catch (Exception ex)
		{
			// 1.1.4 (states): the address as a log may show it (no user info, no query)
			logger.LogError(ex, "Sign-in to {Url} failed", BridgeRedaction.ServerForLog(url));
			return (await Describe(ex, url, cancellationToken), ex is ApiException { ResponseStatusCode: 401 or 403 } refused ? refused.ResponseStatusCode : null);
		}
	}

	/// <summary>
	/// Why a sign-in failed, in the page's words (<see cref="DescribeFailure"/>); an HTTPS failure is looked into first, for
	/// up to 10 s and no longer than <paramref name="cancellationToken"/> allows (174-sessions: the start's bound).
	/// </summary>
	private static async Task<string> Describe(Exception ex, string url, CancellationToken cancellationToken)
	{
		HttpsProblem https;
		using (var diagnosis = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
		{
			diagnosis.CancelAfter(TimeSpan.FromSeconds(10));
			https = IsSecureConnectionFailure(ex) ? await DiagnoseHttps(url, diagnosis.Token) : HttpsProblem.None;
		}

		return DescribeFailure(ex, url, https);
	}

	/// <summary>The server as the messages name it: its name, else its address's host (and port).</summary>
	private static string NameOf(SavedServer server) => LoginViewModel.ServerNameOf(server) is { Length: > 0 } name ? name : "the server";

	/// <summary>174-signin: a saved sign-in the server refused (a token, 174-sessions: or a password).</summary>
	internal static string NoLongerWorks(SavedServer server) => $"The saved sign-in to {NameOf(server)} no longer works. Sign in again.";

	/// <summary>
	/// 174-sessions (the review's m1): a 403 to a saved sign-in: the server's own rule for the account (disabled, remote
	/// access off, outside its allowed hours), nothing this PC can change.
	/// </summary>
	internal static string NotNow(SavedServer server) =>
		$"{NameOf(server)} doesn't let this account sign in right now. Try again later; if it keeps happening, the server's owner may need to know.";

	/// <summary>
	/// 174-sessions: what a sign-in that worked leaves on this PC. <paramref name="keep"/> ("Remember me") is saved for the
	/// next start, which signs in to the server signed in to last (BS-M2); none leaves the saved sign-ins as they are, and the
	/// next start signs in as nobody (the review's Major and m2).
	/// The server is saved again when it isn't (BS-m2: removed in Settings while signed in to it, "sign in to add it again"
	/// left none). A settings file that can't be written doesn't undo the sign-in: it is logged.
	/// </summary>
	private void SignedIn(SavedServer server, string userName, SavedUser? keep)
	{
		try
		{
			if (settings is { } saved && !saved.Servers.Contains(server) && !saved.Servers.Any(x => x.Id.Length > 0 && x.Id == server.Id))
			{
				saved.Servers.Add(server);
			}

			SavedSignIns.SignedIn(settings?.Servers ?? [server], server, userName, keep is not null);
			if (keep is not null)
			{
				SavedSignIns.Remember(server, keep); // its change saves the server list, the last sign-in with it
			}

			settings?.SaveServerDetails();
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "The sign-in could not be saved in the settings");
		}
	}

	/// <summary>174-sessions (BS-M2): a saved sign-in used again (the start, Sign in again) is the last one, when it is saved.</summary>
	private void SignedInAgain(SavedServer server, SavedUser user)
	{
		if (!server.Users.Any(x => SavedSignIns.CanSignIn(x) && string.Equals(x.Username.Trim(), user.Username.Trim(), StringComparison.OrdinalIgnoreCase)))
		{
			return; // a sign-in without "Remember me", signed in again: still not remembered
		}

		try
		{
			SavedSignIns.SignedIn(settings?.Servers ?? [server], server, user.Username, kept: true);
			settings?.SaveServerDetails();
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "The last sign-in could not be saved in the settings");
		}
	}

	/// <summary>
	/// 174-sessions (BS-M4): a saved sign-in the server refused goes (its password or its token; the name stays as the
	/// server's last user, for the sign-in page), and the session's copy of it, so nothing sends it again.
	/// </summary>
	private void Forget(SavedServer server, string userName)
	{
		if (SessionInfo.CurrentUserCrendentials is { } current && string.Equals(current.Username.Trim(), userName.Trim(), StringComparison.OrdinalIgnoreCase))
		{
			SessionInfo.CurrentUserCrendentials = null!;
		}

		try
		{
			SavedSignIns.Forget(server, userName);
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "The refused saved password could not be removed from the settings");
		}
	}

	/// <summary>
	/// 174-signin: a saved Quick Connect sign-in (<see cref="SavedUser.Token"/>), at a start: the server says whose the token
	/// is (GET /Users/Me) and the session goes on with it, as a password sign-in's does. A token the server refuses (401:
	/// signed out on another device, the device removed; 174-sessions: a 403 keeps it) is forgotten, and <see cref="SavedSignInProblem"/> says so
	/// (the server list opened without a word, and every start tried the dead token again; 174-sessions: on the server's
	/// sign-in page, <see cref="SavedSignInRefused"/>). No answer (the network, the server down) is no sign-in either, but the
	/// token stays for the next start (174-sessions: and the server list says why).
	/// </summary>
	private async Task<bool> Resume(SavedServer server, SavedUser user, CancellationToken cancellationToken)
	{
		var url = server.GetServerUrl();
		try
		{
			var token = user.Token.Unprotect(settingsService.GetEntropyBytes());
			var me = await GetClient(url, token).Users.Me.GetAsync(cancellationToken: cancellationToken);
			if (me?.Id is not { } userId)
			{
				SavedSignInProblem = $"Couldn't sign in to {NameOf(server)}. Try again; if it keeps happening, the app's log has the details."; // 174-sessions (BS-M3)
				return false;
			}

			SessionInfo.CurrentUserCrendentials = null; // as after Quick Connect: no password (Jellyseerr, Sign in again)
			await FinishSignIn(url, new AuthenticationResult { AccessToken = token, User = me, ServerId = me.ServerId ?? server.Id });
			titleBarViewModel.CurrentServer = server;
			SignedInAgain(server, user);
			_ = FindSessionId(url, token, userId); // not waited for: the first screen does not show it
			return true;
		}
		catch (ApiException notNow) when (notNow.ResponseStatusCode is 403)
		{
			// 174-sessions (the review's m1): the token is good, the account isn't let in now (disabled, remote access off,
			// outside its hours): kept for the next start, as a password is
			logger.LogWarning("The server at {Url} doesn't let the saved Quick Connect sign-in's account in now (403); it is kept", BridgeRedaction.ServerForLog(url));
			SavedSignInProblem = NotNow(server);
			return false;
		}
		catch (ApiException refused) when (refused.ResponseStatusCode is 401)
		{
			logger.LogWarning("The server at {Url} refused the saved Quick Connect sign-in ({Status}); it is forgotten", BridgeRedaction.ServerForLog(url), refused.ResponseStatusCode);
			try
			{
				SavedSignIns.ForgetToken(server, user.Username);
			}
			catch (Exception ex)
			{
				logger.LogWarning(ex, "The refused saved sign-in could not be removed from the settings");
			}

			SavedSignInRefused = true; // 174-sessions: the sign-in page next, with the name
			SavedSignInProblem = NoLongerWorks(server);
			return false;
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "The saved Quick Connect sign-in to {Url} didn't work; it is kept for the next start", BridgeRedaction.ServerForLog(url));
			SavedSignInProblem = $"Couldn't sign in to {NameOf(server)}. {await Describe(ex, url, cancellationToken)}"; // 174-sessions (BS-M3): it said nothing
			return false;
		}
	}

	/// <inheritdoc/>
	public async Task EndKeptSessions(SavedServer server, IEnumerable<SavedUser> users)
	{
		var url = server.GetServerUrl();
		foreach (var user in users.Where(x => x.Token.Length > 0).ToList())
		{
			try
			{
				var token = user.Token.Unprotect(settingsService.GetEntropyBytes());
				using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
				await GetClient(url, token).Sessions.Logout.PostAsync(cancellationToken: bound.Token);
				logger.LogInformation("[sign-in] a saved sign-in's kept session on {Url} ended before it is forgotten", BridgeRedaction.ServerForLog(url));
			}
			catch (Exception ex)
			{
				logger.LogWarning("[sign-in] a saved sign-in's kept session on {Url} couldn't be ended ({Error}); it is forgotten all the same", BridgeRedaction.ServerForLog(url), ex.GetType().Name);
			}
		}
	}

	/// <summary>
	/// 174-signin: this device's session on the server, which a sign-in's answer carries and a resumed one's has none of (Play
	/// On leaves it out of its list). Never throws; nothing when it can't be read or the sign-in changed meanwhile.
	/// </summary>
	private async Task FindSessionId(string url, string token, Guid userId)
	{
		try
		{
			var device = Core.InstallDeviceId.Current;
			var sessions = await GetClient(url, token).Sessions.GetAsync(x => x.QueryParameters.DeviceId = device);
			if (sessions?.FirstOrDefault(x => x.UserId == userId)?.Id is { Length: > 0 } id && SessionInfo.AccessToken == token)
			{
				SessionInfo.SessionId = id;
			}
		}
		catch (Exception ex)
		{
			logger.LogDebug(ex, "This device's session on {Url} could not be read", BridgeRedaction.ServerForLog(url));
		}
	}

	/// <summary>
	/// fluentfin-mpv (startup-perf review): FluentFin's session first, then the user in the title bar. The user is what shows
	/// search and the account menu (Log out, Switch user), and the window is shown before an automatic sign-in finishes
	/// (ActivationService): a sign-out in that gap revoked the token, then the sign-in opened the shell on it anyway. Set
	/// last, the menu exists only once the shell is about to open (the callers navigate right after, with no wait between).
	/// A sign-in that fails leaves no user behind.
	/// </summary>
	internal async Task FinishSignIn(string url, AuthenticationResult auth)
	{
		await jellyfinClient.Initialize(url, auth);
		titleBarViewModel.User = auth.User;
	}

	private static JellyfinApiClient GetClient(string url, string? token = null)
	{
		var id = Core.InstallDeviceId.Current; // 1.5.1 fixes (BH-150-M2): this copy's id, not the PC's (sign-in and the server page alike)
		var settings = new JellyfinSdkSettings();
		settings.SetServerUrl(url);
		settings.Initialize(Core.AppIdentity.ClientName, Core.AppVersion.Current, Environment.MachineName, id);
		if (token is not null)
		{
			settings.SetAccessToken(token); // 174-signin: a saved Quick Connect sign-in (Resume)
		}
		return new JellyfinApiClient(new JellyfinRequestAdapter(new JellyfinAuthenticationProvider(settings), settings));
	}
}
