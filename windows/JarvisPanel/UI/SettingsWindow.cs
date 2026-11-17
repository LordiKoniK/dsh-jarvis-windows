using System.Drawing;
using System.Windows.Forms;
using JarvisPanel.App;
using JarvisPanel.Core;

namespace JarvisPanel.UI;

/// <summary>Where the orb is, in absolute screen points, and the edge it is docked to.</summary>
internal readonly record struct OrbPlacement(double X, double Y, DockEdge? Edge, string DeviceName);

/// <summary>
/// What the settings window needs from the app: it owns the settings file, but the
/// orb, the hotkey and the tray icon live elsewhere. Narrow on purpose â€” the window
/// must not be able to reach the whole panel.
/// </summary>
internal interface ISettingsHost
{
    OrbPlacement OrbPlacement { get; }

    /// <summary>Moves the orb to absolute screen points and returns where it actually landed.</summary>
    OrbPlacement ApplyOrbPlacement(double x, double y, DockEdge? edge);

    /// <summary>Registers a shortcut, replacing the current one; false when Windows refuses it.</summary>
    bool TryApplyHotKey(HotKeySpec spec);

    void ResetPosition();

    /// <summary>Settings that the UI reads at draw time need the orb and tray refreshed.</summary>
    void SettingsApplied();
}

/// <summary>
/// The settings window (macOS <c>SettingsView</c>, spec Â§7): every <see cref="PanelSettings"/>
/// field that means something on Windows, edited through <see cref="SettingsStore"/>
/// so a change lands in <c>panel.json</c> immediately.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the orb surfaces this window is a real dialog: it activates, appears in
/// the taskbar and is reopened rather than recreated, because settings are
/// something the user comes back to. It is only ever opened by an explicit user
/// action (the tray menu), so taking focus is correct here.
/// </para>
/// <para>
/// The hotkey field accepts the Windows spelling ("Ctrl+Alt+J") or the macOS glyph
/// spelling, because <see cref="HotKeySpec.TryParse"/> accepts both and the panel
/// shares one <c>panel.json</c> shape with the macOS build.
/// </para>
/// </remarks>
internal sealed class SettingsWindow : Form
{
    private const double WindowWidth = 460;
    private const double WindowHeight = 660;
    private const double Pad = 12;
    private const double LabelWidth = 168;

    private const string HotKeyHint =
        "Use modifiers plus a letter, digit or function key â€” for example Ctrl+Alt+J.";

    private static readonly (string Label, DockEdge? Edge)[] Edges =
    {
        ("Free (remembered spot)", null),
        ("Left", DockEdge.Left),
        ("Right", DockEdge.Right),
        ("Top", DockEdge.Top),
        ("Bottom", DockEdge.Bottom),
        ("Top left", DockEdge.TopLeft),
        ("Top right", DockEdge.TopRight),
        ("Bottom left", DockEdge.BottomLeft),
        ("Bottom right", DockEdge.BottomRight),
    };

    private readonly SettingsStore _store;
    private readonly ISettingsHost _host;
    private readonly Panel _content;
    private readonly TextBox _hotKey = new();
    private readonly Label _hotKeyNote = new();
    private readonly Dictionary<ParticleTier, RadioButton> _tiers = new();
    private readonly CheckBox _followMotion = new();
    private readonly CheckBox _dockStrip = new();
    private readonly CheckBox _captions = new();
    private readonly TrackBar _dshOpacity = new();
    private readonly TrackBar _otherOpacity = new();
    private readonly Label _dshValue = new();
    private readonly Label _otherValue = new();
    private readonly CheckBox _proximity = new();
    private readonly CheckBox _prominent = new();
    private readonly CheckBox _hideFullscreen = new();
    private readonly CheckBox _hideLauncher = new();
    private readonly ComboBox _edge = new();
    private readonly NumericUpDown _x = new();
    private readonly NumericUpDown _y = new();
    private readonly Label _where = new();

    /// <summary>Set while controls are being filled from the store, so nothing is written back.</summary>
    private bool _loading;

