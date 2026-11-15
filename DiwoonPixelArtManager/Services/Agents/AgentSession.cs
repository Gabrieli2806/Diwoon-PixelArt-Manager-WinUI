using System.Text.RegularExpressions;

namespace DiwoonPixelArtManager.Services;

public enum AgentKind
{
    Claude,
    Codex,
    Antigravity
}

public enum AgentStatus
{
    Idle,
    Working,
    Waiting,
    Done,
    Error
}

/// <summary>A tool call without a result yet; a long-pending one means the agent waits for a permission answer.</summary>
public sealed record PendingCall(string Kind, DateTime At);

/// <summary>
/// What one agent conversation is doing, built up from its transcript lines. Logic ported from the author's
/// AI Live Preview daemon (server/sources), which reads the same files.
/// </summary>
public sealed partial class AgentSession(string id, AgentKind agent, string file)
{
    public string Id { get; } = id;
    public AgentKind Agent { get; } = agent;
    public string File { get; } = file;
    public long Offset { get; set; } = -1;
    public string Partial { get; set; } = "";

    public string Cwd { get; set; } = "";
    public string Prompt { get; set; } = "";
    public AgentStatus Phase { get; private set; } = AgentStatus.Idle;
    public DateTime PhaseAt { get; private set; }
    public DateTime LastEventAt { get; private set; }
    public Dictionary<string, PendingCall> Pending { get; } = [];

    /// <summary>Folder name of the project, for messages ("Diwoon-PixelArt-Manager").</summary>
    public string Project => Cwd.Length > 0 ? Path.GetFileName(Cwd.TrimEnd('\\', '/')) : "";

    public void Touch(DateTime at)
    {
        if (at > LastEventAt) LastEventAt = at;
    }

    public void SetPhase(AgentStatus phase, DateTime at)
    {
        Touch(at);
        if (Phase == phase) return;
        Phase = phase;
        PhaseAt = at == default ? LastEventAt : at;
    }

    /// <summary>Removes injected tag blocks (system reminders and the like) and keeps what the person typed.</summary>
    public static string CleanPrompt(string? raw)
    {
        var text = raw ?? "";
        var command = CommandName().Match(text);
        if (command.Success) return command.Groups[1].Value.Trim();
        var stripped = Tags().Replace(TagBlocks().Replace(text, " "), " ").Trim();
        if (stripped.StartsWith('{') || stripped.StartsWith('[')) return "";
        stripped = Whitespace().Replace(stripped, " ");
        return stripped.Length > 140 ? stripped[..139] + "…" : stripped;
    }

    /// <summary>Normalized kind of a tool call: ask and plan wait for the person; read/edit/search/todo finish at once.</summary>
    public static string ToolKind(string? name)
    {
        var lower = (name ?? "").ToLowerInvariant();
        if (lower.StartsWith("mcp__")) return "tool";
        if (lower is "askuserquestion" or "ask_user" or "request_user_input" or "notify_user") return "ask";
        if (lower is "exitplanmode" or "enterplanmode") return "plan";
        if (lower is "todowrite" or "taskcreate" or "taskupdate" or "tasklist" or "manage_task" or "update_plan" or "task_boundary") return "todo";
        if (lower.Contains("bash") || lower.Contains("powershell") || lower.Contains("shell") || lower.Contains("command") || lower.Contains("exec")) return "run";
        if (lower.Contains("write") || lower.Contains("edit") || lower.Contains("replace") || lower.Contains("patch") || lower.Contains("create_file")) return "edit";
        if (lower.Contains("grep") || lower.Contains("glob") || lower.Contains("search") || lower.Contains("find") || lower.Contains("list_dir") || lower == "ls") return "search";
        if (lower.Contains("read") || lower.Contains("view") || lower.Contains("open")) return "read";
        return "tool";
    }

    [GeneratedRegex(@"<command-name>([^<]+)</command-name>")]
    private static partial Regex CommandName();

    [GeneratedRegex(@"<([a-z][\w-]*)\b[^>]*>[\s\S]*?</\1>", RegexOptions.IgnoreCase)]
    private static partial Regex TagBlocks();

    [GeneratedRegex(@"</?[a-z][\w-]*\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
