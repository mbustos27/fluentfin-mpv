using FluentFin.Core.Settings;
using FluentFin.Services;
using FluentFin.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentFin.Views;

public sealed partial class LoginPage
{
	public LoginViewModel ViewModel { get; }

	public LoginPage()
	{
		ViewModel = App.GetService<LoginViewModel>();
		InitializeComponent();
		Loaded += LoginView_Loaded;
		Unloaded += LoginView_Unloaded;
	}

	// Seaglass: the server's splash image is the soft backdrop behind the card
	private void LoginView_Unloaded(object sender, RoutedEventArgs e)
	{
		ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
		ViewModel.SignInFailed -= ViewModel_SignInFailed;
		AmbientBackdrop.Set(null);
		ThemeResources.Applied -= OnThemeApplied;
		GlassMode.Changed -= OnThemeApplied;
	}

	private void OnThemeApplied(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(ShowThemeBackdrop);

	/// <summary>
	/// Nixie 1.3.0: the layout theme's SignInBackdropTemplate behind the form, given this page's view model; none (or high
	/// contrast, which keeps the system's plain colours) leaves the dusk scene behind the window showing.
	/// </summary>
	private void ShowThemeBackdrop()
	{
		var template = ShowsThemeBackdrop(ThemeResources.SignInBackdrop is not null, GlassMode.IsHighContrast) ? ThemeResources.SignInBackdrop : null;
		ThemeBackdrop.ContentTemplate = template;
		ThemeBackdrop.Content = template is null ? null : ViewModel;
		ThemeBackdrop.Visibility = template is null ? Visibility.Collapsed : Visibility.Visible;
	}

	/// <summary>Whether the theme's sign-in backdrop is shown: the theme has one and high contrast is off.</summary>
	public static bool ShowsThemeBackdrop(bool themeHasOne, bool highContrast) => themeHasOne && !highContrast;

	/// <summary>1.5 (the button review's S2): "Type a username to sign in" shows while Sign in is off for want of a username.</summary>
	public static Microsoft.UI.Xaml.Visibility SignInHintVisibility(string? username, bool signingIn) =>
		string.IsNullOrWhiteSpace(username) && !signingIn ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

	private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(LoginViewModel.SplashScreen))
		{
			AmbientBackdrop.Set(ViewModel.SplashScreen);
		}
	}

	private void LoginView_Loaded(object sender, RoutedEventArgs e)
	{
		PasswordBox.Password = ViewModel.Password;
		ViewModel.PropertyChanged += ViewModel_PropertyChanged;
		ViewModel.SignInFailed += ViewModel_SignInFailed;
		AmbientBackdrop.Set(ViewModel.SplashScreen);
		ShowThemeBackdrop();
		ThemeResources.Applied += OnThemeApplied;
		GlassMode.Changed += OnThemeApplied;

		// 1.1.4 (states): the keyboard starts where there is something to type
		FieldToFocus(ViewModel.Username).Focus(FocusState.Programmatic);

		// 174-sessions (BS-M4): the line the page opens with ("The saved sign-in to Den no longer works.") is said to Narrator
		// too: an InfoBar open from the start announces nothing
		if (ViewModel.HasNotice)
		{
			var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(NoticeBar) ?? Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(NoticeBar);
			peer?.RaiseNotificationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationKind.Other,
				Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationProcessing.ImportantMostRecent, ViewModel.Notice, "NixieSignInPageLine");
		}
	}

	/// <summary>The username box when it is empty, else the password box.</summary>
	private Control FieldToFocus(string username) => string.IsNullOrWhiteSpace(username) ? UsernameBox : PasswordBox;

	/// <summary>1.1.4 (states): a refused sign-in puts the keyboard back in the password box, its text selected to retype.</summary>
	private void ViewModel_SignInFailed(object? sender, EventArgs e)
	{
		DispatcherQueue.TryEnqueue(() =>
		{
			var field = FieldToFocus(ViewModel.Username);
			field.Focus(FocusState.Programmatic);
			if (field == PasswordBox)
			{
				PasswordBox.SelectAll();
			}
		});
	}

	private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
	{
		ViewModel.Password = ((PasswordBox)sender).Password;
	}

	private void InputFieldKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
	{
		if (e.Key == Windows.System.VirtualKey.Enter)
		{
			e.Handled = true;
			ViewModel.LoginCommand.Execute(null);
		}
	}

	/// <summary>1.1.4 (states): Enter in the username box goes on to the password (or signs in when one is there already).</summary>
	private void UsernameBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
	{
		if (args.ChosenSuggestion is not null)
		{
			return;
		}

		if (string.IsNullOrEmpty(PasswordBox.Password))
		{
			PasswordBox.Focus(FocusState.Keyboard);
		}
		else
		{
			ViewModel.LoginCommand.Execute(null);
		}
	}

	/// <summary>
	/// fluentfin-mpv: the text field inside the username box (a part of AutoSuggestBox's template) gets the box's name, which
	/// AutoSuggestBox does not pass on: Narrator says "Username, edit", not an unnamed edit.
	/// </summary>
	private void UsernameBox_Loaded(object sender, RoutedEventArgs e)
	{
		if (sender is AutoSuggestBox box && FindTextBox(box) is { } text && string.IsNullOrEmpty(AutomationProperties.GetName(text)))
		{
			AutomationProperties.SetName(text, AutomationProperties.GetName(box));
		}
	}

	private static TextBox? FindTextBox(DependencyObject parent)
	{
		for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
		{
			var child = VisualTreeHelper.GetChild(parent, i);
			if ((child as TextBox ?? FindTextBox(child)) is { } found)
			{
				return found;
			}
		}

		return null;
	}

	private void AutoSuggestBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
	{
		if (args.SelectedItem is not SavedUser user)
		{
			return;
		}

		PasswordBox.Password = ViewModel.Unprotect(user.Password);
	}
}
