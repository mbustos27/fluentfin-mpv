using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.ViewModels;

namespace FluentFin.Core.Admin;

/// <summary>
/// Nixie 1.2.0 (admin, review fix): the server's restart and shut down, and what the dashboard says about them, kept for the
/// app's lifetime (a singleton). The dashboard's view model is new on each visit: when it held this, leaving during
/// "Restarting…" and coming back enabled Restart again while the first wait ran, and a second restart could be sent. Any
/// dashboard shows this state, for the server it is about; one restart or shut down at a time.
/// </summary>
public partial class ServerPower(IJellyfinClient jellyfinClient) : ObservableObject
{
	/// <summary>How often a restarting server is asked whether it is back, and for how long.</summary>
	internal static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);
	internal const int PollsBeforeGivingUp = 90;
	/// <summary>Answers this many polls in a row without ever being seen down: it restarted between two polls.</summary>
	internal const int PollsUpCountAsBack = 10;

	/// <summary>The wait between polls of a restarting server (tests make it instant, or hold it).</summary>
	public Func<TimeSpan, Task> Delay { get; set; } = Task.Delay;

	/// <summary>The server the notice is about (SessionInfo.ServerId when it was set).</summary>
	public string? ServerId { get; private set; }

	/// <summary>The last restart or shut down's notice; null before any.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsBusy))]
	public partial ServerNotice? Notice { get; private set; }

	/// <summary>A restart is under way, or the server was shut down: nothing more is sent to it from here.</summary>
	public bool IsBusy => Notice is { Busy: true } or { State: ServerPowerState.ShutDown };

	/// <summary>The notice when it is about <paramref name="serverId"/> (a dashboard of another server shows none).</summary>
	public ServerNotice? NoticeFor(string? serverId) => serverId == ServerId ? Notice : null;

	/// <summary>True when this server is restarting or shut down (the buttons wait).</summary>
	public bool IsBusyFor(string? serverId) => serverId == ServerId && IsBusy;

	/// <summary>Sends the restart (once: nothing while one is under way) and follows it until the server answers again.</summary>
	public async Task Restart(string serverId, string serverName)
	{
		if (IsBusyFor(serverId))
		{
			return;
		}

		ServerId = serverId;
		// busy before the request goes, so a second dashboard cannot send another meanwhile
		Notice = new ServerNotice(ServerPowerState.Restarting, $"Restarting {serverName}…", "Playback on every device stops. This page goes on once the server answers again.", NoticeKind.Info, Busy: true);
		if (!await jellyfinClient.Restart())
		{
			Notice = new ServerNotice(ServerPowerState.Failed, $"{serverName} didn't restart", "The server didn't accept the request. Nothing changed.", NoticeKind.Error);
			return;
		}

		Notice = await WaitUntilBack()
			? new ServerNotice(ServerPowerState.Back, $"{serverName} restarted", "It is answering again.", NoticeKind.Success)
			: new ServerNotice(ServerPowerState.NotBack, $"{serverName} hasn't come back yet", "It may still be starting. If it doesn't, start it again on its computer.", NoticeKind.Warning);
	}

	/// <summary>Sends the shut down (once); the server then stays off, and nothing more is sent to it from here.</summary>
	public async Task ShutDown(string serverId, string serverName)
	{
		if (IsBusyFor(serverId))
		{
			return;
		}

		ServerId = serverId;
		Notice = new ServerNotice(ServerPowerState.Restarting, $"Shutting down {serverName}…", "Playback on every device stops.", NoticeKind.Info, Busy: true);
		Notice = await jellyfinClient.Shutdown()
			? new ServerNotice(ServerPowerState.ShutDown, $"{serverName} is shutting down", "It stays off until someone starts it again on its computer.", NoticeKind.Warning)
			: new ServerNotice(ServerPowerState.Failed, $"{serverName} didn't shut down", "The server didn't accept the request. Nothing changed.", NoticeKind.Error);
	}

	/// <summary>Clears the notice (its bar was closed); not while a restart is under way.</summary>
	public void Dismiss()
	{
		if (Notice is { Busy: false })
		{
			Notice = null;
		}
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
				return true;
			}
		}

		return false;
	}
}
