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

	/// <summary>1.1.4 (states): the Quick Connect instructions, with the code (the harness reads "Enter code NNNNNN").</summary>
	public static string QuickConnectInstructions(string code, string serverName) =>
		$"Enter code {code} in Quick Connect on a device that is already signed in to {(string.IsNullOrEmpty(serverName) ? "this server" : serverName)}: " +
		"in Jellyfin's web app, open your profile, then Quick Connect. This app signs in as soon as the code is approved.";

	/// <summary>The code as shown in large type: two groups of three ("123 456"), easier to read across a room.</summary>
	public static string SpacedCode(string? code) =>
		code is { Length: 6 } && code.All(char.IsAsciiDigit) ? $"{code[..3]} {code[3..]}" : code ?? "";

	partial void OnUsernameChanged(string value) => UpdateCanLogin();

	partial void OnServerChanged(SavedServer? value) => UpdateCanLogin();

	partial void OnIsSigningInChanged(bool value) => UpdateCanLogin();

	private void UpdateCanLogin() => CanLogin = !string.IsNullOrWhiteSpace(Username) && Server is not null && !IsSigningIn;

	[RelayCommand(CanExecute = nameof(CanLogin))]
	public async Task Login()
	{
		if (Server is null || IsSigningIn)
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
			error = $"Couldn't sign in to {ServerName} ({ex.Message}). Try again.";
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
		var panel = new StackPanel { Spacing = 14, MaxWidth = 460 };
		var big = new TextBlock
		{
			Text = SpacedCode(code),
			FontSize = 40,
			FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
			CharacterSpacing = 120,
			HorizontalAlignment = HorizontalAlignment.Center,
			IsTextSelectionEnabled = true,
		};
		// the instructions below say the code once; Narrator does not read it twice
		AutomationProperties.SetAccessibilityView(big, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
		panel.Children.Add(big);
		panel.Children.Add(new TextBlock { Text = QuickConnectInstructions(code, serverName), TextWrapping = TextWrapping.WrapWholeWords });
		var waiting = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
		waiting.Children.Add(new ProgressRing { Width = 16, Height = 16, IsActive = true, IsIndeterminate = true });
		waiting.Children.Add(new TextBlock { Text = "Waiting for the code to be approved…", Opacity = 0.85 });
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
