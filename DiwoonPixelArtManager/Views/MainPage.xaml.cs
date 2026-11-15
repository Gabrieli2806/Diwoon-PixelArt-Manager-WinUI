using DiwoonPixelArtManager.Services;

namespace DiwoonPixelArtManager.Views;

public sealed partial class MainPage : Page
{
    private readonly DivoomConnection _connection = DivoomConnection.Instance;
    private readonly DisplayPlayer _player = DisplayPlayer.Instance;

    public MainPage()
    {
        InitializeComponent();
        Loaded += (_, _) => App.MainWindow.SetTitleBar(AppTitleBar);
        _connection.ConnectionChanged += () => DispatcherQueue.TryEnqueue(UpdateStatus);
        _player.StateChanged += () => DispatcherQueue.TryEnqueue(UpdateStatus);
        SettingsService.Instance.SettingsChanged += UpdateStatus;
        NavView.SelectedItem = GalleryItem;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var device = _connection.Device;
        string mode = _player.ActiveProfile is { } profile ? $" · {profile.Name} profile" : _player.Mode switch
        {
            PlayerMode.Slideshow => " · Slideshow",
            PlayerMode.Monitor => " · PC monitor",
            PlayerMode.NowPlaying => " · Now playing",
            PlayerMode.Timer => " · Timer",
            _ => ""
        };
        StatusText.Text = device is not null
            ? $"{device.Name} · {_connection.DisplaySize}×{_connection.DisplaySize}{mode}"
            : _connection.Reconnecting is { } pending
                ? $"Reconnecting to {pending.Name}…"
                : "Not connected";
        StatusDot.Fill = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
            device is not null ? "SystemFillColorSuccessBrush"
            : _connection.Reconnecting is not null ? "SystemFillColorCautionBrush"
            : "SystemFillColorCriticalBrush"];
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args) =>
        NavView.IsPaneOpen = !NavView.IsPaneOpen;

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var page = args.IsSettingsSelected
            ? typeof(SettingsPage)
            : (args.SelectedItem as NavigationViewItem)?.Tag switch
            {
                "device" => typeof(DevicePage),
                "monitor" => typeof(MonitorPage),
                "widgets" => typeof(WidgetsPage),
                "profiles" => typeof(ProfilesPage),
                "editor" => typeof(EditorPage),
                _ => typeof(GalleryPage)
            };
        var parameter = _pendingParameter;
        _pendingParameter = null;
        if (ContentFrame.CurrentSourcePageType != page || parameter is not null)
            ContentFrame.Navigate(page, parameter);
    }

    private object? _pendingParameter;

    /// <summary>Opens the editor with a copy of a gallery image.</summary>
    public void ShowEditor(string path)
    {
        var editor = NavView.MenuItems.OfType<NavigationViewItem>().First(i => (string)i.Tag == "editor");
        if (ReferenceEquals(NavView.SelectedItem, editor))
            ContentFrame.Navigate(typeof(EditorPage), path);
        else
        {
            _pendingParameter = path;
            NavView.SelectedItem = editor;
        }
    }

    /// <summary>Opens the Device page, used when an action needs a connection first.</summary>
    public void ShowDevicePage() => ShowPage("device");

    /// <summary>Opens a page by its tag ("settings" for the settings page).</summary>
    public void ShowPage(string tag) => NavView.SelectedItem = tag == "settings"
        ? NavView.SettingsItem
        : NavView.MenuItems.Concat(NavView.FooterMenuItems).OfType<NavigationViewItem>().First(i => (string)i.Tag == tag);
}
