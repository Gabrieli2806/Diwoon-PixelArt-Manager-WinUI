using DiwoonPixelArtManager.Models;

namespace DiwoonPixelArtManager.Services;

/// <summary>
/// Watches which app is in front and which apps are running, and hands the matching profile to the display.
/// A profile for the app in front beats one for an app merely running; within each group the first in the list wins.
/// Switching to this app's own window keeps the current profile, so editing profiles does not flip the display.
/// </summary>
public sealed class AppProfileService
{
    public static AppProfileService Instance { get; } = new();

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RunningInterval = TimeSpan.FromSeconds(3);
    private static readonly string OwnProcess = System.Diagnostics.Process.GetCurrentProcess().ProcessName;

    private readonly SettingsService _settings = SettingsService.Instance;
    private HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _runningAt = DateTime.MinValue;
    private bool _started;

    /// <summary>The app in front at the last check, for the profiles page.</summary>
    public string ForegroundProcess { get; private set; } = "";

    public void Start()
    {
        if (_started) return;
        _started = true;
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(CheckInterval);
        while (await timer.WaitForNextTickAsync())
        {
            try
            {
                Check();
            }
            catch (Exception ex)
            {
                AppLog.Write("Checking app profiles failed", ex);
            }
        }
    }

    private void Check()
    {
        var s = _settings.Settings;
        var profiles = s.AppProfiles.Where(p => p.Enabled && p.Process.Length > 0).ToList();
        if (!s.AppProfilesEnabled || profiles.Count == 0)
        {
            DisplayPlayer.Instance.SetProfile(null);
            return;
        }

        var (foreground, _) = DesktopApps.Foreground();
        if (foreground.Equals(OwnProcess, StringComparison.OrdinalIgnoreCase)) return;
        ForegroundProcess = foreground;

        if (profiles.Any(p => p.Trigger == ProfileTrigger.Running) && DateTime.UtcNow - _runningAt > RunningInterval)
        {
            _running = DesktopApps.RunningProcesses();
            _runningAt = DateTime.UtcNow;
        }

        var match = profiles.FirstOrDefault(p => p.Trigger == ProfileTrigger.InFront && p.Process.Equals(foreground, StringComparison.OrdinalIgnoreCase))
            ?? profiles.FirstOrDefault(p => p.Trigger == ProfileTrigger.Running && _running.Contains(p.Process));
        DisplayPlayer.Instance.SetProfile(match);
    }
}
