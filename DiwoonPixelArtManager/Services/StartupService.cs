using Microsoft.Win32;
using Windows.ApplicationModel;

namespace DiwoonPixelArtManager.Services;

/// <summary>
/// Start with Windows. The installed .msix uses its declared startup task; the portable and installer builds use the
/// current user's Run key. Either way the app starts hidden in the notification area.
/// </summary>
public static class StartupService
{
    public const string MinimizedArgument = "--minimized";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DiwoonPixelArtManager";
    private const string StartupTaskId = "DiwoonPixelArtManagerStartup";

    /// <summary>True when running from the .msix package.</summary>
    public static bool IsPackaged { get; } = DetectPackage();

    private static bool DetectPackage()
    {
        try
        {
            return Package.Current is not null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static async Task<bool> IsEnabledAsync()
    {
        if (IsPackaged)
        {
            var task = await StartupTask.GetAsync(StartupTaskId);
            return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>Turns start with Windows on or off. Returns the resulting state (Windows can refuse for the package).</summary>
    public static async Task<bool> SetEnabledAsync(bool enabled)
    {
        if (IsPackaged)
        {
            var task = await StartupTask.GetAsync(StartupTaskId);
            if (enabled) return await task.RequestEnableAsync() is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            task.Disable();
            return false;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" {MinimizedArgument}");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        return enabled;
    }

    /// <summary>Points the Run entry at this copy of the app, so it keeps working after the folder is moved.</summary>
    public static void RefreshPath()
    {
        if (IsPackaged) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is string)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" {MinimizedArgument}");
    }
}
