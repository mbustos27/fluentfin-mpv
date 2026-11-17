namespace FluentFin.Core.Mpv;

/// <summary>
/// fluentfin-mpv: the intros the backend learned from the user's own skips live in <see cref="FileName"/> in its config
/// folder (docs/BRIDGE.md §4 <c>set_skip_options</c>); forgetting them is deleting that file, which the backend notices.
/// </summary>
public static class LearnedIntros
{
	public const string FileName = "learned-intros.json";

	/// <summary>The backend's config folder: the same as WindowsMpvBackendLauncher.ConfigDirectory.</summary>
	public static string ConfigDirectory(KnownFolders folders) => Path.Combine(folders.ApplicationData, "mpv-backend");

	/// <summary>Deletes the learned intros; true when there was nothing, or they are gone. Never throws.</summary>
	public static bool Forget(string configDirectory)
	{
		try
		{
			File.Delete(Path.Combine(configDirectory, FileName));
			return true;
		}
		catch (DirectoryNotFoundException)
		{
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}
}
