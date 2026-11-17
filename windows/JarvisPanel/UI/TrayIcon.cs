using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using JarvisPanel.App;
using JarvisPanel.Core;

namespace JarvisPanel.UI;

/// <summary>
/// The notification-area icon. It is the panel's only always-available entry
/// point: the orb can be hidden, the quick bar closes, and a global hotkey can
/// fail to register, but the tray icon is always there with a way to quit.
/// </summary>
/// <remarks>
/// <para>
/// Every Stage B surface is reachable from here without touching the orb — the
/// quick bar, the history pane, each target, the approval cards, the auto-approval
/// audit list and the settings window — so a user whose orb is hidden or whose
/// pointer is busy can still get to all of it.
/// </para>
/// <para>
/// No <c>.ico</c> is shipped — the orb is drawn from the same code and the same
/// palette as the window, so the icon can never drift from the thing it
/// represents. <see cref="Icon.FromHandle"/> does not own the handle it is given,
/// which is why the icon is cloned before the HICON is destroyed.
/// </para>
/// </remarks>
internal sealed class TrayIcon : IDisposable
{
    private const int IconPixels = 32;

    private readonly NotifyIcon _icon;
    private readonly UiActions _actions;

    public TrayIcon(UiActions actions)
    {
        _actions = actions;
        _icon = new NotifyIcon
        {
            Icon = Render(Theme.Tint(OrbTint.Cyan)),
            Text = "Jarvis panel",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        // A left double-click is the conventional "bring it back" gesture.
        _icon.DoubleClick += (_, _) => _actions.OpenQuickBar();
    }

    /// <summary>Rebuilds the menu so the show/hide entry always matches reality.</summary>
    public void RebuildMenu() => _icon.ContextMenuStrip = BuildMenu();

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open quick bar", null, (_, _) => _actions.OpenQuickBar()));
        menu.Items.Add(new ToolStripMenuItem("Open in the middle of the screen", null,
            (_, _) => _actions.OpenQuickBarCentred()));
        menu.Items.Add(new ToolStripMenuItem(
            _actions.HistoryExpanded ? "Hide conversation history" : "Show conversation history", null,
            (_, _) => _actions.ToggleHistory()));

        var targets = _actions.Targets;
        if (targets.Count > 0)
        {
            var pick = new ToolStripMenuItem("Talk to");
            for (var i = 0; i < targets.Count; i++)
            {
                var option = targets[i];
                var label = option.Label;
                if (option.Status is { } status) label += "  ·  " + EnumText.Label(status);
                var item = new ToolStripMenuItem(label, null, (_, _) => _actions.SelectTarget(option.Target))
                {
                    Checked = option.Target == _actions.CurrentTarget,
                    ShortcutKeyDisplayString = i < 9 ? $"Ctrl+{i + 1}" : "",
                };
                if (option.Task is { } task) item.ToolTipText = task.Summary ?? task.Status.Label();
                pick.DropDownItems.Add(item);
            }
            menu.Items.Add(pick);
        }

        var voice = new ToolStripMenuItem("Voice");
        foreach (var button in _actions.VoiceButtons)
            voice.DropDownItems.Add(new ToolStripMenuItem(button.Label(), null, (_, _) => _actions.PressVoice(button)));
        menu.Items.Add(voice);

        menu.Items.Add(new ToolStripMenuItem("Pending requests…", null, (_, _) => _actions.ShowApprovalCards()));
        menu.Items.Add(new ToolStripMenuItem("Auto-approved actions…", null, (_, _) => _actions.ShowAutoApprovals()));
        menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => _actions.ShowSettings()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(_actions.OrbToggleLabel, null, (_, _) => _actions.ToggleOrb()));
        menu.Items.Add(new ToolStripMenuItem("Reset position", null, (_, _) => _actions.ResetPosition()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => _actions.Quit()));
        return menu;
    }

    /// <summary>
    /// A filled orb with a soft rim, drawn at 32px and left for the shell to scale.
    /// </summary>
    private static Icon Render(Color tint)
    {
        using var bitmap = new Bitmap(IconPixels, IconPixels);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var core = new RectangleF(6, 6, IconPixels - 12, IconPixels - 12);
            using (var glow = new GraphicsPath())
            {
                glow.AddEllipse(2, 2, IconPixels - 4, IconPixels - 4);
                using var halo = new PathGradientBrush(glow)
                {
                    CenterColor = Color.FromArgb(150, tint),
                    SurroundColors = new[] { Color.FromArgb(0, tint) },
                };
                g.FillPath(halo, glow);
            }

            using (var body = new SolidBrush(tint))
            {
                g.FillEllipse(body, core);
            }
            using (var rim = new Pen(Color.FromArgb(180, Color.White), 1.4f))
            {
                g.DrawEllipse(rim, core);
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Icon?.Dispose();
        _icon.Dispose();
    }
}
