using System.Text.Json;

namespace JarvisPanel.Core;

public enum Activity { Idle, Awaiting, Thinking }

public enum SessionStatus { Running, Waiting, Done, Failed, Idle }

public enum TaskStatus { Open, Judging, Unsatisfied }

public enum PendingKind { Approval, Question }

public enum ConnectionKind { Connecting, Online, Offline }

public static class EnumText
{
    public static string Label(this TaskStatus s) => s switch
    {
        TaskStatus.Open => "In progress",
        TaskStatus.Judging => "Judging",
        _ => "Unsatisfied",
    };

    /// <summary>Target-picker status text; the enum name alone would read as jargon.</summary>
    public static string Label(this SessionStatus s) => s switch
    {
        SessionStatus.Running => "Working",
        SessionStatus.Waiting => "Needs you",
        SessionStatus.Done => "Finished",
        SessionStatus.Failed => "Failed",
        _ => "Idle",
    };

    public static string Label(this AutoApprovalTier t) => t switch
    {
        AutoApprovalTier.Safe => "Safe",
        AutoApprovalTier.Grey => "Grey",
        _ => "Medium risk",
    };
}

public enum AutoApprovalTier { Safe, Grey, Medium }

/// <summary>Whose conversation the bar is addressed to (SPEC §12: no auto-routing).</summary>
public readonly record struct Target(bool IsJarvis, string? SessionId)
{
    public static Target Jarvis => new(true, null);
    public static Target Session(string id) => new(false, id);

    public string StorageKey => IsJarvis ? "jarvis" : SessionId ?? "jarvis";
}

public sealed class TargetOption
{
    public string Id => Target.StorageKey;
    public Target Target { get; init; }
    public string Label { get; init; } = "";
    public SessionStatus? Status { get; init; }
    public TaskInfo? Task { get; init; }
}

public sealed class Counts
{
    public int Running { get; set; }
    public int Pending { get; set; }
    public int Unread { get; set; }
    public int Failed { get; set; }

    public static Counts Parse(JsonElement? e)
    {
        var c = new Counts();
        if (e is not { } v || v.ValueKind != JsonValueKind.Object) return c;
        c.Running = J.Int(v, "running");
        c.Pending = J.Int(v, "pending");
        c.Unread = J.Int(v, "unread");
        c.Failed = J.Int(v, "failed");
        return c;
    }
}

public sealed class TaskInfo
{
    public TaskStatus Status { get; set; }
    public string? Summary { get; set; }

    public static TaskInfo? Parse(JsonElement? e)
    {
        if (e is not { } v || v.ValueKind != JsonValueKind.Object) return null;
        var raw = J.Str(v, "status");
        var status = raw switch
        {
            "open" => TaskStatus.Open,
            "judging" => TaskStatus.Judging,
            "unsatisfied" => TaskStatus.Unsatisfied,
            _ => TaskStatus.Open,
        };
        return new TaskInfo { Status = status, Summary = J.Str(v, "summary") };
    }
}

public sealed class SessionInfo
{
    /// <summary>
    /// The names the host gives worker sessions it creates for Jarvis itself. Kept
    /// here rather than in <see cref="PanelModel"/> so the prefix that
    /// <see cref="ShortName"/> strips and the label the picker shows can never drift
    /// apart. The Chinese spellings are still stripped: the host plugin writes them
    /// for sessions created by the macOS build, and the same profile can be opened
    /// by either.
    /// </summary>
    internal static readonly string[] JarvisPrefixes = { "Jarvis-", "[Jarvis]", "贾维斯-", "[贾维斯]" };

    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public SessionStatus Status { get; set; } = SessionStatus.Idle;
    public bool Unread { get; set; }
    public string? Workspace { get; set; }
    public bool Managed { get; set; } = true;
    public TaskInfo? Task { get; set; }

    /// <summary>Display name without the "Jarvis-" / "[Jarvis]" worker prefix (SPEC §9.2).</summary>
    public string ShortName
    {
        get
        {
            var t = Title.Trim();
            foreach (var prefix in JarvisPrefixes)
            {
                if (t.StartsWith(prefix, StringComparison.Ordinal))
                    t = t[prefix.Length..].Trim();
            }
            return t.Length == 0 ? Tail(Id, 8) : t;
        }
    }

