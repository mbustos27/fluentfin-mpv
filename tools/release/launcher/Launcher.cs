// Nixie's launcher: the Nixie.exe at the top of the release folder, the one program there to run. The app
// itself (its Nixie.exe and hundreds of runtime files) is in app\, so people don't have to find it among them.
// This starts app\Nixie.exe with the same arguments, working folder and environment, then ends; it shows nothing
// unless it can't. Built by tools\release\build-release.ps1 with Windows' own C# compiler (.NET Framework 4, part of
// Windows 10 and 11), so it needs nothing that the app doesn't, and ships no third-party code.
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

static class Launcher
{
    const string Title = "Nixie";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(uint processId);

    static int Main()
    {
        string here = AppDomain.CurrentDomain.BaseDirectory;
        string app = Path.Combine(Path.Combine(here, "app"), "Nixie.exe");
        if (!File.Exists(app))
        {
            Show("Nixie can't start: there is no app\\Nixie.exe beside this Nixie.exe (in " + here + ").\n\n" +
                 "Unzip the whole folder and keep its files together. To start Nixie from somewhere else, " +
                 "make a shortcut to this Nixie.exe.");
            return 2;
        }
        var start = new ProcessStartInfo(app, ArgumentsAfterProgram(Environment.CommandLine));
        start.UseShellExecute = false;   // CreateProcess: this working folder and environment, the arguments as given
        try
        {
            using (var process = Process.Start(start))
            {
                // the app's window may come to the front, as it would if it had been started directly
                AllowSetForegroundWindow((uint)process.Id);
            }
            return 0;
        }
        catch (Win32Exception e)
        {
            Show("Nixie can't start " + app + ":\n\n" + e.Message);
            return 3;
        }
    }

    /// <summary>The command line without its first part, the program. Windows reads that part as a quoted run up to the
    /// next quote, or else everything up to the first space or tab; the rest goes to the app exactly as it was typed.</summary>
    static string ArgumentsAfterProgram(string commandLine)
    {
        int i = 0;
        if (commandLine.Length > 0 && commandLine[0] == '"')
        {
            int close = commandLine.IndexOf('"', 1);
            i = close < 0 ? commandLine.Length : close + 1;
        }
        else
        {
            while (i < commandLine.Length && commandLine[i] != ' ' && commandLine[i] != '\t') i++;
        }
        return commandLine.Substring(i).TrimStart(' ', '\t');
    }

    static void Show(string text)
    {
        MessageBoxW(IntPtr.Zero, text, Title, 0x10);   // MB_ICONERROR
    }
}
