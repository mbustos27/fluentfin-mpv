using System.Collections.ObjectModel;
using System.Reactive.Linq;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Admin;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;

namespace FluentFin.Core.ViewModels;

/// <remarks>
/// Nixie 1.2.0 (admin, button review A75-A77): Restart and Shut down ask first, naming the server (<see cref="Confirm"/>), send
/// the request while still signed in (they used to sign out first, so the server refused it), then say what is happening
/// (<see cref="Notice"/>); a restart is followed until the server answers again. "Scan all libraries" scans whether or not a
/// scheduled task of that name exists, and says it started.
/// </remarks>
public partial class DashboardViewModel(IJellyfinClient jellyfinClient,
										[FromKeyedServices(UserInputs.MessageToSession)]IUserInput<string> stringUserInput,
										IContentDialogServiceCore contentDialogService) : ObservableObject, INavigationAware
{
	/// <summary>How often a restarting server is asked whether it is back, and for how long.</summary>
	internal static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);
	internal const int PollsBeforeGivingUp = 90;
	/// <summary>Answers this many polls in a row without ever being seen down: it restarted between two polls.</summary>
	internal const int PollsUpCountAsBack = 10;

	private IDisposable? _sessionsSubscription;

	/// <summary>Asks before a destructive action; true only when the user confirmed. Unset (nobody to ask), nothing happens.</summary>
	public Func<ConfirmPrompt, Task<bool>>? Confirm { get; set; }

	/// <summary>The wait between polls of a restarting server (tests make it instant).</summary>
	public Func<TimeSpan, Task> Delay { get; set; } = Task.Delay;

	/// <summary>What the server is doing after a restart, shut down or scan from here; null when there is nothing to say.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsBusy), nameof(HasNotice), nameof(IsWaiting))]
	[NotifyCanExecuteChangedFor(nameof(RestartCommand), nameof(ShutdownCommand), nameof(StartScanCommand))]
	public partial ServerNotice? Notice { get; set; }

	/// <summary>A restart is under way, or the server is shutting down: the buttons wait.</summary>
	public bool IsBusy => Notice is { Busy: true } or { State: ServerPowerState.ShutDown };

	/// <summary>The notice bar opens from this bool (x:Bind does not call a function whose argument is null, so a bar opened through one never closed).</summary>
	public bool HasNotice => Notice is not null;

	/// <summary>Waiting for the server: the notice shows a progress bar.</summary>
	public bool IsWaiting => Notice is { Busy: true };

	[ObservableProperty]
	public partial SystemInfo? SystemInfo { get; set; }

	[ObservableProperty]
	public partial ObservableCollection<SessionInfoDtoViewModel> ActiveSessions { get; set; } = [];

	[ObservableProperty]
	public partial List<ActivityLogEntry> UserActivities { get; set; } = [];

	[ObservableProperty]
	public partial List<ActivityLogEntry> OtherActivities { get; set; } = [];

	[ObservableProperty]
	public partial List<NameValuePair> Paths { get; set; } = [];

	[ObservableProperty]
	public partial string Version { get; set; } = AppVersion.Current;


	public Task OnNavigatedFrom()
	{
		// disposing the SDK subscription sends SessionsStop
		_sessionsSubscription?.Dispose();
		_sessionsSubscription = null;
		return Task.CompletedTask;
	}

	public async Task OnNavigatedTo(object parameter)
	{
		SystemInfo = await jellyfinClient.GetSystemInfo();
		ActiveSessions = [..(await jellyfinClient.GetActiveSessions()).Where(x => x.Id != SessionInfo.SessionId).Select(CreateViewModel)];

		var storage = await jellyfinClient.GetSystemStorage();
		Paths = [
			new NameValuePair { Name = "Cache", Value = storage?.CacheFolder?.Path },
			new NameValuePair { Name = "Logs", Value = storage?.LogFolder?.Path },
			new NameValuePair { Name = "Metadata", Value = storage?.InternalMetadataFolder?.Path },
			new NameValuePair { Name = "Transcodes", Value = storage?.TranscodingTempFolder?.Path },
			new NameValuePair { Name = "Web", Value = storage?.WebFolder?.Path },
		];

		var userActivityResult = await jellyfinClient.GetActivities(TimeProvider.System.GetUtcNow().AddDays(-1), true);
		if (userActivityResult is { Items.Count: > 0 })
		{
			UserActivities = userActivityResult.Items;
		}

		var otherActivityResult = await jellyfinClient.GetActivities(TimeProvider.System.GetUtcNow().AddDays(-7), false);
		if (otherActivityResult is { Items.Count: > 0 })
		{
			OtherActivities = otherActivityResult.Items;
		}

		_sessionsSubscription?.Dispose();
		_sessionsSubscription = jellyfinClient.ObserveWebSocket<SessionsMessage>()
			.ObserveOn(RxSchedulers.MainThreadScheduler)
			.Select(x => x.Data?.Where(x => x.Id != SessionInfo.SessionId) ?? [])
			.Subscribe(sessions =>
			{
				foreach (var dto in sessions)
				{
					if(dto.LastActivityDate is { } date && (TimeProvider.System.GetUtcNow() - date).TotalMinutes > 30)
					{
						continue;
					}

					if(ActiveSessions.FirstOrDefault(x => x.Id == dto.Id) is { } vm)
					{
						vm.Update(dto);
					}
					else
					{
						ActiveSessions.Add(CreateViewModel(dto));
					}
				}
			});
	}

	private bool CanAct() => !IsBusy;

	private string ServerName => SystemInfo?.ServerName is { } name && !string.IsNullOrWhiteSpace(name) ? name.Trim() : "The server";

	[RelayCommand(CanExecute = nameof(CanAct))]
	private async Task Shutdown()
	{
		if (!await Asked(AdminPrompts.ShutDown(SystemInfo?.ServerName)))
		{
			return;
		}

		Notice = await jellyfinClient.Shutdown()
			? new ServerNotice(ServerPowerState.ShutDown, $"{ServerName} is shutting down", "It stays off until someone starts it again on its computer.", NoticeKind.Warning)
			: new ServerNotice(ServerPowerState.Failed, $"{ServerName} didn't shut down", "The server didn't accept the request. Nothing changed.", NoticeKind.Error);
	}

	[RelayCommand(CanExecute = nameof(CanAct))]
	private async Task Restart()
	{
		if (!await Asked(AdminPrompts.Restart(SystemInfo?.ServerName)))
		{
			return;
		}

		if (!await jellyfinClient.Restart())
		{
			Notice = new ServerNotice(ServerPowerState.Failed, $"{ServerName} didn't restart", "The server didn't accept the request. Nothing changed.", NoticeKind.Error);
			return;
		}

		Notice = new ServerNotice(ServerPowerState.Restarting, $"Restarting {ServerName}…", "Playback on every device stops. This page goes on once the server answers again.", NoticeKind.Info, Busy: true);
		Notice = await WaitUntilBack()
			? new ServerNotice(ServerPowerState.Back, $"{ServerName} restarted", "It is answering again.", NoticeKind.Success)
			: new ServerNotice(ServerPowerState.NotBack, $"{ServerName} hasn't come back yet", "It may still be starting. If it doesn't, start it again on its computer.", NoticeKind.Warning);
	}

	/// <summary>
	/// After a restart was accepted: true once the server answers again having been seen down (or answering for a while, when it
	/// restarted between two polls), false after <see cref="PollsBeforeGivingUp"/> polls.
	/// </summary>
	private async Task<bool> WaitUntilBack()
	{
		var sawDown = false;
		for (var poll = 1; poll <= PollsBeforeGivingUp; poll++)
		{
			await Delay(PollEvery);
			var info = await jellyfinClient.GetSystemInfo();
			if (info is null || info.IsShuttingDown == true)
			{
				sawDown = true;
				continue;
			}

			if (sawDown || poll >= PollsUpCountAsBack)
			{
				SystemInfo = info;
				return true;
			}
		}

		return false;
	}

	[RelayCommand(CanExecute = nameof(CanAct))]
	private async Task StartScan()
	{
		Notice = await jellyfinClient.ScanAllLibraries()
			? new ServerNotice(ServerPowerState.None, "Scan started", "The server is looking for new and changed files in every library. Scheduled tasks shows its progress.", NoticeKind.Success)
			: new ServerNotice(ServerPowerState.Failed, "The scan didn't start", "The server didn't accept the request.", NoticeKind.Error);
	}

	private async Task<bool> Asked(ConfirmPrompt prompt) => Confirm is { } confirm && await confirm(prompt);

	private SessionInfoDtoViewModel CreateViewModel(SessionInfoDto dto)
	{
		return new SessionInfoDtoViewModel(dto, jellyfinClient, contentDialogService, stringUserInput);
	}
}

/// <summary>Nixie 1.2.0 (admin): what the dashboard says the server is doing after a restart, shut down or scan.</summary>
public enum ServerPowerState
{
	None,
	Restarting,
	Back,
	NotBack,
	ShutDown,
	Failed,
}

/// <summary>Nixie 1.2.0 (admin): how a notice reads (the page shows it as an InfoBar of that severity).</summary>
public enum NoticeKind
{
	Info,
	Success,
	Warning,
	Error,
}

/// <param name="Busy">Under way: the page shows a progress ring and the buttons wait.</param>
public sealed record ServerNotice(ServerPowerState State, string Title, string Message, NoticeKind Kind, bool Busy = false);