    public static string Tail(string s, int n) => s.Length <= n ? s : s[^n..];

    public static SessionInfo? Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var id = J.Str(e, "id");
        if (string.IsNullOrEmpty(id)) return null;
        var status = J.Str(e, "status") switch
        {
            "running" => SessionStatus.Running,
            "waiting" => SessionStatus.Waiting,
            "done" => SessionStatus.Done,
            "failed" => SessionStatus.Failed,
            _ => SessionStatus.Idle,
        };
        var ws = J.Str(e, "workspace")?.Trim();
        return new SessionInfo
        {
            Id = id,
            Title = J.Str(e, "title") ?? "",
            Status = status,
            Unread = J.Bool(e, "unread"),
            Workspace = string.IsNullOrEmpty(ws) ? null : ws,
            Managed = J.Bool(e, "managed", true),
            Task = TaskInfo.Parse(J.Obj(e, "task")),
        };
    }
}

public sealed class PendingItem
{
    public string Id { get; set; } = "";
    public PendingKind Kind { get; set; } = PendingKind.Question;
    public string Session { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Detail { get; set; }
    public string? Note { get; set; }
    public List<string> Choices { get; set; } = new();
    public bool AllowsWorkspaceRule { get; set; }

    public bool CanAlwaysAllow => Kind == PendingKind.Approval && AllowsWorkspaceRule;

    public static PendingItem? Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var id = J.Str(e, "id");
        if (string.IsNullOrEmpty(id)) return null;
        var choices = new List<string>();
        if (J.Arr(e, "choices") is { } arr)
        {
            foreach (var c in arr.EnumerateArray())
                if (c.ValueKind == JsonValueKind.String && c.GetString() is { } s) choices.Add(s);
        }
        return new PendingItem
        {
            Id = id,
            Kind = J.Str(e, "kind") == "approval" ? PendingKind.Approval : PendingKind.Question,
            Session = J.Str(e, "session") ?? "",
            Title = J.Str(e, "title") ?? "",
            Detail = J.Str(e, "detail"),
            Note = J.Str(e, "note"),
            Choices = choices,
            AllowsWorkspaceRule = J.Bool(e, "canAlwaysAllow"),
        };
    }
}

public sealed class Snapshot
{
    public string AgentId { get; set; } = "";
    public Activity Activity { get; set; } = Activity.Idle;
    public string? Error { get; set; }
    public Counts Counts { get; set; } = new();
    public List<SessionInfo> Sessions { get; set; } = new();
    public List<PendingItem> Pending { get; set; } = new();
    public List<AutoApproval> AutoApprovals { get; set; } = new();

    public static Snapshot Parse(JsonElement e)
    {
        var s = new Snapshot();
        if (e.ValueKind != JsonValueKind.Object) return s;
        s.AgentId = J.Str(e, "agentId") ?? "";
        s.Error = J.Str(e, "error");
        s.Counts = Counts.Parse(J.Obj(e, "counts"));
        if (J.Arr(e, "pending") is { } pend)
        {
            foreach (var p in pend.EnumerateArray())
                if (PendingItem.Parse(p) is { } item) s.Pending.Add(item);
        }
        if (J.Arr(e, "autoApprovals") is { } auto)
        {
            foreach (var a in auto.EnumerateArray())
                if (AutoApproval.Parse(a) is { } entry) s.AutoApprovals.Add(entry);
        }
        s.Activity = J.Str(e, "activity") switch
        {
            "awaiting" => Activity.Awaiting,
            "thinking" => Activity.Thinking,
            "idle" => Activity.Idle,
            _ => Activity.Idle,
        };
        if (J.Arr(e, "sessions") is { } sessions)
        {
            foreach (var item in sessions.EnumerateArray())
                if (SessionInfo.Parse(item) is { } session) s.Sessions.Add(session);
        }
        else if (J.Arr(e, "managed") is { } managed)
        {
            foreach (var item in managed.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } id)
                    s.Sessions.Add(new SessionInfo { Id = id, Title = "", Status = SessionStatus.Idle, Unread = false });
        }
        return s;
    }
}

public sealed class ChatMessage
{
    public string Id { get; set; } = "";
    public string Role { get; set; } = "";
    public string Text { get; set; } = "";
    public string? RoutedTo { get; set; }

