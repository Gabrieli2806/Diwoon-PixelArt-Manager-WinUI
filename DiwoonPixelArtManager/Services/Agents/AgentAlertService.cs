namespace DiwoonPixelArtManager.Services;

/// <summary>
/// Puts AI agents on the display: while one waits for an answer its pulsing bubble stays until you answer;
/// a finished turn or an error plays once, like any alert.
/// </summary>
public sealed class AgentAlertService
{
    public static AgentAlertService Instance { get; } = new();

    private readonly AgentWatcher _watcher = AgentWatcher.Instance;
    private readonly DisplayPlayer _player = DisplayPlayer.Instance;
    private readonly SettingsService _settings = SettingsService.Instance;

    // Claude Code marks a session as waiting for a moment while it checks a permission on its own;
    // only a wait that lasts is shown, so the display does not flicker.
    private static readonly TimeSpan WaitingDelay = TimeSpan.FromSeconds(8);
    private readonly Dictionary<string, DateTime> _waitingSince = [];

    public void Start()
    {
        _watcher.StatusChanged += OnStatusChanged;
        _watcher.Updated += UpdateAttention;
        _watcher.Start();
    }

    private bool Follows(AgentKind agent) =>
        _settings.Settings.AgentAlertsEnabled && !_settings.Settings.AgentsMuted.Contains(agent);

    private async void OnStatusChanged(AgentSnapshot session)
    {
        var s = _settings.Settings;
        if (!Follows(session.Agent) || !DivoomConnection.Instance.IsConnected) return;
        bool show = session.Status switch
        {
            AgentStatus.Done => s.AgentDoneEnabled,
            AgentStatus.Error => s.AgentErrorEnabled,
            _ => false
        };
        if (show) await ShowAsync(session.Agent, session.Status, session.Project);
    }

    /// <summary>Keeps the "needs you" animation on while any followed agent waits, and removes it once none does.</summary>
    private void UpdateAttention()
    {
        if (!_settings.Settings.AgentWaitingEnabled || !DivoomConnection.Instance.IsConnected)
        {
            _player.ClearAttention();
            return;
        }
        var now = DateTime.UtcNow;
        var waitingNow = _watcher.Snapshot.Where(a => a.Status == AgentStatus.Waiting && Follows(a.Agent)).ToList();
        foreach (var gone in _waitingSince.Keys.Where(id => waitingNow.All(a => a.Id != id)).ToList())
            _waitingSince.Remove(gone);
        foreach (var session in waitingNow)
            _waitingSince.TryAdd(session.Id, now);

        var waiting = waitingNow.FirstOrDefault(a => now - _waitingSince[a.Id] >= WaitingDelay);
        if (waiting is null)
        {
            _player.ClearAttention();
            return;
        }
        int size = DivoomConnection.Instance.DisplaySize;
        var text = AgentScreens.Message(waiting.Agent, AgentStatus.Waiting, waiting.Project);
        _player.SetAttention($"{waiting.Id}|{size}", AgentScreens.Render(waiting.Agent, AgentStatus.Waiting, text, size, _player.MaxAnimationFrames));
    }

    /// <summary>Plays an agent animation once; also used by the Test buttons.</summary>
    public Task ShowAsync(AgentKind agent, AgentStatus status, string project)
    {
        int size = DivoomConnection.Instance.DisplaySize;
        var art = AgentScreens.Render(agent, status, AgentScreens.Message(agent, status, project), size, _player.MaxAnimationFrames);
        return _player.ShowAlertArtAsync(art);
    }
}
