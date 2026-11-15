using System.Reflection;
using DiwoonPixelArtManager.Services;

namespace DiwoonPixelArtManager.Views;

public sealed partial class SettingsPage : Page
{
    private readonly SettingsService _settings = SettingsService.Instance;
    private readonly bool _loaded;

    public SettingsPage()
    {
        InitializeComponent();
        var s = _settings.Settings;
        Select(ThemeCombo, s.Theme);
        Select(BackdropCombo, s.Backdrop);
        Select(SizeCombo, s.DisplaySize.ToString());
        Select(ScalingCombo, s.Scaling.ToString());
        Select(FitCombo, s.Fit.ToString());
        SlideshowBox.Value = s.SlideshowSeconds;
        ShuffleToggle.IsOn = s.SlideshowShuffle;
        StartupToggle.IsOn = StartupService.IsEnabled;
        TrayToggle.IsOn = s.CloseToTray;
        AutoConnectToggle.IsOn = s.AutoConnect;
        BridgeToggle.IsOn = s.HttpBridgeEnabled;
        PortBox.Value = s.HttpBridgePort;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Diwoon PixelArt Manager {version?.ToString(3)}";

        _loaded = true;
        UpdateBridgeStatus();
    }

    private static void Select(ComboBox combo, string tag) =>
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? combo.Items[0];

    private static string TagOf(ComboBox combo) => (string)((ComboBoxItem)combo.SelectedItem).Tag;

    private void Setting_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        var s = _settings.Settings;
        s.Theme = TagOf(ThemeCombo);
        s.Backdrop = TagOf(BackdropCombo);
        s.DisplaySize = int.Parse(TagOf(SizeCombo));
        s.Scaling = Enum.Parse<ScalingMode>(TagOf(ScalingCombo));
        s.Fit = Enum.Parse<FitMode>(TagOf(FitCombo));
        _settings.Save();
    }

    private void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        var s = _settings.Settings;
        s.SlideshowShuffle = ShuffleToggle.IsOn;
        s.CloseToTray = TrayToggle.IsOn;
        s.AutoConnect = AutoConnectToggle.IsOn;
        _settings.Save();
    }

    private void Slideshow_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_loaded || double.IsNaN(args.NewValue)) return;
        _settings.Settings.SlideshowSeconds = (int)args.NewValue;
        _settings.Save();
    }

    private void Startup_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loaded) StartupService.SetEnabled(StartupToggle.IsOn);
    }

    private void Bridge_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _settings.Settings.HttpBridgeEnabled = BridgeToggle.IsOn;
        ApplyBridge();
    }

    private void Port_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_loaded || double.IsNaN(args.NewValue)) return;
        _settings.Settings.HttpBridgePort = (int)args.NewValue;
        ApplyBridge();
    }

    private void ApplyBridge()
    {
        _settings.Save();
        HttpBridgeService.Instance.ApplySettings();
        UpdateBridgeStatus();
    }

    private void UpdateBridgeStatus()
    {
        var bridge = HttpBridgeService.Instance;
        int port = _settings.Settings.HttpBridgePort;
        BridgeStatusText.Text = bridge.LastError is not null
            ? $"Could not start on port {port}: {bridge.LastError}"
            : bridge.IsRunning
                ? $"Running at http://127.0.0.1:{port}/  (POST /img, /notify, /cmd?cmd=NN, /raw)"
                : "Stopped";
    }
}
