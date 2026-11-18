using FluentFin.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Dialogs;

/// <summary>
/// Nixie 1.2.0 (admin, button review S2 and §3.6): asks before a change the server can't undo (Core.Admin.AdminPrompts). The
/// confirm button is the named verb in the Destructive role; Cancel is the default button, so Enter, Escape and Cancel all
/// leave everything as it is. Only the confirm button confirms.
/// </summary>
public static class DestructiveConfirm
{
	public const string CancelText = "Cancel";

	public static ContentDialog Create(ConfirmPrompt prompt)
	{
		var dialog = new ContentDialog();
		Build(new ContentDialogTarget(dialog), prompt);
		return dialog;
	}

	/// <summary>
	/// Everything the confirmation is given: its title, message, the verb, Cancel as the default button (Enter presses it) and
	/// the Destructive role's styles once they exist. Separate from the ContentDialog so a test can read what a built dialog
	/// gets without the WinUI runtime.
	/// </summary>
	internal static void Build(IConfirmDialogTarget dialog, ConfirmPrompt prompt)
	{
		dialog.Title = prompt.Title;
		dialog.Message = prompt.Message;
		dialog.PrimaryButtonText = prompt.ConfirmText;
		dialog.CloseButtonText = CancelText;
		dialog.DefaultButton = ContentDialogButton.Close;

		if (ButtonRoles.Find(ButtonRoles.DestructiveDialog) is { } dialogStyle)
		{
			dialog.Style = dialogStyle;
		}

		if (ButtonRoles.Find(ButtonRoles.DestructiveCompact) is { } confirmStyle)
		{
			dialog.PrimaryButtonStyle = confirmStyle;
		}
	}

	/// <summary>Escape and Cancel close with None, Enter presses Cancel: only the confirm button (Primary) confirms.</summary>
	public static bool Confirmed(ContentDialogResult result) => result == ContentDialogResult.Primary;

	/// <summary>True only when the user picked the confirm button; false when nobody could be asked (no window, another dialog open).</summary>
	public static async Task<bool> AskAsync(XamlRoot? root, ConfirmPrompt prompt)
	{
		if (root is null)
		{
			return false;
		}

		var dialog = Create(prompt);
		dialog.XamlRoot = root;
		// a dialog lives in the popup layer, outside the page: it takes the window's theme from its content
		if (root.Content is FrameworkElement content)
		{
			dialog.RequestedTheme = content.ActualTheme;
		}

		try
		{
			return Confirmed(await dialog.ShowAsync());
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
		{
			// another dialog is open (only one can be): not asked, so not confirmed
			return false;
		}
	}

	/// <summary>For a view model's Confirm hook: asks in the main window.</summary>
	public static Task<bool> AskInMainWindow(ConfirmPrompt prompt) => AskAsync(App.MainWindow?.Content?.XamlRoot, prompt);
}

/// <summary>What <see cref="DestructiveConfirm.Build"/> sets on a confirmation.</summary>
internal interface IConfirmDialogTarget
{
	string Title { set; }
	string Message { set; }
	string PrimaryButtonText { set; }
	string CloseButtonText { set; }
	ContentDialogButton DefaultButton { set; }
	Style Style { set; }
	Style PrimaryButtonStyle { set; }
}

internal sealed class ContentDialogTarget(ContentDialog dialog) : IConfirmDialogTarget
{
	public string Title { set => dialog.Title = value; }
	public string Message { set => dialog.Content = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap }; }
	public string PrimaryButtonText { set => dialog.PrimaryButtonText = value; }
	public string CloseButtonText { set => dialog.CloseButtonText = value; }
	public ContentDialogButton DefaultButton { set => dialog.DefaultButton = value; }
	public Style Style { set => dialog.Style = value; }
	public Style PrimaryButtonStyle { set => dialog.PrimaryButtonStyle = value; }
}
