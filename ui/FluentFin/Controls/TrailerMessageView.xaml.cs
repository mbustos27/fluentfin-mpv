using FluentFin.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Controls;

/// <summary>
/// Nixie 1.4.0: the theatre's message when a trailer can't play there (see TrailerMessageView.xaml): the heading, the reason,
/// the way to watch it elsewhere and Close. Narrator reads it as it appears (<see cref="Announce"/>). The browser opens only
/// from <see cref="LinkRequested"/>, the user's click.
/// </summary>
public sealed partial class TrailerMessageView : UserControl
{
	public TrailerMessageView(TrailerMessage message)
	{
		InitializeComponent();
		Message = message;
		HeadingText.Text = message.Heading;
		ReasonText.Text = message.Reason;
		LinkButton.Content = new TextBlock { Text = message.LinkText, TextTrimming = TextTrimming.CharacterEllipsis };
		AutomationProperties.SetName(LinkButton, message.LinkText);
		ToolTipService.SetToolTip(LinkButton, message.Link.AbsoluteUri);
		AutomationProperties.SetName(Panel, message.AutomationText);
		AutomationProperties.SetName(this, message.AutomationText);
	}

	public TrailerMessage Message { get; }

	/// <summary>The user picked the way to watch it elsewhere (Watch on YouTube, Open in browser).</summary>
	public event EventHandler? LinkRequested;

	/// <summary>The user picked Close.</summary>
	public event EventHandler? CloseRequested;

	/// <summary>Puts the keyboard on Close (never on the button that opens the browser).</summary>
	public bool FocusClose(FocusState how) => CloseButton.Focus(how);

	/// <summary>Narrator hears the message as it appears.</summary>
	public void Announce()
	{
		var peer = FrameworkElementAutomationPeer.FromElement(this) ?? FrameworkElementAutomationPeer.CreatePeerForElement(this);
		peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionAborted, AutomationNotificationProcessing.ImportantMostRecent,
			Message.AutomationText, "TrailerMessage");
	}

	private void LinkButton_Click(object sender, RoutedEventArgs e) => LinkRequested?.Invoke(this, EventArgs.Empty);

	private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
