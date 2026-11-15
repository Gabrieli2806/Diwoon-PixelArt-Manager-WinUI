using Microsoft.Windows.System.Power;

namespace DiwoonPixelArtManager.Services;

/// <summary>
/// Turns PC events into alerts on the display: high temperature, low battery, charger plugged or unplugged, and the
/// screen going off while the PC is locked. Each alert can be switched off in the Monitor page.
/// </summary>
public sealed class AlertService
{
    public static AlertService Instance { get; } = new();

    private const int HotHysteresis = 5;
    private const int BatteryHysteresis = 5;
    private static readonly TimeSpan HotRepeat = TimeSpan.FromMinutes(5);

    private readonly DisplayPlayer _player = DisplayPlayer.Instance;
    private readonly SettingsService _settings = SettingsService.Instance;

    private bool _hotArmed = true, _batteryArmed = true;
    private DateTime _lastHotAlert = DateTime.MinValue;
    private PowerSupplyStatus _lastSupply;

    public void Start()
    {
        SystemMonitorService.Instance.SampleUpdated += CheckTemperature;

        _lastSupply = PowerManager.PowerSupplyStatus;
        PowerManager.PowerSupplyStatusChanged += (_, _) => OnPowerSupplyChanged();
        PowerManager.RemainingChargePercentChanged += (_, _) => CheckBattery();
    }

    /// <summary>Shows a sample alert so the user can see how alerts look on their display.</summary>
    public Task TestAsync() => _player.ShowAlertAsync(new DisplayAlert("Hello!", PixelIcons.Bell, Screens.AlertBlue));

    /// <summary>Alert sent by another program through the HTTP API.</summary>
    public Task NotifyAsync(string text, string? icon, RgbColor? color) =>
        _player.ShowAlertAsync(new DisplayAlert(text, PixelIcons.ByName(icon), color ?? Screens.AlertBlue));

    public async void OnSessionLocked()
    {
        if (_settings.Settings.ScreenOffWhenLocked)
            await _player.SetScreenAsync(false);
    }

    public async void OnSessionUnlocked()
    {
        if (_settings.Settings.ScreenOffWhenLocked)
            await _player.SetScreenAsync(true);
    }

    private async void CheckTemperature(SystemSample sample)
    {
        var s = _settings.Settings;
        if (!s.HotAlertEnabled || sample.HottestTemperature is not { } hottest) return;

        if (hottest < s.HotThreshold - HotHysteresis)
        {
            _hotArmed = true;
            return;
        }
        if (hottest < s.HotThreshold || (!_hotArmed && DateTime.UtcNow - _lastHotAlert < HotRepeat)) return;

        _hotArmed = false;
        _lastHotAlert = DateTime.UtcNow;
        bool cpuHotter = (sample.CpuTemperature ?? 0) >= (sample.GpuTemperature ?? 0);
        await _player.ShowAlertAsync(new DisplayAlert($"{(cpuHotter ? "CPU" : "GPU")} {hottest:0}°", PixelIcons.Hot, Screens.AlertRed));
    }

    private async void OnPowerSupplyChanged()
    {
        var supply = PowerManager.PowerSupplyStatus;
        bool changed = (supply == PowerSupplyStatus.NotPresent) != (_lastSupply == PowerSupplyStatus.NotPresent);
        _lastSupply = supply;
        if (!changed || !_settings.Settings.PowerAlertEnabled || PowerManager.BatteryStatus == BatteryStatus.NotPresent) return;

        int percent = PowerManager.RemainingChargePercent;
        await _player.ShowAlertAsync(supply == PowerSupplyStatus.NotPresent
            ? new DisplayAlert($"{percent}%", PixelIcons.BatteryLow, Screens.AlertAmber)
            : new DisplayAlert($"{percent}%", PixelIcons.Charging, Screens.AlertGreen));
    }

    private async void CheckBattery()
    {
        var s = _settings.Settings;
        if (!s.BatteryAlertEnabled || PowerManager.BatteryStatus == BatteryStatus.NotPresent) return;

        int percent = PowerManager.RemainingChargePercent;
        bool charging = PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent;
        if (charging || percent > s.LowBatteryPercent + BatteryHysteresis)
        {
            _batteryArmed = true;
            return;
        }
        if (!_batteryArmed || percent > s.LowBatteryPercent) return;

        _batteryArmed = false;
        await _player.ShowAlertAsync(new DisplayAlert($"LOW {percent}%", PixelIcons.BatteryLow, Screens.AlertRed));
    }
}
