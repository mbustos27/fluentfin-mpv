using FluentFin.Core.Settings;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core
{
#nullable disable
	public static class SessionInfo
	{
		public static string ServerId { get; set; }
		// fluentfin-mpv: the signed-in server's saved name, for messages ("Couldn't load Home from <name>"); null = not known
		public static string ServerName { get; set; }
		public static string BaseUrl { get; set; }
		public static UserDto CurrentUser { get; set; }
		public static SavedUser CurrentUserCrendentials { get; set; }
		public static List<PluginInfo> Plugins { get; set; }
		public static string AccessToken { get; set; }
		public static string SessionId { get; set; }
		public static string RemoteSessionId { get; set; }
		public static Guid? GroupId { get; set; }

		// the identity FluentFin authenticates with; the mpv backend reuses it so the server sees one session
		public static string DeviceId { get; set; }
		public static string DeviceName { get; set; }
		public static string ClientName { get; set; }
		public static string ClientVersion { get; set; }

		public static bool CanEditMediaSegments() => CurrentUser?.Policy?.IsAdministrator == true &&
													 Plugins.FirstOrDefault(x => x.Name == "MediaSegments API") is { } &&
													 Plugins.FirstOrDefault(x => x.Name == "Intro Skipper") is { };

		public static bool HasPlaybackReporting() => CurrentUser?.Policy?.IsAdministrator == true &&
													 Plugins.FirstOrDefault(x => x.Name == "Playback Reporting") is { };
	}
#nullable restore
}
