using DiwoonPixelArtManager.Services;

namespace DiwoonPixelArtManager.Models;

public sealed class AppSettings
{
    public string Theme { get; set; } = "System";
    public string Backdrop { get; set; } = "Mica";

    /// <summary>0 = detect from the device, otherwise 16, 32 or 64.</summary>
    public int DisplaySize { get; set; }
    public ScalingMode Scaling { get; set; } = ScalingMode.Pixel;
    public FitMode Fit { get; set; } = FitMode.Fit;

    public int SlideshowSeconds { get; set; } = 30;
    public bool SlideshowShuffle { get; set; }

    public bool HttpBridgeEnabled { get; set; }
    public int HttpBridgePort { get; set; } = 10119;

    public DeviceKind LastDeviceKind { get; set; }
    public string? LastDeviceAddress { get; set; }
    public string? LastDeviceName { get; set; }
    public List<WifiDeviceSetting> WifiDevices { get; set; } = [];

    public bool AutoConnect { get; set; } = true;
    public bool CloseToTray { get; set; } = true;

    public int MonitorIntervalSeconds { get; set; } = 2;
    public string LibreHardwareMonitorUrl { get; set; } = "http://localhost:8085/data.json";

    public int TimerMinutes { get; set; } = 10;
    public int FocusMinutes { get; set; } = 25;
    public int BreakMinutes { get; set; } = 5;
    public int LongBreakMinutes { get; set; } = 15;
    public bool AnnounceTracks { get; set; } = true;

    public bool AppProfilesEnabled { get; set; } = true;
    public List<AppProfile> AppProfiles { get; set; } = [];

    public bool AgentAlertsEnabled { get; set; } = true;
    public bool AgentWaitingEnabled { get; set; } = true;
    public bool AgentDoneEnabled { get; set; } = true;
    public bool AgentErrorEnabled { get; set; } = true;
    public List<AgentKind> AgentsMuted { get; set; } = [];

        public int AlertSeconds { get; set; } = 6;
    public bool HotAlertEnabled { get; set; } = true;
    public int HotThreshold { get; set; } = 85;
    public bool BatteryAlertEnabled { get; set; } = true;
    public int LowBatteryPercent { get; set; } = 20;
    public bool PowerAlertEnabled { get; set; } = true;
    public bool ScreenOffWhenLocked { get; set; }
}

public sealed class WifiDeviceSetting
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
}
