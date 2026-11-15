using DiwoonPixelArtManager.Services;

namespace DiwoonPixelArtManager.Views;

public sealed partial class MonitorPage : Page
{
    private const int PreviewSize = 256;

    private readonly SettingsService _settings = SettingsService.Instance;
    private readonly DisplayPlayer _player = DisplayPlayer.Instance;
    private readonly DivoomConnection _connection = DivoomConnection.Instance;
    private readonly bool _loaded;

    public MonitorPage()
    {
        InitializeComponent();
        var s = _settings.Settings;
        LhmUrlBox.Text = s.LibreHardwareMonitorUrl;
        IntervalCombo.SelectedItem = IntervalCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == s.MonitorIntervalSeconds.ToString()) ?? IntervalCombo.Items[1];
        HotToggle.IsOn = s.HotAlertEnabled;
        HotBox.Value = s.HotThreshold;
        BatteryToggle.IsOn = s.BatteryAlertEnabled;
        BatteryBox.Value = s.LowBatteryPercent;
        PowerToggle.IsOn = s.PowerAlertEnabled;
        LockToggle.IsOn = s.ScreenOffWhenLocked;
        DurationBox.Value = s.AlertSeconds;
        _loaded = true;

        SystemMonitorService.Instance.SampleUpdated += sample => DispatcherQueue.TryEnqueue(() => Show(sample));
        _player.StateChanged += () => DispatcherQueue.TryEnqueue(UpdateToggle);
        _settings.SettingsChanged += UpdateNotifyExample;
        if (SystemMonitorService.Instance.Latest is { } latest) Show(latest);
        UpdateToggle();
        UpdateNotifyExample();
    }

    private void Show(SystemSample sample)
    {
        CpuTempText.Text = sample.CpuTemperature is { } cpu ? $"{cpu:0} °C" : "--";
        GpuTempText.Text = sample.GpuTemperature is { } gpu ? $"{gpu:0} °C" : "--";
        CpuLoadText.Text = sample.CpuLoad is { } cpuLoad ? $"{cpuLoad:0} %" : "--";
        GpuLoadText.Text = sample.GpuLoad is { } gpuLoad ? $"{gpuLoad:0} %" : "--";
        RamText.Text = $"{sample.RamLoad:0} %";

        SourceText.Text = sample.FromLibreHardwareMonitor
            ? "Temperatures from LibreHardwareMonitor. Load from Windows."
            : "Built-in sensors: GPU temperature from the graphics driver, CPU temperature from ACPI, load from Windows.";
        LhmTip.IsOpen = !sample.FromLibreHardwareMonitor;

        int size = _connection.DisplaySize;
        var art = new DisplayArt(size, [Screens.Monitor(sample, size)], [1000]);
        PreviewImage.Source = BitmapFactory.Create(PixelImageService.Enlarge(art, PreviewSize)[0]);
    }

    private void UpdateToggle()
    {
        bool on = _player.Mode == PlayerMode.Monitor;
        if (MonitorToggle.IsOn != on) MonitorToggle.IsOn = on;
    }

    private void UpdateNotifyExample() =>
        NotifyExampleText.Text = $"curl --data \"Build done\" \"http://127.0.0.1:{_settings.Settings.HttpBridgePort}/notify?icon=check&color=3DDC84\"";

    private async void MonitorToggle_Toggled(object sender, RoutedEventArgs e)
    {
        bool running = _player.Mode == PlayerMode.Monitor;
        if (MonitorToggle.IsOn == running) return;
        if (!MonitorToggle.IsOn)
        {
            await _player.StopAsync();
            return;
        }
        if (!_connection.IsConnected)
        {
            MonitorToggle.IsOn = false;
            ShowMessage(InfoBarSeverity.Warning, "No display connected", "Connect your Divoom display on the Device page first.");
            return;
        }
        _player.StartMonitor();
    }

    private void Interval_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        _settings.Settings.MonitorIntervalSeconds = int.Parse((string)((ComboBoxItem)IntervalCombo.SelectedItem).Tag);
        _settings.Save();
    }

    private void LhmUrl_LostFocus(object sender, RoutedEventArgs e)
    {
        var url = LhmUrlBox.Text.Trim();
        if (url == _settings.Settings.LibreHardwareMonitorUrl) return;
        _settings.Settings.LibreHardwareMonitorUrl = url;
        _settings.Save();
    }

    private void Alert_Toggled(object sender, RoutedEventArgs e) => SaveAlerts();

    private void Alert_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => SaveAlerts();

    private void SaveAlerts()
    {
        if (!_loaded) return;
        var s = _settings.Settings;
        s.HotAlertEnabled = HotToggle.IsOn;
        s.BatteryAlertEnabled = BatteryToggle.IsOn;
        s.PowerAlertEnabled = PowerToggle.IsOn;
        s.ScreenOffWhenLocked = LockToggle.IsOn;
        if (!double.IsNaN(HotBox.Value)) s.HotThreshold = (int)HotBox.Value;
        if (!double.IsNaN(BatteryBox.Value)) s.LowBatteryPercent = (int)BatteryBox.Value;
        if (!double.IsNaN(DurationBox.Value)) s.AlertSeconds = (int)DurationBox.Value;
        _settings.Save();
    }

    private async void TestAlert_Click(object sender, RoutedEventArgs e)
    {
        if (!_connection.IsConnected)
        {
            ShowMessage(InfoBarSeverity.Warning, "No display connected", "Connect your Divoom display on the Device page first.");
            return;
        }
        await AlertService.Instance.TestAsync();
    }

    private void ShowMessage(InfoBarSeverity severity, string title, string text)
    {
        Message.Severity = severity;
        Message.Title = title;
        Message.Message = text;
        Message.IsOpen = true;
    }
}
