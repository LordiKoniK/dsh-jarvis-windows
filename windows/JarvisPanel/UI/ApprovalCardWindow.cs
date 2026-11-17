using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using JarvisPanel.App;
using JarvisPanel.Core;

namespace JarvisPanel.UI;

/// <summary>
/// The pending-approval surface: one card per pending approval or question from
/// the host snapshot, stacked beside the orb (macOS <c>PendingCardView</c>, spec §6.2).
/// </summary>
/// <remarks>
/// <para>
/// It deliberately <b>does not</b> activate. An approval usually arrives while the
/// user is typing somewhere else, and a card that takes the foreground eats the
/// next keystroke. <c>WS_EX_NOACTIVATE</c> keeps the buttons clickable while focus
/// stays where it was; the keyboard route to a free-text answer is the quick bar,
/// which this card opens on purpose.
/// </para>
/// <para>
/// Position comes from <see cref="Placement"/>. With the quick bar closed the card
/// takes the orb's readout slot, falling back to the audit-card slot that
/// <see cref="Placement.AutoApprovals"/> reserves when the readout cannot hold it;
/// with the bar open it stacks with the bar on the side the bar grows toward, the
/// way the macOS column does.
/// </para>
/// </remarks>
internal sealed class ApprovalCardWindow : Form
{
    /// <summary>Matches <c>QuickBarView.width</c> in the macOS sources.</summary>
    private const double CardWidth = 320;
    private const double CardGap = 6;
    private const double WindowPadding = 8;

    private readonly PanelModel _model;
    private readonly OrbWindow _orb;
    private readonly ToolTip _tips = new();
    private readonly List<(HudPanel Card, string Id)> _cards = new();
    private readonly HashSet<string> _busy = new();

    private string _signature = "";

    public ApprovalCardWindow(PanelModel model, OrbWindow orb)
    {
        _model = model;
        _orb = orb;

        SuspendLayout();
        Text = "Jarvis approvals";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.SurfaceOpaque;
        ForeColor = Theme.Text;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(ScreenMetrics.Px(CardWidth), ScreenMetrics.Px(80));
        ResumeLayout(true);
    }

    /// <summary>The model opened the bar for a typed answer; the host must show it.</summary>
    public event Action? TextAnswerRequested;

    /// <summary>The user asked for the auto-approval list from the card.</summary>
    public event Action? AutoApprovalsRequested;

