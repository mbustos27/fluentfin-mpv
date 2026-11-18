using System.Collections.ObjectModel;
using System.Reactive.Concurrency;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI.Helpers;
using FluentFin.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Helpers;
using FluentFin.Services;
using FluentFin.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;

namespace FluentFin.Core.ViewModels;

public partial class SelectServerViewModel(ISettings settings,
										   [FromKeyedServices(NavigationRegions.InitialSetup)] INavigationService navigationService,
										   HomeCache homeCache) : ObservableObject
{
	private int _found;

	[ObservableProperty]
	public partial bool HasServers { get; set; } = settings.Servers.Count > 0;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsBusy))]
	public partial bool IsDetecting { get; set; }

	/// <summary>fluentfin-mpv: an address is being checked (docs/research/ux-review.md F2): Next shows progress, inputs wait.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsBusy), nameof(IsIdle))]
	public partial bool IsConnecting { get; set; }

	/// <summary>fluentfin-mpv: what is going on, under the buttons ("Connecting to …", "Looking for servers on this network…").</summary>
	[ObservableProperty]
	public partial string StatusText { get; set; } = "";

	/// <summary>fluentfin-mpv: why the last attempt did not work and what to do, shown on the page instead of in a dialog.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasError))]
	public partial string ErrorMessage { get; set; } = "";

	public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

	public bool IsBusy => IsConnecting || IsDetecting;

	public bool IsIdle => !IsConnecting;

	public ObservableCollection<SavedServer> Servers => settings.Servers;

	/// <summary>fluentfin-mpv: how long an address may take to answer before the page gives up on it and says why.</summary>
	internal TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

	/// <summary>fluentfin-mpv: the server's public info (a seam for tests).</summary>
	internal Func<string, CancellationToken, Task<(PublicSystemInfo? Info, string? Error)>> PublicInfo { get; set; } = JellyfinAuthenticationService.GetPublicInfo;

	/// <summary>fluentfin-mpv: the LAN discovery broadcast (a seam for tests).</summary>
	internal Func<Action<DiscoveryInfo>, Action, Task> Discover { get; set; } = ServerDiscovery.DiscoverServersAsync;

	/// <summary>fluentfin-mpv: the networks this PC is on, remembered with a local address (a seam for tests).</summary>
	internal Func<IReadOnlyList<string>> NetworkNames { get; set; } = () => [.. NetworkHelper.Instance.ConnectionInformation.NetworkNames];

	/// <summary>fluentfin-mpv: the first time (no saved server) the page looks for servers on this network by itself.</summary>
	public Task OnShown() => HasServers || IsDetecting ? Task.CompletedTask : DetectServers();

	[RelayCommand]
	private async Task DetectServers()
	{
		if (IsDetecting)
		{
			return;
		}

		ErrorMessage = "";
		IsDetecting = true;
		if (!IsConnecting)
		{
			StatusText = "Looking for servers on this network…";
		}

		Interlocked.Exchange(ref _found, 0);
		var done = new TaskCompletionSource();
		void Finish(string? error)
		{
			IsDetecting = false;
			if (!IsConnecting)
			{
				StatusText = "";
			}

			// fluentfin-mpv: a search that found nothing says so, and what to do instead
			if (!IsConnecting && (error is not null || Volatile.Read(ref _found) == 0))
			{
				ErrorMessage = error ?? "No Jellyfin server answered on this network. Enter its address above, for example https://jellyfin.example.com:8920.";
			}

			done.TrySetResult();
		}

		try
		{
			await Discover(ServerDiscovered, () => RxSchedulers.MainThreadScheduler.Schedule(() => Finish(null)));
		}
		catch (Exception ex)
		{
			// no network, or broadcasts refused: the button must not spin for ever
			// 1.1.4 (states; layout audit #11): the exception's words go to the log, not onto the page
			Serilog.Log.Warning(ex, "Looking for servers on this network failed");
			Finish("Couldn't look for servers on this network (it may be blocked by a firewall or a VPN). Enter the server's address above instead.");
		}

		await done.Task;
	}

	[RelayCommand]
	private async Task CheckConnectivityAndGoToLogin(string url)
	{
		if (IsConnecting)
		{
			return;
		}

		url = url?.Trim() ?? "";
		if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
		{
			ErrorMessage = string.IsNullOrEmpty(url)
				? "Enter the server's address, for example https://jellyfin.example.com:8920."
				: $"“{url}” is not a full address. Start it with http:// or https://, for example https://jellyfin.example.com:8920.";
			return;
		}

		ErrorMessage = "";
		IsConnecting = true;
		StatusText = $"Connecting to {address.Authority}…";
		PublicSystemInfo? info;
		string? error;
		try
		{
			using var timeout = new CancellationTokenSource(ConnectTimeout);
			(info, error) = await PublicInfo(url, timeout.Token);
		}
		finally
		{
			IsConnecting = false;
			StatusText = "";
		}

		if (info is null)
		{
			ErrorMessage = error ?? $"No Jellyfin server answered at {url}. Check the address and port, then try again.";
			return;
		}

		var server = Servers.FirstOrDefault(x => x.Id == info.Id) ?? new SavedServer()
		{
			Id = info.Id ?? "",
			DisplayName = info.ServerName ?? "",
			LocalUrl = info.LocalAddress ?? ""
		};

		var networkNames = NetworkNames();
		var isLocalAddress = url.IsLocalUrl() || server.LocalNetworkNames.SequenceEqual(networkNames);

		if (isLocalAddress)
		{
			// keep the address that just answered: the server's own LocalAddress can be unreachable from here
			// (loopback like http://[::1]:8096, a container IP), which made every login fail
			server.LocalUrl = url;
			server.LocalNetworkNames = [.. networkNames];
			settings.SaveServerDetails();
		}
		else
		{
			server.PublicUrl = url;
			settings.SaveServerDetails();
		}

		if (!Servers.Any(x => x.Id == info.Id))
		{
			RxSchedulers.MainThreadScheduler.Schedule(() =>
			{
				Servers.Add(server);
				HasServers = true;
			});
		}

		navigationService.NavigateTo<LoginViewModel>(server);
	}

	/// <summary>1.1.4 (states): asks before a saved server goes; set by the page. Without it nothing is removed.</summary>
	public Func<ConfirmPrompt, Task<bool>>? Confirm { get; set; }

	/// <summary>
	/// 1.1.4 (states; layout audit #12): Remove on a saved server's row: after a yes to the same prompt as Settings' Remove
	/// server, the server goes from the list with its saved sign-ins and their saved home pages. Nothing changes on the server.
	/// </summary>
	[RelayCommand]
	private async Task RemoveServer(SavedServer server)
	{
		if (Confirm is not { } confirm || !await confirm(SettingsViewModel.RemoveServerPrompt(server)))
		{
			return;
		}

		Servers.Remove(server);
		HasServers = Servers.Count > 0;
		foreach (var user in server.Users.ToList())
		{
			await homeCache.ForgetLogin(server.Id, user.Username);
		}
	}

	/// <summary>
	/// Which row takes the keyboard after the row at <paramref name="removedIndex"/> went: the one now in its place, the
	/// last one when it was the last, none (the address box) when no server is left.
	/// </summary>
	public static int? FocusAfterRemove(int removedIndex, int remaining) =>
		remaining <= 0 ? null : Math.Clamp(removedIndex, 0, remaining - 1);

	private void ServerDiscovered(DiscoveryInfo info)
	{
		Interlocked.Increment(ref _found);
		var server = Servers.FirstOrDefault(x => x.Id == info.Id) ?? new SavedServer
		{
			Id = info.Id,
			DisplayName = info.Name,
			LocalUrl = info.Address,
			LocalNetworkNames = [.. NetworkNames()]
		};

		if (!Servers.Any(x => x.Id == info.Id))
		{
			RxSchedulers.MainThreadScheduler.Schedule(() =>
			{
				Servers.Add(server);
				HasServers = true;
			});
		}
	}
}
