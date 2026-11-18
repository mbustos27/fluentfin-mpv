using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFin.Core.Contracts.Services;

namespace FluentFin.Dialogs.ViewModels;

public partial class QuickConnectViewModel(IJellyfinClient jellyfinClient) : ObservableObject, IHandleClose
{
	[ObservableProperty]
	public partial string QuickConnectCode { get; set; }
	public bool CanClose { get; set; }

	/// <summary>
	/// Seaglass 1.1.4 (Settings > Other devices > Quick Connect): what the last OK did, under the code. The dialog stays open
	/// (it closes with Close), so the answer is said here; it used to say nothing either way.
	/// </summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(CloseText))]
	[NotifyPropertyChangedFor(nameof(CanApprove))]
	public partial string Status { get; private set; } = "";

	/// <summary>Approve is offered until a code is approved (a second press would approve nothing).</summary>
	public bool CanApprove => Status != ApprovedStatus;

	/// <summary>The dialog's close button: Cancel until a code is approved, then Close.</summary>
	public string CloseText => Status == ApprovedStatus ? "Close" : "Cancel";

	public const string ApprovedStatus = "Approved. The other device signs in now; you can close this.";

	public const string NoCodeStatus = "Enter the code the other device shows.";

	public const string FailedStatus = "That code didn't work. Check it on the other device and try again: a code lasts a few minutes.";

	[RelayCommand]
	private async Task VerifyQuickConnect()
	{
		if (string.IsNullOrWhiteSpace(QuickConnectCode))
		{
			Status = NoCodeStatus;
			return;
		}

		Status = await jellyfinClient.Authenticate(QuickConnectCode.Trim()) ? ApprovedStatus : FailedStatus;
	}
}
