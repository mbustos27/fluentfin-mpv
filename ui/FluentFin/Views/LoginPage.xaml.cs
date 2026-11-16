using FluentFin.Core.Settings;
using FluentFin.ViewModels;
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
	}

	private void LoginView_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
	{
		PasswordBox.Password = ViewModel.Password;
	}

	private void PasswordBox_PasswordChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
	{
		ViewModel.Password = ((PasswordBox)sender).Password;
	}

	private void InputFieldKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
	{
		if (e.Key == Windows.System.VirtualKey.Enter)
		{
			ViewModel.LoginCommand.Execute(null);
		}
	}

	/// <summary>
	/// fluentfin-mpv: the text field inside the username box (a part of AutoSuggestBox's template) gets the box's name, which
	/// AutoSuggestBox does not pass on: Narrator says "Username, edit", not an unnamed edit.
	/// </summary>
	private void UsernameBox_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
	{
		if (sender is AutoSuggestBox box && FindTextBox(box) is { } text && string.IsNullOrEmpty(AutomationProperties.GetName(text)))
		{
			AutomationProperties.SetName(text, AutomationProperties.GetName(box));
		}
	}

	private static TextBox? FindTextBox(Microsoft.UI.Xaml.DependencyObject parent)
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
