using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace JarvisPanel.Core;

/// <summary>
/// Runtime discovery file written by the host plugin. The Desktop shell's
/// web server rejects routes lacking the renderer capability header (even on
/// loopback), so it is sent on every request.
/// </summary>
public sealed class RuntimeFile
{
    public string Origin { get; init; } = "";
    public string Token { get; init; } = "";
    public string? RendererHeaderName { get; init; }
    public string? RendererHeaderValue { get; init; }
    public int Pid { get; init; }

    public static RuntimeFile? Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var origin = J.Str(e, "origin");
        var token = J.Str(e, "token");
        if (string.IsNullOrEmpty(origin) || string.IsNullOrEmpty(token)) return null;
        string? name = null, value = null;
        if (J.Obj(e, "rendererHeader") is { } header)
        {
            name = J.Str(header, "name");
            value = J.Str(header, "value");
        }
        return new RuntimeFile
        {
            Origin = origin.TrimEnd('/'),
            Token = token,
            RendererHeaderName = name,
            RendererHeaderValue = value,
            Pid = J.Int(e, "pid"),
        };
    }
}

/// <summary>
/// HTTP client for the host plugin's <c>/jarvis/*</c> routes. The runtime file
/// is re-read on every call so a host restart (new port/token) is picked up
/// without relaunching the panel.
/// </summary>
public sealed class JarvisClient : IJarvisApi, IDisposable
{
    private readonly string _runtimeUrl;
    private readonly HttpClient _http;
    private readonly object _lock = new();

