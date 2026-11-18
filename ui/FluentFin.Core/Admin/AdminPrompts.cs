namespace FluentFin.Core.Admin;

/// <summary>
/// Nixie 1.2.0 (admin, button review §2.8 and §4): the questions the server administration and the item dialogs ask before
/// a change the server cannot undo. Each names what goes, says what it costs, and its confirm button is the verb itself
/// ("Delete user", never "Yes"); Cancel is the default (DestructiveConfirm in the app).
/// </summary>
public static class AdminPrompts
{
	public static ConfirmPrompt Restart(string? serverName) => new(
		$"Restart {Name(serverName, "the server")}?",
		"Everyone watching will be interrupted. The server is usually back within a minute.",
		"Restart server");

	public static ConfirmPrompt ShutDown(string? serverName) => new(
		$"Shut down {Name(serverName, "the server")}?",
		"Everyone watching will be interrupted, and the server stays off until someone starts it again on its computer.",
		"Shut down server");

	public static ConfirmPrompt RemovePassword(string? userName) => new(
		$"Remove {Name(userName, "this user")}'s password?",
		$"{Name(userName, "This user")} can then sign in with no password until a new one is set.",
		"Remove password");

	public static ConfirmPrompt DeleteUser(string? userName) => new(
		$"Delete {Name(userName, "this user")}?",
		"Their settings and watch history are removed from the server. This can't be undone.",
		"Delete user");

	public static ConfirmPrompt DeleteLibrary(string? libraryName) => new(
		$"Delete {Name(libraryName, "this library")}?",
		"The library and its metadata are removed from the server; the media files on disk stay. This can't be undone.",
		"Delete library");

	public static ConfirmPrompt DeleteImage(string? itemName, string? imageType) => new(
		$"Delete the {Lower(imageType, "image")} image?",
		$"It is removed from {Name(itemName, "this item")} on the server. This can't be undone.",
		"Delete image");

	public static ConfirmPrompt DeleteSubtitle(string? itemName, string? subtitleName) => new(
		string.IsNullOrWhiteSpace(subtitleName) ? "Delete this subtitle?" : $"Delete the subtitle \"{subtitleName.Trim()}\"?",
		$"The subtitle file is removed from {Name(itemName, "this item")} on the server. This can't be undone.",
		"Delete subtitle");

	public static ConfirmPrompt DeleteSegment(string? segmentType, string? itemName) => new(
		$"Delete the {Lower(segmentType, "media")} segment?",
		$"It is removed from {Name(itemName, "this item")} on the server. This can't be undone.",
		"Delete segment");

	private static string Name(string? name, string fallback) => string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();

	private static string Lower(string? word, string fallback) => string.IsNullOrWhiteSpace(word) ? fallback : word.Trim().ToLowerInvariant();
}
