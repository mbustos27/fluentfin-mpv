// Nixie's launcher: the Nixie.exe at the top of the release folder, the one program there to run. The app
// itself (its Nixie.exe and hundreds of runtime files) is in app\, so people don't have to find it among them.
// This starts app\Nixie.exe with the same arguments, working folder and environment, then ends; it shows nothing
// unless it can't. Built by tools\release\build-release.ps1 with Windows' own C# compiler (.NET Framework 4, part of
// Windows 10 and 11), so it needs nothing that the app doesn't, and ships no third-party code.
// Nixie 1.5: it is also the install helper behind "Restart to update" (UpdateHelper below, started as
// "Nixie.exe --nixie-install-update ..." from a copy in Nixie's data folder; ui\FluentFin.Core\Updates\UpdateHandoff.cs
// writes the same arguments). Windows' csc is C# 5: no string interpolation, no "?.", no expression-bodied members.
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class Launcher
{
    const string Title = "Nixie";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(uint processId);

    static int Main()
    {
        string[] given = Environment.GetCommandLineArgs();
        if (given.Length > 1 && given[1] == UpdateHelper.Switch)
        {
            return UpdateHelper.Run(given);
        }

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

// Nixie 1.5, "Restart to update": the install helper. Started by Nixie (from its copy in Nixie's data folder, so the installer
// never finds this file in use) just before it closes. It waits for that Nixie to end (its process id AND its start time, so
// a reused id is never waited on, and never touched), runs Windows Installer on the downloaded installer, quietly and per user
// (no administrator: msiexec /i <msi> /passive /norestart /l*v <log>, or /qn when it installs as Nixie closes), writes
// msiexec's exit code to the result file, and opens Nixie again with its own arguments: with --reopen, or when a Nixie started
// meanwhile asked for it (reopen.request beside the result file; that Nixie ended at once). It ends nothing but itself.
// Exit status: msiexec's, or 2 (bad arguments), 4 (Nixie did not close in time: nothing installed), 5 (another helper runs).
static class UpdateHelper
{
    public const string Switch = "--nixie-install-update";
    const string MutexName = @"Local\Nixie-update-install";
    const string ReopenRequest = "reopen.request";
    const uint SYNCHRONIZE = 0x00100000;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const uint WAIT_OBJECT_0 = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    public static int Run(string[] given)
    {
        string msi = null, log = null, result = null, app = null;
        int pid = 0;
        long started = 0;
        bool quiet = false, reopen = false;
        uint waitMs = 120000;
        string msiexec = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
        string mutexName = MutexName;
        var appArguments = new System.Collections.Generic.List<string>();
        for (int i = 2; i < given.Length; i++)
        {
            string a = given[i];
            if (a == "--") { for (int j = i + 1; j < given.Length; j++) appArguments.Add(given[j]); break; }
            else if (a == "--quiet") quiet = true;
            else if (a == "--reopen") reopen = true;
            else if (i + 1 < given.Length && a == "--msi") msi = given[++i];
            else if (i + 1 < given.Length && a == "--log") log = given[++i];
            else if (i + 1 < given.Length && a == "--result") result = given[++i];
            else if (i + 1 < given.Length && a == "--app") app = given[++i];
            else if (i + 1 < given.Length && a == "--wait-pid") { if (!int.TryParse(given[++i], NumberStyles.None, CultureInfo.InvariantCulture, out pid)) return 2; }
            else if (i + 1 < given.Length && a == "--wait-started") { if (!long.TryParse(given[++i], NumberStyles.None, CultureInfo.InvariantCulture, out started)) return 2; }
#if NIXIE_LAUNCHER_TEST
            // the tests' stand-ins only: a release build has no way to run anything but Windows' own msiexec
            else if (i + 1 < given.Length && a == "--msiexec") msiexec = given[++i];
            else if (i + 1 < given.Length && a == "--wait-ms") { if (!uint.TryParse(given[++i], NumberStyles.None, CultureInfo.InvariantCulture, out waitMs)) return 2; }
            // a mutex of the test's own, so a test never looks like an install to a Nixie starting on this PC
            else if (i + 1 < given.Length && a == "--mutex") mutexName = given[++i];
#endif
            else return 2;
        }

        if (msi == null || !msi.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) || log == null || result == null || pid <= 0 || started <= 0)
        {
            return 2;
        }

        bool mine;
        int code;
        using (var mutex = new Mutex(true, mutexName, out mine))
        {
            if (!mine)
            {
                return 5;
            }

            try
            {
                if (!WaitForEnd(pid, started, waitMs))
                {
                    Write(result, "exit=-1", "note=Nixie did not close in time, so nothing was installed");
                    return 4;
                }

                code = Install(msiexec, msi, log, quiet, result);
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }

        // review m6: the mutex released AND its handle closed before Nixie opens again: the Nixie it opens must not find an
        // install under way (it would end at once and ask to be opened again)
        string request = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(result)), ReopenRequest);
        bool asked = File.Exists(request);
        if ((reopen || asked) && app != null && File.Exists(app))
        {
            try
            {
                if (asked) File.Delete(request);
                var start = new ProcessStartInfo(app, Join(appArguments));
                start.UseShellExecute = false;
                start.WorkingDirectory = Path.GetDirectoryName(app);
                using (Process.Start(start)) { }
            }
            catch (Exception)
            {
                // nothing to open: the next start from the Start menu shows what happened
            }
        }

        return code;
    }

    // true once the process that started at <started> (a FILETIME, UTC) has ended, or was already gone; false if it still runs
    // after waitMs. A process with that id but another start time is someone else's: not waited on.
    static bool WaitForEnd(int pid, long started, uint waitMs)
    {
        IntPtr handle = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
        {
            return true;
        }

        try
        {
            long creation, exit, kernel, user;
            if (!GetProcessTimes(handle, out creation, out exit, out kernel, out user) || creation != started)
            {
                return true;
            }

            return WaitForSingleObject(handle, waitMs) == WAIT_OBJECT_0;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    static int Install(string msiexec, string msi, string log, bool quiet, string result)
    {
        string arguments = "/i " + Quote(msi) + (quiet ? " /qn" : " /passive") + " /norestart /l*v " + Quote(log);
        int code;
        try
        {
            var start = new ProcessStartInfo(msiexec, arguments);
            start.UseShellExecute = false;
            using (var process = Process.Start(start))
            {
                process.WaitForExit();
                code = process.ExitCode;
            }
        }
        catch (Win32Exception)
        {
            code = 1601;   // the installer could not be started
        }

        Write(result, "exit=" + code.ToString(CultureInfo.InvariantCulture), "finished=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        return code;
    }

    static void Write(string path, string first, string second)
    {
        try
        {
            File.WriteAllText(path, first + "\r\n" + second + "\r\n", new UTF8Encoding(false));
        }
        catch (Exception)
        {
            // the next start then says the installer did not finish
        }
    }

    // the arguments as one command line, each quoted as CommandLineToArgvW reads it back (the app gets them unchanged)
    public static string Join(System.Collections.Generic.IList<string> arguments)
    {
        var line = new StringBuilder();
        foreach (string a in arguments)
        {
            if (line.Length > 0) line.Append(' ');
            line.Append(Quote(a));
        }

        return line.ToString();
    }

    static string Quote(string a)
    {
        if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
        {
            return a;
        }

        var quoted = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in a)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { quoted.Append('\\', slashes * 2 + 1); quoted.Append('"'); }
            else { quoted.Append('\\', slashes); quoted.Append(c); }
            slashes = 0;
        }

        quoted.Append('\\', slashes * 2);
        quoted.Append('"');
        return quoted.ToString();
    }
}
