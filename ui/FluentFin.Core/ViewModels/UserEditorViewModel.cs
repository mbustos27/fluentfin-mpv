using System.Diagnostics;
using System.Reactive.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;

namespace FluentFin.Dialogs.ViewModels;

public partial class UserEditorViewModel([FromKeyedServices(NavigationRegions.UserEditor)] INavigationServiceCore navigationService,
										 IJellyfinClient jellyfinClient) : ObservableObject, INavigationAware
{
	[ObservableProperty]
	public partial UserDto? User { get; set; }

	[ObservableProperty]
	public partial UserEditorSection Section { get; set; } = UserEditorSection.Profile;

	[ObservableProperty]
	public partial UserSectionEditorViewModel? SelectedSectionViewModel { get; set; }

	/// <summary>Nixie 1.2.0 (admin, D36): how the last Save went; null when there is nothing to say.</summary>
	[ObservableProperty]
	public partial ServerNotice? Notice { get; set; }

	public Task OnNavigatedFrom()
	{
		return Task.CompletedTask;
	}

	public Task OnNavigatedTo(object parameter)
	{
		if (parameter is UserEditorViewModelNavigationParameter args)
		{
			User = args.User;
			Section = args.Section;
		}

		this.WhenAnyValue(x => x.Section)
			.Select(ConvertToPageKey)
			.Subscribe(key => navigationService.NavigateTo(key, User));

		return Task.CompletedTask;
	}

	private string ConvertToPageKey(UserEditorSection section)
	{
		return section switch
		{
			UserEditorSection.Profile => typeof(UserProfileEditorViewModel).FullName!,
			UserEditorSection.Access => typeof(UserAccessEditorViewModel).FullName!,
			UserEditorSection.ParentalControl => typeof(UserParentalControlEditorViewModel).FullName!,
			UserEditorSection.Password => typeof(UserPasswordEditorViewModel).FullName!,
			_ => throw new UnreachableException()
		};
	}

	[RelayCommand]
	private async Task Save()
	{
		if (User is not { Policy: not null })
		{
			return;
		}

		Notice = await jellyfinClient.UpdatePolicy(User, User.Policy)
			? new ServerNotice(ServerPowerState.None, "Saved", $"{User.Name}'s settings are saved on the server.", NoticeKind.Success)
			: new ServerNotice(ServerPowerState.Failed, "Not saved", "The server didn't accept the changes. Try again, or Undo changes.", NoticeKind.Error);
	}

	[RelayCommand]
	private async Task Reset()
	{
		if (User?.Id is not { } id)
		{
			return;
		}

		var user = await jellyfinClient.GetUser(id);

		if (user is null)
		{
			return;
		}

		User = user;
		Notice = null;
		SelectedSectionViewModel?.OnNavigatedTo(user);
	}
}
