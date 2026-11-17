using System.Drawing;
using System.Windows.Forms;
using JarvisPanel.App;
using JarvisPanel.Core;

namespace JarvisPanel.UI;

/// <summary>
/// The auto-approval audit list (macOS <c>AutoApprovalsView</c>): what the host
/// approved without asking, and the saved workspace rules behind it.
/// </summary>
/// <remarks>
/// This is a normal activating window: the user opens it from the tray on purpose,
/// so it takes focus and can be read at leisure. Revoking a rule only stops future
/// approvals — the operations that already ran are the host's history, not ours.
/// </remarks>
internal sealed class AutoApprovalsWindow : Form
{
    private const double WindowWidth = 400;
    private const double WindowHeight = 460;
    private const double Pad = 10;
    private const double FooterHeight = 62;

    private readonly PanelModel _model;
    private readonly OrbWindow _orb;
    private readonly Panel _list;
    private readonly Label _header;
    private readonly Label _footer;
    private readonly Label _error;
    private readonly PillButton _refresh;
    private readonly ToolTip _tips = new();
    private readonly List<Control> _rows = new();

    private string _signature = "";

    public AutoApprovalsWindow(PanelModel model, OrbWindow orb)
    {
        _model = model;
        _orb = orb;

        SuspendLayout();
        Text = "Jarvis — auto-approved actions";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.SurfaceOpaque;
        ForeColor = Theme.Text;
        AutoScaleMode = AutoScaleMode.None;
        MinimizeBox = false;
        MaximizeBox = false;
        KeyPreview = true;

        var width = ScreenMetrics.Px(WindowWidth);
        var height = ScreenMetrics.Px(WindowHeight);
        var pad = ScreenMetrics.Px(Pad);
        var inner = width - pad * 2;

        _header = Widgets.Text("", Widgets.Shared(9f, FontStyle.Bold), Theme.Text, Theme.SurfaceOpaque, inner,
            ScreenMetrics.Px(18));
        _header.Location = new Point(pad, pad);

        _error = Widgets.Wrapped("", Widgets.Shared(8f), Theme.Red, Theme.SurfaceOpaque, inner, ScreenMetrics.Px(28));
        _error.Location = new Point(pad, _header.Bottom);
        _error.Visible = false;

        var listTop = _header.Bottom + ScreenMetrics.Px(18);
        _list = new Panel
        {
            Location = new Point(pad, listTop),
            Size = new Size(inner, height - listTop - ScreenMetrics.Px(FooterHeight) - pad),
            BackColor = Theme.SurfaceOpaque,
            AutoScroll = true,
        };

        _footer = Widgets.Wrapped(
            "Revoking only affects future approvals; operations that have already run are not undone.",
            Widgets.Shared(7.5f), Theme.Dim, Theme.SurfaceOpaque, inner, ScreenMetrics.Px(28));
        _footer.Location = new Point(pad, _list.Bottom + ScreenMetrics.Px(6));

        _refresh = Widgets.Pill("Refresh", Theme.Text, Theme.PillFill, Theme.PillStroke, 20, 8f);
        _refresh.Location = new Point(pad, _footer.Bottom + ScreenMetrics.Px(4));
        _refresh.Click += (_, _) => _ = ActAsync(() => _model.RefreshAsync());

        var close = Widgets.Pill("Close", Theme.Dim, Theme.PillFill, Theme.PillStroke, 20, 8f);
        close.Location = new Point(pad + _refresh.Width + ScreenMetrics.Px(6), _footer.Bottom + ScreenMetrics.Px(4));
        close.Click += (_, _) => Hide();

        Controls.Add(_header);
        Controls.Add(_error);
        Controls.Add(_list);
        Controls.Add(_footer);
        Controls.Add(_refresh);
        Controls.Add(close);
        ClientSize = new Size(width, height);

        _model.Changed += OnModelChanged;
        ResumeLayout(true);
    }

    /// <summary>Rebuilds the list from the model; the window's visibility is the caller's choice.</summary>
    public void SyncToModel()
    {
        var entries = _model.VisibleAutoApprovals;
        var signature = Signature(entries);
        if (signature != _signature)
        {
            _signature = signature;
            Rebuild(entries);
        }

        _error.Visible = _model.AutoApprovalError is { Length: > 0 };
        if (_error.Visible) _error.Text = _model.AutoApprovalError;
        _refresh.Enabled = _model.RevokingAutoApproval is null;
    }

    /// <summary>Brings the window up on the orb's monitor, centred, without disturbing a hidden orb.</summary>
    public void ShowOnOrbMonitor()
    {
        var screen = Screen.FromPoint(_orb.CenterPixels);
        var area = screen.WorkingArea;
        ClientSize = new Size(Math.Min(ClientSize.Width, area.Width), Math.Min(ClientSize.Height, area.Height));
        Location = Widgets.ClampToWorkspace(
            new Point(area.X + (area.Width - Width) / 2, area.Y + (area.Height - Height) / 2),
            Size, area);
        SyncToModel();
        Show();
        Activate();
    }

    private string Signature(List<AutoApproval> entries)
    {
        var parts = new List<string>(entries.Count + 2)
        {
            (_model.AutoApprovalError ?? "").Length.ToString(),
            (_model.RevokingAutoApproval is null).ToString(),
        };
        foreach (var entry in entries)
        {
            parts.Add(string.Join('\u001f', entry.Id, entry.Title, entry.Tool, entry.Command, entry.Tier.ToString(),
                entry.Rule is null ? "-" : entry.Rule.Fingerprint, entry.At.ToString("F0")));
        }
        return string.Join('\u001d', parts);
    }

