using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using JarvisPanel.App;
using JarvisPanel.Core;

namespace JarvisPanel.UI;

/// <summary>
/// The full quick reply bar (spec Â§6): target picker, conversation history,
/// voice controls, pending answers and the input line, anchored beside the orb.
/// </summary>
/// <remarks>
/// <para>
/// This is the one orb surface that <b>does</b> take focus: it has to, or typing
/// would land in whatever is behind it. The orb hands focus over deliberately when
/// the bar opens and never takes it back. Focus is only requested on the hidden â†’
/// visible transition â€” the model raises a change roughly once a second, and
/// activating on every one of those would yank the user out of whatever they are
/// typing in, which is the "grabbed my mouse" failure in a different costume.
/// </para>
/// <para>
/// It is a follower, not an owner: every visibility and enablement decision comes
/// from <see cref="PanelModel"/>, so Esc, Send, the hotkey and the orb's menu all
/// agree on whether the bar is open.
/// </para>
/// <para>
/// The bar grows toward the side with room (<see cref="QuickBarLayout.GrowsUp"/>):
/// the input row always sits beside the orb, and the target picker, history and
/// voice rows stack away from it. Card 0 is therefore either the topmost or the
/// bottommost element depending on that flag, which <see cref="PlaceRow"/> hides
/// from the rest of the file.
/// </para>
/// </remarks>
internal sealed class QuickBarWindow : Form
{
    /// <summary>Width matches <c>QuickBarView.width</c> in the macOS sources.</summary>
    private const double BarWidth = 320;
    /// <summary>
    /// Height of the target chip, the caret and the buttons. The pills are built at
    /// 24pt and sit on a 1pt stroke, so the row needs a little air around them.
    /// </summary>
    private const double ChipRowHeight = 28;
    /// <summary>The dim status line under the chip row (an 7.5pt font needs 14px).</summary>
    private const double StatusStripHeight = 15;
    private const double Gutter = 8;
    private const double MaxHistoryHeight = 260;
    private const double MinHistoryHeight = 90;

    /// <summary>Whole input row: the chip row, plus the status strip beneath it.</summary>
    private const double InputRowHeight = ChipRowHeight + StatusStripHeight;

    private const int InputRow = 0;
    private const int HistoryRow = 1;
    private const int TargetRow = 2;

    private readonly PanelModel _model;
    private readonly OrbWindow _orb;
    private readonly TextBox _input;
    private readonly PillButton _target;
    private readonly PillButton _send;
    private readonly PillButton _historyToggle;
    private readonly FlowLayoutPanel _voice;
    private readonly Panel _history;
    private readonly Label _status;
    private readonly Panel _targetRow;

    private string _lastStatus = "";
    private string _historySignature = "";
    private int _historyMaxHeight;
    /// <summary>Which way the stack grows from the input row; see <see cref="QuickBarLayout.GrowsUp"/>.</summary>
    private bool _growsUp = true;

    public QuickBarWindow(PanelModel model, OrbWindow orb)
    {
        _model = model;
        _orb = orb;

        SuspendLayout();
        Text = "Jarvis";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.SurfaceOpaque;
        ForeColor = Theme.Text;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;

        _input = new TextBox
        {
            BorderStyle = BorderStyle.FixedSingle,
            // Deliberately not Dock=Fill: the bar places every control at explicit
            // pixel offsets, and a docked child has its bounds rewritten by the
            // parent's layout engine after each assignment, which silently moved the
            // caret to cover the whole bar.
            Font = Theme.Font(9.5f),
            BackColor = Color.FromArgb(18, 26, 44),
            ForeColor = Theme.Text,
        };
        _input.TextChanged += (_, _) => _model.Draft = _input.Text;
        _input.KeyDown += OnInputKeyDown;

        _target = Widgets.Pill("Jarvis", Theme.Text, Theme.PillFill, Theme.PillStroke, 24, 8.5f,
            ContentAlignment.MiddleLeft);
        _target.Click += (_, _) => Run(ShowTargetMenu);

        _historyToggle = Widgets.Pill("≡", Theme.Dim, Theme.PillFill, Theme.PillStroke, 24, 9f);
        _historyToggle.Width = ScreenMetrics.Px(26);
        _historyToggle.Click += (_, _) => _ = RunAsync(() => _model.SetHistoryExpandedAsync(!_model.HistoryExpanded));

        _voice = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.SurfaceOpaque,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };

