using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
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
using ReactiveUI;

namespace FluentFin.ViewModels;

public partial class LoginViewModel(IJellyfinAuthenticationService jellyfinAuthenticator,
									[FromKeyedServices(NavigationRegions.InitialSetup)] INavigationService navigationService,
									ILocalSettingsService settingsService) : ObservableObject, INavigationAware
{
	private CompositeDisposable? _disposable;

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

	/// <summary>A sign-in is on its way to the server: the button shows progress, the fields wait.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsIdle), nameof(StatusText))]
	public partial bool IsSigningIn { get; set; }

	public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

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

	partial void OnUsernameChanged(string value) => UpdateCanLogin();

	partial void OnServerChanged(SavedServer? value) => UpdateCanLogin();

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
		try
		{
			error = await jellyfinAuthenticator.Authenticate(Server, Username.Trim(), Password, KeepMeLoggedIn);
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

	[RelayCommand]
	private async Task QuickConnect()
	{
		if (Server is null)
		{
			return;
		}

		ErrorMessage = "";
		var response = await jellyfinAuthenticator.GetQuickConnectCode(Server);

		if (response is null)
		{
			ErrorMessage = $"Quick Connect isn't available on {ServerName}: it may be turned off on the server (Dashboard > General > Quick Connect), " +
				"or the server can't be reached. Sign in with your username and password instead.";
			SignInFailed?.Invoke(this, EventArgs.Empty);
			return;
		}

		_disposable?.Dispose();
		_disposable = [];
		var approved = false;
		var dialog = new ContentDialog
		{
			XamlRoot = App.MainWindow.Content.XamlRoot,
			Title = "Sign in with Quick Connect",
			Content = QuickConnectContent(response.Code ?? "", ServerName),
			CloseButtonText = "Cancel",
			DefaultButton = ContentDialogButton.None,
		};

		Observable.Timer(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5))
			.Where(_ => Server is not null)
			.SelectMany(_ => jellyfinAuthenticator.CheckQuickConnectStatus(Server, response))
			.WhereNotNull()
			.ObserveOn(RxSchedulers.MainThreadScheduler)
			.Subscribe(result =>
			{
				if (result.Authenticated == true)
				{
					approved = true;
					_disposable?.Dispose();
					dialog.Hide();
					var isAuthenticated = jellyfinAuthenticator.Authenticate(Server, result);
				}
			})
			.DisposeWith(_disposable);

		await dialog.ShowAsync();

		// Cancel (or Escape): stop asking the server; a code approved later signs nobody in
		if (!approved)
		{
			_disposable?.Dispose();
			_disposable = null;
		}
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

	[RelayCommand]
	private void SwitchServer()
	{
		navigationService.NavigateTo<SelectServerViewModel>();
	}

	public Task OnNavigatedFrom() => Task.CompletedTask;

	public Task OnNavigatedTo(object parameter)
	{
		if (parameter is not SavedServer server)
		{
			return Task.CompletedTask;
		}

		ErrorMessage = "";
		Server = server;
		SplashScreen = JellyfinAuthenticationService.GetSplashScreen(Server.GetServerUrl());

		return Task.CompletedTask;
	}

	public string Unprotect(byte[] protectedBytes) => protectedBytes.Unprotect(settingsService.GetEntropyBytes());
}
