using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DiwoonPixelArtManager.Services;

/// <summary>A window on screen and the app behind it.</summary>
public sealed record AppWindowInfo(IntPtr Window, string Process, string Title, string? ExePath);

/// <summary>Which apps have windows open, which one is in front, and what an executable calls itself.</summary>
public static partial class DesktopApps
{
    /// <summary>Executable descriptions that say nothing about which app it is.</summary>
    private static readonly string[] GenericNames = ["Application", "Game", "Launcher", "Electron", "Unity Player", "UnityPlayer", "UnrealEngine"];

    private static readonly Dictionary<uint, (string Name, string? Path, long Seen)> ProcessCache = [];

    /// <summary>The process name of the window in front, or "" when Windows will not say.</summary>
    public static (string Process, string? ExePath) Foreground()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return ("", null);
        var (name, path) = ProcessOf(window);
        return (name, path);
    }

    /// <summary>Names of every running process, lower-case, without ".exe".</summary>
    public static HashSet<string> RunningProcesses()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            names.Add(process.ProcessName);
            process.Dispose();
        }
        return names;
    }

    /// <summary>Top-level windows a person would call "an app", this app's own left out.</summary>
    public static List<AppWindowInfo> AppWindows()
    {
        var result = new List<AppWindowInfo>();
        uint own = (uint)Environment.ProcessId;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || IsCloaked(window) || GetWindow(window, 4) != IntPtr.Zero) return true;
            long extended = GetWindowLongPtr(window, -20).ToInt64();
            if ((extended & 0x80) != 0) return true; // tool window
            GetWindowThreadProcessId(window, out uint pid);
            if (pid == own) return true;
            var title = new StringBuilder(256);
            if (GetWindowText(window, title, title.Capacity) == 0) return true;
            var (name, path) = ProcessOf(window);
            if (name.Length > 0) result.Add(new AppWindowInfo(window, name, title.ToString(), path));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>The name an executable gives itself ("Spotify", "Google Chrome"), or its file name.</summary>
    public static string DisplayName(string process, string? exePath)
    {
        try
        {
            if (exePath is not null && File.Exists(exePath))
            {
                var info = FileVersionInfo.GetVersionInfo(exePath);
                foreach (var candidate in new[] { info.FileDescription, info.ProductName })
                {
                    var text = candidate?.Trim();
                    if (!string.IsNullOrEmpty(text) && text.Length <= 60 && !GenericNames.Contains(text, StringComparer.OrdinalIgnoreCase))
                        return text;
                }
            }
        }
        catch (IOException)
        {
            // Some protected executables cannot be read; the process name is good enough.
        }
        return process;
    }

    /// <summary>
    /// The app behind a window. Store apps run inside a shared frame host; the app is the window inside it.
    /// </summary>
    private static (string Name, string? Path) ProcessOf(IntPtr window)
    {
        GetWindowThreadProcessId(window, out uint pid);
        var found = FromPid(pid);
        if (found.Name.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
        {
            var core = FindWindowEx(window, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
            if (core != IntPtr.Zero && GetWindowThreadProcessId(core, out uint inner) != 0 && inner != pid)
                found = FromPid(inner);
        }
        return found;
    }

    private static (string Name, string? Path) FromPid(uint pid)
    {
        lock (ProcessCache)
        {
            // Briefly cached: asked every second, and process ids get reused.
            if (ProcessCache.TryGetValue(pid, out var known) && Environment.TickCount64 - known.Seen < 5000)
                return (known.Name, known.Path);

            string name = "";
            string? path = null;
            var handle = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
            if (handle != IntPtr.Zero)
            {
                var buffer = new StringBuilder(1024);
                int length = buffer.Capacity;
                if (QueryFullProcessImageName(handle, 0, buffer, ref length))
                {
                    path = buffer.ToString(0, length);
                    name = Path.GetFileNameWithoutExtension(path);
                }
                CloseHandle(handle);
            }
            if (ProcessCache.Count > 512) ProcessCache.Clear();
            ProcessCache[pid] = (name, path, Environment.TickCount64);
            return (name, path);
        }
    }

    private static bool IsCloaked(IntPtr window) =>
        DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr data);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? title);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int length);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