    public JarvisClient(string? runtimeUrl = null)
    {
        _runtimeUrl = runtimeUrl ?? DefaultRuntimeUrl;
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static string DefaultRuntimeUrl
    {
        get
        {
            var over = Environment.GetEnvironmentVariable("JARVIS_RUNTIME_FILE");
            if (!string.IsNullOrWhiteSpace(over)) return over;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dsh", "jarvis", "runtime.json");
        }
    }

    /// <summary>DSH Desktop pid from the last successful runtime read (0 = unknown).</summary>
    public int HostPid { get; private set; }

    public async Task<Snapshot> SnapshotAsync(CancellationToken ct = default)
    {
        var data = await RequestAsync(HttpMethod.Get, "/jarvis/state", null, 5, ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(data);
            return Snapshot.Parse(doc.RootElement);
        }
        catch (JsonException)
        {
            throw JarvisApiException.Decoding();
        }
    }

    public async Task<int> WaitForChangeAsync(int after, double timeoutSeconds, CancellationToken ct = default)
    {
        var ms = (int)Math.Max(0, timeoutSeconds * 1000);
        var data = await RequestAsync(HttpMethod.Get, $"/jarvis/wait?since={after}&timeout={ms}", null,
            timeoutSeconds + 5, ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(data);
            return J.Int(doc.RootElement, "version");
        }
        catch (JsonException)
        {
            throw JarvisApiException.Decoding();
        }
    }

    public async Task<List<ChatMessage>> MessagesAsync(string? session, CancellationToken ct = default)
    {
        var path = "/jarvis/messages";
        if (!string.IsNullOrEmpty(session)) path += "?session=" + Uri.EscapeDataString(session);
        var data = await RequestAsync(HttpMethod.Get, path, null, 5, ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(data);
            return ChatMessage.DecodeAll(doc.RootElement);
        }
        catch (JsonException)
        {
            throw JarvisApiException.Decoding();
        }
    }

    public Task SetManagedAsync(string session, bool managed, CancellationToken ct = default)
        => RequestAsync(HttpMethod.Post, "/jarvis/managed",
            w => { w.WriteString("session", session); w.WriteBoolean("managed", managed); }, 5, ct);

    public Task SetNarrationAsync(string session, Narration? narration, CancellationToken ct = default)
        => RequestAsync(HttpMethod.Post, "/jarvis/narration", w =>
        {
            w.WriteString("session", session);
            if (narration is { } n) w.WriteString("narration", n.Raw());
            else w.WriteNull("narration");
        }, 5, ct);

    public Task SendAsync(string text, string? target, CancellationToken ct = default)
        => RequestAsync(HttpMethod.Post, "/jarvis/input", w =>
        {
            w.WriteString("text", text);
            if (!string.IsNullOrEmpty(target)) w.WriteString("session", target);
        }, 5, ct);

    public Task AnswerAsync(PendingAnswer answer, CancellationToken ct = default)
        => RequestAsync(HttpMethod.Post, "/jarvis/pending/answer", w =>
        {
            switch (answer)
            {
                case PendingAnswer.Decision d:
                    w.WriteString("id", d.Id);
                    w.WriteString("decision", d.Allow ? "allow" : "deny");
                    break;
                case PendingAnswer.Always a:
                    w.WriteString("id", a.Id);
                    w.WriteString("decision", "always");
                    break;
                case PendingAnswer.Choice c:
                    w.WriteString("id", c.Id);
                    w.WriteString("choice", c.Value);
                    break;
                case PendingAnswer.Text t:
                    w.WriteString("id", t.Id);
                    w.WriteString("text", t.Value);
                    break;
            }
        }, 5, ct);

    public async Task<List<ApprovalRule>> ApprovalRulesAsync(CancellationToken ct = default)
    {
        var data = await RequestAsync(HttpMethod.Get, "/jarvis/approval-rules", null, 5, ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(data);
            var rules = new List<ApprovalRule>();
            if (J.Arr(doc.RootElement, "rules") is { } arr)
            {
                foreach (var item in arr.EnumerateArray())
                    if (ApprovalRule.Parse(item) is { } rule) rules.Add(rule);
            }
            return rules;
        }
        catch (JsonException)
        {
            throw JarvisApiException.Decoding();
        }
    }

    public Task RemoveApprovalRuleAsync(ApprovalRule.Identity identity, CancellationToken ct = default)
        => RequestAsync(HttpMethod.Post, "/jarvis/approval-rules/remove", w =>
        {
            w.WriteString("fingerprint", identity.Fingerprint);
            w.WriteString("tool", identity.Tool);
            w.WriteString("workspace", identity.Workspace);
        }, 5, ct);

    public Task VoiceAsync(VoiceAction action, CancellationToken ct = default)
        => RequestAsync(HttpMethod.Post, "/jarvis/voice", w => w.WriteString("action", action.Raw()), 5, ct);

    public Task MarkReadAsync(string? session, CancellationToken ct = default)
        => RequestAsync(HttpMethod.Post, "/jarvis/read", w =>
        {
            if (!string.IsNullOrEmpty(session)) w.WriteString("session", session);
        }, 5, ct);

    public Task OpenAsync(string session, CancellationToken ct = default)
        => RequestAsync(HttpMethod.Post, "/jarvis/open", w => w.WriteString("session", session), 5, ct);

    private RuntimeFile LoadRuntime()
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(_runtimeUrl);
        }
        catch (IOException)
        {
            throw JarvisApiException.NoRuntime();
        }
        catch (UnauthorizedAccessException)
        {
            throw JarvisApiException.NoRuntime();
        }

        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var file = RuntimeFile.Parse(doc.RootElement) ?? throw JarvisApiException.NoRuntime();
            lock (_lock) HostPid = file.Pid;
            return file;
        }
        catch (JsonException)
        {
            throw JarvisApiException.NoRuntime();
        }
    }

    private async Task<byte[]> RequestAsync(HttpMethod method, string path, Action<Utf8JsonWriter>? body,
        double timeoutSeconds, CancellationToken ct)
    {
        var runtime = LoadRuntime();
        if (!Uri.TryCreate(runtime.Origin + path, UriKind.Absolute, out var url))
            throw JarvisApiException.NoRuntime();

        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + runtime.Token);
        if (!string.IsNullOrEmpty(runtime.RendererHeaderName) && runtime.RendererHeaderValue is not null)
            request.Headers.TryAddWithoutValidation(runtime.RendererHeaderName, runtime.RendererHeaderValue);
        if (body is not null)
        {
            var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer)) body(w);
            request.Content = new ByteArrayContent(buffer.ToArray());
            request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            throw JarvisApiException.Transport(ex.Message);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (status < 200 || status >= 300) throw JarvisApiException.Http(status);
            return await response.Content.ReadAsByteArrayAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public void Dispose() => _http.Dispose();
}
