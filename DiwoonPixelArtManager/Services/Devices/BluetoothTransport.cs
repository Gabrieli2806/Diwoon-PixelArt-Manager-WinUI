using System.IO;
using InTheHand.Net;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;

namespace DiwoonPixelArtManager.Services;

/// <summary>Bluetooth (RFCOMM serial) displays: Pixoo, Pixoo-Max, Timebox Evo, Ditoo…</summary>
public sealed class BluetoothTransport(DeviceEntry device) : IDisplayTransport
{
    // Divoom's Bluetooth vendor prefix (11:75:58); the name check covers devices sold under other prefixes.
    private const string DivoomAddressPrefix = "117558";
    private static readonly string[] DivoomNames = ["Pixoo", "Timebox", "Ditoo", "Divoom", "Tivoo", "Planet"];
    // Divoom's notes: write at most 666 bytes at a time and stay under ~30 messages per second, or the device disconnects.
    private const int WriteChunkSize = 666;
    private const int PacketGapMs = 40;

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private BluetoothClient? _client;
    private Stream? _stream;

    public DeviceEntry Device { get; } = device;

    /// <summary>Pixoo-Max has a 32×32 screen; the other Bluetooth models are 16×16.</summary>
    public int NativeSize => Device.Name.Contains("Max", StringComparison.OrdinalIgnoreCase) ? 32 : 16;

    public static bool IsDivoom(BluetoothDeviceInfo info) =>
        info.DeviceAddress.ToString().StartsWith(DivoomAddressPrefix, StringComparison.OrdinalIgnoreCase)
        || DivoomNames.Any(n => info.DeviceName.Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>Paired Divoom devices, plus the ones found by a Bluetooth scan (takes ~10 s).</summary>
    public static Task<List<DeviceEntry>> FindAsync(bool scan) => Task.Run(() =>
    {
        using var client = new BluetoothClient();
        var found = client.PairedDevices.Where(IsDivoom).ToList();
        if (scan)
        {
            foreach (var info in client.DiscoverDevices().Where(IsDivoom))
            {
                if (!found.Any(d => d.DeviceAddress == info.DeviceAddress))
                    found.Add(info);
            }
        }
        return found.Select(ToEntry).ToList();
    });

    public static DeviceEntry ToEntry(BluetoothDeviceInfo info) =>
        new(DeviceKind.Bluetooth, info.DeviceName, info.DeviceAddress.ToString("C"), info);

    /// <summary>Rebuilds an entry from a saved address, to reconnect at start without a scan.</summary>
    public static DeviceEntry FromAddress(string name, string address) =>
        new(DeviceKind.Bluetooth, name, address, new BluetoothDeviceInfo(BluetoothAddress.Parse(address)));

    public async Task ConnectAsync()
    {
        var info = Device.Bluetooth ?? throw new InvalidOperationException("Missing Bluetooth device information.");
        var client = new BluetoothClient();
        try
        {
            await Task.Run(() => client.Connect(info.DeviceAddress, BluetoothService.SerialPort));
        }
        catch
        {
            client.Dispose();
            throw;
        }
        _client = client;
        _stream = client.GetStream();
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
    }

    public Task SendAsync(byte[] packet) => SendAllAsync([packet]);

    public async Task SendAllAsync(IReadOnlyList<byte[]> packets)
    {
        await _sendLock.WaitAsync();
        try
        {
            var stream = _stream ?? throw new DeviceLostException("The display is not connected.");
            for (int i = 0; i < packets.Count; i++)
            {
                for (int offset = 0; offset < packets[i].Length; offset += WriteChunkSize)
                    await stream.WriteAsync(packets[i].AsMemory(offset, Math.Min(WriteChunkSize, packets[i].Length - offset)));
                await stream.FlushAsync();
                if (i < packets.Count - 1)
                    await Task.Delay(PacketGapMs);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new DeviceLostException("The Bluetooth connection to the display was lost.", ex);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<bool> ShowArtAsync(DisplayArt art)
    {
        bool isLarge = art.Size > 16;
        if (art.IsAnimated && !isLarge)
        {
            await SendAllAsync(DivoomProtocol.Animation(art));
            return true;
        }
        await SendAsync(DivoomProtocol.Image(art.Frames[0], isLarge));
        return !art.IsAnimated;
    }

    public Task SetBrightnessAsync(int percent) => SendAsync(DivoomProtocol.Brightness(percent));

    public Task SetScreenAsync(bool on) =>
        SendAsync(DivoomProtocol.Light(new RgbColor(0, 0, 0), on ? 100 : 0, LightEffect.PlainColor, on));

    public Task ShowClockAsync() => SendAsync(DivoomProtocol.Clock(ClockStyle.FullScreen, new RgbColor(255, 255, 255)));

    public Task ShowScoreboardAsync(int red, int blue) => SendAsync(DivoomProtocol.Scoreboard(red, blue));
}
