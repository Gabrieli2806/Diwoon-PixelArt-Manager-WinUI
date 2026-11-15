using InTheHand.Net.Sockets;

namespace DiwoonPixelArtManager.Services;

public enum DeviceKind
{
    Bluetooth,
    Wifi
}

/// <summary>A display the app can connect to: a Bluetooth model or a Wi-Fi Pixoo found on the network.</summary>
public sealed record DeviceEntry(DeviceKind Kind, string Name, string Address, BluetoothDeviceInfo? Bluetooth = null)
{
    public override string ToString() =>
        Kind == DeviceKind.Wifi ? $"{Name}  (Wi-Fi · {Address})" : $"{Name}  (Bluetooth · {Address})";
}

/// <summary>The connection to one display. Implementations serialize their own sends.</summary>
public interface IDisplayTransport : IDisposable
{
    DeviceEntry Device { get; }

    /// <summary>Display size in LEDs, guessed from the model.</summary>
    int NativeSize { get; }

    Task ConnectAsync();

    /// <summary>Shows a picture. Returns false when an animation had to be reduced to its first frame.</summary>
    Task<bool> ShowArtAsync(DisplayArt art);

    Task SetBrightnessAsync(int percent);
    Task SetScreenAsync(bool on);
    Task ShowClockAsync();
    Task ShowScoreboardAsync(int red, int blue);
}

/// <summary>Raised by transports when the device stops answering.</summary>
public sealed class DeviceLostException(string message, Exception? inner = null) : InvalidOperationException(message, inner);
