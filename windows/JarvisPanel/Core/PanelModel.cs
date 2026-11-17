namespace JarvisPanel.Core;

/// <summary>
/// Connection state: <see cref="ConnectionKind.Offline"/> carries the reason.
/// </summary>
public readonly record struct Connection(ConnectionKind Kind, string? Reason)
{
    public static Connection Connecting => new(ConnectionKind.Connecting, null);
    public static Connection Online => new(ConnectionKind.Online, null);
    public static Connection Offline(string reason) => new(ConnectionKind.Offline, reason);
    public bool IsOnline => Kind == ConnectionKind.Online;
}

/// <summary>
/// View model for the orb + quick-reply bar. Owns the host snapshot, the draft,
/// the explicit send target (SPEC §12: no auto-routing) and pending answers.
/// </summary>
/// <remarks>
/// All members are expected to be touched from the UI thread. The polling loop
/// starts there and never suppresses the synchronization context, so every
/// continuation that mutates state resumes on the UI thread.
/// </remarks>
public sealed class PanelModel
{
    public const int MaxVisiblePending = 3;
    public const int MaxVisibleAutoApprovals = 5;

    /// <summary>The display name for Jarvis itself (macOS label: 贾维斯).</summary>
    public const string JarvisLabel = "Jarvis";

    private readonly IJarvisApi _api;
    private readonly SettingsStore _store;
    private readonly EscapeGate _escapeGate = new();
    private double _escapeHintUntil = -1;
    private readonly HashSet<string> _revokedAutoApprovalIds = new();
    private int _hostVersion = -1;

    /// <summary>Raised after any observable state change.</summary>
    public event Action? Changed;

    private void Notify() => Changed?.Invoke();

    public PanelModel(IJarvisApi api, SettingsStore store)
    {
        _api = api;
        _store = store;
        HistoryExpanded = store.Settings.HistoryExpanded;
        Target = RestoredTarget();
    }

    /// <summary>Monotonic seconds, standing in for Swift's <c>systemUptime</c>.</summary>
    public static double Now => Environment.TickCount64 / 1000.0;

    // MARK: Observable state

    public Snapshot? Snapshot { get; private set; }
    public Connection Connection { get; private set; } = Connection.Connecting;
    public List<ChatMessage> Messages { get; private set; } = new();
    public string? SendError { get; private set; }
    public string? Toast { get; private set; }
    public string? AnsweringId { get; private set; }
    public string Draft { get; set; } = "";
    public Target Target { get; private set; } = Target.Jarvis;
    public bool QuickBarOpen { get; private set; }
    public bool EscapeHint { get; private set; }
    public bool HistoryExpanded { get; private set; }
    /// <summary>The pointer is over the orb: it shows standby, as when the quick bar is open.</summary>
    public bool Hovering { get; private set; }
    public string? ManagingId { get; private set; }
    public ApprovalRule.Identity? RevokingAutoApproval { get; private set; }
    public string? AutoApprovalError { get; private set; }

    // MARK: Derived

    public OrbAppearance Appearance
        => OrbStateResolver.Resolve(Snapshot, Connection.IsOnline, QuickBarOpen || Hovering);

    /// <summary>Jarvis plus the sessions handed to it.</summary>
    public List<TargetOption> Targets
    {
        get
        {
            var list = new List<TargetOption>
            {
                new() { Target = Target.Jarvis, Label = JarvisLabel, Status = null },
            };
            foreach (var s in Snapshot?.Sessions ?? new List<SessionInfo>())
                if (s.Managed) list.Add(Option(s));
            return list;
        }
    }

    /// <summary>Sessions that could be handed to Jarvis.</summary>
    public List<TargetOption> Candidates
    {
        get
        {
            var list = new List<TargetOption>();
            foreach (var s in Snapshot?.Sessions ?? new List<SessionInfo>())
                if (!s.Managed) list.Add(Option(s));
            return list;
        }
    }

