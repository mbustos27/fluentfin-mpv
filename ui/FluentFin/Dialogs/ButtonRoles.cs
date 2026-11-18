using Microsoft.UI.Xaml;

namespace FluentFin.Dialogs;

/// <summary>
/// Nixie 1.2.0 (admin): the button roles of the button review's §3 that code-built dialogs ask for, by the style keys the
/// review gives them. The glass stream adds those keys to Styles\; until they are there a role finds nothing and the stock
/// WinUI style stays, so the merge needs no change here.
/// </summary>
public static class ButtonRoles
{
	/// <summary>Destructive, Compact (32 px): danger ink, never filled, never the accent.</summary>
	public const string DestructiveCompact = "NixieDestructiveCompactButtonStyle";

	/// <summary>A confirmation of a destructive action: Compact buttons, the default (Cancel) drawn Secondary, the confirm Destructive.</summary>
	public const string DestructiveDialog = "NixieDestructiveDialogStyle";

	/// <summary>The style with that key in the app's resources, or null when there is none (yet), or no running app to ask.</summary>
	public static Style? Find(string key)
	{
		try
		{
			return Application.Current?.Resources is { } resources && resources.TryGetValue(key, out var value) ? value as Style : null;
		}
		catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or TypeInitializationException)
		{
			return null;
		}
	}
}
