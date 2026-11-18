using System.Reflection;
using System.Security.Authentication;
using DeviceId;
using FluentFin.Contracts.Services;
using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
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
			// 1.1.4 (states): the technical detail goes to the log; the page says what happened and what to do
			Serilog.Log.Warning(ex, "The server page's address {Url} did not answer as a Jellyfin server", url);
			return (null, DescribeFailure(ex, url));
		}
	}

	/// <summary>
	/// Why a request to the server page's address or a sign-in failed, and what to do, in words the page shows.
	/// 1.1.4 (states; layout audit #11): a refused password, an account the server will not sign in, a server error and an
	/// unreachable address each say what to do next, in plain words; the exception's own text (Kiota's "unexpected status
	/// code", Windows' "actively refused it") is never shown: the callers log it.
	/// </summary>
	internal static string DescribeFailure(Exception ex, string url) => ex switch
	{
		ApiException { ResponseStatusCode: 401 } => "Wrong username or password. Check both (the password is case-sensitive) and try again, or sign in with Quick Connect.",
		ApiException { ResponseStatusCode: 403 } =>
			$"{url} doesn't let this account sign in (it may be disabled, or not allowed on this device). Ask the server's administrator.",
		ApiException { ResponseStatusCode: >= 500 } api =>
			$"{url} answered with an error ({api.ResponseStatusCode}): it may be starting up or have a problem. Wait a moment and try again; if it keeps happening, check the server's logs.",
		ApiException api =>
			$"{url} answered with an error ({api.ResponseStatusCode}). Check that this is your Jellyfin server's address, then try again.",
		HttpRequestException { InnerException: AuthenticationException } =>
			$"The HTTPS certificate of {url} isn't trusted by Windows (for example a self-signed certificate). " +
			"Use the server's http:// address, or add its certificate to the Windows certificate store.",
		HttpRequestException => $"Can't reach {url}. Check that the server is running, that the address and port are right, and that this PC can reach it (VPN, firewall), then try again.",
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
				setupNavigationService.NavigateTo<ShellViewModel>();
			}

			return auth is not null;
		}
		catch
		{
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
			logger.LogError(ex, "Sign-in to {Url} failed", url);
			return DescribeFailure(ex, url);
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
		var id = new DeviceIdBuilder().OnWindows(windows => windows.AddWindowsDeviceId()).ToString();
		var settings = new JellyfinSdkSettings();
		settings.SetServerUrl(url);
		settings.Initialize(Core.AppIdentity.ClientName, Core.AppVersion.Current, Environment.MachineName, id);
		return new JellyfinApiClient(new JellyfinRequestAdapter(new JellyfinAuthenticationProvider(settings), settings));
	}
}
