using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFin.Contracts.Services;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using FluentFin.Helpers;
using FluentFin.Services;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.ViewModels;

public partial class LoginViewModel(IJellyfinAuthenticationService jellyfinAuthenticator,
									[FromKeyedServices(NavigationRegions.InitialSetup)] INavigationService navigationService,
									ILocalSettingsService settingsService) : ObservableObject, INavigationAware
{
	[ObservableProperty]
	public partial string Username { get; set; } = "";

	[ObservableProperty]
	public partial string Password { get; set; } = "";

	[ObservableProperty]
	public partial bool KeepMeLoggedIn { get; set; }

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(LoginCommand))]
	public partial bool CanLogin { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ServerName), nameof(Heading))]
	public partial SavedServer? Server { get; private set; }

	[ObservableProperty]
	public partial Uri? SplashScreen { get; set; }

	/// <summary>1.1.4 (states): why the last sign-in did not work and what to do, on the page (not in a dialog).</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasError))]
	public partial string ErrorMessage { get; set; } = "";

	/// <summary>
	/// A sign-in is on its way to the server (a password, or a Quick Connect code just approved): the button shows progress,
	/// the fields and Use Quick Connect wait.
	/// </summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsIdle), nameof(StatusText))]
	[NotifyCanExecuteChangedFor(nameof(QuickConnectCommand), nameof(SwitchServerCommand))]
	public partial bool IsSigningIn { get; set; }

	public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

	/// <summary>174-signin: a line the page opens with that is no sign-in error (<see cref="SetupNotice"/>), in a bar of its own.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasNotice))]
	public partial string Notice { get; set; } = "";

	public bool HasNotice => !string.IsNullOrEmpty(Notice);

	public bool IsIdle => !IsSigningIn;

	/// <summary>The server as the user knows it: its name, else its address.</summary>
	public string ServerName => ServerNameOf(Server);

	/// <summary>The card's line under the app's name: which server this signs in to.</summary>
	public string Heading => string.IsNullOrEmpty(ServerName) ? "Sign in" : $"Sign in to {ServerName}";

	/// <summary>What is going on, under the buttons (Narrator hears it: a polite live region).</summary>
	public string StatusText => IsSigningIn ? $"Signing in to {ServerName}…" : "";

	/// <summary>A sign-in was refused or failed; the page puts the keyboard back in the password box.</summary>
	public event EventHandler? SignInFailed;

	/// <summary>The server's name, else its address's host (and port), else nothing.</summary>
	public static string ServerNameOf(SavedServer? server)
	{
		if (server is null)
		{
			return "";
		}

		if (!string.IsNullOrWhiteSpace(server.DisplayName))
		{
			return server.DisplayName.Trim();
		}

		var url = string.IsNullOrEmpty(server.LocalUrl) ? server.PublicUrl : server.LocalUrl;
		return Uri.TryCreate(url, UriKind.Absolute, out var address) ? address.Authority : url;
	}

	/// <summary>1.1.4 (states; layout audit #5): where to enter the Quick Connect code, under the code.</summary>
	public static string QuickConnectInstructions(string serverName) =>
		$"On a device that's already signed in to {(string.IsNullOrEmpty(serverName) ? "this server" : serverName)}, open Settings > Quick Connect " +
		"and enter this code. You're signed in here as soon as it's approved.";

	/// <summary>The code as shown in large type: two groups of three ("123 456"), easier to read across a room.</summary>
	public static string SpacedCode(string? code) =>
		code is { Length: 6 } && code.All(char.IsAsciiDigit) ? $"{code[..3]} {code[3..]}" : code ?? "";

	/// <summary>What Narrator reads for the code: digit by digit ("1 2 3 4 5 6"), not as a number.</summary>
	public static string SpokenCode(string? code) => string.Join(' ', (code ?? "").Where(c => !char.IsWhiteSpace(c)));

	partial void OnUsernameChanged(string value)
	{
		UpdateCanLogin();
		TickIfRemembered();
	}

	partial void OnServerChanged(SavedServer? value)
	{
		UpdateCanLogin();
		TickIfRemembered();
	}

	/// <summary>
	/// 174-sessions (the review's Major): a name that was remembered here (picked from the list, which fills in its saved
	/// password, typed, or the one the page opens with) ticks "Remember me": unticked, the sign-in wasn't kept, and the next
	/// start signed in as someone else, or nobody. Never unticks.
	/// </summary>
	private void TickIfRemembered()
	{
		if (!KeepMeLoggedIn && SavedSignIns.WasRemembered(Server, Username))
		{
			KeepMeLoggedIn = true;
		}
	}

	/// <summary>The name "Remember me" was unticked for, by the user (the page only ever ticks it); null once ticked again.</summary>
	private string? _untickedFor;

	partial void OnKeepMeLoggedInChanged(bool value) => _untickedFor = value ? null : Username?.Trim();

	/// <summary>
	/// 174-sessions (BS-m1, the review's Major): after a sign-in as <paramref name="signedIn"/> with "Remember me" off, an
	/// earlier saved sign-in of that user goes only when the box was unticked for that very name. An unticked box alone
	/// means "don't keep this one", never "forget the one kept before".
	/// </summary>
	private void ForgetIfUnticked(SavedServer server, string? signedIn)
	{
		if (KeepMeLoggedIn || _untickedFor is not { Length: > 0 } name || !SavedSignIns.SameUserName(name, signedIn))
		{
			return;
		}

		try
		{
			SavedSignIns.ForgetName(server, name);
			SavedSignIns.Forget(server, name); // its change saves the server list
		}
		catch (Exception ex)
		{
			Serilog.Log.Warning(ex, "The saved sign-in that \"Remember me\" was unticked for could not be removed from the settings");
		}
	}

	partial void OnIsSigningInChanged(bool value) => UpdateCanLogin();

	private void UpdateCanLogin() => CanLogin = !string.IsNullOrWhiteSpace(Username) && Server is not null && !IsSigningIn;

	[RelayCommand(CanExecute = nameof(CanLogin))]
	public async Task Login()
	{
		// 1.1.4 (states): Enter in either field calls this directly, past CanExecute: no username, no request
		if (!CanLogin)
		{
			return;
		}

		ErrorMessage = "";
		IsSigningIn = true;
		string? error;
		var server = Server!;
		try
		{
			// 1.7.5 (the basics gate; the review's Major): a saved sign-in of this name with no password typed (a saved user picked
			// from the list, or the name typed) signs in as the start does: a Quick Connect token with no code, a saved password
			// sent once. A password or a token the server refuses (401) is forgotten there and the page says "The saved
			// sign-in to ... no longer works" (sent as a typed password, a refused one went again at every pick, and Jellyfin
			// disables an account after a few: only the server's owner can enable it again)
			if (string.IsNullOrEmpty(Password) && SavedSignIns.Saved(server, Username) is { } saved)
			{
				if (await jellyfinAuthenticator.Authenticate(server, saved, CancellationToken.None))
				{
					error = null;
					ForgetIfUnticked(server, Username);
					navigationService.NavigateTo<ShellViewModel>();
				}
				else
				{
					error = jellyfinAuthenticator.SavedSignInProblem ?? $"Couldn't sign in to {ServerName}. Try again, or use Quick Connect.";
				}
			}
			else
			{
				error = await jellyfinAuthenticator.Authenticate(server, Username.Trim(), Password, KeepMeLoggedIn);
				if (error is null)
				{
					ForgetIfUnticked(server, Username);
				}
			}
		}
		catch (Exception ex)
		{
			Serilog.Log.Warning(ex, "Sign-in to {Server} failed", ServerName);
			error = $"Couldn't sign in to {ServerName}. Try again; if it keeps happening, the app's log has the details.";
		}
		finally
		{
			IsSigningIn = false;
		}

		if (error is not null)
		{
			ErrorMessage = error;
			SignInFailed?.Invoke(this, EventArgs.Empty);
		}
	}

	/// <summary>
	/// How long a Quick Connect code is waited for: Jellyfin forgets a code after 10 minutes, and then only answers "not
	/// found", which looked like "not yet" (the dialog waited for ever).
	/// </summary>
	public static readonly TimeSpan QuickConnectLifetime = TimeSpan.FromMinutes(10);

	/// <summary>Status checks that fail in a row before the code is given up on (the server forgot it, or is gone).</summary>
	public const int QuickConnectFailuresAllowed = 4;

	public const string QuickConnectExpired = "This code expired. Try again.";

	/// <summary>Between two status checks (the tests make it short).</summary>
	internal TimeSpan QuickConnectPollInterval { get; set; } = TimeSpan.FromSeconds(5);

	/// <summary>The clock the code's lifetime is measured on (the tests move it).</summary>
	internal TimeProvider Time { get; set; } = TimeProvider.System;

	/// <summary>
	/// Shows the code until the token closes it or the user does (Cancel, Escape); the task ends when the dialog is closed.
	/// The tests replace the dialog.
	/// </summary>
	internal Func<string, string, CancellationToken, Task> ShowQuickConnectCode { get; set; } = ShowQuickConnectDialog;

	/// <summary>
	/// 1.1.4 (states): Use Quick Connect. The code is shown while the server is asked, every few seconds, whether it was
	/// approved. Approved: the dialog closes and the sign-in runs like a password one (progress, the page waits; a failure is
	/// said on the page). Expired (10 minutes, or the server stopped knowing the code): the dialog closes and the page says
	/// so. Cancel: the asking stops, and a code approved later signs nobody in.
	/// </summary>
	[RelayCommand(CanExecute = nameof(IsIdle))]
	private async Task QuickConnect()
	{
		if (Server is not { } server || IsSigningIn)
		{
			return;
		}

		ErrorMessage = "";
		var response = await jellyfinAuthenticator.GetQuickConnectCode(server);

		if (response is null)
		{
			// 1.7.3 (rule 11): no step on the server's dashboard; what the user can do here
			ErrorMessage = $"Quick Connect isn't available on {ServerName}: it may be off there, or the server can't be reached. " +
				"Sign in with your username and password instead.";
			SignInFailed?.Invoke(this, EventArgs.Empty);
			return;
		}

		using var stop = new CancellationTokenSource();
		var shown = ShowQuickConnectCode(response.Code ?? "", ServerName, stop.Token);
		var waiting = WaitForQuickConnect(server, response, stop.Token);
		var first = await Task.WhenAny(shown, waiting);
		stop.Cancel();
		if (first == shown)
		{
			// Cancel or Escape: the asking stops at its next step (WaitForQuickConnect never throws; a check already on its
			// way to a slow server is not waited for, so Use Quick Connect is back at once)
			return;
		}

		await shown;
		var approved = await waiting;
		if (approved is null)
		{
			ErrorMessage = QuickConnectExpired;
			SignInFailed?.Invoke(this, EventArgs.Empty);
			return;
		}

		IsSigningIn = true;
		bool signedIn;
		try
		{
			signedIn = await jellyfinAuthenticator.Authenticate(server, approved, KeepMeLoggedIn); // 174-signin: "Remember me" keeps it too
			if (signedIn)
			{
				ForgetIfUnticked(server, SessionInfo.CurrentUser?.Name); // the user the code was approved for
			}
		}
		catch (Exception ex)
		{
			Serilog.Log.Warning(ex, "Quick Connect sign-in to {Server} failed", ServerName);
			signedIn = false;
		}
		finally
		{
			IsSigningIn = false;
		}

		if (!signedIn)
		{
			ErrorMessage = $"The code was approved, but signing in to {ServerName} didn't work. Try again, or sign in with your username and password.";
			SignInFailed?.Invoke(this, EventArgs.Empty);
		}
	}

	/// <summary>
	/// Asks the server whether the code was approved, every <see cref="QuickConnectPollInterval"/>: the approved result, or
	/// null when the code expired (<see cref="QuickConnectLifetime"/>, or <see cref="QuickConnectFailuresAllowed"/> failed
	/// checks in a row) or the token stopped the asking. Never throws.
	/// </summary>
	internal async Task<QuickConnectResult?> WaitForQuickConnect(SavedServer server, QuickConnectResult code, CancellationToken cancellationToken)
	{
		var started = Time.GetUtcNow();
		var failures = 0;
		try
		{
			await Task.Delay(TimeSpan.FromSeconds(Math.Min(1, QuickConnectPollInterval.TotalSeconds)), cancellationToken);
			while (!cancellationToken.IsCancellationRequested)
			{
				QuickConnectResult? status;
				try
				{
					status = await jellyfinAuthenticator.CheckQuickConnectStatus(server, code);
				}
				catch (Exception)
				{
					status = null;
				}

				if (cancellationToken.IsCancellationRequested)
				{
					return null;
				}

				if (status is null)
				{
					if (++failures >= QuickConnectFailuresAllowed)
					{
						return null;
					}
				}
				else
				{
					failures = 0;
					if (status.Authenticated == true)
					{
						return status;
					}
				}

				if (Time.GetUtcNow() - started >= QuickConnectLifetime)
				{
					return null;
				}

				await Task.Delay(QuickConnectPollInterval, cancellationToken);
			}
		}
		catch (OperationCanceledException)
		{
		}

		return null;
	}

	private static async Task ShowQuickConnectDialog(string code, string serverName, CancellationToken close)
	{
		var dialog = new ContentDialog
		{
			XamlRoot = App.MainWindow.Content.XamlRoot,
			Title = "Sign in with Quick Connect",
			Content = QuickConnectContent(code, serverName),
			CloseButtonText = "Cancel",
			DefaultButton = ContentDialogButton.None,
		};
		if (App.MainWindow.Content is FrameworkElement content)
		{
			dialog.RequestedTheme = content.ActualTheme;
		}

		// the token is cancelled on the UI thread (QuickConnect runs there), so Hide is called there too
		using var closing = close.Register(dialog.Hide);
		await dialog.ShowAsync();
	}

	private static StackPanel QuickConnectContent(string code, string serverName)
	{
		var panel = new StackPanel { Spacing = 14, MaxWidth = 440 };
		// the code first, large and monospaced; Narrator reads it digit by digit
		var big = new TextBlock
		{
			Text = SpacedCode(code),
			FontSize = 28,
			FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
			FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
			CharacterSpacing = 80,
			HorizontalAlignment = HorizontalAlignment.Center,
			IsTextSelectionEnabled = true,
		};
		AutomationProperties.SetAutomationId(big, "QuickConnectCode");
		AutomationProperties.SetName(big, SpokenCode(code));
		panel.Children.Add(big);
		panel.Children.Add(new TextBlock { Text = QuickConnectInstructions(serverName), TextWrapping = TextWrapping.WrapWholeWords });
		var waiting = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
		waiting.Children.Add(new ProgressRing { Width = 16, Height = 16, IsActive = true, IsIndeterminate = true });
		var status = new TextBlock { Text = "Waiting for approval…", Opacity = 0.85 };
		AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
		waiting.Children.Add(status);
		panel.Children.Add(waiting);
		return panel;
	}

	// 1.2.0 (the button review, S1): not while a sign-in is under way
	[RelayCommand(CanExecute = nameof(IsIdle))]
	private void SwitchServer()
	{
		navigationService.NavigateTo<SelectServerViewModel>();
	}

	public Task OnNavigatedFrom() => Task.CompletedTask;

	public Task OnNavigatedTo(object parameter)
	{
		// 174-signin: Switch user with a line to say (SetupNotice: the saved sign-in couldn't be removed from this PC)
		Notice = parameter is SetupNotice { Message: var message } ? message : "";
		var server = parameter switch { SetupNotice notice => notice.Server, SavedServer saved => saved, _ => null };
		if (server is null)
		{
			return Task.CompletedTask;
		}

		ErrorMessage = "";
		Server = server;
		SplashScreen = JellyfinAuthenticationService.GetSplashScreen(Server.GetServerUrl());

		// 174-sessions (BS-m3): the page opens with the name it signs in (it opened empty, also with one saved user): the one
		// it is given (a refused saved sign-in's, Sign in again's; none for Switch user), else the server's last remembered
		// one (kept after Sign out), else its one saved user. Not over a name typed already. "Remember me" stays ticked for a
		// name that was remembered: off, the sign-in would forget it (BS-m1)
		if (string.IsNullOrWhiteSpace(Username))
		{
			Username = parameter is SetupNotice { Username: { } name } ? name.Trim() : SavedSignIns.UsernameFor(server); // ticks "Remember me" for a remembered name
		}

		return Task.CompletedTask;
	}

	// 174-signin: a saved Quick Connect sign-in has no password (an empty one is no DPAPI blob: Unprotect threw)
	public string Unprotect(byte[] protectedBytes) => protectedBytes.Length == 0 ? "" : protectedBytes.Unprotect(settingsService.GetEntropyBytes());

	/// <summary>
	/// 1.7.5: a saved user taken from the username box's list (LoginPage): signed in at once with their saved sign-in, never
	/// as a typed password (the review's Major: a refused one was sent again at every pick). The password box stays empty.
	/// </summary>
	public Task SignInAs(SavedUser saved)
	{
		Username = saved.Username;
		Password = "";
		return Login();
	}
}
