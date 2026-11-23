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
										   ILogger<JellyfinAuthenticationService> logger) : IJellyfinAuthenticationService
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

	public async Task<bool> Authenticate(SavedServer server, QuickConnectResult result)
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

	public async Task<bool> Authenticate(SavedServer server, SavedUser user)
	{
		var success = await Authenticate(server.GetServerUrl(), user.Username, user.Password.Unprotect(settingsService.GetEntropyBytes())) is null;

		if (success)
		{
			titleBarViewModel.CurrentServer = server;
			SessionInfo.CurrentUserCrendentials = user;
		}

		return success;
	}

	public async Task<string?> Authenticate(SavedServer server, string username, string password, bool remember)
	{
		var error = await Authenticate(server.GetServerUrl(), username, password);

		if (error is null)
		{
			titleBarViewModel.CurrentServer = server;

			var user = new SavedUser
			{
				Username = username,
				Password = password.Protect(settingsService.GetEntropyBytes()),
			};
			SessionInfo.CurrentUserCrendentials = user;

			if (remember)
			{
				// replace rather than mutate so the Users collection change gets saved
				var existing = server.Users.FirstOrDefault(x => x.Username == username);
				if (existing is null)
				{
					server.Users.Add(user);
				}
				else
				{
					server.Users[server.Users.IndexOf(existing)] = user;
				}
			}

			setupNavigationService.NavigateTo<ShellViewModel>();
		}

		return error;
	}

	/// <returns><c>null</c> on success, otherwise a message describing why sign-in failed.</returns>
	private async Task<string?> Authenticate(string url, string username, string password)
	{
		try
		{
			var client = GetClient(url);
			var auth = await client.Users.AuthenticateByName.PostAsync(new AuthenticateUserByName
			{
				Username = username,
				Pw = password
			});

			if (auth is null)
			{
				return "The server returned no session.";
			}

			await FinishSignIn(url, auth);
			return null;
		}
		catch (Exception ex)
		{
			// 1.1.4 (states): the address as a log may show it (no user info, no query)
			logger.LogError(ex, "Sign-in to {Url} failed", BridgeRedaction.ServerForLog(url));
			HttpsProblem https;
			using (var diagnosis = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
			{
				https = IsSecureConnectionFailure(ex) ? await DiagnoseHttps(url, diagnosis.Token) : HttpsProblem.None;
			}

			return DescribeFailure(ex, url, https);
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

	private static JellyfinApiClient GetClient(string url)
	{
		var id = Core.InstallDeviceId.Current; // 1.5.1 fixes (BH-150-M2): this copy's id, not the PC's (sign-in and the server page alike)
		var settings = new JellyfinSdkSettings();
		settings.SetServerUrl(url);
		settings.Initialize(Core.AppIdentity.ClientName, Core.AppVersion.Current, Environment.MachineName, id);
		return new JellyfinApiClient(new JellyfinRequestAdapter(new JellyfinAuthenticationProvider(settings), settings));
	}
}