    private void Rebuild(List<AutoApproval> entries)
    {
        foreach (var row in _rows)
        {
            _list.Controls.Remove(row);
            row.Dispose();
        }
        _rows.Clear();

        var width = ListWidth();
        var stack = new Stack(_list, 0, 0, width);

        if (entries.Count == 0)
        {
            _header.Text = "Auto-approved actions";
            stack.Add(Widgets.Text("Nothing has been approved automatically in the current snapshot.",
                Widgets.Shared(8.5f), Theme.Faint, Theme.SurfaceOpaque, width, ScreenMetrics.Px(20)), ScreenMetrics.Px(8));
            return;
        }

        _header.Text = $"Auto-approved actions · last {entries.Count}";
        foreach (var entry in entries)
        {
            var row = BuildRow(entry, width);
            stack.Add(row, _rows.Count == 0 ? 0 : ScreenMetrics.Px(6), stretch: false);
            _rows.Add(row);
        }
    }

    /// <summary>The scrollable width, minus the bar that appears once the rows overflow.</summary>
    private int ListWidth()
    {
        var width = _list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth;
        return Math.Max(ScreenMetrics.Px(120), width - ScreenMetrics.Px(2));
    }

    private HudPanel BuildRow(AutoApproval entry, int width)
    {
        var tier = TierColor(entry.Tier);
        var pad = ScreenMetrics.Px(9);
        var inner = width - pad * 2;
        var row = new HudPanel
        {
            Width = width,
            Fill = Theme.Card,
            Stroke = Color.FromArgb(40, Theme.Stroke),
            Accent = tier,
        };
        var stack = new Stack(row, pad, ScreenMetrics.Px(7), inner);

        var tierLabel = Widgets.Text(entry.Tier.Label(), Widgets.Shared(8f), tier, Theme.Card, ScreenMetrics.Px(90),
            ScreenMetrics.Px(16), ContentAlignment.MiddleRight);
        var title = string.IsNullOrWhiteSpace(entry.Title) ? entry.Tool : entry.Title;
        var head = new Panel { Width = inner, Height = ScreenMetrics.Px(16), BackColor = Theme.Card };
        tierLabel.Location = new Point(Math.Max(0, inner - tierLabel.Width), 0);
        head.Controls.Add(tierLabel);
        var titleLabel = Widgets.Text(title, Widgets.Shared(8.5f, FontStyle.Bold), Theme.Text, Theme.Card,
            Math.Max(ScreenMetrics.Px(40), inner - tierLabel.Width - ScreenMetrics.Px(6)), ScreenMetrics.Px(16));
        head.Controls.Add(titleLabel);
        stack.Add(head, 0);

        var command = string.IsNullOrWhiteSpace(entry.Command) ? entry.Tool : entry.Command;
        var commandLabel = Widgets.Text(command, Widgets.Shared(7.5f, mono: true), Theme.Dim, Theme.Card, inner,
            ScreenMetrics.Px(14));
        _tips.SetToolTip(commandLabel, $"{entry.Tool} · {command}");
        stack.Add(commandLabel, ScreenMetrics.Px(2));

        if (entry.Rule is { } rule)
        {
            var revoke = Widgets.Pill("Revoke rule", Theme.Cyan, Theme.PillFill, Theme.PillStroke, 19, 7.5f);
            revoke.Enabled = _model.RevokingAutoApproval is null;
            _tips.SetToolTip(revoke, $"Stop auto-approving {entry.Tool} in {rule.Workspace} from now on");
            revoke.Click += (_, _) => _ = ActAsync(() => _model.RevokeAutoApprovalAsync(entry));
            stack.Add(revoke, ScreenMetrics.Px(5), stretch: false);

            var workspace = Widgets.Text(_model.RevokingAutoApproval == rule
                    ? "Revoking…"
                    : $"{rule.Workspace} · {rule.Tool}",
                Widgets.Shared(7.5f), Theme.Faint, Theme.Card, inner, ScreenMetrics.Px(14));
            stack.Add(workspace, ScreenMetrics.Px(3));
        }
        else
        {
            stack.Add(Widgets.Text("No revocable preset — this entry is a record only.", Widgets.Shared(7.5f),
                Theme.Faint, Theme.Card, inner, ScreenMetrics.Px(14)), ScreenMetrics.Px(5));
        }

        if (entry.At > 0)
        {
            var when = DateTimeOffset.FromUnixTimeMilliseconds((long)entry.At).LocalDateTime;
            stack.Add(Widgets.Text(when.ToString("yyyy-MM-dd HH:mm"), Widgets.Shared(7f), Theme.Faint, Theme.Card,
                inner, ScreenMetrics.Px(12)), ScreenMetrics.Px(2));
        }

        row.Height = stack.Bottom + ScreenMetrics.Px(7);
        return row;
    }

    private static Color TierColor(AutoApprovalTier tier) => tier switch
    {
        AutoApprovalTier.Safe => Theme.Cyan,
        AutoApprovalTier.Grey => Theme.Dim,
        _ => Theme.Amber,
    };

    private async Task ActAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log.Write("auto-approval action failed: " + Log.Describe(ex));
        }
        if (!IsDisposed) SyncToModel();
    }

    private void OnModelChanged()
    {
        try
        {
            if (Visible) SyncToModel();
            else _signature = "";
        }
        catch (Exception ex)
        {
            Log.Write("auto-approval sync failed: " + Log.Describe(ex));
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            Hide();
            return;
        }
        base.OnKeyDown(e);
    }

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
        if (disposing)
        {
            _model.Changed -= OnModelChanged;
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }
}
