using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using JarvisPanel.App;
using JarvisPanel.Core;

namespace JarvisPanel.UI;

/// <summary>
/// The orb: a 140pt layered window holding the particle cloud, the badge and a
/// one-line caption. It is the Windows counterpart of macOS's <c>OrbPanel</c>
/// plus the parts of <c>AppController</c> that drive the orb itself — press,
/// drag, snap, strip dock, hover and the context menu.
/// </summary>
/// <remarks>
/// <para>
/// The layout layer is shared verbatim with the macOS port: <see cref="Placement"/>,
/// <see cref="Workspace"/> and <see cref="ParticleSim"/> all speak <b>points</b>
/// (and, for particles, orb units), while Win32 and GDI+ speak physical pixels.
/// <see cref="Render"/> is the single place the two meet.
/// </para>
/// <para>
/// The frame timer is only a metronome. <see cref="FramePacing.Fps"/> decides
/// whether a tick actually draws, so a settled idle orb repaints ~30 times a
/// second, a docked strip ~20, and any transition runs at 60.
/// </para>
/// </remarks>
internal sealed class OrbWindow : LayeredWindow
{
    /// <summary>Metronome rate. Individual ticks are skipped, never queued.</summary>
    private const int TimerHz = 60;
    private const int HoverHz = 30;
    private const int MinTimerMs = 8;
    private const double CollapseDelay = 0.4;
    private const double TapMaxSeconds = 0.6;
    private const double CaptionHeight = 26;
    private const double CaptionGap = 6;
    private const double DefaultEdgeMargin = 16;
    private const int MaxBadgeCount = 99;

    private readonly PanelModel _model;
    private readonly SettingsStore _store;
    private readonly ParticleSim _sim;
    private readonly List<PointSprite> _sprites = new(1024);
    private readonly System.Windows.Forms.Timer _frameTimer;
    private readonly System.Windows.Forms.Timer _hoverTimer;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly Font _badgeFont = Theme.Font(7.5f, FontStyle.Bold);
    private readonly Font _captionFont = Theme.Font(8f);

    /// <summary>Screen-space edge the orb is snapped to; kept even while the strip is open.</summary>
    private DockEdge? _edge;
    /// <summary>Docked but slid out to the full orb because the pointer or the quick bar is on it.</summary>
    private bool _stemOpen;
    private bool _hovering;
    private bool _dragging;
    private bool _visible = true;

    private bool _pressed;
    private Point _pressCursor;
    private Point _pressOrigin;
    private double _pressedAt = -100;
    private double _hoverLeftAt = -100;
    private double _transitionUntil = 1.5;
    private double _forceFramesUntil;
    private double _suppressTapUntil;
    private double _lastDrawAt = -1;
    private bool _snapping;
    private int _hotKeyId = -1;

    private OrbTint _tint;
    private OrbTint _previousTint;
    private double _tintChangedAt;
    private OrbAppearance _appearance;
    private bool _docked;
    private int _appliedFps;

    private Action? _resetRequested;
    private Action? _quitRequested;

    public OrbWindow(PanelModel model, SettingsStore store)
        : base(ScreenMetrics.Px(Placement.OrbWindow), ScreenMetrics.Px(Placement.OrbWindow))
    {
        _model = model;
        _store = store;
        _sim = new ParticleSim(store.Settings.Tier.Count(), 0x5A17C0DEUL);
        _tint = OrbStateResolver.Resolve(model.Snapshot, false, false).Tint;
        _previousTint = _tint;

        _frameTimer = new System.Windows.Forms.Timer { Interval = Math.Max(8, 1000 / TimerHz) };
        _frameTimer.Tick += (_, _) => StepFrame();
        _hoverTimer = new System.Windows.Forms.Timer { Interval = Math.Max(8, 1000 / HoverHz) };
        _hoverTimer.Tick += (_, _) => CheckHover();

        model.Changed += OnModelChanged;
        RestoreSavedPosition();
        _frameTimer.Start();
    }

    /// <summary>Clicking the orb (or a menu entry) asks for the quick bar.</summary>
    public event Action? QuickBarRequested;

    /// <summary>
    /// Raised when the dock edge changes so the caller can persist position and
    /// edge together. Fires after a snap, a drag release and a position reset.
    /// </summary>
    public event Action<DockEdge?>? DockEdgeChanged;

