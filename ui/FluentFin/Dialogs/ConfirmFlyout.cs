using FluentFin.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace FluentFin.Dialogs;

/// <summary>
/// Nixie 1.2.0 (admin, button review D53 / D62): the same question as <see cref="DestructiveConfirm"/>, for a delete inside a
/// dialog, where a second ContentDialog cannot open: a flyout at the button that asked, with the named verb (Destructive) and
/// Cancel, which has the focus. Escape, a click outside, Enter and Cancel leave everything as it is; only the verb confirms.
/// </summary>
public static class ConfirmFlyout
{
	/// <summary>True only when the user picked the confirm button.</summary>
	public static Task<bool> AskAsync(FrameworkElement anchor, ConfirmPrompt prompt)
	{
		var answer = new TaskCompletionSource<bool>();
		var flyout = new Flyout { Placement = FlyoutPlacementMode.Bottom };

		var confirm = new Button { Content = prompt.ConfirmText };
		var cancel = new Button { Content = DestructiveConfirm.CancelText };
		if (ButtonRoles.Find(ButtonRoles.DestructiveCompact) is { } confirmStyle)
		{
			confirm.Style = confirmStyle;
		}

		confirm.Click += (_, _) =>
		{
			answer.TrySetResult(true);
			flyout.Hide();
		};
		cancel.Click += (_, _) => flyout.Hide();

		var title = new TextBlock { Text = prompt.Title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextWrapping = TextWrapping.Wrap };
		var panel = new StackPanel
		{
			MaxWidth = 360,
			Spacing = 12,
			Children =
			{
				title,
				new TextBlock { Text = prompt.Message, TextWrapping = TextWrapping.Wrap },
				new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, HorizontalAlignment = HorizontalAlignment.Right, Children = { confirm, cancel } },
			},
		};
		AutomationProperties.SetName(panel, prompt.Title);
		flyout.Content = panel;
		flyout.Opened += (_, _) => cancel.Focus(FocusState.Programmatic);
		flyout.Closed += (_, _) => answer.TrySetResult(false);
		flyout.ShowAt(anchor);
		return answer.Task;
	}

	/// <summary>The element the question is asked at: the one with the keyboard focus (the button just pressed), else <paramref name="fallback"/>.</summary>
	public static FrameworkElement AnchorIn(FrameworkElement fallback) =>
		fallback.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is FrameworkElement focused ? focused : fallback;
}