        _history = new Panel
        {
            BackColor = Theme.SurfaceOpaque,
            AutoScroll = true,
        };

        _targetRow = new Panel { BackColor = Theme.SurfaceOpaque };

        _send = Widgets.Pill("Send", Theme.Text, Theme.PillFill, Theme.PillStroke, 24, 8.5f);
        _send.Click += (_, _) => _ = RunAsync(SendAsync);

        _status = new Label
        {
            AutoSize = false,
            // Not docked, for the same reason as the caret: the status strip is one
            // row of the bar's explicit layout, not a container edge.
            Font = Theme.Font(7.5f),
            ForeColor = Theme.Dim,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Theme.SurfaceOpaque,
        };

        Controls.Add(_input);
        Controls.Add(_target);
        Controls.Add(_historyToggle);
        Controls.Add(_voice);
        Controls.Add(_history);
        Controls.Add(_targetRow);
        Controls.Add(_send);
        Controls.Add(_status);

        _model.Changed += OnModelChanged;
        // Visibility is the one thing about this window that four other objects can
        // change (the model, the hotkey, the orb and the tray), so a bar that comes
        // and goes on its own is worth a line in the log to say who did it.
        VisibleChanged += (_, _) => Log.Write($"quick bar visible={Visible} open={_model.QuickBarOpen}");
        ResumeLayout(true);
    }

    /// <summary>Keyboard route into the bar from the hotkey and the tray menu.</summary>
    public void FocusInput()
    {
        if (!Visible) Show();
        if (!ContainsFocus) Activate();
        _input.Focus();
        _input.SelectionStart = _input.TextLength;
    }

    /// <summary>Snaps the bar beside the orb on the side with more room; the bar is a follower.</summary>
    public void FollowOrb()
    {
        LayoutRows(Place(ContentHeight()));
        ScrollHistoryToEnd();
    }

    // ------------------------------------------------------------------- syncing

    /// <summary>
    /// Rebuilds the bar from the model. Called on every model change and after
    /// every local action, so the window never holds an opinion of its own.
    /// </summary>
    public void SyncToModel(bool quickBarOpen)
    {
        if (!quickBarOpen)
        {
            Hide();
            return;
        }

        // Only adopt the model's draft on the way in: doing it while the caret is
        // in the box would fight the user's typing.
        if (_input.Text != _model.Draft) _input.Text = _model.Draft;

        SyncTargetChip();
        SyncSendLabel();
        SyncVoiceRow();
        SyncHistoryToggle();
        SyncHistory();

        // Size, then clamp, then lay out: the rows are placed at pixel offsets, so
        // the layout has to know the final client height rather than a desired one.
        var height = Place(ContentHeight());
        LayoutRows(height);
        UpdateStatus();

        var wasHidden = !Visible;
        if (wasHidden) Show();
        // Only the first show takes focus. Later model changes must leave the
        // caret â€” and whatever the user is typing into â€” exactly where it was.
        if (wasHidden) FocusInput();
    }

    private void SyncTargetChip()
    {
        var count = _model.Targets.Count;
        var label = _model.AnsweringItem is not null ? "Answering" : _model.LabelFor(_model.Target);
        var trimmed = label.Length > 12 ? label[..11] + "â€¦" : label;
        var text = count > 1 ? trimmed + " â–¾" : trimmed;
        if (_target.Text != text)
        {
            _target.Text = text;
            _target.Width = Math.Min(ScreenMetrics.Px(140),
                TextRenderer.MeasureText(text, _target.Font).Width + ScreenMetrics.Px(18));
        }
    }

    /// <summary>
    /// A pending answer has a destination the user cannot choose, so the send
    /// button says what it will do rather than offering a target.
    /// </summary>
    private void SyncSendLabel()
    {
        var answering = _model.AnsweringItem is not null;
        var text = answering ? (_model.Target.IsJarvis ? "Allow" : "Send") : "Send";
        if (_send.Text != text) _send.Text = text;
        _send.Enabled = _model.Draft.Trim().Length > 0;
    }

    /// <summary>
    /// Playback controls mirror <see cref="PanelModel.HoverButtons"/>, so the bar
    /// and the orb's hover fan can never disagree about what is available.
    /// </summary>
    private void SyncVoiceRow()
    {
        var wanted = new List<HoverButton>(_model.HoverButtons);
        var same = _voice.Controls.Count == wanted.Count;
        if (same)
        {
            for (var i = 0; i < wanted.Count; i++)
            {
                if (_voice.Controls[i] is PillButton pill && pill.Tag is HoverButton button && button == wanted[i])
                    continue;
                same = false;
                break;
            }
        }
        if (same) return;

        _voice.SuspendLayout();
        foreach (Control control in _voice.Controls) control.Dispose();
        _voice.Controls.Clear();

        foreach (var button in wanted)
        {
            var pill = Widgets.Pill(button.Label(), Theme.Dim, Theme.PillFill, Theme.PillStroke, 24, 8f);
            pill.Tag = button;
            var captured = button;
            pill.Click += (_, _) => _ = RunAsync(() => _model.PressAsync(captured));
            _voice.Controls.Add(pill);
        }
        _voice.ResumeLayout(true);
    }

    /// <summary>
    /// The history toggle is a glyph button rather than a pill: the row it lives in
    /// has to fit the target chip, the caret, the playback pills and Send, and a
    /// word-wide "History" pill left the caret about forty pixels of typing room.
    /// </summary>
    private void SyncHistoryToggle()
    {
        var expanded = _model.HistoryExpanded;
        if (_historyToggle.Text == Glyph(expanded) && _historyToggle.Tag is bool tagged && tagged == expanded) return;
        _historyToggle.Tag = expanded;
        _historyToggle.Text = Glyph(expanded);

        static string Glyph(bool open) => open ? "▾" : "≡";
    }

    // ------------------------------------------------------------------- history

    private void SyncHistory()
    {
        var signature = HistorySignature();
        if (signature == _historySignature) return;
        _historySignature = signature;
        RebuildHistory();
    }

    private string HistorySignature()
    {
        if (!_model.HistoryExpanded) return "closed";
        var parts = new List<string> { _model.LabelFor(_model.Target), _model.Messages.Count.ToString() };
        foreach (var message in _model.Messages)
            parts.Add(string.Join('\u001f', message.Id, message.Role, message.RoutedTo ?? "", message.Text.Length.ToString()));
        return string.Join('\u001d', parts);
    }

    private void RebuildHistory()
    {
        _history.SuspendLayout();
        foreach (Control control in _history.Controls) control.Dispose();
        _history.Controls.Clear();
        _history.AutoScrollPosition = Point.Empty;

        if (!_model.HistoryExpanded)
        {
            _history.ResumeLayout(true);
            return;
        }

        var width = Math.Max(ScreenMetrics.Px(80), _history.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
        var stack = new Stack(_history, 0, 0, width);

        if (_model.Messages.Count == 0)
        {
            stack.Add(Widgets.Wrapped("Nothing in this conversation yet.", Widgets.Shared(8f), Theme.Faint,
                Theme.SurfaceOpaque, width, ScreenMetrics.Px(20)));
        }
        else
        {
            // Newest last, like every chat log; the pane scrolls to the bottom.
            foreach (var message in _model.Messages)
                stack.Add(BuildMessage(message, width), stack.Bottom == 0 ? 0 : ScreenMetrics.Px(5), stretch: false);
        }

        _history.AutoScrollMinSize = new Size(0, stack.Bottom + ScreenMetrics.Px(2));
        _history.ResumeLayout(true);
    }

    /// <summary>One bubble: who said it, then as much of the text as a card can hold.</summary>
    private HudPanel BuildMessage(ChatMessage message, int width)
    {
        var pad = ScreenMetrics.Px(7);
        var inner = width - pad * 2;
        var mine = message.IsMine;
        var accent = mine ? Theme.Cyan : Theme.Violet;
        var card = new HudPanel
        {
            Width = width,
            Fill = mine ? Theme.Well : Theme.Card,
            Stroke = Color.FromArgb(mine ? 60 : 40, accent),
            Accent = Color.FromArgb(150, accent),
        };
        var stack = new Stack(card, pad, ScreenMetrics.Px(6), inner);

        var who = mine ? "You" : _model.LabelFor(_model.Target);
        if (!string.IsNullOrEmpty(message.RoutedTo)) who += " â†’ " + message.RoutedTo;
        stack.Add(Widgets.Text(who, Widgets.Shared(7.5f, FontStyle.Bold), Color.FromArgb(230, accent),
            card.Fill, inner, ScreenMetrics.Px(13)));
        stack.Add(Widgets.Wrapped(message.Preview, Widgets.Shared(8.5f), Theme.Text, card.Fill, inner,
            ScreenMetrics.Px(120)), ScreenMetrics.Px(2));

        card.Height = stack.Bottom + ScreenMetrics.Px(6);
        return card;
    }

    /// <summary>
    /// Scrolls the newest bubble into view. Called after <see cref="Layout"/> has
    /// given the pane its height, because a control outside the client area cannot
    /// be scrolled to.
    /// </summary>
    private void ScrollHistoryToEnd()
    {
        if (!_model.HistoryExpanded || _history.Controls.Count == 0) return;
        var last = _history.Controls[_history.Controls.Count - 1];
        _history.ScrollControlIntoView(last);
    }

    // -------------------------------------------------------------------- layout

    /// <summary>
    /// Height the window would like, in points: the input row, the history pane at
    /// its ceiling, the target row, and a gutter between each. <see cref="Place"/>
    /// clamps it to the workspace afterwards.
    /// </summary>
    private double ContentHeight()
    {
        // The row is the target chip plus the status line beneath it, and the two
        // must not be laid out on top of each other just because the window is
        // short: Place() only ever grows the height to this floor.
        const double row = InputRowHeight;
        var height = row + Gutter;
        if (_model.HistoryExpanded) height += MaxHistoryHeight + Gutter;
        if (_model.Targets.Count > 1) height += row + Gutter;
        var workArea = ScreenMetrics.ForPoint(_orb.CenterPixels).WorkArea;
        return Math.Min(height, Math.Max(row * 2, ScreenMetrics.Pt(workArea.Height) - 32));
    }

    /// <summary>
    /// Lays every row out. The input row is pinned beside the orb and the other
    /// rows stack away from it â€” the bar reads as a column growing out of the orb,
    /// so which end of the window the input sits at depends on
    /// <see cref="QuickBarLayout.GrowsUp"/>.
    /// </summary>
    private void LayoutRows(double heightPoints)
    {
        var width = ScreenMetrics.Px(BarWidth);
        var height = ScreenMetrics.Px(heightPoints);
        var pad = ScreenMetrics.Px(Gutter);
        var inputHeight = ScreenMetrics.Px(InputRowHeight);
        var chipHeight = ScreenMetrics.Px(ChipRowHeight);
        var targetHeight = _model.Targets.Count > 1 ? inputHeight : 0;

        // The status line lives inside the input row's height so the bar keeps the
        // same overall size whether or not it has something to say.
        var chrome = inputHeight + targetHeight + pad * 3;
        var historyHeight = 0;
        if (_model.HistoryExpanded)
        {
            historyHeight = Math.Min(ScreenMetrics.Px(MaxHistoryHeight), Math.Max(0, height - chrome));
            if (historyHeight < ScreenMetrics.Px(MinHistoryHeight)) historyHeight = ScreenMetrics.Px(MinHistoryHeight);
        }
        _historyMaxHeight = historyHeight;

        // Away from the orb: top-to-bottom when the bar grows up (the stack sits
        // above the input), bottom-to-top when it grows down.
        var stacked = new List<(Control Control, int Height)>();
        if (historyHeight > 0) stacked.Add((_history, historyHeight));
        if (targetHeight > 0) stacked.Add((_targetRow, targetHeight));

        var orbNearestTop = _growsUp ? Math.Max(0, height - pad - inputHeight) : pad;
        var y = _growsUp ? orbNearestTop - pad : orbNearestTop + inputHeight + pad;
        var order = _growsUp ? Enumerable.Reverse(stacked).ToList() : stacked;
        foreach (var (control, rowHeight) in order)
        {
            if (_growsUp) y -= rowHeight;
            control.Location = new Point(pad, y);
            control.Size = new Size(width - pad * 2, rowHeight);
            if (!_growsUp) y += rowHeight + pad;
        }

        PlaceInput(orbNearestTop, pad, inputHeight);
    }

    /// <summary>
    /// Positions the chip row left to right: target chip, playback pills, caret,
    /// history toggle, Send. The caret is what yields when the row is tight — the
    /// other four are fixed-width and a narrow caret is still usable, while a
    /// clobbered button is not.
    /// </summary>
    private void PlaceInput(int top, int pad, int height)
    {
        var width = ClientSize.Width - pad * 2;
        var statusHeight = ScreenMetrics.Px(StatusStripHeight);
        _status.Location = new Point(pad, top + height - statusHeight);
        _status.Size = new Size(width, statusHeight);
        // The controls get the band above the status line, never the whole row: the
        // two share it, so centring a 24pt pill in a 43pt row would sit it on the text.
        var band = Math.Max(ScreenMetrics.Px(ChipRowHeight), height - statusHeight);
        var gap = ScreenMetrics.Px(4);
        int Centre(Control control) => top + (band - control.Height) / 2;

        var x = pad;
        _target.Location = new Point(x, Centre(_target));
        x += _target.Width + gap;

        var voiceWidth = 0;
        foreach (Control pill in _voice.Controls)
        {
            pill.Location = new Point(voiceWidth, Math.Max(0, (band - pill.Height) / 2));
            voiceWidth += pill.Width + ScreenMetrics.Px(4);
        }
        _voice.Location = new Point(x, top);
        _voice.Size = new Size(voiceWidth, band);
        x += voiceWidth + gap;

        var sendWidth = Math.Max(ScreenMetrics.Px(54), _send.Width);
        _send.Location = new Point(pad + width - sendWidth, Centre(_send));
        _historyToggle.Location = new Point(_send.Left - gap - _historyToggle.Width, Centre(_historyToggle));

        // The caret gets what is left between the pills and the toggle; a text box
        // that reached under a button would swallow its clicks. Past the minimum the
        // caret is what gives, so the fixed controls never overlap each other.
        _input.Location = new Point(x, Centre(_input));
        _input.Width = Math.Max(ScreenMetrics.Px(24), _historyToggle.Left - gap - x);
        _targetRow.Visible = _model.Targets.Count > 1;
    }

    // --------------------------------------------------------------- placement

    /// <summary>
    /// Sizes the window for <paramref name="heightPoints"/>, clamps it to the
    /// workspace, moves it beside the orb and returns the client height in points
    /// that the rows must be laid out in.
    /// </summary>
    /// <remarks>
    /// Everything in Placement is in ABSOLUTE screen points (the corollary of
    /// MonitorMetrics dividing raw device pixels by the monitor scale), so the orb
    /// centre goes in unconverted and the result comes out as pixels by multiplying
    /// alone. Adding the monitor origin here as well would move the bar by the whole
    /// monitor offset â€” invisible on a primary monitor at 0,0, badly wrong on any
    /// display to the left of or above it.
    /// </remarks>
    private double Place(double heightPoints)
    {
        var scale = Scale();
        var width = ScreenMetrics.Px(BarWidth);
        var workspace = WorkspaceBounds();
        var layout = Placement.QuickBar(OrbCentre(), workspace, new SizeD(BarWidth, heightPoints));
        _growsUp = layout.GrowsUp;

        // Placement.QuickBar clamps only the far side; the near side needs the same
        // treatment or an orb on a low edge puts the bar past its monitor's origin.
        // Clamping the origin first also yields the tallest frame that still fits,
        // which is the honest answer to "how many rows may this window have".
        var origin = Placement.ClampBothSides(layout.Bar, workspace);
        var top = Math.Max(origin.Y, workspace.MinY);
        var height = Math.Max(InputRowHeight + Gutter,
            Math.Min(heightPoints, workspace.MaxY - top - Placement.ScreenMargin));

        // Assign only on a real change: the model raises a change about once a
        // second, and resizing or moving a window on every one of those is a lot of
        // window-manager traffic for a bar that has not actually moved.
        var size = new Size(width, ScreenMetrics.Px(height));
        if (ClientSize != size) ClientSize = size;
        var location = new PointD(origin.X, top).ToPixels(scale);
        if (Location != location) Location = location;
        return ScreenMetrics.Pt(ClientSize.Height);
    }

    private PointD OrbCentre()
    {
        var scale = Scale();
        return new PointD(_orb.CenterPixels.X / scale, _orb.CenterPixels.Y / scale);
    }

    private RectD WorkspaceBounds() => ScreenMetrics.WorkspaceFor(_orb.CenterPixels).Bounds;

    private static double Scale()
    {
        var scale = ScreenMetrics.Scale;
        return scale <= 0 ? 1 : scale;
    }

    // ------------------------------------------------------------------ actions

    private async Task SendAsync()
    {
        await _model.SubmitAsync();
        SyncToModel(_model.QuickBarOpen);
    }

    /// <summary>
    /// The target picker (spec Â§6.1): every target the host reports, the pending
    /// item's session first, with Ctrl+1â€¦Ctrl+9 as the keyboard route.
    /// </summary>
    private void ShowTargetMenu()
    {
        var options = _model.Targets;
        if (options.Count == 0) return;

        var menu = new ContextMenuStrip { ShowImageMargin = false };
        for (var i = 0; i < options.Count; i++)
        {
            var option = options[i];
            var shortcut = i < 9 ? $"Ctrl+{i + 1}" : "";
            var label = option.Label;
            if (option.Status is { } status) label += "  Â·  " + EnumText.Label(status);
            var item = new ToolStripMenuItem(label, null, (_, _) => Run(() => SelectTarget(option.Target)))
            {
                Tag = option.Target,
                ShortcutKeyDisplayString = shortcut,
                Checked = option.Target == _model.Target,
                CheckOnClick = false,
            };
            if (option.Task is { } task) item.ToolTipText = task.Summary ?? task.Status.Label();
            menu.Items.Add(item);
        }

        menu.Closed += (_, _) =>
        {
            menu.Dispose();
            // Focus moved to the popup; hand it back to the caret so the user can
            // carry on typing without clicking into the box again.
            if (Visible) FocusInput();
        };
        menu.Show(_target, new Point(0, _target.Height));
    }

    private void SelectTarget(Target target)
    {
        _model.SelectTarget(target);
        SyncToModel(_model.QuickBarOpen);
    }

    /// <summary>
    /// The approval answer is explicit here â€” the bar is what a pending card opens
    /// when the user chooses to type, so Ctrl+Enter is the "yes, and send it" chord.
    /// </summary>
    private async Task AnswerApprovalAsync(bool allow)
    {
        if (_model.AnsweringItem is not { } item) return;
        if (_model.Draft.Trim().Length > 0)
        {
            await _model.SubmitAsync();
        }
        else if (allow)
        {
            await _model.DecideAsync(item, true);
            _model.CloseQuickBar();
        }
        SyncToModel(_model.QuickBarOpen);
    }

    // ------------------------------------------------------------------- status

    private void UpdateStatus()
    {
        string text;
        if (_model.SendError is { Length: > 0 } error)
        {
            _status.ForeColor = Theme.Red;
            text = error;
        }
        else if (_model.EscapeHint)
        {
            _status.ForeColor = Theme.Dim;
            text = "Esc again to close";
        }
        else if (_model.Toast is { Length: > 0 } toast)
        {
            _status.ForeColor = Theme.Dim;
            text = toast;
        }
        else if (_model.AnsweringItem is { } pending)
        {
            // The card cannot show a text box without taking the keyboard, so the
            // bar is where a free-text answer is typed (spec Â§6.2).
            _status.ForeColor = Theme.Amber;
            text = $"Type an answer to {pending.Title.Trim()} Â· Ctrl+Enter allows without a note";
            if (text.Length > 96) text = text[..95] + "â€¦";
        }
        else if (_model.Connection.IsOnline)
        {
            _status.ForeColor = Theme.Dim;
            text = "To " + _model.LabelFor(_model.Target) +
                   (_model.Targets.Count > 1 ? " Â· Ctrl+1â€¦9 switches target" : "");
        }
        else
        {
            _status.ForeColor = Theme.Dim;
            text = "Not connected â€” sends will fail until DSH is back";
        }

        if (text == _lastStatus) return;
        _lastStatus = text;
        _status.Text = text;
    }

    // -------------------------------------------------------------- keyboard/UI

    /// <summary>Enter sends, Esc runs the model's two-press gate, Ctrl+1â€¦9 pick a target.</summary>
    private async void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            Run(() =>
            {
                _model.EscapePressed();
                SyncToModel(_model.QuickBarOpen);
            });
        }
        else if (e.KeyCode == Keys.Enter && e.Control && _model.AnsweringItem is not null && !e.Shift)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            await RunAsync(() => AnswerApprovalAsync(allow: true));
        }
        else if (e.KeyCode == Keys.Enter && !e.Shift && !e.Control)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            await RunAsync(SendAsync);
        }
        else if (e.Control && e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D9)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            var number = e.KeyCode - Keys.D0;
            Run(() =>
            {
                _model.SelectTargetByNumber(number);
                SyncToModel(_model.QuickBarOpen);
            });
        }
        else if (e.KeyCode == Keys.Tab)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            Run(() =>
            {
                _model.CycleTarget();
                SyncToModel(_model.QuickBarOpen);
            });
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.StrokeRounded(e.Graphics, new RectangleF(0.5f, 0.5f, ClientSize.Width - 1f, ClientSize.Height - 1f),
            ScreenMetrics.Pxf(10), Color.FromArgb(90, Theme.Stroke), 1f);
    }

    private void OnModelChanged()
    {
        try
        {
            var historyChanged = _model.HistoryExpanded && HistorySignature() != _historySignature;
            SyncToModel(_model.QuickBarOpen);
            // The pane only gets its final size from Layout, so the scroll has to
            // happen after it; doing it inside the rebuild would scroll nothing.
            // Only when the log actually changed, since the model ticks every second.
            if (historyChanged) ScrollHistoryToEnd();
        }
        catch (Exception ex)
        {
            Log.Write("quick bar sync failed: " + Log.Describe(ex));
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ScrollHistoryToEnd();
    }

    /// <summary>
    /// Keeps one bad click from taking the panel down: these handlers run on the
    /// message loop, so an escaping exception would land in its catch-all.
    /// </summary>
    private void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Write("quick bar action failed: " + Log.Describe(ex));
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Log.Write("quick bar action failed: " + Log.Describe(ex));
        }
    }

    /// <summary>
    /// Closing the window must not bypass the model, or the orb and the hotkey
    /// would disagree about whether the bar is open. Hiding is the only way this
    /// form leaves the screen.
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            _model.CloseQuickBar();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _model.Changed -= OnModelChanged;
        base.Dispose(disposing);
    }
}
