using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.Services
{
	public partial class JellyfinClient
	{
		public async Task<List<GroupInfoDto>> GetSyncPlayGroups()
		{
			try
			{
				return await _jellyfinApiClient.SyncPlay.List.GetAsync() ?? [];
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Unhandled Exception");
				return [];
			}
		}

		public async Task CreateSyncPlayGroup()
		{
			try
			{
				await _jellyfinApiClient.SyncPlay.New.PostAsync(new NewGroupRequestDto
				{
					GroupName = $"{SessionInfo.CurrentUser.Name}'s Group"
				});
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Unhandled Exception");
				return;
			}
		}

		/// <summary>
		/// Seaglass 1.1.4 (review): starts a group, and says why not when the server refuses: null when it started. The refusal
		/// was swallowed (CreateSyncPlayGroup), so "New group" did nothing for an account allowed only to join.
		/// </summary>
		public async Task<string?> StartSyncPlayGroup()
		{
			try
			{
				await _jellyfinApiClient.SyncPlay.New.PostAsync(new NewGroupRequestDto
				{
					GroupName = $"{SessionInfo.CurrentUser?.Name}'s Group"
				});
				return null;
			}
			catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode is 401 or 403)
			{
				logger.LogInformation("SyncPlay: the server does not let this account start a group ({Status})", ex.ResponseStatusCode);
				return SyncPlayRefused;
			}
			catch (Exception ex)
			{
				logger.LogWarning("SyncPlay: starting a group failed: {Error}", ex.Message);
				return $"Couldn't start a group on {ViewModels.LoadMessages.ServerName()}. Check the connection, then try again.";
			}
		}

		public const string SyncPlayRefused = "This server doesn't let your account start a SyncPlay group. You can still join one.";

		public async Task JoinSyncPlayGroup(Guid groupId)
		{
			try
			{
				await _jellyfinApiClient.SyncPlay.Join.PostAsync(new JoinGroupRequestDto
				{
					GroupId = groupId
				});
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Unhandled Exception");
				return;
			}
		}

		public async Task LeaveSyncPlayGroup()
		{
			try
			{
				await _jellyfinApiClient.SyncPlay.Leave.PostAsync();
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Unhandled Exception");
				return;
			}
		}

		public async Task SignalReadyForSyncPlay(ReadyRequestDto request)
		{
			if (SessionInfo.GroupId is null)
			{
				return;
			}

			try
			{
				await _jellyfinApiClient.SyncPlay.Ready.PostAsync(request);
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Unhandled Exception");
				return;
			}
		}

		public async Task SignalPauseForSyncPlay()
		{
			if (SessionInfo.GroupId is null)
			{
				return;
			}

			try
			{
				await _jellyfinApiClient.SyncPlay.Pause.PostAsync();
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Unhandled Exception");
				return;
			}
		}

		public async Task SignalUnpauseForSyncPlay()
		{
			if (SessionInfo.GroupId is null)
			{
				return;
			}

			try
			{
				await _jellyfinApiClient.SyncPlay.Unpause.PostAsync();
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Unhandled Exception");
				return;
			}
		}

		public async Task SignalSeekForSyncPlay(TimeSpan position)
		{
			if (SessionInfo.GroupId is null)
			{
				return;
			}

			try
			{
				await _jellyfinApiClient.SyncPlay.Seek.PostAsync(new SeekRequestDto
				{
					PositionTicks = position.Ticks,
				});
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Unhandled Exception");
				return;
			}
		}

		public async Task<DateTimeOffset> SyncTime()
		{
			var localTime = TimeProvider.System.GetUtcNow();
			try
			{
				var response = await _jellyfinApiClient.GetUtcTime.GetAsync();
				if (response is null || response.RequestReceptionTime is null)
				{
					return localTime;
				}

				var pingStart = TimeProvider.System.GetTimestamp();
				var result = await _jellyfinApiClient.System.Ping.GetAsync();
				var ping = TimeProvider.System.GetElapsedTime(pingStart);
				var diff = (response.RequestReceptionTime.Value - (ping / 2)) - localTime;

				return localTime + diff;
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Unhandled Exception");
				return localTime;
			}
		}

		public async Task SignalNewPlaylist(PlayRequestDto request)
		{
			try
			{
				await _jellyfinApiClient.SyncPlay.SetNewQueue.PostAsync(request);
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Unhandled Exception");
				throw;
			}
		}
	}
}
