using System.Text;

namespace DiwoonPixelArtManager.Services;

/// <summary>One conversation as shown in the app.</summary>
public sealed record AgentSnapshot(string Id, AgentKind Agent, string Project, string Prompt, AgentStatus Status);

/// <summary>
/// Follows the transcripts of Claude Code, Codex and Antigravity (Gemini), read-only, and reports when one starts
/// waiting for the person, fails, or finishes its turn. The first look is a baseline: what is already waiting is not news.
/// </summary>
public sealed class AgentWatcher
{
    public static AgentWatcher Instance { get; } = new();

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, AgentSession> _sessions = [];
    private readonly Dictionary<string, AgentStatus> _previous = [];
    private DateTime _discoveredAt = DateTime.MinValue;
    private bool _baselineDone, _started;

    /// <summary>Conversations active in the last day, refreshed every second.</summary>
    public IReadOnlyList<AgentSnapshot> Snapshot { get; private set; } = [];

    /// <summary>Raised on a background thread: a conversation started waiting, failed or finished.</summary>
    public event Action<AgentSnapshot>? StatusChanged;

    /// <summary>Raised on a background thread after every refresh.</summary>
    public event Action? Updated;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                Refresh();
            }
            catch (Exception ex)
            {
                AppLog.Write("Reading AI agent transcripts failed", ex);
            }
        }
        while (await timer.WaitForNextTickAsync());
    }

    private void Refresh()
    {
        var now = DateTime.UtcNow;
        if (now - _discoveredAt > DiscoveryInterval)
        {
            _discoveredAt = now;
            foreach (var (id, agent, file) in AgentSources.Discover(now))
                if (!_sessions.ContainsKey(id)) _sessions[id] = new AgentSession(id, agent, file);
        }

        foreach (var session in _sessions.Values) ReadNewLines(session);

        var registry = AgentSources.ClaudeRegistry();
        var snapshot = new List<AgentSnapshot>();
        foreach (var session in _sessions.Values)
        {
            var status = AgentSources.Status(session, registry, now);
            var item = new AgentSnapshot(session.Id, session.Agent, session.Project, session.Prompt, status);
            if (status != AgentStatus.Idle) snapshot.Add(item);

            bool known = _previous.TryGetValue(session.Id, out var before);
            _previous[session.Id] = status;
            if (status == AgentStatus.Waiting && before != AgentStatus.Waiting)
                AppLog.Write($"{session.Agent} {session.Project} waits: phase {session.Phase}, pending "
                    + string.Join(", ", session.Pending.Values.Select(p => $"{p.Kind} for {(now - p.At).TotalSeconds:0}s")));
            if (!_baselineDone || (known && before == status) || (!known && status != AgentStatus.Waiting)) continue;

            bool needsYou = status is AgentStatus.Waiting or AgentStatus.Error;
            bool finished = status == AgentStatus.Done && before is AgentStatus.Working or AgentStatus.Waiting;
            bool answered = before == AgentStatus.Waiting && status != AgentStatus.Waiting;
            if (needsYou || finished || answered) StatusChanged?.Invoke(item);
        }
        _baselineDone = true;
        Snapshot = snapshot.OrderBy(s => s.Status switch { AgentStatus.Waiting => 0, AgentStatus.Error => 1, AgentStatus.Working => 2, _ => 3 }).ToList();
        Updated?.Invoke();
    }

    /// <summary>Reads what was appended since the last look; the first look starts near the end of the file.</summary>
    private static void ReadNewLines(AgentSession session)
    {
        try
        {
            using var stream = new FileStream(session.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = stream.Length;
            bool first = session.Offset < 0;
            if (first) session.Offset = Math.Max(0, length - AgentSources.InitialBytes(session.Agent));
            if (length < session.Offset) session.Offset = 0; // Rewritten.
            if (length == session.Offset) return;

            stream.Seek(session.Offset, SeekOrigin.Begin);
            var buffer = new byte[length - session.Offset];
            int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            session.Offset += read;

            var text = session.Partial + Encoding.UTF8.GetString(buffer, 0, read);
            var lines = text.Split('\n');
            session.Partial = lines[^1];
            // A first read that starts mid-file begins with a partial line.
            int start = first && session.Offset - read > 0 ? 1 : 0;
            for (int i = start; i < lines.Length - 1; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (line.Length > 0) AgentSources.Parse(session, line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked for a moment or removed; tried again on the next round.
        }
    }
}
