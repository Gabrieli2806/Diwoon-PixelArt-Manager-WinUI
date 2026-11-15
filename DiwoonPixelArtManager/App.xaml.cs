using System.Runtime.InteropServices;
using DiwoonPixelArtManager.Services;
using DiwoonPixelArtManager.Views;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.Windows.AppLifecycle;
using WinRT.Interop;

namespace DiwoonPixelArtManager;

public partial class App : Application
{
    private const string InstanceKey = "main";

    public static Window MainWindow { get; private set; } = null!;

    private TrayService? _tray;
    private bool _exiting;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => AppLog.Write("Unhandled", e.Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => AppLog.Write("Unobserved task", e.Exception);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // A second start (for example from the Start menu while the app sits in the tray) shows the running window.
        var mainInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!mainInstance.IsCurrent)
        {
            await mainInstance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            Current.Exit();
            return;
        }

        SettingsService.Instance.AttachToUiThread();
        MainWindow = new Window
        {
            Title = "Diwoon PixelArt Manager",
            ExtendsContentIntoTitleBar = true,
            Content = new MainPage()
        };
        mainInstance.Activated += (_, _) => MainWindow.DispatcherQueue.TryEnqueue(ShowWindow);

        var hwnd = WindowNative.GetWindowHandle(MainWindow);
        var appWindow = MainWindow.AppWindow;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        appWindow.SetIcon(iconPath);
        double scale = GetDpiForWindow(hwnd) / 96.0;
        appWindow.Resize(new global::Windows.Graphics.SizeInt32((int)(1180 * scale), (int)(780 * scale)));
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 760;
            presenter.PreferredMinimumHeight = 520;
        }
        appWindow.Closing += AppWindow_Closing;

        ThemeService.Apply(MainWindow);
        SettingsService.Instance.SettingsChanged += () => ThemeService.Apply(MainWindow);

        _tray = new TrayService(iconPath, "Diwoon PixelArt Manager");
        _tray.OpenRequested += ShowWindow;
        _tray.MenuRequested += ShowTrayMenu;
        _tray.SessionLocked += AlertService.Instance.OnSessionLocked;
        _tray.SessionUnlocked += AlertService.Instance.OnSessionUnlocked;

        StartupService.RefreshPath();
        SystemMonitorService.Instance.Start();
        AppProfileService.Instance.Start();
        AgentAlertService.Instance.Start();
        _ = StartNowPlayingAsync();
        DisplayPlayer.Instance.Start();
        AlertService.Instance.Start();
        HttpBridgeService.Instance.ApplySettings();
        _ = DivoomConnection.Instance.ConnectToLastDeviceAsync();

        if (!Environment.GetCommandLineArgs().Contains(StartupService.MinimizedArgument))
            MainWindow.Activate();
    }

    private static async Task StartNowPlayingAsync()
    {
        try
        {
            await NowPlayingService.Instance.StartAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write("Media session tracking is not available", ex);
        }
    }

    private void ShowTrayMenu(int x, int y) => new TrayMenuWindow(TrayStatus(), BuildTrayMenu()).ShowNear(x, y);

    /// <summary>What the display is doing, for the top of the tray menu.</summary>
    private static TrayStatus TrayStatus()
    {
        var connection = DivoomConnection.Instance;
        var player = DisplayPlayer.Instance;
        if (connection.Device is not { } device)
        {
            return connection.Reconnecting is { } pending
                ? new("\uE895", $"Reconnecting to {pending.Name}", "Retrying every 15 seconds", false)
                : new("\uE702", "No display connected", "Open the app to connect one", false);
        }

        string showing = player.ActiveProfile is { } profile
            ? $"Profile: {profile.Name}"
            : player.Mode switch
            {
                PlayerMode.Monitor => "PC monitor",
                PlayerMode.NowPlaying => "Now playing",
                PlayerMode.Timer => TimerService.Instance.IsPomodoro ? "Pomodoro" : "Timer",
                PlayerMode.Slideshow => "Slideshow",
                PlayerMode.Image => player.CurrentName ?? "Picture",
                _ => "The display's own channel"
            };
        return new("\uE7F4", device.Name, $"{connection.DisplaySize}×{connection.DisplaySize} · {showing}", true);
    }

    private IReadOnlyList<TrayMenuItem> BuildTrayMenu()
    {
        var player = DisplayPlayer.Instance;
        var timer = TimerService.Instance;
        var settings = SettingsService.Instance;
        bool connected = DivoomConnection.Instance.IsConnected;
        var items = new List<TrayMenuItem>
        {
            new("PC monitor", "\uE9D9", connected ? () => Toggle(PlayerMode.Monitor, player.StartMonitor) : null, player.Mode == PlayerMode.Monitor),
            new("Now playing", "\uE8D6", connected ? () => Toggle(PlayerMode.NowPlaying, player.StartNowPlaying) : null, player.Mode == PlayerMode.NowPlaying)
        };
        if (timer.IsActive)
        {
            int seconds = (int)Math.Ceiling(timer.Remaining.TotalSeconds);
            items.Add(new(timer.IsPaused ? "Resume timer" : "Pause timer", timer.IsPaused ? "\uE768" : "\uE769", timer.TogglePause, Detail: $"{seconds / 60:00}:{seconds % 60:00}"));
            items.Add(new("Stop timer", "\uE71A", () =>
            {
                timer.Stop();
                if (player.Mode == PlayerMode.Timer) _ = player.StopAsync();
            }));
        }
        else
        {
            items.Add(new("Start Pomodoro", "\uE916", () =>
            {
                timer.StartPomodoro();
                if (DivoomConnection.Instance.IsConnected) player.StartTimerDisplay();
            }, Detail: $"{settings.Settings.FocusMinutes} min"));
        }
        items.Add(new("App profiles", "\uE7FC", () =>
        {
            settings.Settings.AppProfilesEnabled = !settings.Settings.AppProfilesEnabled;
            settings.Save();
        }, settings.Settings.AppProfilesEnabled));

        items.Add(new(null));
        items.Add(new("Gallery", "\uE8B9", () => ShowPage("gallery")));
        items.Add(new("Open Diwoon PixelArt Manager", "\uE8A7", ShowWindow));
        items.Add(new("Settings", "\uE713", () => ShowPage("settings")));
        items.Add(new(null));
        items.Add(new("Exit", "\uE7E8", ExitApp));
        return items;

        void Toggle(PlayerMode mode, Action start)
        {
            if (player.Mode == mode) _ = player.StopAsync();
            else start();
        }
    }

    private void ShowPage(string tag)
    {
        ShowWindow();
        (MainWindow.Content as MainPage)?.ShowPage(tag);
    }

    private void ShowWindow()
    {
        MainWindow.AppWindow.Show();
        MainWindow.Activate();
    }

    /// <summary>Closing the window keeps the app in the notification area so slideshows, monitor and alerts keep running.</summary>
    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting || !SettingsService.Instance.Settings.CloseToTray)
        {
            Shutdown();
            return;
        }
        args.Cancel = true;
        sender.Hide();
    }

    private void ExitApp()
    {
        _exiting = true;
        MainWindow.Close();
    }

    private void Shutdown()
    {
        _tray?.Dispose();
        _tray = null;
        HttpBridgeService.Instance.Stop();
        DivoomConnection.Instance.Disconnect();
        Current.Exit();
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr hwnd);
}
