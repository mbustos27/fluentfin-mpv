using Microsoft.Extensions.Logging;

namespace FluentFin.Services;

/// <summary>
/// 1.5: no Windows "ding" on Alt shortcuts. Nixie's windows have no menu bar, so an Alt chord that types a character
/// (Alt+Enter for full screen, Alt+X) goes on to Windows' menu handling after the app has used it: Windows looks the
/// character up in the window's system menu, finds nothing, asks the window what it means (<c>WM_MENUCHAR</c>) and, when
/// the answer is "ignore" (what an unhandled <c>WM_MENUCHAR</c> returns), plays the error sound. Every top-level window of
/// the app (the main window, the player controls' window) answers <c>MNC_CLOSE</c> ("close the menu"), which ends that
/// quietly; the app's own handling of the key is untouched (it ran before, in WinUI's key events). Alt+Space (the system
/// menu) and its own letters still work: Windows asks only when no item matches.
/// </summary>
internal static class MenuCharSilencer
{
	public const uint WM_MENUCHAR = 0x0120;
	private const int MNC_CLOSE = 1;

	/// <summary>How many log lines a session gets (the first answers only: a key matrix sends hundreds).</summary>
	private const int LoggedAtMost = 20;

	// one per window, held for its lifetime (the monitor is the window's subclass)
	private static readonly List<WinUIEx.Messaging.WindowMessageMonitor> s_monitors = [];
	private static int _answered;

	/// <summary>How many <c>WM_MENUCHAR</c> the app's windows answered this session.</summary>
	public static int Answered => _answered;

	/// <summary>The window procedure's answer to <paramref name="message"/>, or null to leave it to Windows.</summary>
	public static nint? Answer(uint message) => message == WM_MENUCHAR ? MNC_CLOSE << 16 : null;

	/// <param name="window">A top-level window of the app.</param>
	/// <param name="name">Its name in the log ("main", "player controls").</param>
	public static void Attach(Microsoft.UI.Xaml.Window window, string name)
	{
		var monitor = new WinUIEx.Messaging.WindowMessageMonitor(window);
		lock (s_monitors)
		{
			s_monitors.Add(monitor);
		}

		window.Closed += (_, _) =>
		{
			lock (s_monitors)
			{
				s_monitors.Remove(monitor);
			}

			monitor.Dispose();
		};
		monitor.WindowMessageReceived += (_, e) =>
		{
			if (Answer(e.Message.MessageId) is not { } answer)
			{
				return;
			}

			e.Handled = true;
			e.Result = answer;
			var count = Interlocked.Increment(ref _answered);
			if (count <= LoggedAtMost)
			{
				// the key's character only (the low word of wParam): a menu key needs Alt, so it is never text typed into a box
				App.GetService<ILogger<MainWindow>>().LogInformation(
					"[keys] Alt+{Key} on the {Window} window: WM_MENUCHAR answered MNC_CLOSE, no system sound ({Count})",
					KeyName((char)((long)e.Message.WParam & 0xFFFF)), name, count);
			}
		};
	}

	/// <summary>"Enter" for the carriage return Alt+Enter brings, else the character (a control character as its code).</summary>
	public static string KeyName(char c) => c switch
	{
		'\r' => "Enter",
		< ' ' => $"0x{(int)c:X2}",
		_ => c.ToString(),
	};
}