    /// <summary>Window centre in physical screen pixels; used for monitor lookup.</summary>
    public Point CenterPixels => new(Left + Width / 2, Top + Height / 2);

    /// <summary>Screen-space edge the orb is docked to; null while it floats.</summary>
    public DockEdge? Edge => _edge;

    /// <summary>
    /// Moves the orb to an absolute screen point and then snaps it, so a position
    /// typed into the settings window goes through exactly the same clamp, snap and
    /// persistence as a drag. <paramref name="edge"/> overrides the edge the snap
    /// found, which is how a docked-without-touching-the-edge state is restored.
    /// </summary>
    public void MoveToPoints(PointD origin, DockEdge? edge = null)
    {
        _snapping = true;
        try
        {
            MoveTo((int)Math.Round(origin.X * ScreenMetrics.Scale), (int)Math.Round(origin.Y * ScreenMetrics.Scale));
        }
        finally
        {
            _snapping = false;
        }
        SnapToEdge();
        if (edge is not { } wanted || !_store.Settings.DockToStrip) return;
        _edge = wanted;
        _stemOpen = _model.QuickBarOpen || _hovering;
        TickDock();
        DockEdgeChanged?.Invoke(_edge);
    }

    /// <summary>
    /// Re-reads the settings the orb's own rendering depends on. The particle tier
    /// and the reduced-motion flag are otherwise only picked up on a state change,
    /// which can be minutes away when the host is idle.
    /// </summary>
    public void RefreshSettings()
    {
        _sim.ReduceMotion = _store.Settings.ReduceMotion();
        _sim.SetCount(_store.Settings.Tier.Count(), Now);
        TickDock();
        MarkTransition();
    }

    /// <summary>Raised on <c>WM_HOTKEY</c> for the id last passed to <see cref="RegisterHotKey"/>.</summary>
    public event Action? HotKeyPressed;