    private TargetOption Option(SessionInfo s) => new()
    {
        Target = Target.Session(s.Id),
        Label = DisplayName(s),
        Status = s.Status,
        Task = s.Task,
    };

    /// <summary>
    /// A session named like Jarvis itself, or like another session, gets its
    /// workspace folder appended; the id tail breaks any tie that remains.
    /// </summary>
    public string DisplayName(SessionInfo session)
    {
        var name = session.ShortName;
        var others = (Snapshot?.Sessions ?? new List<SessionInfo>())
            .Where(s => s.Id != session.Id && s.ShortName == name).ToList();
        if (name != JarvisLabel && others.Count == 0) return name;
        if (session.Workspace is { } ws && !others.Any(o => o.Workspace == ws)) return $"{name} · {ws}";
        return $"{name} · {SessionInfo.Tail(session.Id, 6)}";
    }

    public List<PendingItem> VisiblePending
        => (Snapshot?.Pending ?? new List<PendingItem>()).Take(MaxVisiblePending).ToList();

    public List<AutoApproval> VisibleAutoApprovals
    {
        get
        {
            var seen = new HashSet<string>();
            var result = new List<AutoApproval>();
            foreach (var entry in (Snapshot?.AutoApprovals ?? new List<AutoApproval>())
                         .OrderByDescending(e => e.At))
            {
                if (!seen.Add(entry.Id)) continue;
                if (result.Count >= MaxVisibleAutoApprovals) break;
                var copy = entry;
                if (_revokedAutoApprovalIds.Contains(copy.Id)) copy.Rule = null;
                result.Add(copy);
            }
            return result;
        }
    }

    public int HiddenPendingCount => Math.Max(0, (Snapshot?.Pending.Count ?? 0) - MaxVisiblePending);

    public PendingItem? AnsweringItem
        => AnsweringId is { } id ? Snapshot?.Pending.FirstOrDefault(p => p.Id == id) : null;

    public string LabelFor(Target target)
    {
        if (target.IsJarvis) return JarvisLabel;
        var id = target.SessionId ?? "";
        var agent = Snapshot?.AgentId;
        if (!string.IsNullOrEmpty(agent) && id == agent) return JarvisLabel;
        var session = Snapshot?.Sessions.FirstOrDefault(s => s.Id == id);
        return session is null ? SessionInfo.Tail(id, 8) : DisplayName(session);
    }

    // MARK: Polling

    public async Task RefreshAsync()
    {
        try
        {
            Snapshot = await _api.SnapshotAsync();
            Connection = Connection.Online;
        }
        catch (Exception ex)
        {
            Connection = Connection.Offline(Describe(ex));
            Notify();
            return;
        }
        if (QuickBarOpen && HistoryExpanded) await LoadMessagesAsync();
        Notify();
    }

