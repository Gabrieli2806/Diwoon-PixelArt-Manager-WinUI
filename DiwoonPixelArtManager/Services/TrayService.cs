using System.Runtime.InteropServices;

namespace DiwoonPixelArtManager.Services;

/// <summary>One line of the tray menu; a null text draws a separator. Detail is shown on the right.</summary>
public sealed record TrayMenuItem(string? Text, string? Glyph = null, Action? Action = null, bool Checked = false, string? Detail = null);

/// <summary>
/// Notification-area icon with its menu, and Windows lock / unlock events. Both arrive as messages
/// to a hidden window created on the UI thread, so the callbacks also run on the UI thread.
/// </summary>
public sealed partial class TrayService : IDisposable
{
    private const int WmApp = 0x8000;
    private const int TrayCallback = WmApp + 1;
    private const int WmWtsSessionChange = 0x02B1;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;
    private const int WtsSessionLock = 0x7;
    private const int WtsSessionUnlock = 0x8;
    private const int NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4;
    private const int NimAdd = 0, NimDelete = 2;

    private readonly WndProc _wndProc;
    private readonly IntPtr _window;
    private readonly IntPtr _icon;
    private readonly uint _taskbarCreated;

    /// <summary>Left click on the icon.</summary>
    public event Action? OpenRequested;
    public event Action? SessionLocked;
    public event Action? SessionUnlocked;

    /// <summary>Right click on the icon, with the pointer position in screen pixels.</summary>
    public event Action<int, int>? MenuRequested;

    public TrayService(string iconPath, string tooltip)
    {
        _wndProc = WindowProc;
        var className = "DiwoonPixelArtManagerTray";
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            WndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            Instance = GetModuleHandle(null),
            ClassName = className
        };
        RegisterClassEx(ref windowClass);
        _window = CreateWindowEx(0, className, tooltip, 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, windowClass.Instance, IntPtr.Zero);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _icon = LoadImage(IntPtr.Zero, iconPath, 1, GetSystemMetrics(49), GetSystemMetrics(50), 0x10);
        _tooltip = tooltip;

        AddIcon();
        WTSRegisterSessionNotification(_window, 0);
    }

    private readonly string _tooltip;

    private void AddIcon()
    {
        var data = IconData();
        data.Flags = NifMessage | NifIcon | NifTip;
        Shell_NotifyIcon(NimAdd, ref data);
    }

    private NotifyIconData IconData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = _window,
        Id = 1,
        CallbackMessage = TrayCallback,
        Icon = _icon,
        Tip = _tooltip
    };

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == TrayCallback)
        {
            int mouse = (int)lParam & 0xFFFF;
            if (mouse == WmLButtonUp) OpenRequested?.Invoke();
            else if (mouse == WmRButtonUp) ShowMenu();
            return IntPtr.Zero;
        }
        if (message == WmWtsSessionChange)
        {
            if ((int)wParam == WtsSessionLock) SessionLocked?.Invoke();
            else if ((int)wParam == WtsSessionUnlock) SessionUnlocked?.Invoke();
            return IntPtr.Zero;
        }
        if (message == _taskbarCreated)
        {
            // Explorer restarted: the icon must be added again.
            AddIcon();
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        GetCursorPos(out var point);
        MenuRequested?.Invoke(point.X, point.Y);
    }

    public void Dispose()
    {
        var data = IconData();
        Shell_NotifyIcon(NimDelete, ref data);
        WTSUnRegisterSessionNotification(_window);
        DestroyWindow(_window);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public IntPtr WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y,
        int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSRegisterSessionNotification(IntPtr hwnd, int flags);

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);
}
