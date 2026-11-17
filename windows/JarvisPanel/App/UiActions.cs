using JarvisPanel.Core;
using JarvisPanel.UI;

namespace JarvisPanel.App;

/// <summary>
/// Owns every window the panel can show and the handful of things the tray menu,
/// the orb's context menu and the settings dialog can all do. They share one
/// object so the "show/hide orb" label, the quick bar, the approval column and the
/// persisted position stay in step no matter which surface was used.
/// </summary>
/// <remarks>
/// <para>
/// Stage B put every surface behind this class, because they overlap: a pending
/// card opens the quick bar for a typed answer, the bar stacks with the approval
/// column, and moving the orb from the settings window has to move the surfaces
/// that follow it. One owner makes those orderings explicit instead of emergent.
/// </para>
/// <para>
/// Nothing here may throw into the message loop. The handlers below are reached
/// from <c>ToolStripMenuItem</c> callbacks and <see cref="PanelModel.Changed"/>,
/// both of which WinForms invokes without a try — an escaping exception would end
/// up in <c>Application.ThreadException</c> and, at best, be logged. Every public
/// entry point therefore wraps its work.
/// </para>
/// </remarks>
internal sealed class UiActions : IDisposable, ISettingsHost
{
    /// <summary>Matches the id <see cref="PanelContext"/> registers at startup.</summary>
    public const int HotKeyId = 0x4A50;

    private readonly PanelModel _model;
    private readonly SettingsStore _store;

    private OrbWindow? _orb;
    private QuickBarWindow? _quickBar;
    private TrayIcon? _tray;
    private ApprovalCardWindow? _cards;
    private AutoApprovalsWindow? _autoApprovals;
    private SettingsWindow? _settings;
    private bool _disposed;

    public UiActions(PanelModel model, SettingsStore store)
    {
        _model = model;
        _store = store;
    }

    /// <summary>Raised when the tray or the orb menu asks the process to end.</summary>
    public event Action? QuitRequested;

    public bool OrbVisible => _orb?.Visible ?? false;

    /// <summary>Second entry of the orb's context menu, kept in sync with <see cref="OrbVisible"/>.</summary>
    public string OrbToggleLabel => OrbVisible ? "Hide orb" : "Show orb";

    public Target CurrentTarget => _model.Target;

    public bool HistoryExpanded => _model.HistoryExpanded;

    public List<TargetOption> Targets => _model.Targets;

    /// <summary>Playback controls for the tray, mirroring the bar and the hover fan.</summary>
    public List<HoverButton> VoiceButtons => _model.HoverButtons;

    public void Attach(OrbWindow orb)
    {
        _orb = orb;
        orb.QuickBarRequested += ToggleQuickBar;
        orb.DockEdgeChanged += PersistPosition;
        orb.OrbMoved += FollowOrb;
    }

    public void Attach(TrayIcon tray) => _tray = tray;

    // ------------------------------------------------------------------ orb menu

    /// <summary>Ctrl+Alt+J: open, focus, or close the quick bar (spec §6.1).</summary>
    public void HotKeyPressed()
    {
        Guard(() =>
        {
            var focused = _quickBar is { Visible: true, ContainsFocus: true };
            var action = InputHotKey.Resolve(_model.QuickBarOpen, focused);
            Log.Write($"quick bar: hotkey -> {action} (open={_model.QuickBarOpen} focused={focused})");
            switch (action)
            {
                case InputHotKeyAction.Open:
                    OpenQuickBar();
                    break;
                case InputHotKeyAction.Focus:
                    FocusQuickBar();
                    break;
                default:
                    CloseQuickBar();
                    break;
            }
        });
    }

    public void ToggleQuickBar()
    {
        Guard(() =>
        {
            if (_model.QuickBarOpen) CloseQuickBar();
            else OpenQuickBar();
        });
    }

    public void OpenQuickBar()
    {
        Guard(() =>
        {
            if (_orb is null) return;
            _orb.Undock();
            _model.OpenQuickBar();
            _quickBar ??= new QuickBarWindow(_model, _orb);
            _quickBar.SyncToModel(quickBarOpen: true);
            _orb.StartQuickBarFrames();
            SyncCards();
        });
    }

    /// <summary>
    /// The tray's "open in the middle" entry: an orb dragged to a monitor that has
    /// since been unplugged leaves the bar unreachable, and a menu that can only
    /// open a window next to a missing orb is a dead end.
    /// </summary>
    public void OpenQuickBarCentred()
    {
        Guard(() =>
        {
            if (_orb is null) return;
            OpenQuickBar();
            if (_quickBar is not { } bar) return;
            var area = Screen.FromPoint(_orb.CenterPixels).WorkingArea;
            bar.Location = new System.Drawing.Point(
                area.X + (area.Width - bar.Width) / 2,
                area.Y + (area.Height - bar.Height) / 2);
            bar.FocusInput();
        });
    }

    public void FocusQuickBar() => Guard(() => _quickBar?.FocusInput());

    public void CloseQuickBar()
    {
        Guard(() =>
        {
            _model.CloseQuickBar();
            _quickBar?.SyncToModel(quickBarOpen: false);
            _orb?.StopQuickBarFrames();
            SyncCards();
        });
    }

    public void ToggleHistory()
    {
        Guard(() =>
        {
            OpenQuickBar();
            _ = RunAsync(() => _model.SetHistoryExpandedAsync(!_model.HistoryExpanded));
        });
    }

    public void SelectTarget(Target target)
    {
        Guard(() =>
        {
            OpenQuickBar();
            _model.SelectTarget(target);
            _quickBar?.SyncToModel(_model.QuickBarOpen);
        });
    }