    /// <summary>
    /// Returns as soon as the host reports a change (turns, pending requests,
    /// the managed set) or after <paramref name="timeout"/>. Against a host
    /// without the change feed, or while it is unreachable, this is a plain sleep.
    /// </summary>
    public async Task WaitForChangeAsync(double timeout)
    {
        try
        {
            _hostVersion = await _api.WaitForChangeAsync(_hostVersion, timeout);
            // A line's end and the next line's start arrive back to back; read them as one change.
            await Task.Delay(50);
        }
        catch
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, timeout)));
        }
    }

    private async Task LoadMessagesAsync()
    {
        var wanted = Target;
        List<ChatMessage> list;
        try
        {
            list = await _api.MessagesAsync(wanted.SessionId);
        }
        catch
        {
            return;
        }
        // A slower load for the previous target must not overwrite the current one.
        if (Target == wanted)
        {
            Messages = list;
            Notify();
        }
    }

    public void SetHovering(bool on)
    {
        if (Hovering == on) return;
        Hovering = on;
        Notify();
    }

    // MARK: Quick bar

    public void OpenQuickBar()
    {
        var first = Snapshot?.Pending.FirstOrDefault();
        Target = first is not null && IsManaged(first.Session) ? Target.Session(first.Session) : RestoredTarget();
        HistoryExpanded = _store.Settings.HistoryExpanded;
        SendError = null;
        ClearEscapeHint();
        QuickBarOpen = true;
        if (HistoryExpanded) _ = LoadMessagesAsync();
        Notify();
    }

    public void CloseQuickBar()
    {
        QuickBarOpen = false;
        AnsweringId = null;
        SendError = null;
        ClearEscapeHint();
        Notify();
    }

    /// <summary>
    /// The first Esc only shows a hint; a second within <see cref="EscapeGate.Window"/>
    /// closes. The draft survives either way.
    /// </summary>
    public void EscapePressed(double? at = null)
    {
        if (!QuickBarOpen) return;
        var time = at ?? Now;
        switch (_escapeGate.Press(time))
        {
            case EscapeGate.Action.Close:
                CloseQuickBar();
                break;
            default:
                EscapeHint = true;
                _escapeHintUntil = time + EscapeGate.Window;
                Notify();
                break;
        }
    }

    /// <summary>Clears the Esc hint once its window elapses (driven by the UI timer).</summary>
    public void ExpireEscapeHint()
    {
        if (!EscapeHint) return;
        if (_escapeHintUntil >= 0 && Now >= _escapeHintUntil)
        {
            EscapeHint = false;
            Notify();
        }
    }

    private void ClearEscapeHint()
    {
        _escapeGate.Reset();
        _escapeHintUntil = -1;
        if (!EscapeHint) return;
        EscapeHint = false;
    }

    public void SelectTarget(Target target)
    {
        if (Target == target) return;
        Target = target;
        AnsweringId = null;
        if (QuickBarOpen && HistoryExpanded)
        {
            Messages = new List<ChatMessage>();
            _ = LoadMessagesAsync();
        }
        Notify();
    }

    private Target RestoredTarget()
    {
        var key = _store.Settings.LastTarget;
        if (string.IsNullOrEmpty(key) || key == Target.Jarvis.StorageKey) return Target.Jarvis;
        return IsManaged(key!) ? Target.Session(key!) : Target.Jarvis;
    }

    private bool IsManaged(string id)
        => Snapshot?.Sessions.Any(s => s.Id == id && s.Managed) ?? false;

    // MARK: Managed set

    /// <summary>
    /// Hands <paramref name="session"/> to Jarvis or takes it back. Taking back
    /// the current target falls back to talking to Jarvis directly.
    /// </summary>
    public async Task SetManagedAsync(string session, bool on)
    {
        if (ManagingId is not null) return;
        ManagingId = session;
        Notify();
        try
        {
            await _api.SetManagedAsync(session, on);
            SendError = null;
        }
        catch (Exception ex)
        {
            SendError = (on ? "Could not hand over: " : "Could not take back: ") + Describe(ex);
            ManagingId = null;
            Notify();
            return;
        }
        if (!on && Target == Target.Session(session)) Target = Target.Jarvis;
        ManagingId = null;
        await RefreshAsync();
    }

    /// <summary>Ctrl+1…Ctrl+9; 1-based, out-of-range numbers are ignored.</summary>
    public void SelectTargetByNumber(int number)
    {
        var options = Targets;
        if (number < 1 || number > options.Count) return;
        SelectTarget(options[number - 1].Target);
    }

    public void CycleTarget()
    {
        var options = Targets;
        if (options.Count == 0) return;
        var index = options.FindIndex(o => o.Target == Target);
        SelectTarget(options[(index + 1) % options.Count].Target);
    }

    public async Task<bool> SubmitAsync()
    {
        var text = Draft.Trim();
        if (text.Length == 0) return false;
        var destination = Target;
        try
        {
            if (AnsweringId is { } id) await _api.AnswerAsync(new PendingAnswer.Text(id, text));
            else if (destination.SessionId is { } session) await _api.SendAsync(text, session);
            else await _api.SendAsync(text, null);
        }
        catch (Exception ex)
        {
            SendError = "Could not send: " + Describe(ex);
            Notify();
            return false;
        }
        Toast = $"Sent to {LabelFor(destination)}";
        _store.Update(s => s.LastTarget = destination.StorageKey);
        Draft = "";
        SendError = null;
        AnsweringId = null;
        QuickBarOpen = false;
        await RefreshAsync();
        return true;
    }

    public void ClearToast()
    {
        if (Toast is null) return;
        Toast = null;
        Notify();
    }

    /// <summary>
    /// Sets the one-line notice the quick bar shows. Used by the tray for actions
    /// that have nothing to say through the normal state path (an entry that was
    /// clicked while there was nothing to show).
    /// </summary>
    public void SetToast(string text)
    {
        Toast = text;
        Notify();
    }

    // MARK: Pending

    public Task DecideAsync(PendingItem item, bool allow)
        => SubmitAnswerAsync(new PendingAnswer.Decision(item.Id, allow));

    public Task AlwaysAllowAsync(PendingItem item)
        => item.CanAlwaysAllow ? SubmitAnswerAsync(new PendingAnswer.Always(item.Id)) : Task.CompletedTask;

    public Task ChooseAsync(PendingItem item, string choice)
        => SubmitAnswerAsync(new PendingAnswer.Choice(item.Id, choice));

    public void BeginTextAnswer(PendingItem item)
    {
        Target = Target.Session(item.Session);
        AnsweringId = item.Id;
        QuickBarOpen = true;
        Notify();
    }

    private async Task SubmitAnswerAsync(PendingAnswer answer)
    {
        try
        {
            await _api.AnswerAsync(answer);
            SendError = null;
        }
        catch (Exception ex)
        {
            SendError = "Could not submit: " + Describe(ex);
        }
        await RefreshAsync();
    }

    // MARK: History, DSH

    public async Task SetHistoryExpandedAsync(bool on)
    {
        HistoryExpanded = on;
        _store.Update(s => s.HistoryExpanded = on);
        Notify();
        if (!on) return;
        try { await _api.MarkReadAsync(null); } catch { /* best effort */ }
        await LoadMessagesAsync();
    }

    public async Task OpenInDshAsync(string session)
    {
        try { await _api.OpenAsync(session); } catch { /* best effort */ }
        try { await _api.MarkReadAsync(session); } catch { /* best effort */ }
    }

    /// <summary>Revoking the saved preset does not undo an operation that already ran.</summary>
    public async Task RevokeAutoApprovalAsync(AutoApproval entry)
    {
        if (RevokingAutoApproval is not null) return;
        var rule = VisibleAutoApprovals.FirstOrDefault(e => e.Id == entry.Id)?.Rule;
        if (rule is null) return;
        RevokingAutoApproval = rule;
        Notify();
        try
        {
            await _api.RemoveApprovalRuleAsync(rule);
        }
        catch (JarvisApiException ex) when (ex.IsNotFound)
        {
            // A different client already removed the same exact preset.
        }
        catch (Exception ex)
        {
            AutoApprovalError = "Could not revoke the rule: " + Describe(ex);
            RevokingAutoApproval = null;
            Notify();
            return;
        }
        foreach (var id in (Snapshot?.AutoApprovals ?? new List<AutoApproval>())
                     .Where(e => e.Rule == rule).Select(e => e.Id))
            _revokedAutoApprovalIds.Add(id);
        AutoApprovalError = null;
        RevokingAutoApproval = null;
        await RefreshAsync();
    }

    public static string Describe(Exception error)
        => error is JarvisApiException api ? api.Message : error.Message;
}