    /// <summary>Puts the card back above other topmost windows without activating it.</summary>
    public void Raise()
    {
        if (!IsHandleCreated) return;
        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Rebuilds the card list from the model. <paramref name="quickBarPixels"/> is
    /// the open quick bar's screen bounds, or null when it is closed.
    /// </summary>
    public void SyncToModel(Rectangle? quickBarPixels)
    {
        var items = _model.VisiblePending;
        if (items.Count == 0)
        {
            if (Visible) Hide();
            _signature = "";
            return;
        }

        var signature = Signature(items);
        if (signature != _signature)
        {
            _signature = signature;
            Rebuild(items);
        }

        if (!Visible) Show();
        Place(quickBarPixels);
        Raise();
    }

    /// <summary>Everything the card draws, so a rebuild happens only when it would differ.</summary>
    private string Signature(List<PendingItem> items)
    {
        var parts = new List<string>(items.Count + 2)
        {
            _model.HiddenPendingCount.ToString(),
            _model.VisibleAutoApprovals.Count.ToString(),
        };
        foreach (var item in items)
        {
            parts.Add(string.Join('\u001f', item.Id, item.Kind.ToString(), item.Title, item.Detail, item.Note,
                item.CanAlwaysAllow.ToString(), string.Join('\u001e', item.Choices),
                (_model.AnsweringId == item.Id).ToString(), _busy.Contains(item.Id).ToString()));
        }
        return string.Join('\u001d', parts);
    }

    private void Rebuild(List<PendingItem> items)
    {
        SuspendLayout();
        foreach (var (card, _) in _cards)
        {
            Controls.Remove(card);
            card.Dispose();
        }
        _cards.Clear();

        var pad = ScreenMetrics.Px(WindowPadding);
        var width = ScreenMetrics.Px(CardWidth);
        var inner = width - pad * 2;
        var stack = new Stack(this, pad, pad, inner);

        foreach (var item in items)
        {
            var card = BuildCard(item, inner);
            stack.Add(card, _cards.Count == 0 ? 0 : ScreenMetrics.Px(CardGap), stretch: false);
            _cards.Add((card, item.Id));
        }

        if (_model.HiddenPendingCount > 0)
        {
            stack.Add(Widgets.Text($"+{_model.HiddenPendingCount} more waiting", Widgets.Shared(8f),
                Color.FromArgb(220, Theme.Amber), Theme.SurfaceOpaque, inner, ScreenMetrics.Px(16),
                ContentAlignment.MiddleCenter), ScreenMetrics.Px(CardGap));
        }

        if (_model.VisibleAutoApprovals.Count > 0)
        {
            var link = Widgets.Pill("Auto-approved actions…", Theme.Dim, Theme.PillFill, Theme.PillStroke, 20, 8f);
            link.Click += (_, _) => Run(() => AutoApprovalsRequested?.Invoke());
            _tips.SetToolTip(link, "Show what this workspace is allowed to run without asking");
            stack.Add(link, ScreenMetrics.Px(CardGap + 2), stretch: false);
        }

        ClientSize = new Size(width, stack.Bottom + pad);
        ApplyBusyState();
        ResumeLayout(true);
    }

    private HudPanel BuildCard(PendingItem item, int width)
    {
        var approval = item.Kind == PendingKind.Approval;
        var accent = approval ? Theme.Amber : Theme.Cyan;
        var pad = ScreenMetrics.Px(9);
        var inner = width - pad * 2;

        var card = new HudPanel
        {
            Width = width,
            Fill = Theme.Card,
            Stroke = Color.FromArgb(90, accent),
            Accent = accent,
        };
        var stack = new Stack(card, pad, ScreenMetrics.Px(8), inner);

        BuildHeader(stack, item, approval, accent, inner);
        BuildBody(stack, item, approval, inner);
        if (!string.IsNullOrWhiteSpace(item.Note))
            stack.Add(Widgets.Wrapped(item.Note!, Widgets.Shared(8f), Theme.Dim, Theme.Card, inner, ScreenMetrics.Px(30)),
                ScreenMetrics.Px(5));
        BuildActions(stack, item, approval, inner);
        if (_model.AnsweringId == item.Id)
            stack.Add(Widgets.Text("Answering in the quick bar…", Widgets.Shared(7.5f),
                Color.FromArgb(220, Theme.Amber), Theme.Card, inner, ScreenMetrics.Px(14)), ScreenMetrics.Px(4));

        card.Height = stack.Bottom + ScreenMetrics.Px(8);
        return card;
    }

    private void BuildHeader(Stack stack, PendingItem item, bool approval, Color accent, int width)
    {
        var height = ScreenMetrics.Px(20);
        var header = new Panel { Width = width, Height = height, BackColor = Theme.Card };

        var open = Widgets.Pill("Open", Theme.Dim, Theme.PillFill, Theme.PillStroke, 18, 7.5f);
        open.Location = new Point(Math.Max(0, width - open.Width), 0);
        _tips.SetToolTip(open, "Show this session in DSH");
        open.Click += (_, _) => _ = ActAsync(item, () => _model.OpenInDshAsync(item.Session));
        header.Controls.Add(open);

        var dot = Widgets.Text("●", Widgets.Shared(7f), accent, Theme.Card, ScreenMetrics.Px(12), height,
            ContentAlignment.MiddleLeft);
        dot.Location = new Point(0, 0);
        header.Controls.Add(dot);

        var kind = approval ? "Approval request" : "Question";
        var label = Widgets.Text($"{_model.LabelFor(Target.Session(item.Session))} · {kind}",
            Widgets.Shared(8.5f, FontStyle.Bold), accent, Theme.Card,
            Math.Max(ScreenMetrics.Px(40), width - open.Width - ScreenMetrics.Px(18)), height);
        label.Location = new Point(ScreenMetrics.Px(13), 0);
        header.Controls.Add(label);

        stack.Add(header, 0);
    }

    private void BuildBody(Stack stack, PendingItem item, bool approval, int width)
    {
        if (approval)
        {
            var command = item.Detail?.Trim() ?? "";
            if (command.Length == 0)
            {
                // With no command text the title is the only thing worth showing.
                stack.Add(Widgets.Well(width, (item.Title, Widgets.Shared(8f, mono: true), Theme.Text)),
                    ScreenMetrics.Px(6));
                return;
            }
            if (!string.IsNullOrWhiteSpace(item.Title))
                stack.Add(Widgets.Wrapped(item.Title, Widgets.Shared(8f), Theme.Text, Theme.Card, width, ScreenMetrics.Px(34)),
                    ScreenMetrics.Px(5));
            stack.Add(Widgets.Well(width, (command, Widgets.Shared(8f, mono: true), Theme.Text)), ScreenMetrics.Px(6));
            return;
        }

        var lines = new List<(string Text, Font Font, Color Ink)>
        {
            (item.Title, Widgets.Shared(9f), Theme.Text),
        };
        if (!string.IsNullOrWhiteSpace(item.Detail)) lines.Add((item.Detail!, Widgets.Shared(8f), Theme.Dim));
        stack.Add(Widgets.Well(width, lines.ToArray()), ScreenMetrics.Px(6));
    }

    private void BuildActions(Stack stack, PendingItem item, bool approval, int width)
    {
        var pills = new List<Control>();
        if (approval)
        {
            var allow = Widgets.Primary("Allow", Theme.Amber);
            allow.Click += (_, _) => _ = ActAsync(item, () => _model.DecideAsync(item, true));
            _tips.SetToolTip(allow, "Run this operation once");
            pills.Add(allow);

            var deny = Widgets.Pill("Deny", Theme.Text, Theme.PillFill, Theme.PillStroke);
            deny.Click += (_, _) => _ = ActAsync(item, () => _model.DecideAsync(item, false));
            _tips.SetToolTip(deny, "Refuse this operation");
            pills.Add(deny);

            if (item.CanAlwaysAllow)
            {
                // Offered only when the host says a workspace rule is possible
                // (PendingItem.CanAlwaysAllow), mirroring the macOS card.
                var always = Widgets.Pill("Always allow (this workspace)", Theme.Text, Theme.PillFill, Theme.PillStroke);
                always.Click += (_, _) => _ = ActAsync(item, () => _model.AlwaysAllowAsync(item));
                _tips.SetToolTip(always, "Approve this and matching commands in this workspace from now on");
                pills.Add(always);
            }
        }
        else
        {
            foreach (var choice in item.Choices)
            {
                var value = choice;
                var pill = Widgets.Pill(value, Theme.Text, Theme.PillFill, Theme.PillStroke);
                pill.Click += (_, _) => _ = ActAsync(item, () => _model.ChooseAsync(item, value));
                pills.Add(pill);
            }
            var answer = Widgets.Pill("Type an answer", Theme.Dim, Theme.PillFill, Theme.PillStroke);
            answer.Click += (_, _) => Run(() =>
            {
                _model.BeginTextAnswer(item);
                TextAnswerRequested?.Invoke();
            });
            _tips.SetToolTip(answer, "Answer in the quick bar, where you can type");
            pills.Add(answer);
        }

        stack.Add(Widgets.PillRow(width, pills.ToArray()), ScreenMetrics.Px(7));
    }

    /// <summary>
    /// Runs one answer, keeping the card alive if it fails: the model reports the
    /// error through <see cref="PanelModel.SendError"/> and the next snapshot may
    /// still carry the item.
    /// </summary>
    private async Task ActAsync(PendingItem item, Func<Task> action)
    {
        _busy.Add(item.Id);
        ApplyBusyState();
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            // A rejected answer must not reach the message loop: the orb has to survive it.
            Log.Write("approval action failed: " + Log.Describe(ex));
        }
        finally
        {
            _busy.Remove(item.Id);
            if (!IsDisposed) ApplyBusyState();
        }
    }