    public bool IsMine => Role == "user";

    public const int PreviewCharacters = 280;
    public const int PreviewLines = 8;

    public string Preview => PreviewText(Text);

    public static string PreviewText(string text)
    {
        var lines = text.Split('\n');
        var cut = lines.Length > PreviewLines;
        if (cut) lines = lines[..PreviewLines];
        var s = string.Join("\n", lines);
        if (s.Length > PreviewCharacters)
        {
            s = s[..PreviewCharacters];
            cut = true;
        }
        if (!cut) return text;
        return s.Trim() + "…";
    }

    /// <summary>GET /jarvis/messages. Tool results and empty messages are dropped (spec §6.3).</summary>
    public static List<ChatMessage> DecodeAll(JsonElement root)
    {
        var list = new List<ChatMessage>();
        if (root.ValueKind != JsonValueKind.Object) return list;
        if (J.Arr(root, "messages") is not { } arr) return list;
        var index = 0;
        foreach (var m in arr.EnumerateArray())
        {
            var i = index++;
            if (m.ValueKind != JsonValueKind.Object) continue;
            var role = J.Str(m, "role") ?? "";
            var text = (J.Str(m, "text") ?? "").Trim();
            if (role == "tool" || text.Length == 0) continue;
            list.Add(new ChatMessage
            {
                Id = J.Str(m, "id") ?? $"m{i}",
                Role = role,
                Text = text,
                RoutedTo = J.Str(m, "routedTo"),
            });
        }
        return list;
    }
}

public sealed class ApprovalRule
{
    public sealed record Identity(string Fingerprint, string Tool, string Workspace);

    public string Fingerprint { get; set; } = "";
    public string Tool { get; set; } = "";
    public string Workspace { get; set; } = "";
    public double CreatedAt { get; set; }
    public double? ExpiresAt { get; set; }

    public Identity IdentityValue => new(Fingerprint, Tool, Workspace);

    public static ApprovalRule? Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var fp = J.Str(e, "fingerprint");
        var tool = J.Str(e, "tool");
        var ws = J.Str(e, "workspace");
        if (string.IsNullOrEmpty(fp) || string.IsNullOrEmpty(tool) || string.IsNullOrEmpty(ws)) return null;
        return new ApprovalRule
        {
            Fingerprint = fp,
            Tool = tool,
            Workspace = ws,
            CreatedAt = J.Dbl(e, "createdAt"),
            ExpiresAt = e.TryGetProperty("expiresAt", out var ex) && ex.ValueKind == JsonValueKind.Number
                ? ex.GetDouble() : null,
        };
    }
}

public sealed class AutoApproval
{
    public string Id { get; set; } = "";
    public string Session { get; set; } = "";
    public string Title { get; set; } = "";
    public string Tool { get; set; } = "";
    public string Command { get; set; } = "";
    public AutoApprovalTier Tier { get; set; }
    public double At { get; set; }
    public ApprovalRule.Identity? Rule { get; set; }

    public static AutoApproval? Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var id = J.Str(e, "id");
        if (string.IsNullOrEmpty(id)) return null;
        var at = J.Dbl(e, "at", -1);
        if (!double.IsFinite(at) || at < 0) return null;
        var tier = J.Str(e, "tier") switch
        {
            "safe" => AutoApprovalTier.Safe,
            "grey" => AutoApprovalTier.Grey,
            "medium" => AutoApprovalTier.Medium,
            _ => AutoApprovalTier.Safe,
        };
        ApprovalRule.Identity? rule = null;
        if (J.Obj(e, "rule") is { } r)
        {
            var fp = J.Str(r, "fingerprint");
            var tool = J.Str(r, "tool");
            var ws = J.Str(r, "workspace");
            if (!string.IsNullOrWhiteSpace(fp) && !string.IsNullOrWhiteSpace(tool) && !string.IsNullOrWhiteSpace(ws))
                rule = new ApprovalRule.Identity(fp!, tool!, ws!);
        }
        return new AutoApproval
        {
            Id = id,
            Session = J.Str(e, "session") ?? "",
            Title = J.Str(e, "title") ?? "",
            Tool = J.Str(e, "tool") ?? "",
            Command = J.Str(e, "command") ?? "",
            Tier = tier,
            At = at,
            Rule = rule,
        };
    }
}
