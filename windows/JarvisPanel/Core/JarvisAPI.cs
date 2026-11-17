namespace JarvisPanel.Core;

/// <summary>An answer to a pending approval or question.</summary>
public abstract record PendingAnswer
{
    public sealed record Decision(string Id, bool Allow) : PendingAnswer;
    public sealed record Always(string Id) : PendingAnswer;
    public sealed record Choice(string Id, string Value) : PendingAnswer;
    public sealed record Text(string Id, string Value) : PendingAnswer;
}

public enum JarvisApiErrorKind { NoRuntime, Http, Transport, Decoding }

public sealed class JarvisApiException : Exception
{
    public JarvisApiErrorKind Kind { get; }
    public int StatusCode { get; }

    public JarvisApiException(JarvisApiErrorKind kind, string message, int statusCode = 0) : base(message)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    /// <summary>Another client already removed the resource; treat as success.</summary>
    public bool IsNotFound => Kind == JarvisApiErrorKind.Http && StatusCode == 404;

    /// <summary>
    /// Shown verbatim in the status line and in every "could not send" prefix, so
    /// these read as sentences the user can act on rather than as error codes.
    /// </summary>
    public static JarvisApiException NoRuntime()
        => new(JarvisApiErrorKind.NoRuntime, "The Jarvis plugin is not running (runtime.json is missing)");

    public static JarvisApiException Http(int status)
        => new(JarvisApiErrorKind.Http, $"HTTP {status}", status);

    public static JarvisApiException Transport(string message)
        => new(JarvisApiErrorKind.Transport, message);

    public static JarvisApiException Decoding()
        => new(JarvisApiErrorKind.Decoding, "The host's reply could not be parsed");
}

/// <summary>
/// Everything the panel needs from the host plugin. A null target in
/// <see cref="SendAsync"/> means "talk to Jarvis directly" (no injection into a
/// worker session).
/// </summary>
public interface IJarvisApi
{
    Task<Snapshot> SnapshotAsync(CancellationToken ct = default);

    /// <summary>
    /// Blocks until the host's state version differs from <paramref name="after"/>
    /// (or the timeout passes) and returns the current version.
    /// </summary>
    Task<int> WaitForChangeAsync(int after, double timeoutSeconds, CancellationToken ct = default);

    /// <summary>Jarvis's own conversation when <paramref name="session"/> is null, otherwise that session's.</summary>
    Task<List<ChatMessage>> MessagesAsync(string? session, CancellationToken ct = default);

    /// <summary>Hands a session to Jarvis (<c>managed == true</c>) or takes it back.</summary>
    Task SetManagedAsync(string session, bool managed, CancellationToken ct = default);

    /// <summary>Overrides a managed session's narration; null restores the host default.</summary>
    Task SetNarrationAsync(string session, Narration? narration, CancellationToken ct = default);

    Task SendAsync(string text, string? target, CancellationToken ct = default);

    Task<List<ApprovalRule>> ApprovalRulesAsync(CancellationToken ct = default);

    Task RemoveApprovalRuleAsync(ApprovalRule.Identity identity, CancellationToken ct = default);

    Task AnswerAsync(PendingAnswer answer, CancellationToken ct = default);

    Task VoiceAsync(VoiceAction action, CancellationToken ct = default);

    Task MarkReadAsync(string? session, CancellationToken ct = default);

    Task OpenAsync(string session, CancellationToken ct = default);
}
