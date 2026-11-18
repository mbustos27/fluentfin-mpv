using FluentFin.Core.Admin;
using FluentFin.Dialogs;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Tests.Admin;

/// <summary>
/// Nixie 1.2.0 (admin, button review S2, D81 / D83): a destructive confirmation's default button is Cancel, so Enter, Escape
/// (both close a ContentDialog with None) and Cancel leave everything as it is; only the named verb confirms. The prompts name
/// what goes and never say "Yes".
/// </summary>
public sealed class DestructiveConfirmTests
{
	[Fact]
	public void OnlyTheConfirmButtonConfirms()
	{
		Assert.Equal("Cancel", DestructiveConfirm.CancelText);
		Assert.True(DestructiveConfirm.Confirmed(ContentDialogResult.Primary));
		Assert.False(DestructiveConfirm.Confirmed(ContentDialogResult.None));
		Assert.False(DestructiveConfirm.Confirmed(ContentDialogResult.Secondary));
	}

	/// <summary>
	/// What DestructiveConfirm.Create gives the ContentDialog it builds (through the same Build): Cancel is the default button,
	/// the confirm the verb. (The off-screen run presses Enter on the real dialog: nothing is sent.)
	/// </summary>
	[Fact]
	public void TheBuiltDialog_HasCancelAsItsDefault_AndTheVerbAsItsConfirm()
	{
		var dialog = new RecordingDialog();

		DestructiveConfirm.Build(dialog, AdminPrompts.Restart("Harness"));

		Assert.Equal(ContentDialogButton.Close, dialog.DefaultButton);
		Assert.Equal("Cancel", dialog.CloseButtonText);
		Assert.Equal("Restart server", dialog.PrimaryButtonText);
		Assert.Equal("Restart Harness?", dialog.Title);
		Assert.Contains("Everyone watching", dialog.Message);
	}

	private sealed class RecordingDialog : IConfirmDialogTarget
	{
		public string? Title { get; set; }
		public string? Message { get; set; }
		public string? PrimaryButtonText { get; set; }
		public string? CloseButtonText { get; set; }
		public ContentDialogButton? DefaultButton { get; set; }
		string IConfirmDialogTarget.Title { set => Title = value; }
		string IConfirmDialogTarget.Message { set => Message = value; }
		string IConfirmDialogTarget.PrimaryButtonText { set => PrimaryButtonText = value; }
		string IConfirmDialogTarget.CloseButtonText { set => CloseButtonText = value; }
		ContentDialogButton IConfirmDialogTarget.DefaultButton { set => DefaultButton = value; }
		Microsoft.UI.Xaml.Style IConfirmDialogTarget.Style { set { } }
		Microsoft.UI.Xaml.Style IConfirmDialogTarget.PrimaryButtonStyle { set { } }
	}

	[Fact]
	public void ThePromptsNameWhatGoes_WithAVerb()
	{
		var prompts = new[]
		{
			AdminPrompts.Restart("Harness"),
			AdminPrompts.ShutDown("Harness"),
			AdminPrompts.RemovePassword("bob"),
			AdminPrompts.DeleteUser("bob"),
			AdminPrompts.DeleteLibrary("Kids"),
			AdminPrompts.DeleteImage("Night Train", "Backdrop"),
			AdminPrompts.DeleteSubtitle("Night Train", "English - SRT"),
			AdminPrompts.DeleteSegment("Intro", "Night Train"),
		};

		Assert.Equal(["Restart server", "Shut down server", "Remove password", "Delete user", "Delete library", "Delete image", "Delete subtitle", "Delete segment"],
			prompts.Select(x => x.ConfirmText));
		Assert.All(prompts, x => Assert.DoesNotContain("Yes", x.ConfirmText));
		Assert.All(prompts, x => Assert.EndsWith("?", x.Title));
		Assert.Equal("Delete bob?", prompts[3].Title);
		Assert.Equal("Delete Kids?", prompts[4].Title);
		Assert.Equal("Remove bob's password?", prompts[2].Title);
		Assert.Equal("Delete the backdrop image?", prompts[5].Title);
		Assert.Contains("Night Train", prompts[5].Message);
		Assert.Contains("can't be undone", prompts[3].Message);
		Assert.Contains("files on disk stay", prompts[4].Message);
	}

	[Fact]
	public void APromptWithoutAName_StillReads()
	{
		Assert.Equal("Restart the server?", AdminPrompts.Restart(null).Title);
		Assert.Equal("Delete this user?", AdminPrompts.DeleteUser(" ").Title);
		Assert.Equal("Delete this subtitle?", AdminPrompts.DeleteSubtitle(null, null).Title);
	}
}
