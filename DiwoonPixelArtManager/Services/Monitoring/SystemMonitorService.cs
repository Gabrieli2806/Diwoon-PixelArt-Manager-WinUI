using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DiwoonPixelArtManager.Services;

public sealed record SystemSample(
    double? CpuTemperature,
    double? GpuTemperature,
    double? CpuLoad,
    double? GpuLoad,
    double RamLoad,
    bool FromLibreHardwareMonitor)
{
    public double? HottestTemperature => new[] { CpuTemperature, GpuTemperature }.Max();
}

/// <summary>
/// Reads PC temperatures and load every few seconds. When LibreHardwareMonitor runs with its web server on,
/// its temperatures are used (they include the real CPU package temperature); otherwise the built-in readings.
/// </summary>
public sealed partial class SystemMonitorService
{
    public static SystemMonitorService Instance { get; } = new();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(1) };
    private static readonly TimeSpan LhmRetryInterval = TimeSpan.FromSeconds(60);
    private static readonly string[] CpuPreferred = ["CPU Package", "Core (Tctl/Tdie)", "Tctl/Tdie", "Core Max", "Core Average"];
    private static readonly string[] GpuPreferred = ["GPU Core", "GPU Temperature"];

    // Many desktops expose an ACPI zone that always reports the same placeholder value. It is only trusted once it moves.
    private const double AcpiChangeThreshold = 0.5;
    private double? _firstAcpi;
    private bool _acpiMoves;

    private NativeSensors? _native;
    private DateTime _lhmRetryAt = DateTime.MinValue;
    private bool _started;

    public SystemSample? Latest { get; private set; }

    /// <summary>Raised on a background thread after each reading.</summary>
    public event Action<SystemSample>? SampleUpdated;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        _native = new NativeSensors();
        while (true)
        {
            var sample = await ReadAsync();
            Latest = sample;
            SampleUpdated?.Invoke(sample);
            int seconds = Math.Clamp(SettingsService.Instance.Settings.MonitorIntervalSeconds, 1, 60);
            await Task.Delay(TimeSpan.FromSeconds(seconds));
        }
    }

    private async Task<SystemSample> ReadAsync()
    {
        var native = _native!.Read();
        var lhm = await ReadLibreHardwareMonitorAsync();
        return new SystemSample(
            lhm?.Cpu ?? TrustedAcpi(native.AcpiTemperature),
            lhm?.Gpu ?? native.GpuTemperature,
            native.CpuLoad,
            native.GpuLoad,
            native.RamLoad,
            lhm is not null);
    }

    private double? TrustedAcpi(double? celsius)
    {
        if (celsius is not { } value) return null;
        _firstAcpi ??= value;
        if (Math.Abs(value - _firstAcpi.Value) >= AcpiChangeThreshold) _acpiMoves = true;
        return _acpiMoves ? value : null;
    }

    private async Task<(double? Cpu, double? Gpu)?> ReadLibreHardwareMonitorAsync()
    {
        var url = SettingsService.Instance.Settings.LibreHardwareMonitorUrl;
        if (string.IsNullOrWhiteSpace(url) || DateTime.UtcNow < _lhmRetryAt) return null;
        try
        {
            using var document = JsonDocument.Parse(await Http.GetStringAsync(url));
            var temperatures = new List<(string Id, string Name, double Value)>();
            CollectTemperatures(document.RootElement, temperatures);
            if (temperatures.Count == 0) return null;
            return (Pick(temperatures, "cpu", CpuPreferred), Pick(temperatures, "gpu", GpuPreferred));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            // Not running, or the web server is off: check again later without slowing every reading.
            _lhmRetryAt = DateTime.UtcNow + LhmRetryInterval;
            return null;
        }
    }

    private static void CollectTemperatures(JsonElement node, List<(string, string, double)> result)
    {
        if (node.TryGetProperty("SensorId", out var id) && id.GetString() is { } sensorId
            && sensorId.Contains("/temperature/", StringComparison.OrdinalIgnoreCase)
            && node.TryGetProperty("Value", out var value) && ParseNumber(value.GetString()) is { } number)
        {
            result.Add((sensorId, node.GetProperty("Text").GetString() ?? "", number));
        }
        if (node.TryGetProperty("Children", out var children))
            foreach (var child in children.EnumerateArray())
                CollectTemperatures(child, result);
    }

    private static double? Pick(List<(string Id, string Name, double Value)> sensors, string kind, string[] preferred)
    {
        var matching = sensors.Where(s => s.Id.Contains(kind, StringComparison.OrdinalIgnoreCase) && s.Value > 0).ToList();
        foreach (var name in preferred)
            if (matching.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Id: not null } hit)
                return hit.Value;
        return matching.Count > 0 ? matching.Max(s => s.Value) : null;
    }

    /// <summary>"45,5 °C" or "45.5 °C" → 45.5</summary>
    private static double? ParseNumber(string? text)
    {
        var match = NumberPattern().Match(text ?? "");
        return match.Success && double.TryParse(match.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    [GeneratedRegex(@"-?\d+(?:[.,]\d+)?")]
    private static partial Regex NumberPattern();
}
