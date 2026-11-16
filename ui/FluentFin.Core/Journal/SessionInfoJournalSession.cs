using FluentFin.Core.Settings;

namespace FluentFin.Core.Journal;

/// <summary>
/// The journal of whoever FluentFin is signed in as (<see cref="SessionInfo"/>, set at login): keyed by the server's id and
/// the user's id (decision 6). A server that sent no id is keyed by its address (scheme, host, port and path, nothing else).
/// </summary>
public sealed class SessionInfoJournalSession(ISettings? settings = null) : IJournalSession
{
	public JournalProfile? Current
	{
		get
		{
			if (SessionInfo.CurrentUser?.Id is not { } userId || userId == Guid.Empty || string.IsNullOrEmpty(SessionInfo.BaseUrl))
			{
				return null;
			}

			var serverId = SessionInfo.ServerId;
			if (string.IsNullOrWhiteSpace(serverId))
			{
				if (!Uri.TryCreate(SessionInfo.BaseUrl, UriKind.Absolute, out var url))
				{
					return null;
				}

				serverId = $"url:{url.Scheme}://{url.IdnHost}:{url.Port}{url.AbsolutePath.TrimEnd('/')}";
			}

			var serverName = settings?.Servers?.FirstOrDefault(x => !string.IsNullOrEmpty(x.Id) && x.Id == SessionInfo.ServerId)?.DisplayName;
			return new JournalProfile(serverId, userId, string.IsNullOrWhiteSpace(serverName) ? null : serverName, SessionInfo.CurrentUser.Name, SessionInfo.BaseUrl);
		}
	}
}