    public void PressVoice(HoverButton button)
    {
        Guard(() => _ = RunAsync(() => _model.PressAsync(button)));
    }

    // ------------------------------------------------------------- stage B panes

    /// <summary>Shows the pending cards even when nothing is pending, so the entry is not a dead click.</summary>
    public void ShowApprovalCards()
    {
        Guard(() =>
        {
            EnsureCards();
            if (_model.VisiblePending.Count == 0)
            {
                // An empty click would read as a broken menu entry, so say so in the
                // one place the panel already talks to the user.
                _model.SetToast("Nothing is waiting for an answer");
                OpenQuickBar();
                return;
            }
            SyncCards();
        });
    }

    public void ShowAutoApprovals()
    {
        Guard(() =>
        {
            if (_orb is null) return;
            _autoApprovals ??= new AutoApprovalsWindow(_model, _orb);
            _autoApprovals.ShowOnOrbMonitor();
        });
    }

    public void ShowSettings()
    {
        Guard(() =>
        {
            _settings ??= new SettingsWindow(_store, this);
            _settings.Reload();
            if (!_settings.Visible) _settings.Show();
            _settings.Activate();
        });
    }

    public void ToggleOrb()
    {
        Guard(() =>
        {
            if (_orb is null) return;
            if (_orb.Visible)
            {
                CloseQuickBar();
                _cards?.Hide();
                _orb.HideOrb();
            }
            else
            {
                _orb.ShowOrb();
            }
            _tray?.RebuildMenu();
        });
    }

    /// <summary>Forgets the saved spot and returns to the default top-right corner.</summary>
    public void ResetPosition()
    {
        Guard(() =>
        {
            if (_orb is null) return;
            var screen = Screen.FromPoint(_orb.CenterPixels);
            _store.Update(s => s.Positions.Remove(screen.DeviceName));
            _orb.RestoreSavedPosition();
            FollowOrb();
            _tray?.RebuildMenu();
        });
    }

    public void Quit() => Guard(() => QuitRequested?.Invoke());

    // ------------------------------------------------------------- settings host

    /// <inheritdoc />
    public OrbPlacement OrbPlacement
    {
        get
        {
            if (_orb is null) return new OrbPlacement(0, 0, null, "");
            var screen = Screen.FromPoint(_orb.CenterPixels);
            return new OrbPlacement(ScreenMetrics.Pt(_orb.Left), ScreenMetrics.Pt(_orb.Top), _orb.Edge,
                screen.DeviceName);
        }
    }

    /// <inheritdoc />
    public OrbPlacement ApplyOrbPlacement(double x, double y, DockEdge? edge)
    {
        if (_orb is null) return OrbPlacement;
        _orb.MoveToPoints(new PointD(x, y), edge);
        FollowOrb();
        // Read back rather than echo: the clamp and the snap may both have moved it.
        return OrbPlacement;
    }

    /// <inheritdoc />
    public bool TryApplyHotKey(HotKeySpec spec)
    {
        if (_orb is null) return false;
        var ok = _orb.RegisterHotKey(HotKeyId, spec);
        if (!ok) Log.Write($"hotkey {spec.DisplayString} is already taken");
        return ok;
    }

    /// <inheritdoc />
    public void SettingsApplied()
    {
        Guard(() =>
        {
            _tray?.RebuildMenu();
            _orb?.RefreshSettings();
        });
    }

    // ------------------------------------------------------------------ plumbing

    /// <summary>
    /// Keeps the approval column in step with the model. Called on every model
    /// change by <see cref="PanelContext"/>, so the column cannot be left behind by
    /// a state that arrived while one of its buttons was busy.
    /// </summary>
    public void ModelChanged()
    {
        Guard(() =>
        {
            if (_quickBar is { Visible: true } && !_model.QuickBarOpen) _quickBar.Hide();
            SyncCards();
        });
    }

    private void SyncCards()
    {
        // The column only exists once something is pending, so a session with no
        // approvals never pays for a window it would keep hidden.
        if (_cards is null && _model.VisiblePending.Count == 0) return;
        EnsureCards();
        if (_cards is null) return;
        _cards.SyncToModel(_quickBar is { Visible: true } bar ? bar.Bounds : null);
    }

    private void EnsureCards()
    {
        if (_cards is not null || _orb is null) return;
        _cards = new ApprovalCardWindow(_model, _orb);
        _cards.TextAnswerRequested += OpenQuickBar;
        _cards.AutoApprovalsRequested += ShowAutoApprovals;
    }

    /// <summary>Moves everything that follows the orb after it has been moved or re-docked.</summary>
    private void FollowOrb()
    {
        Guard(() =>
        {
            if (_quickBar is { Visible: true }) _quickBar.FollowOrb();
            SyncCards();
        });
    }

    /// <summary>
    /// Stores the orb origin in the monitor's point space (the same space
    /// <see cref="PanelSettings.Positions"/> is keyed by on macOS), plus the edge
    /// it is docked to so the strip comes back after a restart.
    /// </summary>
    private void PersistPosition(DockEdge? edge)
    {
        if (_orb is null) return;
        var screen = Screen.FromPoint(_orb.CenterPixels);
        var x = ScreenMetrics.Pt(_orb.Left);
        var y = ScreenMetrics.Pt(_orb.Top);
        _store.Update(s => s.Positions[screen.DeviceName] = new SavedPosition { X = x, Y = y, Dock = edge });
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log.Write("panel action failed: " + Log.Describe(ex));
        }
    }

    /// <summary>Runs a UI action, logging rather than propagating whatever it throws.</summary>
    private static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Write("panel action failed: " + Log.Describe(ex));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _quickBar?.Dispose();
        _cards?.Dispose();
        _autoApprovals?.Dispose();
        _settings?.Dispose();
    }
}
