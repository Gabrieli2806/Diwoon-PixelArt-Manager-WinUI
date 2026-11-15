using System.Net;
using DiwoonPixelArtManager.Services;
using Microsoft.UI.Xaml.Media;

namespace DiwoonPixelArtManager.Views;

public sealed partial class DevicePage : Page
{
    private const int VjEffectCount = 16;
    private const int VisualizerCount = 12;

    private readonly DivoomConnection _connection = DivoomConnection.Instance;
    private readonly DisplayPlayer _player = DisplayPlayer.Instance;
    private readonly SettingsService _settings = SettingsService.Instance;
    private readonly DispatcherTimer _brightnessTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly List<DeviceEntry> _devices = [];

    public DevicePage()
    {
        InitializeComponent();
        for (int i = 1; i <= VjEffectCount; i++) VjCombo.Items.Add($"Effect {i}");
        for (int i = 1; i <= VisualizerCount; i++)
        {
            VisualizerCombo.Items.Add($"Visualizer {i}");
            WifiVisualizerCombo.Items.Add($"Visualizer {i}");
        }
        VjCombo.SelectedIndex = 0;
        VisualizerCombo.SelectedIndex = 0;
        WifiVisualizerCombo.SelectedIndex = 0;

        _brightnessTimer.Tick += async (_, _) =>
        {
            _brightnessTimer.Stop();
            await RunAsync(t => t.SetBrightnessAsync((int)BrightnessSlider.Value), releasesDisplay: false);
        };
        _connection.ConnectionChanged += () => DispatcherQueue.TryEnqueue(UpdateConnectionState);

        AddDevices(_settings.Settings.WifiDevices.Select(w => new DeviceEntry(DeviceKind.Wifi, w.Name, w.Address)));
        UpdateConnectionState();
        _ = FindDevicesAsync(scan: false);
    }

    private void AddDevices(IEnumerable<DeviceEntry> devices)
    {
        foreach (var device in devices)
            if (!_devices.Any(d => d.Kind == device.Kind && d.Address == device.Address))
                _devices.Add(device);

        var selected = DeviceCombo.SelectedItem as DeviceEntry;
        DeviceCombo.ItemsSource = null;
        DeviceCombo.ItemsSource = _devices;
        DeviceCombo.SelectedItem = selected is not null ? _devices.FirstOrDefault(d => d.Address == selected.Address)
            : _devices.FirstOrDefault(d => d.Address == (_connection.Device?.Address ?? _settings.Settings.LastDeviceAddress))
              ?? _devices.FirstOrDefault();
    }

