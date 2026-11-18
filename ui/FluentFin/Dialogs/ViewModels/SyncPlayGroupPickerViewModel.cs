using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Dialogs.ViewModels;

/// <summary>
/// SyncPlay's groups: join one, start one, leave the one you are in. Seaglass 1.1.4 (layout audit 18, button review A68,
/// review): "Join" only with a group chosen; "New group" always starts one, offered to an account the server lets start
/// groups, and a refusal is said here (it was swallowed); "Leave group" is its own button, shown only while in a group, and
/// asks first.
/// </summary>
public partial class SyncPlayGroupPickerViewModel(IJellyfinClient jellyfinClient) : ObservableObject, IHandleClose
{
	[ObservableProperty]
	public partial List<GroupInfoDto> Groups { get; set; } = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(CanJoin))]
	public partial GroupInfoDto? SelectedGroup { get; set; }

	/// <summary>Seaglass 1.1.4: Join is enabled once a group is chosen (it was an enabled "Select" over an empty list).</summary>
	public bool CanJoin => SelectedGroup?.GroupId is not null;

	[ObservableProperty]
	public partial bool HasActiveGroups { get; set; }

	/// <summary>The group this session is in, if any.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsInGroup))]
	public partial Guid? CurrentGroupId { get; private set; }

	public bool IsInGroup => CurrentGroupId is not null;

	/// <summary>"New group" is offered to an account the server lets start groups (unknown: offered, the server decides).</summary>
	public bool CanCreateGroup { get; private set; } = CanCreate(SessionInfo.CurrentUser);

	public static bool CanCreate(UserDto? user) => user?.Policy?.SyncPlayAccess is null or UserPolicy_SyncPlayAccess.CreateAndJoinGroups;

	/// <summary>Under the list when the account may only join.</summary>
	public string CreateHint => CanCreateGroup ? "" : "Your account can join groups on this server, but not start one.";

	public bool HasCreateHint => !CanCreateGroup;

	/// <summary>Why the last action did not work (a refusal from the server), or what it did; read out as it changes.</summary>
	[ObservableProperty]
	public partial string Status { get; private set; } = "";

	/// <summary>"Leave group" was pressed: the dialog asks before leaving.</summary>
	[ObservableProperty]
	public partial bool IsLeaveConfirmShown { get; private set; }

	public bool CanClose { get; set; }

	public async Task Initialize()
	{
		Groups = await jellyfinClient.GetSyncPlayGroups();
		CurrentGroupId = SessionInfo.GroupId;
		SelectedGroup = Groups.FirstOrDefault(x => x.GroupId == CurrentGroupId);
		HasActiveGroups = Groups is { Count: > 0 };
	}

	/// <summary>For tests: an account other than the session's.</summary>
	internal void UseAccount(UserDto? user)
	{
		CanCreateGroup = CanCreate(user);
		OnPropertyChanged(nameof(CanCreateGroup));
		OnPropertyChanged(nameof(CreateHint));
		OnPropertyChanged(nameof(HasCreateHint));
	}

	[RelayCommand]
	private async Task JoinGroup(GroupInfoDto group)
	{
		if (group?.GroupId is not { } id)
		{
			return;
		}

		await jellyfinClient.JoinSyncPlayGroup(id);
		CanClose = true;
	}

	/// <summary>"New group": true when the group started (the dialog closes); else <see cref="Status"/> says why and it stays.</summary>
	public async Task<bool> CreateGroup()
	{
		if (!CanCreateGroup)
		{
			Status = Core.Services.JellyfinClient.SyncPlayRefused;
			return false;
		}

		var refusal = await jellyfinClient.StartSyncPlayGroup();
		Status = refusal ?? "";
		return refusal is null;
	}

	[RelayCommand]
	private void AskToLeave() => IsLeaveConfirmShown = true;

	[RelayCommand]
	private void StayInGroup() => IsLeaveConfirmShown = false;

	[RelayCommand]
	private async Task LeaveGroup()
	{
		await jellyfinClient.LeaveSyncPlayGroup();
		IsLeaveConfirmShown = false;
		CurrentGroupId = null;
		SelectedGroup = null;
		Status = "You left the group.";
	}
}
