using DiwoonPixelArtManager.Models;

namespace DiwoonPixelArtManager.Services;

public enum PlayerMode
{
    /// <summary>The display shows its own channels (clock, lights…).</summary>
    Device,
    Image,
    Slideshow,
    Monitor,
    NowPlaying,
    Timer
}

/// <summary>
/// Decides what the display shows: a gallery picture, a slideshow, the PC monitor, the playing track or a timer.
/// Alerts interrupt any of them for
/// a few seconds and the previous content comes back afterwards, also after the display reconnects.
/// </summary>
public sealed class DisplayPlayer
{
    public static DisplayPlayer Instance { get; } = new();

    private readonly DivoomConnection _connection = DivoomConnection.Instance;
    private readonly SettingsService _settings = SettingsService.Instance;
    private readonly SemaphoreSlim _showLock = new(1, 1);

    private DisplayArt? _image;
    private string? _imagePath;
    private List<string> _slides = [];
    private int _slideIndex;
    private DateTime _nextSlideAt, _nextMonitorAt, _alertUntil;
    private bool _alertShowing, _restorePending, _screenOff;

    // Generated screens (now playing, timer) are only sent when what they show changes, at most once a second.
    private static readonly TimeSpan MinScreenGap = TimeSpan.FromSeconds(1);
    private string? _lastScreenKey;
    private DateTime _lastScreenAt;
    private string? _coverKey;
    private byte[]? _coverRgb;

    // App profile: while one is active it decides what is shown; the user's own choice comes back afterwards.
    private AppProfile? _profile;
    private bool _profileChanged, _profileScreenOff;
    private int _profileSlide;
    private DateTime _profileSlideAt;

    // Attention: an animation that stays (an AI agent waiting for an answer) until it is cleared.
    private DisplayArt? _attention;
    private string? _attentionKey;
    private bool _attentionChanged;

    /// <summary>The app profile showing right now, if any.</summary>
    public AppProfile? ActiveProfile => _profile;

    public PlayerMode Mode { get; private set; } = PlayerMode.Device;

    /// <summary>File name of the picture or slide on the display, if any.</summary>
    public string? CurrentName => Mode is PlayerMode.Image or PlayerMode.Slideshow && _imagePath is not null
        ? Path.GetFileNameWithoutExtension(_imagePath)
        : null;

    /// <summary>Raised on a background thread when the mode or the slide changes.</summary>
    public event Action? StateChanged;

    /// <summary>Raised on a background thread when the display could not be updated.</summary>
    public event Action<string>? Error;

    public void Start()
    {
        _connection.ConnectionChanged += () =>
        {
            if (_connection.IsConnected && Mode != PlayerMode.Device) _restorePending = true;
        };
        NowPlayingService.Instance.TrackChanged += async info =>
        {
            if (Mode == PlayerMode.NowPlaying && _settings.Settings.AnnounceTracks)
            {
                var text = string.IsNullOrEmpty(info.Artist) ? info.Title : $"{info.Artist} - {info.Title}";
                await ShowAlertAsync(new DisplayAlert(text, PixelIcons.Music, Screens.AlertBlue));
            }
        };
        TimerService.Instance.Finished += async phase =>
        {
            if (Mode != PlayerMode.Timer) return;
            var (text, color) = phase switch
            {
                TimerPhase.Focus => ("Break!", Screens.AlertGreen),
                TimerPhase.ShortBreak or TimerPhase.LongBreak => ("Focus!", Screens.PhaseColor(TimerPhase.Focus)),
                _ => ("Time!", Screens.AlertBlue)
            };
            await ShowAlertAsync(new DisplayAlert(text, PixelIcons.Bell, color));
            if (!TimerService.Instance.IsActive) ReleaseDisplay();
        };
        _ = Task.Run(LoopAsync);
    }

    /// <summary>Converts a file for the connected display and shows it.</summary>
    public static Task<DisplayArt> LoadAsync(string path)
    {
        var s = SettingsService.Instance.Settings;
        return PixelImageService.LoadForDisplayAsync(path, DivoomConnection.Instance.DisplaySize, s.Scaling, s.Fit);
    }

