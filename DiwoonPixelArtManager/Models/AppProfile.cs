namespace DiwoonPixelArtManager.Models;

/// <summary>When a profile takes over the display.</summary>
public enum ProfileTrigger
{
    /// <summary>While the app's window is the one in front.</summary>
    InFront,

    /// <summary>While the app is running at all, even minimized or in the background.</summary>
    Running
}

/// <summary>What the display shows while a profile is active.</summary>
public enum ProfileScreen
{
    PcMonitor,
    NowPlaying,
    Timer,
    AppIcon,
    Image,
    Folder,
    Clock,
    ScreenOff
}

/// <summary>A rule: while this app is in front (or running), the display shows this.</summary>
public sealed class AppProfile
{
    /// <summary>Executable name without ".exe", as Windows reports it (for example "Spotify").</summary>
    public string Process { get; set; } = "";

    /// <summary>The name the app gives itself, for the list.</summary>
    public string Name { get; set; } = "";

    /// <summary>Full path of the executable, for its icon.</summary>
    public string? ExePath { get; set; }

    public bool Enabled { get; set; } = true;
    public ProfileTrigger Trigger { get; set; } = ProfileTrigger.InFront;
    public ProfileScreen Screen { get; set; } = ProfileScreen.PcMonitor;

    /// <summary>Picture shown when <see cref="Screen"/> is <see cref="ProfileScreen.Image"/>.</summary>
    public string? ImagePath { get; set; }

    /// <summary>Gallery folder played as a slideshow when <see cref="Screen"/> is <see cref="ProfileScreen.Folder"/>.</summary>
    public string? FolderPath { get; set; }
}
