using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DiwoonPixelArtManager.Services;

/// <summary>Where each agent keeps its transcripts and how to read them. Everything here only reads.</summary>
internal static partial class AgentSources
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static readonly string ClaudeProjects = Path.Combine(Home, ".claude", "projects");
    public static readonly string ClaudeSessions = Path.Combine(Home, ".claude", "sessions");
    public static readonly string CodexSessions = Path.Combine(Home, ".codex", "sessions");
    public static readonly string AntigravityBrain = Path.Combine(Home, ".gemini", "antigravity", "brain");

    /// <summary>Transcripts untouched for longer than this are not followed.</summary>
    public static readonly TimeSpan MaxSessionAge = TimeSpan.FromHours(24);

    /// <summary>A finished turn counts as "done" for this long, then as idle.</summary>
    public static readonly TimeSpan DoneFresh = TimeSpan.FromMinutes(15);

    /// <summary>An instant tool (read, edit…) still pending after this long is blocked on a permission prompt.</summary>
    private static readonly TimeSpan PermissionDelay = TimeSpan.FromSeconds(10);

    private static readonly HashSet<string> InstantKinds = ["read", "edit", "search", "todo"];

    public static long InitialBytes(AgentKind agent) => agent switch
    {
        AgentKind.Codex => 4 * 1024 * 1024,
        AgentKind.Claude => 2 * 1024 * 1024,
        _ => 1024 * 1024
    };

    // ---------------------------------------------------------------- Discovery

    public static IEnumerable<(string Id, AgentKind Agent, string File)> Discover(DateTime now)
    {
        // Main conversations sit directly in each project folder; the subfolders hold subagents and tool output,
        // which can be thousands of files and are not needed.
        foreach (var project in SafeDirectories(ClaudeProjects))
        {
            foreach (var file in SafeFiles(project, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                if (IsRecent(file, now))
                    yield return ($"claude:{Path.GetFileNameWithoutExtension(file)}", AgentKind.Claude, file);
            }
        }
        foreach (var file in SafeFiles(CodexSessions, "*.jsonl", SearchOption.AllDirectories))
        {
            if (!IsRecent(file, now)) continue;
            var uuid = CodexUuid().Match(file);
            yield return ($"codex:{(uuid.Success ? uuid.Groups[1].Value : Path.GetFileNameWithoutExtension(file))}", AgentKind.Codex, file);
        }
        foreach (var dir in SafeDirectories(AntigravityBrain))
        {
            var file = Path.Combine(dir, ".system_generated", "logs", "transcript.jsonl");
            if (File.Exists(file) && IsRecent(file, now))
                yield return ($"antigravity:{Path.GetFileName(dir)}", AgentKind.Antigravity, file);
        }
    }

    private static bool IsRecent(string file, DateTime now)
    {
        try { return now - File.GetLastWriteTimeUtc(file) < MaxSessionAge; }
        catch (IOException) { return false; }
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern, SearchOption option)
    {
        try { return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern, option).ToList() : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> SafeDirectories(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).ToList() : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// Claude Code keeps one file per open process in ~/.claude/sessions saying whether it is busy or idle;
    /// that is the authority on "working", the transcript says on what.
    /// </summary>
    public static Dictionary<string, JsonElement> ClaudeRegistry()
    {
        var open = new Dictionary<string, JsonElement>();
        foreach (var file in SafeFiles(ClaudeSessions, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                if (!root.TryGetProperty("sessionId", out var id) || !root.TryGetProperty("pid", out var pid)) continue;
                if (IsAlive(pid.GetInt32())) open[id.GetString() ?? ""] = root.Clone();
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or FormatException)
            {
                // Being rewritten, or not a registry entry.
            }
        }
        return open;
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return true; // Exists but belongs to another user.
        }
    }

    // ---------------------------------------------------------------- Parsing

    public static void Parse(AgentSession session, string line)
    {
        // Claude request mirrors repeat whole messages and can be very large.
        if (line.StartsWith("{\"type\":\"api-request")) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var entry = doc.RootElement;
            switch (session.Agent)
            {
                case AgentKind.Claude: ParseClaude(session, entry); break;
                case AgentKind.Codex: ParseCodex(session, entry); break;
                case AgentKind.Antigravity: ParseAntigravity(session, entry); break;
            }
        }
        catch (JsonException)
        {
            // Half-written or foreign line.
        }
    }

    private static DateTime At(JsonElement entry, string property) =>
        entry.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
        && DateTime.TryParse(value.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
            ? at : default;

    private static string? Str(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static void ParseClaude(AgentSession session, JsonElement entry)
    {
        if (entry.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True) return;
        var at = At(entry, "timestamp");
        if (Str(entry, "cwd") is { Length: > 0 } cwd) session.Cwd = cwd;

        switch (Str(entry, "type"))
        {
            case "user":
            {
                if (entry.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) return;
                if (!entry.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content)) return;
                if (content.ValueKind == JsonValueKind.Array && content.EnumerateArray().Any(b => Str(b, "type") == "tool_result"))
                {
                    foreach (var block in content.EnumerateArray())
                        if (Str(block, "type") == "tool_result" && Str(block, "tool_use_id") is { } done) session.Pending.Remove(done);
                    session.SetPhase(AgentStatus.Working, at);
                    return;
                }
                string text = content.ValueKind == JsonValueKind.String
                    ? content.GetString() ?? ""
                    : string.Join(' ', content.EnumerateArray().Where(b => Str(b, "type") == "text").Select(b => Str(b, "text")));
                if (text.TrimStart().StartsWith("[Request interrupted"))
                {
                    session.Pending.Clear();
                    session.SetPhase(AgentStatus.Done, at);
                    return;
                }
                var prompt = AgentSession.CleanPrompt(text);
                if (prompt.Length == 0) return;
                session.Prompt = prompt;
                session.Pending.Clear();
                session.SetPhase(AgentStatus.Working, at);
                break;
            }
            case "assistant":
            {
                if (!entry.TryGetProperty("message", out var message)) return;
                if (entry.TryGetProperty("isApiErrorMessage", out var apiError) && apiError.ValueKind == JsonValueKind.True)
                {
                    session.Pending.Clear();
                    session.SetPhase(AgentStatus.Error, at);
                    return;
                }
                if (message.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
                    foreach (var block in blocks.EnumerateArray())
                        if (Str(block, "type") == "tool_use" && Str(block, "id") is { } id)
                            session.Pending[id] = new PendingCall(AgentSession.ToolKind(Str(block, "name")), at == default ? session.LastEventAt : at);

                var stop = Str(message, "stop_reason");
                if (stop is not null && stop != "tool_use" && stop != "pause_turn")
                {
                    session.Pending.Clear();
                    session.SetPhase(AgentStatus.Done, at);
                }
                else session.SetPhase(AgentStatus.Working, at);
                break;
            }
            default:
                session.Touch(at);
                break;
        }
    }

    private static void ParseCodex(AgentSession session, JsonElement entry)
    {
        var at = At(entry, "timestamp");
        entry.TryGetProperty("payload", out var payload);
        string? payloadType = Str(payload, "type");

        switch (Str(entry, "type"))
        {
            case "session_meta":
            case "turn_context":
                if (Str(payload, "cwd") is { Length: > 0 } cwd) session.Cwd = cwd;
                break;
            case "event_msg":
                switch (payloadType)
                {
                    case "task_started":
                        session.SetPhase(AgentStatus.Working, at);
                        break;
                    case "task_complete":
                    case "turn_aborted":
                        session.SetPhase(AgentStatus.Done, at);
                        break;
                    case "item_completed":
                        if (payload.TryGetProperty("item", out var item) && Str(item, "type") == "UserMessage")
                            session.SetPhase(AgentStatus.Working, at);
                        else ResumeAfterAnswer(session, at);
                        break;
                    default:
                        if (payloadType is not null && (payloadType.Contains("approval_request") || payloadType.Contains("elicitation_request")))
                            session.SetPhase(AgentStatus.Waiting, at);
                        else ResumeAfterAnswer(session, at);
                        break;
                }
                break;
            default:
                ResumeAfterAnswer(session, at);
                break;
        }
    }

    /// <summary>Codex writes nothing explicit when an approval is answered; any later activity means it carries on.</summary>
    private static void ResumeAfterAnswer(AgentSession session, DateTime at)
    {
        if (session.Phase == AgentStatus.Waiting && at > session.PhaseAt) session.SetPhase(AgentStatus.Working, at);
        else session.Touch(at);
    }

    private static void ParseAntigravity(AgentSession session, JsonElement step)
    {
        var at = At(step, "created_at");
        switch (Str(step, "type"))
        {
            case "USER_INPUT":
                session.Prompt = AgentSession.CleanPrompt(Str(step, "content"));
                session.SetPhase(AgentStatus.Working, at);
                break;
            case "PLANNER_RESPONSE":
            {
                var calls = step.TryGetProperty("tool_calls", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(c => Str(c, "name")).ToList()
                    : [];
                if (calls.Count == 0) session.SetPhase(AgentStatus.Done, at);
                else if (calls.Any(name => AgentSession.ToolKind(name) == "ask")) session.SetPhase(AgentStatus.Waiting, at);
                else session.SetPhase(AgentStatus.Working, at);
                break;
            }
            default:
                if (session.Phase == AgentStatus.Working) session.Touch(at);
                break;
        }
    }

    // ---------------------------------------------------------------- Status

    /// <summary>What a session is doing now, combining the transcript with (for Claude) the live-process registry.</summary>
    public static AgentStatus Status(AgentSession session, Dictionary<string, JsonElement> claudeRegistry, DateTime now)
    {
        if (session.Agent == AgentKind.Claude)
        {
            var sessionId = session.Id["claude:".Length..];
            if (claudeRegistry.TryGetValue(sessionId, out var entry))
            {
                var state = Str(entry, "status");
                if (state == "idle")
                {
                    bool fresh = now - session.LastEventAt < DoneFresh;
                    return fresh ? (session.Phase == AgentStatus.Error ? AgentStatus.Error : AgentStatus.Done) : AgentStatus.Idle;
                }
                // Calls made together report back together: a quick read next to a long command is not stuck on a
                // permission prompt, it waits for the command. Only a lone instant call that stays pending is.
                bool longCallRunning = session.Pending.Values.Any(c => !InstantKinds.Contains(c.Kind) && c.Kind is not ("ask" or "plan"));
                if (session.Pending.Values.Any(c => c.Kind is "ask" or "plan"
                        || (!longCallRunning && InstantKinds.Contains(c.Kind) && now - c.At > PermissionDelay)))
                    return AgentStatus.Waiting;
                return state == "busy" ? AgentStatus.Working : AgentStatus.Waiting;
            }
            return FromRecency(session, now, TimeSpan.FromMinutes(2));
        }
        return FromRecency(session, now, session.Agent == AgentKind.Codex ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(5));
    }

    /// <summary>For agents without a live registry: trust the transcript, but stop believing "working" once it goes quiet.</summary>
    private static AgentStatus FromRecency(AgentSession session, DateTime now, TimeSpan workingStale) => session.Phase switch
    {
        AgentStatus.Working when now - session.LastEventAt < workingStale => AgentStatus.Working,
        AgentStatus.Waiting when now - session.PhaseAt < TimeSpan.FromHours(12) => AgentStatus.Waiting,
        AgentStatus.Done or AgentStatus.Error when now - session.PhaseAt < DoneFresh => session.Phase,
        _ => AgentStatus.Idle
    };

    [GeneratedRegex(@"([0-9a-f-]{36})\.jsonl$", RegexOptions.IgnoreCase)]
    private static partial Regex CodexUuid();
}
