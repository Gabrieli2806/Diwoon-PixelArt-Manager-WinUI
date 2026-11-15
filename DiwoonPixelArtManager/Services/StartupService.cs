using Microsoft.Win32;

namespace DiwoonPixelArtManager.Services;

/// <summary>Start with Windows through the current user's Run key; the app then starts hidden in the notification area.</summary>
public static class StartupService
{
    public const string MinimizedArgument = "--minimized";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DiwoonPixelArtManager";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    /// <summary>Points the startup entry at this copy of the app, so it keeps working after the folder is moved.</summary>
    public static void RefreshPath()
    {
        if (IsEnabled)
            SetEnabled(true);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" {MinimizedArgument}");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