    private async Task FindDevicesAsync(bool scan)
    {
        SetBusy(true);
        ConnectionMessage.IsOpen = false;
        var problems = new List<string>();
        try
        {
            var bluetooth = BluetoothTransport.FindAsync(scan);
            var wifi = scan ? WifiTransport.FindOnNetworkAsync() : Task.FromResult(new List<DeviceEntry>());
            try { AddDevices(await bluetooth); }
            catch (Exception ex) when (DivoomConnection.IsConnectionFailure(ex)) { problems.Add($"Bluetooth is not available ({ex.Message})."); }
            try { AddDevices(await wifi); }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                problems.Add("Could not search the network for Wi-Fi displays; type the IP address instead.");
            }

            if (scan && _devices.Count == 0)
                problems.Add("No Divoom display found. Check that it is on and nearby.");
            if (problems.Count > 0)
                ShowError(ConnectionMessage, string.Join(" ", problems));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void UpdateConnectionState()
    {
        bool connected = _connection.IsConnected;
        ConnectButton.Content = connected ? "Disconnect" : _connection.Reconnecting is not null ? "Stop retrying" : "Connect";
        DeviceCombo.IsEnabled = !connected;
        ScanButton.IsEnabled = !connected;
        WifiAddressBox.IsEnabled = !connected;
        AddWifiButton.IsEnabled = !connected;
        ControlsHost.IsEnabled = connected;

        bool wifi = _connection.Device?.Kind == DeviceKind.Wifi;
        BluetoothChannels.Visibility = wifi ? Visibility.Collapsed : Visibility.Visible;
        WifiChannels.Visibility = wifi ? Visibility.Visible : Visibility.Collapsed;

        ConnectionText.Text = connected
            ? $"Connected to {_connection.Device!.Name} · {_connection.DisplaySize}×{_connection.DisplaySize} LEDs"
            : _connection.Reconnecting is { } pending
                ? $"Lost the connection to {pending.Name}. Retrying every 15 seconds…"
                : "Bluetooth displays: pair them in Windows settings first. Wi-Fi displays (Pixoo-64): press Scan or type their IP.";
    }

    private void SetBusy(bool busy)
    {
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ScanButton.IsEnabled = !busy && !_connection.IsConnected;
        ConnectButton.IsEnabled = !busy;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await FindDevicesAsync(scan: true);

    private async void AddWifi_Click(object sender, RoutedEventArgs e)
    {
        var address = WifiAddressBox.Text.Trim();
        if (!IPAddress.TryParse(address, out _))
        {
            ShowError(ConnectionMessage, "Type the display's IP address, for example 192.168.1.50. The Divoom app shows it under the device settings.");
            return;
        }
        var device = new DeviceEntry(DeviceKind.Wifi, "Pixoo", address);
        AddDevices([device]);
        DeviceCombo.SelectedItem = _devices.First(d => d.Address == address);
        WifiAddressBox.Text = "";
        await ConnectAsync(device);
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_connection.IsConnected || _connection.Reconnecting is not null)
        {
            _connection.Disconnect();
            return;
        }
        if (DeviceCombo.SelectedItem is DeviceEntry device)
            await ConnectAsync(device);
    }

    private async Task ConnectAsync(DeviceEntry device)
    {
        SetBusy(true);
        ConnectionMessage.IsOpen = false;
        try
        {
            await _connection.ConnectAsync(device);
        }
        catch (Exception ex) when (DivoomConnection.IsConnectionFailure(ex))
        {
            ShowError(ConnectionMessage, device.Kind == DeviceKind.Wifi
                ? $"No answer from {device.Address}. Check the IP and that the display is on the same network. ({ex.Message})"
                : $"Could not connect to {device.Name}. Make sure it is on and not connected to another device. ({ex.Message})");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Brightness_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_connection.IsConnected) return;
        _brightnessTimer.Stop();
        _brightnessTimer.Start();
    }

    private void ClockColor_Changed(ColorPicker sender, ColorChangedEventArgs args) =>
        ClockColorSwatch.Background = new SolidColorBrush(args.NewColor);

    private void LightColor_Changed(ColorPicker sender, ColorChangedEventArgs args) =>
        LightColorSwatch.Background = new SolidColorBrush(args.NewColor);

    private static RgbColor ToRgb(global::Windows.UI.Color color) => new(color.R, color.G, color.B);

    private async void ScreenOff_Click(object sender, RoutedEventArgs e) => await RunAsync(t => t.SetScreenAsync(false), releasesDisplay: false);

    private async void ScreenOn_Click(object sender, RoutedEventArgs e) => await RunAsync(t => t.SetScreenAsync(true), releasesDisplay: false);

    private async void Clock_Click(object sender, RoutedEventArgs e) =>
        await RunBluetoothAsync(DivoomProtocol.Clock((ClockStyle)ClockStyleCombo.SelectedIndex, ToRgb(ClockColorPicker.Color)));

    private async void Light_Click(object sender, RoutedEventArgs e) =>
        await RunBluetoothAsync(DivoomProtocol.Light(ToRgb(LightColorPicker.Color), (int)BrightnessSlider.Value, (LightEffect)LightEffectCombo.SelectedIndex, on: true));

    private async void Vj_Click(object sender, RoutedEventArgs e) => await RunBluetoothAsync(DivoomProtocol.VjEffect(VjCombo.SelectedIndex));

    private async void Visualizer_Click(object sender, RoutedEventArgs e) => await RunBluetoothAsync(DivoomProtocol.Visualizer(VisualizerCombo.SelectedIndex));

    private async void Custom_Click(object sender, RoutedEventArgs e) => await RunBluetoothAsync(DivoomProtocol.CustomAnimationChannel());

    private async void Cloud_Click(object sender, RoutedEventArgs e) => await RunBluetoothAsync(DivoomProtocol.CloudChannel());

    private async void WifiChannel_Click(object sender, RoutedEventArgs e) =>
        await RunWifiAsync(w => w.SetChannelAsync((WifiChannel)WifiChannelCombo.SelectedIndex));

    private async void ClockFace_Click(object sender, RoutedEventArgs e) =>
        await RunWifiAsync(w => w.SetClockFaceAsync(double.IsNaN(ClockFaceBox.Value) ? 0 : (int)ClockFaceBox.Value));

    private async void WifiVisualizer_Click(object sender, RoutedEventArgs e) =>
        await RunWifiAsync(w => w.SetVisualizerAsync(WifiVisualizerCombo.SelectedIndex));

    private async void Scoreboard_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(t => t.ShowScoreboardAsync(ScoreOf(RedScoreBox), ScoreOf(BlueScoreBox)));

    private static int ScoreOf(NumberBox box) => double.IsNaN(box.Value) ? 0 : (int)box.Value;

    private Task RunBluetoothAsync(byte[] packet) =>
        RunAsync(t => ((BluetoothTransport)t).SendAsync(packet));

    private Task RunWifiAsync(Func<WifiTransport, Task> command) =>
        RunAsync(t => command((WifiTransport)t));

    /// <summary>Sends a command. Channel changes stop the slideshow or PC monitor, which would otherwise draw over them.</summary>
    private async Task RunAsync(Func<IDisplayTransport, Task> command, bool releasesDisplay = true)
    {
        CommandMessage.IsOpen = false;
        if (releasesDisplay)
            _player.ReleaseDisplay();
        try
        {
            await _connection.RunAsync(command);
        }
        catch (InvalidOperationException ex)
        {
            ShowError(CommandMessage, ex.Message);
        }
    }

    private static void ShowError(InfoBar bar, string message)
    {
        bar.Severity = InfoBarSeverity.Error;
        bar.Message = message;
        bar.IsOpen = true;
    }
}
