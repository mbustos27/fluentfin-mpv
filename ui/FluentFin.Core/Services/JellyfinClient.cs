using System.Reflection;
using DeviceId;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.WebSockets;
using Flurl;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.Services;

public partial class JellyfinClient(ILogger<JellyfinClient> logger,
									IObserver<IOutboundWebSocketMessage> socketMessageSender,
									IJumpListService jumpListService,
									IDeviceProfileFactory deviceProfileFactory,
									Settings.ISettings settings,
									HomeCache homeCache) : IJellyfinClient // fluentfin-mpv: homeCache
{
	private Jellyfin.Sdk.JellyfinApiClient _jellyfinApiClient = null!;
	private string _token = "";
	private Jellyfin.Sdk.JellyfinSdkSettings _settings = null!;
	private string _deviceId = "";
	private PlaybackProgressInfo? _currentItem = null;

	public Guid UserId { get; set; }
	public string BaseUrl { get; set; } = "";

	public async Task Initialize(string baseUrl, AuthenticationResult authResult)
	{
		ArgumentNullException.ThrowIfNull(authResult.User);
		ArgumentNullException.ThrowIfNull(authResult.User.Id);
		ArgumentNullException.ThrowIfNullOrEmpty(authResult.AccessToken);

		SessionInfo.CurrentUser = authResult.User;
		SessionInfo.BaseUrl = baseUrl;
		SessionInfo.ServerId = authResult.ServerId;
		SessionInfo.AccessToken = authResult.AccessToken;
		SessionInfo.SessionId = authResult.SessionInfo?.Id ?? "";

		UserId = authResult.User.Id.Value;
		BaseUrl = baseUrl;

		// fluentfin-mpv: a sign-in makes the home page in memory stale, and tells it which saved login this user's page is for
		homeCache.SignedIn(baseUrl, UserId, authResult.ServerId, authResult.User.Name);

		_token = authResult.AccessToken;
		_deviceId = new DeviceIdBuilder().OnWindows(windows => windows.AddWindowsDeviceId()).ToString();
		_settings = new Jellyfin.Sdk.JellyfinSdkSettings();
		SessionInfo.ClientName = AppIdentity.ClientName; // Nixie (was "Seaglass", and "FluentFin MPV" before that)
		SessionInfo.ClientVersion = AppVersion.Current; // fluentfin-mpv: the release version ("1.0.0")
		SessionInfo.DeviceName = Environment.MachineName;
		SessionInfo.DeviceId = _deviceId;
		_settings.Initialize(SessionInfo.ClientName, SessionInfo.ClientVersion, SessionInfo.DeviceName, SessionInfo.DeviceId);
		_settings.SetAccessToken(_token);
		_settings.SetServerUrl(baseUrl);
		ForgetUserViews(); // fluentfin-mpv: another server or user (GetUserViews)
		_jellyfinApiClient = new Jellyfin.Sdk.JellyfinApiClient(new Jellyfin.Sdk.JellyfinRequestAdapter(new Jellyfin.Sdk.JellyfinAuthenticationProvider(_settings), _settings));

		// fluentfin-mpv: capabilities, plugins and views do not depend on each other: asked together they cost one round
		// trip instead of three before the window and Home show. The views stay in GetUserViews, so Home does not ask again.
		// (startup-perf, O13) The capabilities carry the device profile, made ready first (the Windows player's probe; it
		// was null for that player): the plugins and views are asked meanwhile, and with any other player nothing waits.
		var plugins = GetPlugins();
		var views = GetUserViews();
		await PrepareDeviceProfile();
		var capabilities = PostCapabilities();

		await Task.WhenAll(capabilities, plugins, views);
		StartWebSocketConnection();

		await jumpListService.Initialize(await views);
	}

	/// <summary>
	/// fluentfin-mpv (startup-perf, O13): the device profile ready for the selected player. A probe that fails (the Windows
	/// player's codec and audio queries) does not fail the sign-in or the play: it is logged, and tried again next time.
	/// </summary>
	private async Task PrepareDeviceProfile()
	{
		try
		{
			await deviceProfileFactory.PrepareAsync();
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "The device profile could not be prepared for the selected player; it is tried again next time");
		}
	}

	/// <summary>What this session tells the server it can do: the selected player's device profile and remote-control commands.</summary>
	private Task PostCapabilities() => _jellyfinApiClient.Sessions.Capabilities.Full.PostAsync(new ClientCapabilitiesDto
	{
		SupportsMediaControl = true,
		PlayableMediaTypes = [MediaType.Video],
		DeviceProfile = deviceProfileFactory.GetDeviceProfile(),
		IconUrl = "https://github.com/insomniachi/FluentFin/raw/master/Installer/base-icon-transparent.png",
		SupportedCommands = SupportedCommands(settings.MediaPlayer),
	});

	/// <summary>
	/// Seaglass 1.1.4 (interaction audit, item 17): the player changed in Settings: the server hears the new player's profile
	/// and commands at once (they were sent only at sign-in, so remote control followed the change after the next one). Never
	/// throws; false when the server could not be told (it hears it at the next sign-in).
	/// </summary>
	public async Task<bool> UpdateCapabilities()
	{
		try
		{
			await PrepareDeviceProfile();
			await PostCapabilities();
			return true;
		}
		catch (Exception ex)
		{
			logger.LogWarning("The server could not be told of the new player; it hears it at the next sign-in: {Error}", ex.Message);
			return false;
		}
	}

	/// <summary>
	/// The remote-control commands the server may send to this session. With the mpv player FluentFin forwards volume and
	/// track commands to the backend (docs/BRIDGE.md), the other players do not handle them. Sent at sign-in and when the
	/// player changes (<see cref="UpdateCapabilities"/>).
	/// </summary>
	internal static List<GeneralCommandType?> SupportedCommands(Contracts.Services.MediaPlayerType player) => player == Contracts.Services.MediaPlayerType.Mpv
		? [
			GeneralCommandType.DisplayMessage,
			GeneralCommandType.SetVolume,
			GeneralCommandType.VolumeUp,
			GeneralCommandType.VolumeDown,
			GeneralCommandType.Mute,
			GeneralCommandType.Unmute,
			GeneralCommandType.ToggleMute,
			GeneralCommandType.SetAudioStreamIndex,
			GeneralCommandType.SetSubtitleStreamIndex,
		]
		: [GeneralCommandType.DisplayMessage];

	// Nixie 1.5 (search): SuggestionKinds and Search moved to JellyfinClient_Search.cs

	public async Task Logout()
	{
		// fluentfin-mpv: the user's saved home page (their watch history) does not outlive the session; first, as it needs no server
		await homeCache.Forget(BaseUrl, UserId);

		try
		{
			await _jellyfinApiClient.Sessions.Logout.PostAsync();
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
		}
	}

	public async Task<int> BitrateTest()
	{
		const int bitrateTestSize = 1024 * 1024 * 3;
		const int chunkSize = 64 * 1024;
		const double safetyRatio = 0.8;
		var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

		int totalBytesRead = 0;
		long startTime = TimeProvider.System.GetTimestamp();
		try
		{
			var stream = await _jellyfinApiClient.Playback.BitrateTest.GetAsync(x => x.QueryParameters.Size = bitrateTestSize, cts.Token);

			if (stream is null)
			{
				return 0;
			}

			var buffer = new byte[chunkSize];

			while (true)
			{
				int bytesRead = await stream.ReadAsync(buffer, cts.Token);
				if (bytesRead == 0)
				{
					break;
				}

				totalBytesRead += bytesRead;
			}
		}
		catch (OperationCanceledException) { }

		double responseTimeSeconds = TimeProvider.System.GetElapsedTime(startTime).TotalSeconds;
		double bytesPerSecond = totalBytesRead / responseTimeSeconds;
		int bitrate = (int)Math.Round(bytesPerSecond * 8 * safetyRatio);
		return bitrate;
	}

	public async Task<SystemInfo?> GetSystemInfo()
	{
		try
		{
			return await _jellyfinApiClient.System.Info.GetAsync();
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return null;
		}
	}

	public async Task<SystemStorageDto?> GetSystemStorage()
	{
		try
		{
			return await _jellyfinApiClient.System.Info.Storage.GetAsync();
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return null;
		}
	}

	public async Task<List<SessionInfoDto>> GetActiveSessions()
	{
		try
		{
			return await _jellyfinApiClient.Sessions.GetAsync(x => x.QueryParameters.ActiveWithinSeconds = 960) ?? [];
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return [];
		}
	}

	public async Task<ActivityLogEntryQueryResult?> GetActivities(DateTimeOffset minDate, bool hasUserId)
	{
		try
		{
			return await _jellyfinApiClient.System.ActivityLog.Entries.GetAsync(x =>
			{
				var query = x.QueryParameters;
				query.StartIndex = 0;
				query.Limit = 7;
				query.MinDate = minDate;
				query.HasUserId = hasUserId;
			});
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return null;
		}
	}

	public async Task<ActivityLogEntryQueryResult?> GetAllActivities()
	{
		try
		{
			return await _jellyfinApiClient.System.ActivityLog.Entries.GetAsync(x =>
			{
				var query = x.QueryParameters;
				query.StartIndex = 0;
			});
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return null;
		}
	}

	public async Task<List<TaskInfo>> GetScheduledTasks(bool? isEnabled)
	{
		try
		{
			return await _jellyfinApiClient.ScheduledTasks.GetAsync(x => x.QueryParameters.IsEnabled = isEnabled) ?? [];
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return [];
		}
	}

	public async Task RunTask(string? taskId)
	{
		try
		{
			await _jellyfinApiClient.ScheduledTasks.Running[taskId].PostAsync();
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return;
		}
	}

	// Nixie 1.2.0 (admin): Restart, Shutdown and ScanAllLibraries say whether the server took the request (the dashboard shows it)
	public async Task<bool> Restart()
	{
		try
		{
			await _jellyfinApiClient.System.Restart.PostAsync();
			return true;
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return false;
		}
	}

	public async Task<bool> Shutdown()
	{
		try
		{
			await _jellyfinApiClient.System.Shutdown.PostAsync();
			return true;
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return false;
		}
	}

	public async Task<bool> ScanAllLibraries()
	{
		try
		{
			await _jellyfinApiClient.Library.Refresh.PostAsync();
			return true;
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return false;
		}
	}

	public async Task<ServerConfiguration?> GetConfiguration()
	{
		try
		{
			return await _jellyfinApiClient.System.Configuration.GetAsync();
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return null;
		}
	}

	public async Task SaveConfiguration(ServerConfiguration configuration) => await TrySaveConfiguration(configuration);

	// 1.5 fixes
	public async Task<bool> TrySaveConfiguration(ServerConfiguration configuration)
	{
		try
		{
			await _jellyfinApiClient.System.Configuration.PostAsync(configuration);
			return true;
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return false;
		}
	}
	// END 1.5 fixes

	public async Task<EndPointInfo?> EndpointInfo()
	{
		try
		{
			return await _jellyfinApiClient.System.Endpoint.GetAsync();
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return null;
		}
	}

	public async Task<List<FileSystemEntryInfo>> GetDirectoryContents(string path)
	{
		try
		{
			return await _jellyfinApiClient.Environment.DirectoryContents.GetAsync(x =>
			{
				var query = x.QueryParameters;
				query.IncludeDirectories = true;
				query.Path = path;
			}) ?? [];
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return [];
		}
	}

	public async Task<List<TaskInfo>> GetScheduledTasks()
	{
		try
		{
			return await _jellyfinApiClient.ScheduledTasks.GetAsync(x => x.QueryParameters.IsHidden = false) ?? [];
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return [];
		}
	}

	public async Task<bool> RunScheduledTask(string id)
	{
		try
		{
			await _jellyfinApiClient.ScheduledTasks.Running[id].PostAsync();
			return true; // Nixie 1.2.0 (admin, D43): the page says "Started" only then
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return false;
		}
	}

	public async Task SendMessage(string sessionId, MessageCommand command)
	{
		try
		{
			await _jellyfinApiClient.Sessions[sessionId].Message.PostAsync(command);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return;
		}
	}

	private Uri AddApiKey(Uri uri)
	{
		return uri.WithApiKey(_token);
	}
}