    /// <summary>
    /// Registers a global shortcut against this window. It is done here rather
    /// than in an invisible helper window because <c>WM_HOTKEY</c> is posted to
    /// the thread that registered it, and this window already exists.
    /// </summary>
    public bool RegisterHotKey(int id, HotKeySpec spec)
    {
        NativeMethods.UnregisterHotKey(Handle, id);
        var ok = NativeMethods.RegisterHotKey(Handle, id,
            (int)(spec.Modifiers | NativeMethods.MOD_NOREPEAT), (int)spec.VirtualKey);
        _hotKeyId = ok ? id : -1;
        return ok;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY && (int)m.WParam == _hotKeyId && _hotKeyId >= 0)
        {
            try
            {
                HotKeyPressed?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Write("hotkey handler failed: " + Log.Describe(ex));
            }
            return;
        }
        base.WndProc(ref m);
    }

    /// <summary>Menu callbacks the host app supplies, kept out of the constructor signature.</summary>
    public void SetMenuActions(Action resetPosition, Action quit)
    {
        _resetRequested = resetPosition;
        _quitRequested = quit;
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    // ---------------------------------------------------------------- frame loop

    private void StepFrame()
    {
        var now = Now;
        if (!_dragging) _model.ExpireEscapeHint();
        _model.ExpireCaptions();

        if (!_visible)
        {
            _lastDrawAt = -1;
            return;
        }

        // dt comes from the wall clock, not the timer interval, so a skipped or
        // late tick integrates the same amount of simulation.
        var dt = _lastDrawAt < 0 ? 0 : Math.Min(now - _lastDrawAt, 0.1);
        _lastDrawAt = now;

        var transitioning = now < _transitionUntil || now < _forceFramesUntil;
        var docked = _sim.Dock is not null;
        var appearance = _model.Appearance;
        if (appearance.Tint != _tint)
        {
            _previousTint = _tint;
            _tint = appearance.Tint;
            _tintChangedAt = now;
            MarkTransition();
            transitioning = true;
        }

        var fps = FramePacing.Fps(docked, appearance.Motion, transitioning);
        if (_appliedFps != fps)
        {
            _appliedFps = fps;
            _frameTimer.Interval = Math.Max(MinTimerMs, 1000 / fps);
        }

        if (!WantsFrames(now))
        {
            _lastDrawAt = -1;
            return;
        }

        _sim.Step(now, dt);
        _sim.Sprites(now, _sprites);
        _appearance = appearance;
        _docked = docked;
        try
        {
            Redraw();
        }
        catch (Exception ex)
        {
            // A failed flush must not kill the frame timer: the next tick tries again.
            Log.Write("frame failed: " + Log.Describe(ex));
        }
    }

    /// <summary>
    /// Whether this tick has anything to show. Idle and settled is the only case
    /// that stops drawing, so the orb never burns a full-rate repaint doing nothing.
    /// </summary>
    private bool WantsFrames(double now)
    {
        // A badge can appear or change while the orb is idle, and this frame loop is
        // the only thing that repaints it, so a changed badge forces a frame.
        if (_model.Appearance.Badge != _appearance.Badge) return true;
        if (_dragging || _hovering || _model.QuickBarOpen) return true;
        if (now < _transitionUntil || now < _forceFramesUntil) return true;
        if (_sim.ReduceMotion) return false;
        return _sim.Motion != OrbMotion.Idle;
    }

    /// <summary>Forces full-rate frames for a moment so a state change reads as motion, not a cut.</summary>
    private void MarkTransition() => _transitionUntil = Now + FramePacing.TransitionSeconds;

    /// <summary>The quick bar sits next to the orb; keep animating the strip while it is up.</summary>
    public void StartQuickBarFrames() => _forceFramesUntil = Now + 4;

    public void StopQuickBarFrames() => _forceFramesUntil = 0;

    private void OnModelChanged()
    {
        var appearance = _model.Appearance;
        _sim.ReduceMotion = _store.Settings.ReduceMotion();
        _sim.SetCount(_store.Settings.Tier.Count(), Now);
        _sim.SetMotion(appearance.Motion, Now);
        if (_model.QuickBarOpen) _stemOpen = true;
        TickDock();
    }

    // ------------------------------------------------------------------- drawing

    /// <summary>
    /// The one frame the base class asks for: particles, then badge, then caption.
    /// The canvas is already cleared by <see cref="LayeredWindow.Redraw"/>, which
    /// also flushes the result.
    /// </summary>
    /// <remarks>
    /// Two unit conversions happen here, and both are load-bearing:
    /// <list type="number">
    /// <item>A sprite's <c>X</c>/<c>Y</c>/<c>Size</c> are in <b>orb units</b>
    /// (<see cref="ParticleSim.UnitPoints"/> points each, y-up), not points, so they
    /// are multiplied by <see cref="ParticleSim.UnitPoints"/> and then by the monitor
    /// scale.</item>
    /// <item>The y axis is flipped per sprite rather than with a
    /// <c>ScaleTransform(1, -1)</c>. GDI+ silently discards every fill made under a
    /// negative y-scale (verified here for both <c>FillRectangle</c> and
    /// <c>FillPath</c>, on both 32bpp formats), which would leave the orb invisible
    /// with no exception to explain it.</item>
    /// </list>
    /// </remarks>
    protected override void Render()
    {
        var now = Now;
        var tint = Blend(_previousTint, _tint, ColorFade.Progress(now - _tintChangedAt));
        var alpha = (int)Math.Round(255 * OrbAlpha);
        var unit = ParticleSim.UnitPoints * ScreenMetrics.Scale;
        var halfW = Width / 2.0;
        var halfH = Height / 2.0;

        using (var brush = new SolidBrush(Color.FromArgb(alpha, tint)))
        {
            foreach (var sprite in _sprites)
            {
                if (sprite.Alpha <= 0.01f) continue;
                var side = sprite.Size * unit;
                var x = halfW + sprite.X * unit;
                var y = halfH - sprite.Y * unit;
                G.FillRectangle(brush, (float)(x - side / 2), (float)(y - side / 2), (float)side, (float)side);
            }
        }

        if (_appearance.Badge is { } badge) DrawBadge(badge, alpha);
        DrawCaption(alpha);
    }

    private void DrawBadge(Badge badge, int alpha)
    {
        var anchor = _docked ? StripBadgeAnchor() : OrbBadgeAnchor();
        if (anchor is not { } p) return;

        var tint = Color.FromArgb(alpha, Theme.Tint(badge.Tint));

        if (badge.Count <= 0)
        {
            // Unread-only state renders as a bare dot in the status colour (spec §5.1).
            var r = ScreenMetrics.Pxf(4);
            using var dot = new SolidBrush(tint);
            G.FillEllipse(dot, p.X - r, p.Y - r, r * 2, r * 2);
            return;
        }

        var text = badge.Count > MaxBadgeCount ? $"{MaxBadgeCount}+" : badge.Count.ToString();
        var measured = G.MeasureString(text, _badgeFont);
        var pad = ScreenMetrics.Pxf(4);
        var h = ScreenMetrics.Pxf(15);
        var w = Math.Max(h, measured.Width + pad * 2);
        var rect = new RectangleF(p.X - w / 2, p.Y - h / 2, w, h);

        using (var capsule = Theme.RoundedRect(rect, h / 2))
        using (var fill = new SolidBrush(tint))
        {
            G.FillPath(fill, capsule);
        }

        using (var ink = new SolidBrush(Color.FromArgb(alpha, 12, 16, 26)))
        using (var format = new StringFormat
               {
                   Alignment = StringAlignment.Center,
                   LineAlignment = StringAlignment.Center,
               })
        {
            G.DrawString(text, _badgeFont, ink, rect, format);
        }

        if (!badge.UnreadDot) return;
        var ur = ScreenMetrics.Pxf(3);
        var ux = rect.Right - ur;
        var uy = rect.Top + ur;
        using (var unread = new SolidBrush(Color.FromArgb(alpha, Color.White)))
        using (var ring = new Pen(Color.FromArgb(alpha, Color.Black), ScreenMetrics.Pxf(1)))
        {
            G.FillEllipse(unread, ux - ur, uy - ur, ur * 2, ur * 2);
            G.DrawEllipse(ring, ux - ur, uy - ur, ur * 2, ur * 2);
        }
    }

    /// <summary>
    /// Upper-right of the orb circle at 45°, which is where <see cref="Placement.Hover"/>
    /// also puts the badge — so it does not jump when the hover controls appear.
    /// </summary>
    private PointF OrbBadgeAnchor()
    {
        var radius = ScreenMetrics.Pxf(Placement.OrbRadius);
        var offset = (float)(Math.Cos(Math.PI / 4) * radius);
        return new PointF(Width / 2f + offset, Height / 2f - offset);
    }

    /// <summary>Far end of the collapsed strip (badge-on-strip in <c>AppController.swift</c>).</summary>
    private PointF? StripBadgeAnchor()
    {
        if (_edge is not { } edge) return null;
        var inset = ScreenMetrics.Pxf(11);
        var arm = ScreenMetrics.Pxf(78);
        return edge switch
        {
            DockEdge.Right => new PointF(Width - inset, inset),
            DockEdge.TopRight => new PointF(Width - arm, inset),
            DockEdge.BottomRight => new PointF(Width - arm, Height - inset),
            DockEdge.Left => new PointF(inset, inset),
            DockEdge.TopLeft => new PointF(arm, inset),
            DockEdge.BottomLeft => new PointF(arm, Height - inset),
            DockEdge.Top => new PointF(Width - inset, inset),
            _ => new PointF(Width - inset, Height - inset),
        };
    }

    /// <summary>
    /// The spoken line as a dark pill beside the orb. Stage A only draws it; the
    /// full caption surface (placement rules, motion, per-session styling) is Stage B.
    /// </summary>
    private void DrawCaption(int alpha)
    {
        if (_model.CaptionText is not { Length: > 0 } text) return;

        var height = ScreenMetrics.Pxf(CaptionHeight);
        var gap = ScreenMetrics.Pxf(CaptionGap);
        var x = 0f;
        var y = Height + gap;

        // Docked to the bottom there is no room below the window, so the caption
        // flips above it instead of hanging off the edge of the screen.
        if (Screen.FromControl(this).WorkingArea.Bottom < Top + y + height) y = -height - gap;

        var rect = new RectangleF(x, y, Width, height);
        using (var path = Theme.RoundedRect(rect, ScreenMetrics.Pxf(Theme.Radius)))
        using (var fill = new SolidBrush(Color.FromArgb((int)(alpha * 0.72), Theme.Surface)))
        using (var stroke = new Pen(Color.FromArgb((int)(alpha * 0.35), Theme.Stroke), 1f))
        {
            G.FillPath(fill, path);
            G.DrawPath(stroke, path);
        }

        // ClearType cannot be used on a transparent canvas; AntiAlias is the only
        // hint that produces sane edges here.
        G.TextRenderingHint = TextRenderingHint.AntiAlias;
        using (var brush = new SolidBrush(Color.FromArgb(alpha, Theme.Text)))
        using (var format = new StringFormat
               {
                   Alignment = StringAlignment.Center,
                   LineAlignment = StringAlignment.Center,
                   Trimming = StringTrimming.EllipsisCharacter,
                   FormatFlags = StringFormatFlags.NoWrap,
               })
        {
            var inset = new RectangleF(rect.X + 10, rect.Y, Math.Max(1, rect.Width - 20), rect.Height);
            G.DrawString(text, _captionFont, brush, inset, format);
        }
        G.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    }

    /// <summary>Tint crossfade in sRGB, mirroring <c>OrbMetalView.apply</c>'s interpolation.</summary>
    private static Color Blend(OrbTint from, OrbTint to, double progress)
    {
        var a = Theme.Tint(from);
        var b = Theme.Tint(to);
        if (progress >= 1 || from == to) return b;
        int Mix(int x, int y) => (int)Math.Round(x + (y - x) * progress);
        return Color.FromArgb(Mix(a.R, b.R), Mix(a.G, b.G), Mix(a.B, b.B));
    }

    /// <summary>
    /// Stage A always draws the orb fully opaque. Stage B feeds
    /// <see cref="VisibilityPolicy"/> in here to fade it against other apps.
    /// </summary>
    private double OrbAlpha => 1.0;

    // -------------------------------------------------------------------- pointer

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            ShowContextMenu();
            return;
        }
        if (e.Button != MouseButtons.Left || Now < _suppressTapUntil) return;

        _pressed = true;
        _dragging = false;
        _pressCursor = Cursor.Position;
        _pressOrigin = Location;
        _pressedAt = Now;
        _hovering = true;
        _stemOpen = true;
        _model.SetHovering(true);
        TickDock();
        StartHoverTracking();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_pressed || e.Button != MouseButtons.Left) return;

        var cursor = Cursor.Position;
        var dx = cursor.X - _pressCursor.X;
        var dy = cursor.Y - _pressCursor.Y;

        if (!_dragging)
        {
            // Placement.DragThreshold is in points, so a scaled screen needs more
            // pixels before the same gesture counts as a drag.
            var threshold = ScreenMetrics.Pxf(Placement.DragThreshold);
            if (Math.Sqrt((double)dx * dx + (double)dy * dy) < threshold) return;
            _dragging = true;
            Undock();
            _stemOpen = false;
            // The bar follows the orb rather than closing: dragging is the gesture
            // the user reaches for when the orb is in the way, and losing the bar
            // mid-sentence because the orb moved reads as a crash.
            RaiseTopmost();
        }

        MoveTo(_pressOrigin.X + dx, _pressOrigin.Y + dy);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!_pressed || e.Button != MouseButtons.Left) return;
        _pressed = false;

        if (!_dragging)
        {
            // A tap toggles the bar; a long press is a deliberate close.
            if (_model.QuickBarOpen && Now - _pressedAt > TapMaxSeconds) CloseBarAndStrip();
            else QuickBarRequested?.Invoke();
            return;
        }

        _dragging = false;
        _hovering = false;
        _model.SetHovering(false);
        _stemOpen = _model.QuickBarOpen;
        SnapToEdge();
    }

    /// <summary>
    /// Raised once a drag, a snap or a position restore has finished. The quick bar
    /// and the approval column sit beside the orb, so they have to be told where it
    /// landed; without this a drag would leave them pointing at the old spot.
    /// </summary>
    public event Action? OrbMoved;

    private void CloseBarAndStrip()
    {
        _model.CloseQuickBar();
        _stemOpen = _hovering;
        TickDock();
    }

    /// <summary>
    /// Polls the cursor while the orb is live. A mouse moving over a
    /// non-activating layered window does not generate the message stream a normal
    /// window gets, so <see cref="Control.MouseEnter"/>/<see cref="Control.MouseLeave"/>
    /// cannot be trusted here — <c>AppController</c> polls for the same reason.
    /// </summary>
    private void CheckHover()
    {
        if (!_visible)
        {
            _hoverTimer.Stop();
            return;
        }
        if (_dragging) return;

        var inside = OverLiveArea(Cursor.Position);
        if (inside && !_hovering)
        {
            _hovering = true;
            _hoverLeftAt = -100;
            _stemOpen = true;
            _model.SetHovering(true);
            TickDock();
            MarkTransition();
        }
        else if (!inside && _hovering)
        {
            if (_hoverLeftAt < 0) _hoverLeftAt = Now;
            if (Now - _hoverLeftAt >= CollapseDelay)
            {
                _hoverLeftAt = -100;
                _hovering = false;
                _model.SetHovering(false);
                _stemOpen = _model.QuickBarOpen;
                TickDock();
                MarkTransition();
            }
        }

        if (!_hovering && !_model.QuickBarOpen) _hoverTimer.Stop();
    }

    private void StartHoverTracking()
    {
        if (!_hoverTimer.Enabled) _hoverTimer.Start();
    }

    /// <summary>
    /// Whether the cursor sits on the orb's live area. Transparent pixels pass
    /// clicks through to whatever is underneath, so hover has to be judged the way
    /// the compositor judges it: the orb circle, plus the strip when there is one.
    /// </summary>
    private bool OverLiveArea(Point cursorPx)
    {
        var scale = ScreenMetrics.Scale <= 0 ? 1 : ScreenMetrics.Scale;
        var frame = new RectD(Left / scale, Top / scale, Width / scale, Height / scale);
        var point = new PointD(cursorPx.X / scale, cursorPx.Y / scale);
        var strip = _store.Settings.DockToStrip ? _edge : null;
        return Placement.OverOrb(point, frame, strip, _stemOpen);
    }

    // ------------------------------------------------------------------- docking

    /// <summary>
    /// Drives <see cref="ParticleSim.SetDock"/> from the current edge and stem
    /// state. The sim only rearranges particles when the value actually changes,
    /// which is what makes docking and blooming animate instead of teleport.
    /// </summary>
    private void TickDock()
    {
        var want = _store.Settings.DockToStrip && _edge is not null && !_stemOpen ? _edge : null;
        if (_sim.Dock == want) return;
        _sim.SetDock(want, Now);
        MarkTransition();
    }

    /// <summary>Slides the orb out of the strip; the quick bar needs the full orb beside it.</summary>
    public void Undock()
    {
        if (_stemOpen) return;
        _stemOpen = true;
        TickDock();
    }

    /// <summary>
    /// Clamps into the workspace and snaps flush to an edge when within
    /// <see cref="Placement.SnapDistance"/> of one, persisting the result.
    /// </summary>
    /// <remarks>
    /// Called by the settings window as well as by the drag gesture, so the
    /// clamping, the "<c>DockEdge</c> moved" event and the stem state can never
    /// disagree between the two routes into it.
    /// </remarks>
    public void SnapToEdge()
    {
        var workspace = ScreenMetrics.WorkspaceFor(CenterPixels);
        var frame = new RectD(Left, Top, Width, Height).ToPoints();
        var result = Placement.Snap(frame, workspace, null);

        _snapping = true;
        var origin = result.Origin.ToPixels(ScreenMetrics.Scale);
        MoveTo(origin.X, origin.Y);
        _snapping = false;
        _edge = result.Dock;
        _stemOpen = _model.QuickBarOpen || _hovering;
        TickDock();
        MarkTransition();
        DockEdgeChanged?.Invoke(_edge);
        OrbMoved?.Invoke();
    }

    /// <summary>
    /// Restores the orb to the spot saved for the monitor it last sat on, or to
    /// the top-right of the primary work area on first run.
    /// </summary>
    public void RestoreSavedPosition()
    {
        _edge = null;
        _stemOpen = false;
        _sim.SetDock(null, Now);

        var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        ScreenMetrics.SetScale(ScreenMetrics.ScaleFor(screen.Bounds.Location));

        var saved = _store.Settings.Positions.TryGetValue(screen.DeviceName, out var p) ? p : null;
        // First run parks the orb in the top-right, inset by the orb window's own
        // 10pt margin so the visible circle clears the work-area edge by 16pt.
        var inset = (int)ScreenMetrics.Pxf(Placement.OrbWindow / 2 - Placement.OrbRadius + DefaultEdgeMargin);
        var x = saved is null ? screen.WorkingArea.Right - inset : ScreenMetrics.Px(saved.X);
        var y = saved is null ? screen.WorkingArea.Top + inset : ScreenMetrics.Px(saved.Y);
        MoveTo(x, y);

        SnapToEdge();
        // A saved edge wins over a re-snap: the orb may have been dragged a long way
        // from the edge before it was docked there.
        if (saved?.Dock is { } edge && _store.Settings.DockToStrip)
        {
            _edge = edge;
            _stemOpen = false;
            TickDock();
        }
    }

    // --------------------------------------------------------------- context menu

    /// <summary>
    /// The right-click menu. Windows will not dismiss a popup owned by a
    /// <c>WS_EX_NOACTIVATE</c> window, so the style comes off for the duration and
    /// goes straight back on afterwards — holding focus is the one thing this
    /// window must never do. The previous foreground window is restored too.
    /// </summary>
    private void ShowContextMenu()
    {
        var previous = NativeMethods.GetForegroundWindow();
        var style = NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(Handle, NativeMethods.GWL_EXSTYLE, style & ~NativeMethods.WS_EX_NOACTIVATE);

        // Put the no-activate style back exactly once, whatever happens: leaving it
        // off would let the orb steal focus from whatever the user is typing into.
        var restored = false;
        void Restore()
        {
            if (restored) return;
            restored = true;
            try
            {
                NativeMethods.SetWindowLong(Handle, NativeMethods.GWL_EXSTYLE, style);
                if (previous != IntPtr.Zero) NativeMethods.SetForegroundWindow(previous);
            }
            catch (Exception ex)
            {
                Log.Write("context menu restore failed: " + Log.Describe(ex));
            }
        }

        try
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open quick bar", null, (_, _) => QuickBarRequested?.Invoke());
            menu.Items.Add("Reset position", null, (_, _) => _resetRequested?.Invoke());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit", null, (_, _) => _quitRequested?.Invoke());
            menu.Closed += (_, _) =>
            {
                Restore();
                menu.Dispose();
            };
            menu.Show(Cursor.Position);
        }
        catch (Exception ex)
        {
            Log.Write("context menu failed: " + Log.Describe(ex));
            Restore();
        }
        finally
        {
            // Swallow the left-button release that dismissing the menu produces.
            _suppressTapUntil = Now + 0.25;
        }
    }

    // ---------------------------------------------------------------- lifecycle

    public void HideOrb()
    {
        _visible = false;
        _hovering = false;
        _pressed = false;
        _dragging = false;
        _model.SetHovering(false);
        _hoverTimer.Stop();
        _frameTimer.Stop();
        Hide();
    }

    public void ShowOrb()
    {
        _visible = true;
        _lastDrawAt = -1;
        Show();
        RaiseTopmost();
        MarkTransition();
        _frameTimer.Start();
        StartHoverTracking();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RaiseTopmost();
    }

    /// <summary>
    /// Persists the position the orb actually settled at, once it is really on
    /// screen. The constructor restores a position before anyone has subscribed to
    /// <see cref="DockEdgeChanged"/>, so without this a first run would never write
    /// <c>panel.json</c> and the orb would forget where it was every time.
    /// </summary>
    public void SaveSettledPosition() => DockEdgeChanged?.Invoke(_edge);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Per-monitor DPI is resolved from the window, so the scale is re-read the
        // first time a handle exists and on every monitor change afterwards.
        ScreenMetrics.SetScale(ScreenMetrics.ScaleFor(CenterPixels));
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (_snapping) return;
        var scale = ScreenMetrics.ScaleFor(CenterPixels);
        if (Math.Abs(scale - ScreenMetrics.Scale) > 0.001) ScreenMetrics.SetScale(scale);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _model.Changed -= OnModelChanged;
            _frameTimer.Dispose();
            _hoverTimer.Dispose();
            _badgeFont.Dispose();
            _captionFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