    private void ApplyBusyState()
    {
        foreach (var (card, id) in _cards) card.Enabled = !_busy.Contains(id);
    }

    private void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Write("approval card action failed: " + Log.Describe(ex));
        }
    }

    /// <summary>
    /// Places the column either in the orb's readout slot or directly above/below
    /// the open quick bar, then clamps it into the monitor's work area.
    /// </summary>
    private void Place(Rectangle? quickBarPixels)
    {
        var workspace = ScreenMetrics.WorkspaceFor(_orb.CenterPixels);
        var scale = ScreenMetrics.Scale <= 0 ? 1 : ScreenMetrics.Scale;
        var centre = new PointD(_orb.CenterPixels.X / scale, _orb.CenterPixels.Y / scale);
        var size = new SizeD(ClientSize.Width / scale, ClientSize.Height / scale);
        var layout = Placement.QuickBar(centre, workspace.Bounds, size);

        Point location;
        if (quickBarPixels is { } bar)
        {
            // Same column as the bar, on the side the column grows toward.
            var gap = ScreenMetrics.Px(CardGap);
            location = new Point(bar.Left, layout.GrowsUp ? bar.Top - gap - ClientSize.Height : bar.Bottom + gap);
        }
        else
        {
            var rect = ReadoutSlot(centre, workspace.Bounds, size).ClampOrigin(workspace.Bounds);
            location = new Point((int)Math.Round(rect.X * scale), (int)Math.Round(rect.Y * scale));
        }

        // Stacking with the bar can push a tall card past the screen edge.
        var screen = Screen.FromPoint(_orb.CenterPixels);
        location = Widgets.ClampToWorkspace(location, ClientSize, screen.WorkingArea);
        if (Location != location) Location = location;
    }

    /// <summary>The orb's readout slot, or the audit-card slot when that cannot hold a card.</summary>
    private RectD ReadoutSlot(PointD centre, RectD visible, SizeD size)
    {
        var hover = Placement.Hover(centre, visible, _model.HoverButtons.Count, size, size);
        if (hover.Readout.Kind != ReadoutKind.None) return hover.Readout.Rect;
        return Placement.AutoApprovals(centre, visible, hover, Math.Max(1, _cards.Count));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.StrokeRounded(e.Graphics, new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f),
            ScreenMetrics.Pxf(10), Color.FromArgb(120, Theme.Amber), 1f);
    }

    /// <summary>
    /// Keeps the card clickable without ever taking focus: it is a tool window (no
    /// Alt+Tab entry, no taskbar button) that refuses activation.
    /// </summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    /// <summary>Closing would dispose a window the model still drives; hide instead.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }
}
