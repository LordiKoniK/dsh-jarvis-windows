using JarvisPanel.Core;

namespace JarvisPanel.App;

/// <summary>
/// Keeps <see cref="PanelModel"/> fresh: one refresh, then a blocking wait for the
/// host to report a change, forever.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here may end the loop or surface an exception: the panel is a status
/// orb, and an unreachable host is a state to display (red), not a failure. Every
/// exception is logged and retried with backoff.
/// </para>
/// <para>
/// Started from the UI thread without <c>ConfigureAwait(false)</c>, so every
/// continuation — including the ones inside <see cref="PanelModel"/> — resumes on
/// the UI thread that owns the model.
/// </para>
/// </remarks>
internal sealed class StateLoop : IDisposable
{
    private const double OnlineTimeout = 1.0;
    private const double ConnectiveDelay = 0.5;
    private const double MaxBackoff = 8.0;

    private static readonly string[] OfflineReasons =
    {
        "Not connected to DSH",
        "Waiting for the Jarvis plugin",
        "Host offline",
    };

    private readonly PanelModel _model;
    private readonly CancellationTokenSource _cts = new();
    private double _lastErrorAt;
    private int _offlineTick;

    public StateLoop(PanelModel model) => _model = model;

    /// <summary>Bumped after each refresh, so the UI can tell that state moved on.</summary>
    public int Ticks { get; private set; }

    /// <summary>Localised-enough reason for the last failed refresh; null while online.</summary>
    public string? LastError { get; private set; }

    public void Start() => _ = PumpAsync();

    private async Task PumpAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _model.RefreshAsync();
                Ticks++;
                LastError = _model.Connection.IsOnline ? null : Describe(_model.Connection);
                if (_model.Connection.IsOnline) _offlineTick = 0;
            }
            catch (Exception ex)
            {
                // Catch-all on purpose: one bad response must not end the loop.
                LastError = Log.Describe(ex);
                _lastErrorAt = PanelModel.Now;
                Log.Write("refresh failed: " + LastError);
            }

            try
            {
                if (_model.Connection.IsOnline)
                    await _model.WaitForChangeAsync(OnlineTimeout);
                else
                    await Task.Delay(TimeSpan.FromSeconds(Backoff()));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Write("wait failed: " + Log.Describe(ex));
                try { await Task.Delay(TimeSpan.FromSeconds(Backoff())); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>Backoff grows only while the host stays unreachable, so a healthy wait never slows down.</summary>
    private double Backoff()
    {
        if (PanelModel.Now - _lastErrorAt > 8) _offlineTick = 0;
        _offlineTick = Math.Min(_offlineTick + 1, 6);
        return Math.Min(ConnectiveDelay * Math.Pow(2, _offlineTick - 1), MaxBackoff);
    }

    /// <summary>
    /// The host reason is a raw exception message; `runtime.json` missing is the
    /// normal case before the plugin is installed, so it gets a readable line.
    /// </summary>
    private string Describe(Connection connection)
    {
        var reason = connection.Reason ?? "";
        if (reason.Contains("runtime.json", StringComparison.OrdinalIgnoreCase))
        {
            var text = OfflineReasons[_offlineTick % OfflineReasons.Length];
            _offlineTick++;
            return text + " — runtime.json not found";
        }
        return reason.Length == 0 ? "Host unreachable" : reason;
    }

    public void Dispose() => _cts.Cancel();
}
