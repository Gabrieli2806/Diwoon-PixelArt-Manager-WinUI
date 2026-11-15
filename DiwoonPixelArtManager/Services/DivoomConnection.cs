using DiwoonPixelArtManager.Models;

namespace DiwoonPixelArtManager.Services;

/// <summary>
/// The display the app is connected to, whatever its transport. Remembers the last device, connects to it at start
/// and keeps retrying in the background when the connection drops.
/// </summary>
public sealed class DivoomConnection
{
    public static DivoomConnection Instance { get; } = new();

    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(15);

    private readonly SettingsService _settings = SettingsService.Instance;
    private CancellationTokenSource? _reconnect;

    public IDisplayTransport? Transport { get; private set; }
    public DeviceEntry? Device => Transport?.Device;
    public bool IsConnected => Transport is not null;

    /// <summary>The device being reconnected after the connection dropped, if any.</summary>
    public DeviceEntry? Reconnecting { get; private set; }

    /// <summary>Raised on any thread when the device connects, disconnects or starts reconnecting.</summary>
    public event Action? ConnectionChanged;

    /// <summary>Display size in LEDs: the size chosen in Settings, or the one of the connected model.</summary>
    public int DisplaySize
    {
        get
        {
            int chosen = _settings.Settings.DisplaySize;
            return chosen > 0 ? chosen : Transport?.NativeSize ?? 16;
        }
    }

    public async Task ConnectAsync(DeviceEntry device)
    {
        StopReconnecting();
        Close();
        IDisplayTransport transport = device.Kind == DeviceKind.Wifi ? new WifiTransport(device) : new BluetoothTransport(device);
        try
        {
            await transport.ConnectAsync();
        }
        catch
        {
            transport.Dispose();
            throw;
        }
        Transport = transport;
        Remember(device);
        ConnectionChanged?.Invoke();
    }

    /// <summary>Disconnects on the user's request; no reconnection is attempted.</summary>
    public void Disconnect()
    {
        StopReconnecting();
        Close();
        ConnectionChanged?.Invoke();
    }

    /// <summary>Connects to the last used display, if the setting is on. Failures start the background retries.</summary>
    public async Task ConnectToLastDeviceAsync()
    {
        var s = _settings.Settings;
        if (!s.AutoConnect || string.IsNullOrEmpty(s.LastDeviceAddress)) return;
        var kind = s.LastDeviceKind;

        string name = s.LastDeviceName ?? "Divoom", address = s.LastDeviceAddress;
        DeviceEntry device;
        try
        {
            // Looking a Bluetooth device up by address blocks on Windows APIs that must not run on the UI thread.
            device = kind == DeviceKind.Wifi
                ? new DeviceEntry(DeviceKind.Wifi, name, address)
                : await Task.Run(() => BluetoothTransport.FromAddress(name, address));
        }
        catch (Exception ex)
        {
            AppLog.Write($"Could not look up {name} ({address})", ex);
            return;
        }

        try
        {
            await ConnectAsync(device);
            AppLog.Write($"Connected to {device.Name} at start");
        }
        catch (Exception ex)
        {
            AppLog.Write($"Connecting to {device.Name} at start failed", ex);
            StartReconnecting(device);
        }
    }

    /// <summary>Runs a command on the connected display. A lost connection is closed and retried in the background.</summary>
    public async Task<T> RunAsync<T>(Func<IDisplayTransport, Task<T>> command)
    {
        var transport = Transport ?? throw new InvalidOperationException("No display is connected.");
        try
        {
            return await command(transport);
        }
        catch (DeviceLostException)
        {
            if (Transport == transport)
            {
                Close();
                if (_settings.Settings.AutoConnect)
                    StartReconnecting(transport.Device);
                ConnectionChanged?.Invoke();
            }
            throw;
        }
    }

    public Task RunAsync(Func<IDisplayTransport, Task> command) =>
        RunAsync(async t => { await command(t); return true; });

    public Task<bool> ShowArtAsync(DisplayArt art) => RunAsync(t => t.ShowArtAsync(art));

    public static bool IsConnectionFailure(Exception ex) =>
        ex is InvalidOperationException or IOException or System.Net.Sockets.SocketException or PlatformNotSupportedException;

    private void Close()
    {
        Transport?.Dispose();
        Transport = null;
    }

    private void Remember(DeviceEntry device)
    {
        var s = _settings.Settings;
        s.LastDeviceKind = device.Kind;
        s.LastDeviceAddress = device.Address;
        s.LastDeviceName = device.Name;
        if (device.Kind == DeviceKind.Wifi && !s.WifiDevices.Any(w => w.Address == device.Address))
            s.WifiDevices.Add(new WifiDeviceSetting { Name = device.Name, Address = device.Address });
        _settings.Save();
    }

    private void StartReconnecting(DeviceEntry device)
    {
        StopReconnecting();
        var cancel = new CancellationTokenSource();
        _reconnect = cancel;
        Reconnecting = device;
        _ = Task.Run(async () =>
        {
            while (!cancel.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(ReconnectInterval, cancel.Token);
                    await ConnectAsync(device);
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // The display is still off or out of range; try again on the next round.
                    AppLog.Write($"Reconnecting to {device.Name} failed", ex);
                }
            }
        });
        ConnectionChanged?.Invoke();
    }

    private void StopReconnecting()
    {
        _reconnect?.Cancel();
        _reconnect = null;
        Reconnecting = null;
    }
}
