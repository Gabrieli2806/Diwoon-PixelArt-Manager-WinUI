using System.IO;
using System.Net;
using System.Text;

namespace DiwoonPixelArtManager.Services;

/// <summary>
/// Local HTTP API (127.0.0.1 only) so scripts and other apps can drive the display:
///   POST /img                                   body = image file (PNG, GIF, JPG…), converted like the gallery does
///   POST /notify?text=..&amp;icon=..&amp;color=RRGGBB  shows an alert for a few seconds, then the previous content
///   POST /cmd?cmd=NN                            Bluetooth: body = command arguments, wrapped in a Divoom packet
///   POST /raw                                   Bluetooth: body = complete packet; Wi-Fi: body = JSON command
/// </summary>
public sealed class HttpBridgeService
{
    public static HttpBridgeService Instance { get; } = new();

    private HttpListener? _listener;

    public bool IsRunning => _listener?.IsListening == true;
    public string? LastError { get; private set; }

    /// <summary>Starts, stops or restarts the server to match the settings.</summary>
    public void ApplySettings()
    {
        var settings = SettingsService.Instance.Settings;
        Stop();
        LastError = null;
        if (!settings.HttpBridgeEnabled) return;

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{settings.HttpBridgePort}/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            LastError = ex.Message;
            listener.Close();
            return;
        }
        _listener = listener;
        _ = AcceptLoopAsync(listener);
    }

    public void Stop()
    {
        _listener?.Close();
        _listener = null;
    }

    private static async Task AcceptLoopAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return;
            }
            await HandleAsync(context);
        }
    }

    private static BluetoothTransport AsBluetooth(IDisplayTransport transport) =>
        transport as BluetoothTransport ?? throw new ArgumentException("This path only works with Bluetooth displays.");

    private static RgbColor? ParseColor(string? hex)
    {
        hex = hex?.TrimStart('#');
        if (hex is null || hex.Length != 6 || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int rgb))
            return null;
        return new RgbColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    private static async Task HandleAsync(HttpListenerContext context)
    {
        int status = 200;
        string message = "OK";
        try
        {
            if (context.Request.HttpMethod != "POST")
                throw new ArgumentException("Use POST.");

            using var body = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(body);
            var query = context.Request.QueryString;
            var connection = DivoomConnection.Instance;

            switch (context.Request.Url?.AbsolutePath)
            {
                case "/img":
                    if (!connection.IsConnected) throw new InvalidOperationException("No display is connected.");
                    var settings = SettingsService.Instance.Settings;
                    var art = await PixelImageService.LoadForDisplayAsync(body.ToArray(), connection.DisplaySize, settings.Scaling, settings.Fit);
                    await DisplayPlayer.Instance.ShowImageAsync(art, null);
                    break;
                case "/notify":
                    if (!connection.IsConnected) throw new InvalidOperationException("No display is connected.");
                    var text = query["text"] ?? Encoding.UTF8.GetString(body.ToArray());
                    if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Give the text in the 'text' parameter or in the body.");
                    await AlertService.Instance.NotifyAsync(text.Trim(), query["icon"], ParseColor(query["color"]));
                    break;
                case "/cmd":
                    if (!byte.TryParse(query["cmd"], out byte command))
                        throw new ArgumentException("Query parameter 'cmd' must be a number from 0 to 255.");
                    await connection.RunAsync(t => AsBluetooth(t).SendAsync(DivoomProtocol.BuildPacket(command, body.ToArray())));
                    break;
                case "/raw":
                    message = await connection.RunAsync(async t =>
                    {
                        if (t is WifiTransport wifi) return await wifi.PostRawAsync(Encoding.UTF8.GetString(body.ToArray()));
                        await AsBluetooth(t).SendAsync(body.ToArray());
                        return "OK";
                    });
                    break;
                default:
                    status = 404;
                    message = "Unknown path. Use /img, /notify, /cmd or /raw.";
                    break;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        {
            status = 400;
            message = ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            status = 503;
            message = ex.Message;
        }

        var bytes = Encoding.UTF8.GetBytes(message);
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        try
        {
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
        catch (HttpListenerException)
        {
            // The caller closed the connection before reading the answer.
        }
    }
}
