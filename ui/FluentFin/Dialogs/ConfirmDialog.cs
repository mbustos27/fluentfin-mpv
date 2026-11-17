using FluentFin.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Dialogs;

/// <summary>
/// Seaglass 1.1.3 (UX review, prioritised fix 2): asks before a destructive action. The prompt names what goes; Cancel is the
/// default button, so Enter or Escape keeps everything.
/// </summary>
public static class ConfirmDialog
{
	public static ContentDialog Create(ConfirmPrompt prompt) => new()
	{
		Title = prompt.Title,
		Content = new TextBlock { Text = prompt.Message, TextWrapping = TextWrapping.Wrap },
		PrimaryButtonText = prompt.ConfirmText,
		CloseButtonText = "Cancel",
		DefaultButton = ContentDialogButton.Close,
	};

	/// <summary>True only when the user picked the prompt's confirm button.</summary>
	public static async Task<bool> AskAsync(XamlRoot? root, ConfirmPrompt prompt)
	{
		if (root is null)
		{
			return false;
		}

		var dialog = Create(prompt);
		dialog.XamlRoot = root;
		// a dialog lives in the popup layer, outside the page: it takes the window's theme (light, dark or forced) from its content
		if (root.Content is FrameworkElement content)
		{
			dialog.RequestedTheme = content.ActualTheme;
		}

		return await dialog.ShowAsync() == ContentDialogResult.Primary;
	}
}