    /// <summary>Shows a picture and keeps it. Returns false when only its first frame could be sent.</summary>
    public async Task<bool> ShowImageAsync(DisplayArt art, string? path)
    {
        Mode = PlayerMode.Image;
        _image = art;
        _imagePath = path;
        StateChanged?.Invoke();
        await _showLock.WaitAsync();
        try
        {
            return await _connection.ShowArtAsync(art);
        }
        finally
        {
            _showLock.Release();
        }
    }

    public void StartSlideshow(IReadOnlyList<string> paths)
    {
        _slides = _settings.Settings.SlideshowShuffle ? paths.OrderBy(_ => Random.Shared.Next()).ToList() : paths.ToList();
        _slideIndex = 0;
        _nextSlideAt = DateTime.MinValue;
        Mode = PlayerMode.Slideshow;
        StateChanged?.Invoke();
    }

    public void StartNowPlaying() => StartScreen(PlayerMode.NowPlaying);

    public void StartTimerDisplay() => StartScreen(PlayerMode.Timer);

    private void StartScreen(PlayerMode mode)
    {
        _lastScreenKey = null;
        Mode = mode;
        StateChanged?.Invoke();
    }

    public void StartMonitor()
    {
        _nextMonitorAt = DateTime.MinValue;
        Mode = PlayerMode.Monitor;
        StateChanged?.Invoke();
    }

    /// <summary>Stops the slideshow or monitor and gives the display back its clock.</summary>
    public async Task StopAsync()
    {
        Mode = PlayerMode.Device;
        StateChanged?.Invoke();
        if (_connection.IsConnected && !_alertShowing)
            await RunSafelyAsync(t => t.ShowClockAsync());
    }

    /// <summary>Hands the display to its own channels (used when the user picks a channel on the Device page).</summary>
    public void ReleaseDisplay()
    {
        if (Mode == PlayerMode.Device) return;
        Mode = PlayerMode.Device;
        StateChanged?.Invoke();
    }

    /// <summary>Called when the app in front (or running) matches a profile, or no longer does.</summary>
    public void SetProfile(AppProfile? profile)
    {
        if (ReferenceEquals(profile, _profile)) return;
        _profile = profile;
        _profileChanged = true;
        _profileSlide = 0;
        StateChanged?.Invoke();
    }

    /// <summary>Shows the active profile again after its settings were edited.</summary>
    public void ProfileEdited() => _profileChanged = true;

    /// <summary>How many animation frames the connected display takes (1 when it cannot play animations).</summary>
    public int MaxAnimationFrames =>
        _connection.Transport is WifiTransport ? WifiTransport.MaxFrames : _connection.DisplaySize <= 16 ? 48 : 1;

    /// <summary>
    /// Keeps an animation on the display until <see cref="ClearAttention"/>: it beats profiles, slideshows and the rest.
    /// The same <paramref name="key"/> again changes nothing.
    /// </summary>
    public void SetAttention(string key, DisplayArt art)
    {
        if (key == _attentionKey) return;
        _attentionKey = key;
        _attention = art;
        _attentionChanged = true;
        StateChanged?.Invoke();
    }

    public void ClearAttention()
    {
        if (_attention is null) return;
        _attention = null;
        _attentionKey = null;
        _restorePending = true;
        StateChanged?.Invoke();
    }

    public bool HasAttention => _attention is not null;

    public Task ShowAlertAsync(DisplayAlert alert) =>
        ShowAlertArtAsync(Screens.Alert(alert, _connection.DisplaySize, MaxAnimationFrames));

    /// <summary>Shows an animation for the alert duration (longer when the animation itself is longer), then what was there.</summary>
    public async Task ShowAlertArtAsync(DisplayArt art)
    {
        if (!_connection.IsConnected || _screenOff) return;

        _alertShowing = true;
        _alertUntil = DateTime.UtcNow.AddSeconds(Math.Clamp(_settings.Settings.AlertSeconds, 2, 60))
            .AddMilliseconds(art.DelaysMs.Sum() > 1000 ? art.DelaysMs.Sum() : 0);
        await SendAsync(art);
    }

