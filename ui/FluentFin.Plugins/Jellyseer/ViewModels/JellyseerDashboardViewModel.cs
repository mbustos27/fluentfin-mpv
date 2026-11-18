using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core;
using FluentFin.Plugins.Jellyseer.Models;

namespace FluentFin.Plugins.Jellyseer.ViewModels;

public partial class JellyseerDashboardViewModel(IJellyseerClient jellyseerClient) : ObservableObject, INavigationAware
{
	[ObservableProperty]
	public partial bool HasServer { get; set; }

	[ObservableProperty]
	public partial ObservableCollection<MediaRequest> Requests { get; set; } = [];

	public IJellyseerClient Client { get; } = jellyseerClient;

	public Task OnNavigatedFrom() => Task.CompletedTask;

	public async Task OnNavigatedTo(object parameter)
	{
		HasServer = HasAssociatedServer();

		if (!HasServer)
		{
			return;
		}

		var loggedIn = await Client.Login();

		if (loggedIn)
		{
			var response = await Client.GetRequests();
			Requests = new(response?.Results ?? []);
		}
	}

	private static bool HasAssociatedServer()
	{
		if(JellyseerOptions.Current.ServerMapping is not { Count : > 0 })
		{
			return false;
		}
		return JellyseerOptions.Current.ServerMapping.ContainsKey(SessionInfo.ServerId);
	}

	/// <summary>The address set up for this server (the form shows it when the address is changed), or empty.</summary>
	public string CurrentAddress => JellyseerOptions.Current.ServerMapping is { } mapping && mapping.TryGetValue(SessionInfo.ServerId, out var url) ? url : "";

	/// <summary>
	/// 1.2.0 (the button review, D92): once connected there was no way to change the address. The form comes back with the
	/// address in it; the requests stay until a new address is connected (SetServerUrl keeps the old one when the new fails).
	/// </summary>
	[RelayCommand]
	private void ChangeAddress()
	{
		OnPropertyChanged(nameof(CurrentAddress));
		HasServer = false;
	}

	[RelayCommand]
	private async Task SetServerUrl(string url)
	{
		// Seaglass 1.1.3: the address is kept only when the login with it works; a failed or throwing login puts back what was
		// there, so Settings > Integrations never shows an address that was never set up
		var mapping = JellyseerOptions.Current.ServerMapping;
		var serverId = SessionInfo.ServerId;
		var had = mapping.TryGetValue(serverId, out var previous);
		mapping[serverId] = url;
		var loggedIn = false;
		try
		{
			loggedIn = await Client.Login();
		}
		finally
		{
			if (!loggedIn)
			{
				if (had)
				{
					mapping[serverId] = previous!;
				}
				else
				{
					mapping.Remove(serverId);
				}
			}
		}

		if(loggedIn)
		{
			JellyseerOptions.Current.Save?.Invoke();
			HasServer = true;
			var response = await Client.GetRequests();
			Requests = new(response?.Results ?? []);
		}
	}
}
