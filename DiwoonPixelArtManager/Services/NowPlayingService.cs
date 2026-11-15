using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DiwoonPixelArtManager.Services;

/// <summary>What Windows reports as playing (Spotify, browsers, media players…), with its cover art.</summary>
public sealed record NowPlayingInfo(
    string Title,
    string Artist,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    byte[]? Cover)
{
    public string TrackKey => $"{Artist}\u0001{Title}";
    public double Progress => Duration.TotalSeconds > 0 ? Math.Clamp(Position / Duration, 0, 1) : 0;
}

/// <summary>Follows the system media session (the one shown in the Windows volume flyout).</summary>
public sealed class NowPlayingService
{
    public static NowPlayingService Instance { get; } = new();

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string? _title, _artist;
    private byte[]? _cover;
    private bool _isPlaying;
    private TimeSpan _position, _duration;
    private DateTimeOffset _positionAt;

    /// <summary>Raised on a background thread when the track changes.</summary>
    public event Action<NowPlayingInfo>? TrackChanged;

    public async Task StartAsync()
    {
        if (_manager is not null) return;
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += (_, _) => Attach(_manager.GetCurrentSession());
        Attach(_manager.GetCurrentSession());
    }

    /// <summary>The current track with its position estimated for now; null when nothing is playing or paused.</summary>
    public NowPlayingInfo? Current
    {
        get
        {
            if (_session is null || string.IsNullOrEmpty(_title)) return null;
            var position = _position;
            if (_isPlaying && _positionAt != default)
                position += DateTimeOffset.Now - _positionAt;
            if (_duration > TimeSpan.Zero && position > _duration) position = _duration;
            return new NowPlayingInfo(_title, _artist ?? "", _isPlaying, position, _duration, _cover);
        }
    }

    private void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
            _session.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= Session_TimelinePropertiesChanged;
        }
        _session = session;
        _title = _artist = null;
        _cover = null;
        if (session is null) return;

        session.MediaPropertiesChanged += Session_MediaPropertiesChanged;
        session.PlaybackInfoChanged += Session_PlaybackInfoChanged;
        session.TimelinePropertiesChanged += Session_TimelinePropertiesChanged;
        ReadPlayback(session);
        ReadTimeline(session);
        _ = ReadMediaAsync(session);
    }

    private void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) =>
        _ = ReadMediaAsync(sender);

    private void Session_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) =>
        ReadPlayback(sender);

    private void Session_TimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) =>
        ReadTimeline(sender);

    private void ReadPlayback(GlobalSystemMediaTransportControlsSession session)
    {
        bool wasPlaying = _isPlaying;
        _isPlaying = session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        // Freeze or restart the position estimate at the moment the state changes.
        if (wasPlaying != _isPlaying)
        {
            if (wasPlaying && _positionAt != default) _position += DateTimeOffset.Now - _positionAt;
            _positionAt = DateTimeOffset.Now;
        }
    }

    private void ReadTimeline(GlobalSystemMediaTransportControlsSession session)
    {
        var timeline = session.GetTimelineProperties();
        _duration = timeline.EndTime - timeline.StartTime;
        _position = timeline.Position - timeline.StartTime;
        _positionAt = timeline.LastUpdatedTime == default ? DateTimeOffset.Now : timeline.LastUpdatedTime;
    }

    private async Task ReadMediaAsync(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var media = await session.TryGetMediaPropertiesAsync();
            if (session != _session || media is null) return;

            bool changed = media.Title != _title || media.Artist != _artist;
            _title = media.Title;
            _artist = media.Artist;
            _cover = media.Thumbnail is null ? null : await ReadAllAsync(media.Thumbnail);
            ReadTimeline(session);
            if (changed && Current is { } info)
                TrackChanged?.Invoke(info);
        }
        catch (Exception ex)
        {
            // Players sometimes drop the session while it is being read; the next change event reads it again.
            AppLog.Write("Reading the playing track failed", ex);
        }
    }

    private static async Task<byte[]> ReadAllAsync(IRandomAccessStreamReference reference)
    {
        using var stream = await reference.OpenReadAsync();
        var buffer = new Windows.Storage.Streams.Buffer((uint)stream.Size);
        await stream.ReadAsync(buffer, buffer.Capacity, InputStreamOptions.None);
        var bytes = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer))
            reader.ReadBytes(bytes);
        return bytes;
    }
}