    /// <summary>Turns the LEDs off (PC locked) or back on; content resumes when they come back.</summary>
    public async Task SetScreenAsync(bool on)
    {
        if (!_connection.IsConnected) return;
        _screenOff = !on;
        await RunSafelyAsync(t => t.SetScreenAsync(on));
        if (on)
            _restorePending = true;
    }

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (await timer.WaitForNextTickAsync())
        {
            if (!_connection.IsConnected || _screenOff) continue;
            var now = DateTime.UtcNow;

            if (_alertShowing)
            {
                if (now < _alertUntil) continue;
                _alertShowing = false;
                _restorePending = true;
            }

            bool restore = _restorePending || _profileChanged;
            _restorePending = false;
            _profileChanged = false;

            if (_attention is { } attention)
            {
                if (restore || _attentionChanged)
                {
                    _attentionChanged = false;
                    await SendAsync(attention);
                }
                continue;
            }

            if (_profileScreenOff && (_profile is null || _profile.Screen != ProfileScreen.ScreenOff))
            {
                _profileScreenOff = false;
                await RunSafelyAsync(t => t.SetScreenAsync(true));
            }
            if (_profile is { } profile)
            {
                await ShowProfileAsync(profile, restore, now);
                continue;
            }

            switch (Mode)
            {
                case PlayerMode.Image when restore && _image is not null:
                    await SendAsync(_image);
                    break;
                case PlayerMode.Slideshow when restore || now >= _nextSlideAt:
                    await NextSlideAsync(advance: !restore);
                    break;
                case PlayerMode.Monitor when restore || now >= _nextMonitorAt:
                    await ShowMonitorAsync();
                    break;
                case PlayerMode.NowPlaying or PlayerMode.Timer:
                    await ShowScreenAsync(restore, Mode);
                    break;
                case PlayerMode.Device when restore:
                    await RunSafelyAsync(t => t.ShowClockAsync());
                    break;
            }
        }
    }

    private async Task ShowProfileAsync(AppProfile profile, bool force, DateTime now)
    {
        switch (profile.Screen)
        {
            case ProfileScreen.PcMonitor when force || now >= _nextMonitorAt:
                await ShowMonitorAsync();
                break;
            case ProfileScreen.NowPlaying:
                await ShowScreenAsync(force, PlayerMode.NowPlaying);
                break;
            case ProfileScreen.Timer:
                await ShowScreenAsync(force, PlayerMode.Timer);
                break;
            case ProfileScreen.Folder when force || now >= _profileSlideAt:
                await NextProfileSlideAsync(profile, advance: !force);
                break;
            case ProfileScreen.AppIcon or ProfileScreen.Image when force:
                if (await LoadProfileArtAsync(profile) is { } art) await SendAsync(art);
                break;
            case ProfileScreen.Clock when force:
                await RunSafelyAsync(t => t.ShowClockAsync());
                break;
            case ProfileScreen.ScreenOff when force:
                _profileScreenOff = true;
                await RunSafelyAsync(t => t.SetScreenAsync(false));
                break;
        }
    }

    /// <summary>A profile's folder plays as its own slideshow, independent of the gallery slideshow.</summary>
    private async Task NextProfileSlideAsync(AppProfile profile, bool advance)
    {
        _profileSlideAt = DateTime.UtcNow.AddSeconds(Math.Clamp(_settings.Settings.SlideshowSeconds, 3, 3600));
        var items = string.IsNullOrEmpty(profile.FolderPath) ? [] : LibraryService.Instance.ItemsIn(profile.FolderPath)
            .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (items.Count == 0) return;
        if (advance) _profileSlide++;

        for (int tries = 0; tries < items.Count; tries++)
        {
            var item = items[(_profileSlide + tries) % items.Count];
            try
            {
                var art = await LoadAsync(item.Path);
                _profileSlide = (_profileSlide + tries) % items.Count;
                await SendAsync(art);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // Moved or unreadable; the next image is tried.
            }
        }
    }

    private async Task<DisplayArt?> LoadProfileArtAsync(AppProfile profile)
    {
        int size = _connection.DisplaySize;
        try
        {
            if (profile.Screen == ProfileScreen.Image)
                return string.IsNullOrEmpty(profile.ImagePath) ? null : await LoadAsync(profile.ImagePath);

            var icon = await AppIconService.IconPngAsync(profile.ExePath);
            return icon is null ? null : await PixelImageService.LoadForDisplayAsync(icon, size, ScalingMode.Smooth, FitMode.Fit);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AppLog.Write($"Profile picture for {profile.Name} could not be loaded", ex);
            return null;
        }
    }

    private async Task NextSlideAsync(bool advance)
    {
        _nextSlideAt = DateTime.UtcNow.AddSeconds(Math.Clamp(_settings.Settings.SlideshowSeconds, 3, 3600));
        if (_slides.Count == 0) return;
        if (advance && _image is not null)
            _slideIndex = (_slideIndex + 1) % _slides.Count;

        // Skip files that were moved or deleted since the slideshow started.
        for (int tries = 0; tries < _slides.Count; tries++)
        {
            var path = _slides[_slideIndex];
            try
            {
                _image = await LoadAsync(path);
                _imagePath = path;
                StateChanged?.Invoke();
                await SendAsync(_image);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _slideIndex = (_slideIndex + 1) % _slides.Count;
            }
        }
    }

    private async Task ShowMonitorAsync()
    {
        _nextMonitorAt = DateTime.UtcNow.AddSeconds(Math.Clamp(_settings.Settings.MonitorIntervalSeconds, 1, 60));
        if (SystemMonitorService.Instance.Latest is not { } sample) return;
        int size = _connection.DisplaySize;
        await SendAsync(new DisplayArt(size, [Screens.Monitor(sample, size)], [1000]));
    }

    private async Task ShowScreenAsync(bool force, PlayerMode screen)
    {
        if (!force && DateTime.UtcNow - _lastScreenAt < MinScreenGap) return;
        int size = _connection.DisplaySize;
        var (key, pixels) = screen == PlayerMode.Timer ? RenderTimer(size) : await RenderNowPlayingAsync(size);
        if (!force && key == _lastScreenKey) return;

        _lastScreenKey = key;
        _lastScreenAt = DateTime.UtcNow;
        await SendAsync(new DisplayArt(size, [pixels], [1000]));
    }

    /// <summary>Renders the timer; the key changes with every displayed second and bar step.</summary>
    public static (string Key, byte[] Pixels) RenderTimer(int size)
    {
        var timer = TimerService.Instance;
        var pixels = Screens.Timer(timer.Remaining, timer.Progress, timer.Phase, timer.IsPaused, size);
        int bar = (int)((1 - timer.Progress) * size);
        return ($"{(int)Math.Ceiling(timer.Remaining.TotalSeconds)}|{bar}|{timer.Phase}|{timer.IsPaused}", pixels);
    }

    /// <summary>Renders the playing track; the key changes with the track, play state and each bar step.</summary>
    public async Task<(string Key, byte[] Pixels)> RenderNowPlayingAsync(int size)
    {
        var info = NowPlayingService.Instance.Current;
        string coverKey = $"{info?.TrackKey}|{info?.Cover?.Length}|{size}";
        if (coverKey != _coverKey)
        {
            _coverKey = coverKey;
            _coverRgb = null;
            if (info?.Cover is { } cover)
            {
                try
                {
                    _coverRgb = (await PixelImageService.LoadForDisplayAsync(cover, size, ScalingMode.Smooth, FitMode.Fill)).Frames[0];
                }
                catch (InvalidDataException)
                {
                    // Some players hand over covers in formats Windows cannot decode; the note icon is shown instead.
                }
            }
        }
        int bar = info is null ? -1 : (int)(info.Progress * size);
        return ($"{coverKey}|{bar}|{info?.IsPlaying}", Screens.NowPlaying(info, _coverRgb, size));
    }

    private async Task<bool> SendAsync(DisplayArt art)
    {
        bool complete = true;
        await RunSafelyAsync(async t => complete = await t.ShowArtAsync(art));
        return complete;
    }

    private async Task RunSafelyAsync(Func<IDisplayTransport, Task> command)
    {
        await _showLock.WaitAsync();
        try
        {
            await _connection.RunAsync(command);
        }
        catch (InvalidOperationException ex)
        {
            Error?.Invoke(ex.Message);
        }
        finally
        {
            _showLock.Release();
        }
    }
}
