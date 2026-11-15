using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DiwoonPixelArtManager.Services;

/// <summary>
/// Wi-Fi displays (Pixoo-64 and other models with the local HTTP API): every command is a JSON POST to http://IP/post.
/// Reference: https://doc.divoom-gz.com/web/#/12?page_id=196
/// </summary>
public sealed class WifiTransport(DeviceEntry device) : IDisplayTransport
{
    /// <summary>The device refuses animations with 60 frames or more.</summary>
    public const int MaxFrames = 59;

    // After a few dozen pictures the device stops accepting new ones unless the picture counter is reset.
    private const int PictureIdResetLimit = 32;
    private const string LanDiscoveryUrl = "https://app.divoom-gz.com/Device/ReturnSameLANDevice";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _pictureId;

    public DeviceEntry Device { get; } = device;
    public int NativeSize => 64;

    /// <summary>Asks Divoom's service which Wi-Fi displays share this network (the same lookup the official app does).</summary>
    public static async Task<List<DeviceEntry>> FindOnNetworkAsync()
    {
        using var response = await Http.PostAsync(LanDiscoveryUrl, new StringContent("{}"));
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        return (json?["DeviceList"]?.AsArray() ?? [])
            .Select(d => new DeviceEntry(DeviceKind.Wifi, (string?)d?["DeviceName"] ?? "Pixoo", (string?)d?["DevicePrivateIP"] ?? ""))
            .Where(d => d.Address.Length > 0)
            .ToList();
    }

    public async Task ConnectAsync()
    {
        await PostAsync(new JsonObject { ["Command"] = "Channel/GetAllConf" });
        var reply = await PostAsync(new JsonObject { ["Command"] = "Draw/GetHttpGifId" });
        _pictureId = (int?)reply["PicId"] ?? 0;
    }

    public void Dispose() { }

    public async Task<JsonObject> PostAsync(JsonObject command)
    {
        await _sendLock.WaitAsync();
        try
        {
            return await PostUnlockedAsync(command);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Sends a JSON command as given (for the HTTP API) and returns the device's answer.</summary>
    public async Task<string> PostRawAsync(string json)
    {
        JsonObject command;
        try
        {
            command = JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("The body must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"The body is not valid JSON: {ex.Message}");
        }
        return (await PostAsync(command)).ToJsonString();
    }

    private async Task<JsonObject> PostUnlockedAsync(JsonObject command)
    {
        try
        {
            using var response = await Http.PostAsJsonAsync($"http://{Device.Address}/post", command);
            response.EnsureSuccessStatusCode();
            var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject ?? [];
            int error = (int?)reply["error_code"] ?? 0;
            if (error != 0)
                throw new InvalidOperationException($"The display rejected '{command["Command"]}' (error {error}).");
            return reply;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new DeviceLostException($"The display at {Device.Address} does not answer.", ex);
        }
    }

    public async Task<bool> ShowArtAsync(DisplayArt art)
    {
        var frames = art.Frames;
        var delays = art.DelaysMs;
        if (frames.Count > MaxFrames)
        {
            // Keep the animation's length: sample frames evenly and lengthen each kept one.
            double step = (double)frames.Count / MaxFrames;
            var picked = Enumerable.Range(0, MaxFrames).Select(i => (int)(i * step)).ToList();
            frames = picked.Select(i => art.Frames[i]).ToList();
            delays = picked.Select(i => (int)(art.DelaysMs[i] * step)).ToList();
        }
        int speed = (int)delays.Average();

        await _sendLock.WaitAsync();
        try
        {
            if (_pictureId >= PictureIdResetLimit)
            {
                await PostUnlockedAsync(new JsonObject { ["Command"] = "Draw/ResetHttpGifId" });
                _pictureId = 0;
            }
            int id = ++_pictureId;
            for (int i = 0; i < frames.Count; i++)
            {
                await PostUnlockedAsync(new JsonObject
                {
                    ["Command"] = "Draw/SendHttpGif",
                    ["PicNum"] = frames.Count,
                    ["PicWidth"] = art.Size,
                    ["PicOffset"] = i,
                    ["PicID"] = id,
                    ["PicSpeed"] = speed,
                    ["PicData"] = Convert.ToBase64String(frames[i])
                });
            }
        }
        finally
        {
            _sendLock.Release();
        }
        return true;
    }

    public Task SetBrightnessAsync(int percent) =>
        PostAsync(new JsonObject { ["Command"] = "Channel/SetBrightness", ["Brightness"] = Math.Clamp(percent, 0, 100) });

    public Task SetScreenAsync(bool on) =>
        PostAsync(new JsonObject { ["Command"] = "Channel/OnOffScreen", ["OnOff"] = on ? 1 : 0 });

    public Task ShowClockAsync() => SetChannelAsync(WifiChannel.Faces);

    public Task ShowScoreboardAsync(int red, int blue) =>
        PostAsync(new JsonObject { ["Command"] = "Tools/SetScoreBoard", ["RedScore"] = Math.Clamp(red, 0, 999), ["BlueScore"] = Math.Clamp(blue, 0, 999) });

    public Task SetChannelAsync(WifiChannel channel) =>
        PostAsync(new JsonObject { ["Command"] = "Channel/SetIndex", ["SelectIndex"] = (int)channel });

    public Task SetClockFaceAsync(int clockId) =>
        PostAsync(new JsonObject { ["Command"] = "Channel/SetClockSelectId", ["ClockId"] = clockId });

    public Task SetVisualizerAsync(int index) =>
        PostAsync(new JsonObject { ["Command"] = "Channel/SetEqPosition", ["EqPosition"] = index });
}

public enum WifiChannel
{
    Faces = 0,
    Cloud = 1,
    Visualizer = 2,
    Custom = 3
}