    public SettingsWindow(SettingsStore store, ISettingsHost host)
    {
        _store = store;
        _host = host;

        SuspendLayout();
        Text = "Jarvis panel settings";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.SurfaceOpaque;
        ForeColor = Theme.Text;
        AutoScaleMode = AutoScaleMode.None;
        MinimizeBox = false;
        MaximizeBox = false;
        KeyPreview = true;

        var width = ScreenMetrics.Px(WindowWidth);
        var height = ScreenMetrics.Px(WindowHeight);
        ClientSize = new Size(width, height);

        _content = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(width, height - ScreenMetrics.Px(44)),
            BackColor = Theme.SurfaceOpaque,
            AutoScroll = true,
        };
        Controls.Add(_content);
        // The scrollable width is only settled once the panel has its size, and the
        // rows are measured from it, so the layout follows the first pass.
        ResumeLayout(true);

        BuildContent();

        var close = Widgets.Pill("Close", Theme.Text, Theme.PillFill, Theme.PillStroke, 22, 8.5f);
        close.Location = new Point(width - ScreenMetrics.Px(Pad) - close.Width, height - ScreenMetrics.Px(38));
        close.Click += (_, _) => Hide();
        Controls.Add(close);
        CancelButton = close;

        Reload();
    }

    /// <summary>Refills every control from <c>panel.json</c>; called whenever the window is reopened.</summary>
    public void Reload()
    {
        _loading = true;
        try
        {
            var s = _store.Settings;
            _hotKey.Text = HotKeySpec.TryParse(s.HotKey)?.DisplayString ?? HotKeySpec.Default.DisplayString;
            Note(HotKeyHint, Theme.Faint);

            foreach (var (tier, radio) in _tiers) radio.Checked = tier == s.Tier;
            _followMotion.Checked = s.FollowReduceMotion;
            _dockStrip.Checked = s.DockToStrip;
            _captions.Checked = s.ShowCaptions;

            _dshOpacity.Value = Percent(s.DshOpacity);
            _otherOpacity.Value = Percent(s.OtherOpacity);
            _dshValue.Text = $"{_dshOpacity.Value}%";
            _otherValue.Text = $"{_otherOpacity.Value}%";

            _proximity.Checked = s.ProximityFade;
            _prominent.Checked = s.KeepProminent;
            _hideFullscreen.Checked = s.HideInFullscreen;
            _hideLauncher.Checked = s.HideInAppLauncher;

            var placement = _host.OrbPlacement;
            _edge.SelectedIndex = EdgeIndex(placement.Edge);
            _x.Value = ClampPoints(placement.X);
            _y.Value = ClampPoints(placement.Y);
            RefreshWhere();
        }
        catch (Exception ex)
        {
            // A window that cannot fill itself is still better than a dead panel.
            Log.Write("settings reload failed: " + Log.Describe(ex));
        }
        finally
        {
            _loading = false;
        }
    }

    // ------------------------------------------------------------------- layout

    private void BuildContent()
    {
        var pad = ScreenMetrics.Px(Pad);
        var width = _content.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - pad * 2;
        var stack = new Stack(_content, pad, pad, width);

        stack.Add(Widgets.Text("Jarvis panel", Widgets.Shared(12f, FontStyle.Bold), Theme.Text,
            Theme.SurfaceOpaque, width, ScreenMetrics.Px(24)), 0);
        stack.Add(Widgets.Wrapped(
            "Changes apply immediately and are saved to panel.json. The orb reads the file; the macOS panel shares its shape.",
            Widgets.Shared(8f), Theme.Dim, Theme.SurfaceOpaque, width, ScreenMetrics.Px(28)), ScreenMetrics.Px(2));
        stack.Add(Widgets.Text(_store.Url, Widgets.Shared(7.5f, mono: true), Theme.Faint, Theme.SurfaceOpaque, width,
            ScreenMetrics.Px(14)), ScreenMetrics.Px(2));

        AddHotKeySection(stack);
        AddMotionSection(stack);
        AddPositionSection(stack);
        AddAppearanceSection(stack);
        AddVisibilitySection(stack);
    }

    private void AddHotKeySection(Stack root)
    {
        Section(root, "Keyboard", stack =>
        {
            var field = FieldWidth(stack);
            var row = new Panel { Width = field, Height = ScreenMetrics.Px(24), BackColor = Theme.Card };
            var apply = Widgets.Pill("Apply", Theme.Text, Theme.PillFill, Theme.PillStroke, 22, 8f);
            apply.Location = new Point(field - apply.Width, ScreenMetrics.Px(1));
            apply.Click += (_, _) => ApplyHotKey();
            _hotKey.Location = new Point(0, ScreenMetrics.Px(2));
            _hotKey.Width = field - apply.Width - ScreenMetrics.Px(6);
            _hotKey.BorderStyle = BorderStyle.FixedSingle;
            _hotKey.Font = Widgets.Shared(9f, mono: true);
            _hotKey.BackColor = Theme.Well;
            _hotKey.ForeColor = Theme.Text;
            _hotKey.KeyDown += (_, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = true;
                e.SuppressKeyPress = true;
                ApplyHotKey();
            };
            row.Controls.Add(_hotKey);
            row.Controls.Add(apply);
            AddRow(stack, "Quick bar hotkey", row);

            _hotKeyNote.Font = Widgets.Shared(7.5f);
            _hotKeyNote.ForeColor = Theme.Faint;
            _hotKeyNote.BackColor = Theme.Card;
            _hotKeyNote.TextAlign = ContentAlignment.TopLeft;
            _hotKeyNote.AutoSize = false;
            _hotKeyNote.Width = stack.Width;
            _hotKeyNote.Height = ScreenMetrics.Px(26);
            stack.Add(_hotKeyNote, ScreenMetrics.Px(3));
        });
    }

    private void AddMotionSection(Stack root)
    {
        Section(root, "Motion", stack =>
        {
            var field = FieldWidth(stack);
            var tiers = new Panel { Width = field, Height = ScreenMetrics.Px(22), BackColor = Theme.Card };
            var x = 0;
            foreach (var tier in new[] { ParticleTier.Minimal, ParticleTier.Lite, ParticleTier.Standard })
            {
                var radio = new RadioButton
                {
                    Text = tier.Label(),
                    Font = Widgets.Shared(8.5f),
                    ForeColor = Theme.Text,
                    BackColor = Theme.Card,
                    AutoSize = true,
                    Location = new Point(x, ScreenMetrics.Px(3)),
                    TabStop = false,
                };
                var value = tier;
                radio.CheckedChanged += (_, _) =>
                {
                    if (_loading || !radio.Checked) return;
                    Apply(s => s.Tier = value);
                };
                tiers.Controls.Add(radio);
                _tiers[tier] = radio;
                x += TextRenderer.MeasureText(radio.Text, radio.Font).Width + ScreenMetrics.Px(18);
            }
            AddRow(stack, "Particle tier", tiers);

            Toggle(stack, _followMotion, "Follow the Windows reduced-motion setting", s => s.FollowReduceMotion = _followMotion.Checked,
                "When Windows is set to show fewer animations, the orb holds its particles still.");
            Toggle(stack, _dockStrip, "Collapse into an edge strip when docked", s => s.DockToStrip = _dockStrip.Checked);
            Toggle(stack, _captions, "Show spoken captions beside the orb", s => s.ShowCaptions = _captions.Checked);
        });
    }

    private void AddPositionSection(Stack root)
    {
        Section(root, "Position", stack =>
        {
            var field = FieldWidth(stack);
            foreach (var (label, _) in Edges) _edge.Items.Add(label);
            _edge.DropDownStyle = ComboBoxStyle.DropDownList;
            _edge.FlatStyle = FlatStyle.Flat;
            _edge.Font = Widgets.Shared(8.5f);
            _edge.BackColor = Theme.Well;
            _edge.ForeColor = Theme.Text;
            _edge.SelectedIndexChanged += (_, _) =>
            {
                if (_loading || _edge.SelectedIndex < 0) return;
                ApplyPlacement();
            };
            AddRow(stack, "Docked edge", _edge);

            var pair = new Panel { Width = field, Height = ScreenMetrics.Px(24), BackColor = Theme.Card };
            Configure(_x);
            Configure(_y);
            _x.Location = new Point(0, ScreenMetrics.Px(1));
            _y.Location = new Point(ScreenMetrics.Px(88), ScreenMetrics.Px(1));
            var xLabel = Widgets.Text("X", Widgets.Shared(8f), Theme.Dim, Theme.Card, ScreenMetrics.Px(12),
                ScreenMetrics.Px(22), ContentAlignment.MiddleCenter);
            xLabel.Location = new Point(ScreenMetrics.Px(76), ScreenMetrics.Px(1));
            var yLabel = Widgets.Text("Y", Widgets.Shared(8f), Theme.Dim, Theme.Card, ScreenMetrics.Px(12),
                ScreenMetrics.Px(22), ContentAlignment.MiddleCenter);
            yLabel.Location = new Point(ScreenMetrics.Px(164), ScreenMetrics.Px(1));
            pair.Controls.Add(_x);
            pair.Controls.Add(xLabel);
            pair.Controls.Add(_y);
            pair.Controls.Add(yLabel);
            AddRow(stack, "Position (screen points)", pair);

            var buttons = new Panel { Width = field, Height = ScreenMetrics.Px(24), BackColor = Theme.Card };
            var move = Widgets.Pill("Move orb here", Theme.Text, Theme.PillFill, Theme.PillStroke, 22, 8f);
            move.Location = new Point(0, ScreenMetrics.Px(1));
            move.Click += (_, _) => ApplyPlacement();
            var reset = Widgets.Pill("Reset position", Theme.Dim, Theme.PillFill, Theme.PillStroke, 22, 8f);
            reset.Location = new Point(move.Width + ScreenMetrics.Px(6), ScreenMetrics.Px(1));
            reset.Click += (_, _) => Run(() =>
            {
                _host.ResetPosition();
                Reload();
            });
            var useCurrent = Widgets.Pill("Use current", Theme.Dim, Theme.PillFill, Theme.PillStroke, 22, 8f);
            useCurrent.Location = new Point(move.Width + reset.Width + ScreenMetrics.Px(12), ScreenMetrics.Px(1));
            useCurrent.Click += (_, _) => Run(() =>
            {
                var placement = _host.OrbPlacement;
                _x.Value = ClampPoints(placement.X);
                _y.Value = ClampPoints(placement.Y);
            });
            buttons.Controls.Add(move);
            buttons.Controls.Add(reset);
            buttons.Controls.Add(useCurrent);
            AddRow(stack, "Move the orb", buttons);

            stack.Add(_where, ScreenMetrics.Px(4));
            _where.Font = Widgets.Shared(7.5f);
            _where.ForeColor = Theme.Faint;
            _where.BackColor = Theme.Card;
            _where.AutoSize = false;
            _where.Width = stack.Width;
            _where.Height = ScreenMetrics.Px(14);
        });
    }

    private void AddAppearanceSection(Stack root)
    {
        Section(root, "Appearance", stack =>
        {
            OpacityRow(stack, "Opacity, DSH in front", _dshOpacity, _dshValue, s => s.DshOpacity = _dshOpacity.Value / 100.0);
            OpacityRow(stack, "Opacity, other apps in front", _otherOpacity, _otherValue,
                s => s.OtherOpacity = _otherOpacity.Value / 100.0);
            Toggle(stack, _proximity, "Reveal when the pointer comes near", s => s.ProximityFade = _proximity.Checked);
            Toggle(stack, _prominent, "Stay prominent while something needs attention",
                s => s.KeepProminent = _prominent.Checked);
            NoteRow(stack,
                "Opacity and proximity are saved for the macOS panel; this Windows orb currently draws fully opaque.");
        });
    }

    private void AddVisibilitySection(Stack root)
    {
        Section(root, "Visibility", stack =>
        {
            Toggle(stack, _hideFullscreen, "Hide while a full-screen app is in front",
                s => s.HideInFullscreen = _hideFullscreen.Checked);
            Toggle(stack, _hideLauncher, "Hide while the app launcher is open",
                s => s.HideInAppLauncher = _hideLauncher.Checked);
            NoteRow(stack,
                "Full-screen and launcher hiding come from the macOS policy; on Windows the orb is hidden from the tray menu.");
        });
    }

    /// <summary>A titled card; the section is measured once its rows are in place.</summary>
    private void Section(Stack root, string title, Action<Stack> rows)
    {
        var pad = ScreenMetrics.Px(10);
        var card = new HudPanel
        {
            Width = root.Width,
            Fill = Theme.Card,
            Stroke = Color.FromArgb(40, Theme.Stroke),
        };
        var stack = new Stack(card, pad, pad, root.Width - pad * 2);
        stack.Add(Widgets.Text(title, Widgets.Shared(9f, FontStyle.Bold), Theme.Cyan, Theme.Card, stack.Width,
            ScreenMetrics.Px(18)), 0);
        rows(stack);
        card.Height = stack.Bottom + pad;
        root.Add(card, ScreenMetrics.Px(10), stretch: false);
    }

    private void AddRow(Stack stack, string label, Control field, string? hint = null)
    {
        var labelWidth = ScreenMetrics.Px(LabelWidth);
        var rowHeight = Math.Max(ScreenMetrics.Px(24), field.Height);
        var hintHeight = hint is null ? 0 : Widgets.MeasureWrapped(hint, Widgets.Shared(7.5f), stack.Width - labelWidth);
        var row = new Panel
        {
            Width = stack.Width,
            Height = rowHeight + (hint is null ? 0 : hintHeight + ScreenMetrics.Px(3)),
            BackColor = Theme.Card,
        };

        var caption = Widgets.Text(label, Widgets.Shared(8.5f), Theme.Dim, Theme.Card, labelWidth, rowHeight);
        caption.Location = new Point(0, 0);
        row.Controls.Add(caption);

        field.Location = new Point(labelWidth + ScreenMetrics.Px(8), Math.Max(0, (rowHeight - field.Height) / 2));
        if (field is not Panel) field.Width = FieldWidth(stack);
        row.Controls.Add(field);

        if (hint is not null)
        {
            var note = Widgets.Wrapped(hint, Widgets.Shared(7.5f), Theme.Faint, Theme.Card, FieldWidth(stack), hintHeight);
            note.Location = new Point(labelWidth + ScreenMetrics.Px(8), rowHeight);
            row.Controls.Add(note);
        }
        stack.Add(row, ScreenMetrics.Px(6));
    }

    private int FieldWidth(Stack stack)
        => Math.Max(ScreenMetrics.Px(80), stack.Width - ScreenMetrics.Px(LabelWidth) - ScreenMetrics.Px(8));

    private void Toggle(Stack stack, CheckBox box, string label, Action<PanelSettings> change, string? hint = null)
    {
        box.Text = label;
        box.Font = Widgets.Shared(8.5f);
        box.ForeColor = Theme.Text;
        box.BackColor = Theme.Card;
        box.FlatStyle = FlatStyle.Flat;
        box.AutoSize = false;
        box.Height = ScreenMetrics.Px(20);
        var row = new Panel { Width = stack.Width, Height = ScreenMetrics.Px(24), BackColor = Theme.Card };
        box.Location = new Point(0, ScreenMetrics.Px(2));
        box.Width = stack.Width;
        box.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            Apply(change);
        };
        row.Controls.Add(box);
        stack.Add(row, ScreenMetrics.Px(4));
        if (hint is not null) NoteRow(stack, hint);
    }

    private void OpacityRow(Stack stack, string label, TrackBar bar, Label value, Action<PanelSettings> change)
    {
        bar.Minimum = (int)Math.Round(PanelSettings.OpacityMin * 100);
        bar.Maximum = (int)Math.Round(PanelSettings.OpacityMax * 100);
        bar.TickStyle = TickStyle.None;
        bar.SmallChange = 5;
        bar.LargeChange = 10;
        bar.BackColor = Theme.Card;
        bar.AutoSize = false;
        bar.Height = ScreenMetrics.Px(22);
        value.Font = Widgets.Shared(8f);
        value.ForeColor = Theme.Text;
        value.BackColor = Theme.Card;
        value.TextAlign = ContentAlignment.MiddleRight;
        value.AutoSize = false;
        value.Width = ScreenMetrics.Px(38);

        var holder = new Panel { Width = FieldWidth(stack), Height = ScreenMetrics.Px(24), BackColor = Theme.Card };
        bar.Location = new Point(0, ScreenMetrics.Px(1));
        bar.Width = holder.Width - value.Width - ScreenMetrics.Px(6);
        value.Location = new Point(holder.Width - value.Width, ScreenMetrics.Px(1));
        value.Height = ScreenMetrics.Px(22);
        bar.ValueChanged += (_, _) =>
        {
            value.Text = $"{bar.Value}%";
            if (_loading) return;
            Apply(change);
        };
        holder.Controls.Add(bar);
        holder.Controls.Add(value);
        AddRow(stack, label, holder);
    }

    /// <summary>A dim paragraph inside the current section.</summary>
    private void NoteRow(Stack stack, string text)
    {
        var height = Widgets.MeasureWrapped(text, Widgets.Shared(7.5f), stack.Width);
        stack.Add(Widgets.Wrapped(text, Widgets.Shared(7.5f), Theme.Faint, Theme.Card, stack.Width, height),
            ScreenMetrics.Px(4));
    }

    // ------------------------------------------------------------------ behaviour

    /// <summary>Moves the orb to the X/Y fields and the chosen edge, then reports where it landed.</summary>
    private void ApplyPlacement()
    {
        Run(() =>
        {
            var edge = _edge.SelectedIndex < 0 ? null : Edges[_edge.SelectedIndex].Edge;
            var landed = _host.ApplyOrbPlacement((double)_x.Value, (double)_y.Value, edge);
            _x.Value = ClampPoints(landed.X);
            _y.Value = ClampPoints(landed.Y);
            _edge.SelectedIndex = EdgeIndex(landed.Edge);
            RefreshWhere();
        });
    }

    private void ApplyHotKey()
    {
        // TryParse validates the key through HotKeySpec.TryCreate, so anything that
        // survives it is a combination RegisterHotKey can actually be given.
        if (HotKeySpec.TryParse(_hotKey.Text) is not { } spec)
        {
            Note("That is not a usable shortcut. " + HotKeyHint, Theme.Red);
            return;
        }
        if (!_host.TryApplyHotKey(spec))
        {
            Note($"Windows refused {spec.DisplayString}: another application already owns it.", Theme.Red);
            return;
        }
        var value = spec == HotKeySpec.Default ? null : spec.DisplayString;
        try
        {
            _store.Update(s => s.HotKey = value);
        }
        catch (Exception ex)
        {
            Log.Write("settings change failed: " + Log.Describe(ex));
        }
        _hotKey.Text = spec.DisplayString;
        Note($"Hotkey set to {spec.DisplayString}.", Theme.Cyan);
        _host.SettingsApplied();
    }

    private void Apply(Action<PanelSettings> change)
    {
        try
        {
            _store.Update(change);
            _host.SettingsApplied();
        }
        catch (Exception ex)
        {
            Log.Write("settings change failed: " + Log.Describe(ex));
        }
    }

    private void Note(string text, Color color)
    {
        _hotKeyNote.Text = text;
        _hotKeyNote.ForeColor = color;
    }

    private void RefreshWhere()
    {
        var placement = _host.OrbPlacement;
        _where.Text = $"Now at {placement.X:0}, {placement.Y:0} on {placement.DeviceName}" +
                      (placement.Edge is { } edge ? $" Â· docked {edge.Raw()}" : "");
    }

    private static void Configure(NumericUpDown box)
    {
        box.Minimum = -20000;
        box.Maximum = 20000;
        box.DecimalPlaces = 0;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.Font = Widgets.Shared(8.5f);
        box.BackColor = Theme.Well;
        box.ForeColor = Theme.Text;
        box.Width = ScreenMetrics.Px(72);
        box.Height = ScreenMetrics.Px(22);
    }

    private static decimal ClampPoints(double value)
        => Math.Min(20000, Math.Max(-20000, (decimal)Math.Round(value)));

    private static int Percent(double value)
        => (int)Math.Round(Math.Min(Math.Max(value, PanelSettings.OpacityMin), PanelSettings.OpacityMax) * 100);

    private static int EdgeIndex(DockEdge? edge)
    {
        for (var i = 0; i < Edges.Length; i++)
            if (Edges[i].Edge == edge) return i;
        return 0;
    }

    /// <summary>Keeps one bad click from taking the panel down.</summary>
    private void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Write("settings action failed: " + Log.Describe(ex));
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

    /// <summary>Reopening a hidden window must not resurrect a disposed one, so closing hides.</summary>
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
}
