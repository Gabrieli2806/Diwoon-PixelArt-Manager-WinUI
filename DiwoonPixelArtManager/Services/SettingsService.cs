using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiwoonPixelArtManager.Models;
using Microsoft.UI.Dispatching;

namespace DiwoonPixelArtManager.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // Declared after JsonOptions: static fields initialize in order, and the constructor reads files with those options.
    public static SettingsService Instance { get; } = new();

    private readonly string _path = Path.Combine(AppPaths.DataFolder, "settings.json");
    private readonly object _saveLock = new();
    private DispatcherQueue? _uiQueue;

    public AppSettings Settings { get; }

    /// <summary>Always raised on the UI thread, whichever thread saved.</summary>
    public event Action? SettingsChanged;

    private SettingsService()
    {
        Settings = Load();
    }

    /// <summary>Call once from the UI thread at start.</summary>
    public void AttachToUiThread() => _uiQueue = DispatcherQueue.GetForCurrentThread();

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? new AppSettings();
        }
        catch (JsonException ex)
        {
            // A damaged settings file falls back to the defaults instead of blocking the start; keep a copy to inspect.
            AppLog.Write("Reading settings failed, using defaults", ex);
            try { File.Copy(_path, _path + ".bad", overwrite: true); } catch (IOException) { }
        }
        return new AppSettings();
    }

    public void Save()
    {
        lock (_saveLock)
            AppPaths.WriteAllTextSafely(_path, JsonSerializer.Serialize(Settings, JsonOptions));

        if (_uiQueue is null || _uiQueue.HasThreadAccess)
            SettingsChanged?.Invoke();
        else
            _uiQueue.TryEnqueue(() => SettingsChanged?.Invoke());
    }
}
