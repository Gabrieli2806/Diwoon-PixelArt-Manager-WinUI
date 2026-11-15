namespace DiwoonPixelArtManager.Services;

public enum TimerPhase
{
    Timer,
    Focus,
    ShortBreak,
    LongBreak
}

/// <summary>Countdown timer, or Pomodoro (focus / short break, a long break after every fourth focus).</summary>
public sealed class TimerService
{
    public static TimerService Instance { get; } = new();

    private const int FocusesBeforeLongBreak = 4;

    private readonly SettingsService _settings = SettingsService.Instance;
    private DateTime _endsAt;
    private TimeSpan _pausedRemaining;
    private CancellationTokenSource? _watch;

    public bool IsActive { get; private set; }
    public bool IsPaused { get; private set; }
    public bool IsPomodoro { get; private set; }
    public TimerPhase Phase { get; private set; }
    public TimeSpan Total { get; private set; }
    public int FocusesDone { get; private set; }

    public TimeSpan Remaining
    {
        get
        {
            if (!IsActive) return TimeSpan.Zero;
            var left = IsPaused ? _pausedRemaining : _endsAt - DateTime.UtcNow;
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }
    }

    public double Progress => Total.TotalSeconds > 0 ? 1 - Remaining / Total : 0;

    /// <summary>Raised on any thread when the timer starts, pauses, changes phase or stops.</summary>
    public event Action? StateChanged;

    /// <summary>Raised on a background thread when a countdown or phase reaches zero.</summary>
    public event Action<TimerPhase>? Finished;

    public void StartTimer(TimeSpan duration)
    {
        IsPomodoro = false;
        FocusesDone = 0;
        Begin(TimerPhase.Timer, duration);
    }

    public void StartPomodoro()
    {
        IsPomodoro = true;
        FocusesDone = 0;
        Begin(TimerPhase.Focus, Minutes(_settings.Settings.FocusMinutes));
    }

    public void TogglePause()
    {
        if (!IsActive) return;
        if (IsPaused)
        {
            _endsAt = DateTime.UtcNow + _pausedRemaining;
            IsPaused = false;
            Watch();
        }
        else
        {
            _pausedRemaining = Remaining;
            IsPaused = true;
            _watch?.Cancel();
        }
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        _watch?.Cancel();
        IsActive = IsPaused = IsPomodoro = false;
        StateChanged?.Invoke();
    }

    /// <summary>Ends the current Pomodoro phase now and moves to the next one.</summary>
    public void Skip()
    {
        if (IsPomodoro && IsActive) Advance();
    }

    private static TimeSpan Minutes(int minutes) => TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 600));

    private void Begin(TimerPhase phase, TimeSpan duration)
    {
        Phase = phase;
        Total = duration;
        _endsAt = DateTime.UtcNow + duration;
        IsActive = true;
        IsPaused = false;
        Watch();
        StateChanged?.Invoke();
    }

    private void Watch()
    {
        _watch?.Cancel();
        var cancel = new CancellationTokenSource();
        _watch = cancel;
        var wait = _endsAt - DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            try
            {
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cancel.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            var finished = Phase;
            if (IsPomodoro) Advance();
            else Stop();
            Finished?.Invoke(finished);
        });
    }

    private void Advance()
    {
        var s = _settings.Settings;
        if (Phase == TimerPhase.Focus)
        {
            FocusesDone++;
            bool longBreak = FocusesDone % FocusesBeforeLongBreak == 0;
            Begin(longBreak ? TimerPhase.LongBreak : TimerPhase.ShortBreak, Minutes(longBreak ? s.LongBreakMinutes : s.BreakMinutes));
        }
        else
        {
            Begin(TimerPhase.Focus, Minutes(s.FocusMinutes));
        }
    }
}
