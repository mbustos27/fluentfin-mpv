using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Core.Admin;
using FluentFin.Core.Contracts.Services;

namespace FluentFin.Core.ViewModels;

/// <remarks>
/// Nixie 1.2.0 (admin, button review D38-D40, worst offender 4): "Reset Password" cleared the user's password in one click.
/// Now "Remove password…" asks first, naming the user (<see cref="Confirm"/>; Cancel the default) and says what happened;
/// "Change password" checks the two new passwords match before anything is sent, and says how it went.
/// </remarks>
public partial class UserPasswordEditorViewModel(IJellyfinClient jellyfinClient) : UserSectionEditorViewModel
{
	/// <summary>Asks before a destructive action; true only when the user confirmed. Unset (nobody to ask), nothing happens.</summary>
	public Func<ConfirmPrompt, Task<bool>>? Confirm { get; set; }

	/// <summary>How the last change went; null when there is nothing to say.</summary>
	[ObservableProperty]
	public partial ServerNotice? Notice { get; set; }

	/// <summary>True when the server took the new password; the passwords themselves are never kept or logged here.</summary>
	public async Task<bool> ChangePassword(string currentPassword, string newPassword, string confirmPassword)
	{
		if (newPassword != confirmPassword)
		{
			Notice = new ServerNotice(ServerPowerState.Failed, "The passwords don't match", "Type the new password the same way twice.", NoticeKind.Error);
			return false;
		}

		var changed = await jellyfinClient.ChangePassword(User, currentPassword, newPassword);
		Notice = changed
			? new ServerNotice(ServerPowerState.None, "Password changed", $"{Name} signs in with the new password from now on.", NoticeKind.Success)
			: new ServerNotice(ServerPowerState.Failed, "The password wasn't changed", "The server refused it. Check the current password, then try again.", NoticeKind.Error);
		return changed;
	}

	public async Task RemovePassword()
	{
		if (Confirm is not { } confirm || !await confirm(AdminPrompts.RemovePassword(User?.Name)))
		{
			return;
		}

		Notice = await jellyfinClient.ResetPassword(User)
			? new ServerNotice(ServerPowerState.None, "Password removed", $"{Name} can sign in with no password until a new one is set.", NoticeKind.Warning)
			: new ServerNotice(ServerPowerState.Failed, "The password wasn't removed", "The server didn't accept the request. Nothing changed.", NoticeKind.Error);
	}

	private string Name => User?.Name is { } name && !string.IsNullOrWhiteSpace(name) ? name : "This user";
}
