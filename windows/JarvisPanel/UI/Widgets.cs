using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace JarvisPanel.UI;

/// <summary>
/// A rounded HUD card. A plain <see cref="Panel"/> paints square corners and
/// every Stage B surface is a rounded card, so the shape comes from
/// <see cref="Theme"/>'s path helpers instead of a second drawing routine.
/// </summary>
/// <remarks>
/// Fills are opaque on purpose: the panel's windows are opaque (see the README),
/// so a card can hand its own face colour to the labels on it and never has to
/// simulate transparency.
/// </remarks>
internal class HudPanel : Panel
{
    public HudPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Theme.Card;
    }

    /// <summary>Card face; <see cref="Control.BackColor"/> is kept in step so children can match it.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Fill
    {
        get => BackColor;
        set => BackColor = value;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Stroke { get; set; } = Theme.Stroke;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float Radius { get; set; } = Theme.Radius;

    /// <summary>Left accent bar colour (pending kind, approval tier); null draws none.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color? Accent { get; set; }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // The corners outside the rounded shape belong to the host surface.
        e.Graphics.Clear(Parent?.BackColor ?? Theme.SurfaceOpaque);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(0.5f, 0.5f, Math.Max(1, Width - 1f), Math.Max(1, Height - 1f));
        Theme.FillRounded(g, rect, Radius, Fill);
        if (Stroke.A > 0) Theme.StrokeRounded(g, rect, Radius, Stroke);
        if (Accent is not { } accent) return;
        var inset = ScreenMetrics.Pxf(6);
        var bar = new RectangleF(rect.X + 1, rect.Y + inset, ScreenMetrics.Pxf(3), Math.Max(2, rect.Height - inset * 2));
        Theme.FillRounded(g, bar, ScreenMetrics.Pxf(1.5f), accent);
    }
}

/// <summary>
/// A capsule button drawn with <see cref="Theme"/>. WinForms' own flat buttons
/// cannot round their corners, and every action in the Stage B surfaces is a pill.
/// </summary>
/// <remarks>
/// <see cref="Button"/> never disposes the <see cref="Control.Font"/> it is given,
/// so the shared fonts from <see cref="Widgets.Shared"/> are safe here even though
/// these buttons are rebuilt whenever a card changes.
/// </remarks>
internal sealed class PillButton : Button
{
    private bool _hover;

    public PillButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        BackColor = Theme.SurfaceOpaque;
        TabStop = false;
        Margin = Padding.Empty;
    }

    /// <summary>
    /// Pill colours are set in code, never by the designer, and each one is read on
    /// every paint, so they are marked hidden from serialization (the WinForms
    /// analyzer otherwise demands a serialization story for every public property).
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Fill { get; set; } = Theme.PillFill;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Stroke { get; set; } = Theme.PillStroke;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Ink { get; set; } = Theme.Text;

    /// <summary>Corner radius; negative draws a full capsule.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float Radius { get; set; } = -1f;

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.SurfaceOpaque);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(0.5f, 0.5f, Math.Max(1, Width - 1f), Math.Max(1, Height - 1f));
        var radius = Radius < 0 ? Math.Min(rect.Width, rect.Height) / 2 : Radius;
        var strength = Enabled ? (_hover ? 1.35 : 1.0) : 0.5;
        Theme.FillRounded(g, rect, radius, Shade(Fill, strength));
        if (Stroke.A > 0) Theme.StrokeRounded(g, rect, radius, Shade(Stroke, strength));

        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                    | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        flags |= TextAlign switch
        {
            ContentAlignment.MiddleLeft => TextFormatFlags.Left,
            ContentAlignment.MiddleRight => TextFormatFlags.Right,
            _ => TextFormatFlags.HorizontalCenter,
        };
        var pad = ScreenMetrics.Px(TextAlign == ContentAlignment.MiddleCenter ? 2 : 8);
        var box = new Rectangle(pad, 0, Math.Max(1, Width - pad * 2), Height);
        TextRenderer.DrawText(g, Text, Font, box, Enabled ? Ink : Shade(Ink, 0.5), flags);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    /// <summary>Alpha for the disabled state, a brighter face for hover.</summary>
    private static Color Shade(Color c, double factor)
    {
        if (Math.Abs(factor - 1) < 0.001) return c;
        if (factor > 1)
        {
            int Lift(int v) => (int)Math.Min(255, Math.Round(v + (255 - v) * (factor - 1) * 0.3));
            return Color.FromArgb(c.A, Lift(c.R), Lift(c.G), Lift(c.B));
        }
        return Color.FromArgb((int)Math.Round(c.A * factor), c.R, c.G, c.B);
    }
}

/// <summary>
/// Places controls top-down at explicit positions. WinForms' auto-layout engines
/// resize children asynchronously, which makes measured card heights unreliable;
/// the Stage B surfaces are small enough that explicit geometry is simpler and
/// gives the same result on every DPI.
/// </summary>
internal sealed class Stack
{
    private readonly Control _host;
    private readonly int _x;
    private int _y;

    public Stack(Control host, int x, int top, int width)
    {
        _host = host;
        _x = x;
        _y = top;
        Width = Math.Max(1, width);
    }

    /// <summary>Content width; children are stretched to it unless told otherwise.</summary>
    public int Width { get; }

    /// <summary>Baseline below the last added control.</summary>
    public int Bottom => _y;

    public T Add<T>(T control, int gapBefore = 0, bool stretch = true) where T : Control
    {
        _y += gapBefore;
        if (stretch) control.Width = Width;
        control.Location = new Point(_x, _y);
        _host.Controls.Add(control);
        _y += control.Height;
        return control;
    }

    public void Skip(int gap) => _y += gap;
}

/// <summary>Theme-based control factories shared by the Stage B windows.</summary>
internal static class Widgets
{
    private static readonly Dictionary<(float Size, FontStyle Style, bool Mono), Font> Fonts = new();

    /// <summary>
    /// Shared fonts. Cards are rebuilt whenever the model changes, so a per-rebuild
    /// font would leak a GDI handle for every state change.
    /// </summary>
    public static Font Shared(float size, FontStyle style = FontStyle.Regular, bool mono = false)
    {
        var key = (size, style, mono);
        lock (Fonts)
        {
            if (Fonts.TryGetValue(key, out var font)) return font;
            var made = mono ? Theme.Mono(size, style) : Theme.Font(size, style);
            Fonts[key] = made;
            return made;
        }
    }

    /// <summary>A single-line label that ellipsizes rather than growing.</summary>
    public static Label Text(string text, Font font, Color ink, Color back, int width, int height = 0,
        ContentAlignment align = ContentAlignment.MiddleLeft)
    {
        return new Label
        {
            Text = text,
            Font = font,
            ForeColor = ink,
            BackColor = back,
            AutoSize = false,
            Width = Math.Max(1, width),
            Height = height > 0 ? height : font.Height + ScreenMetrics.Px(2),
            TextAlign = align,
            AutoEllipsis = true,
            UseMnemonic = false,
        };
    }

    /// <summary>A word-wrapping label sized to its text.</summary>
    public static Label Wrapped(string text, Font font, Color ink, Color back, int width, int maxHeight = 0)
    {
        var height = MeasureWrapped(text, font, width);
        if (maxHeight > 0) height = Math.Min(height, maxHeight);
        return new Label
        {
            Text = text,
            Font = font,
            ForeColor = ink,
            BackColor = back,
            AutoSize = false,
            Width = Math.Max(1, width),
            Height = height,
            TextAlign = ContentAlignment.TopLeft,
            UseMnemonic = false,
        };
    }

    /// <summary>Wrapped text height in pixels; the layout is measured, never guessed.</summary>
    public static int MeasureWrapped(string text, Font font, int width)
    {
        if (width <= 0 || text.Length == 0) return font.Height;
        var size = TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl);
        return Math.Max(font.Height, size.Height);
    }

    /// <summary>A capsule action button sized to its label.</summary>
    public static PillButton Pill(string text, Color ink, Color fill, Color stroke, int heightPoints = 22,
        float fontSize = 8.5f, ContentAlignment align = ContentAlignment.MiddleCenter)
    {
        var font = Shared(fontSize);
        var measured = TextRenderer.MeasureText(text, font);
        var padding = align == ContentAlignment.MiddleCenter ? BarPadPoints : BarPadPoints + 6;
        return new PillButton
        {
            Text = text,
            Font = font,
            Ink = ink,
            Fill = fill,
            Stroke = stroke,
            TextAlign = align,
            Height = ScreenMetrics.Px(heightPoints),
            Width = measured.Width + ScreenMetrics.Px(padding),
            Margin = new Padding(0, 0, ScreenMetrics.Px(5), ScreenMetrics.Px(4)),
        };
    }

    /// <summary>
    /// Horizontal padding a centred pill adds around its label. Kept small because
    /// these sit in rows that also have to hold a caret: every point of chrome here
    /// is a point the user cannot type into.
    /// </summary>
    private const int BarPadPoints = 12;

    /// <summary>An amber "Allow"-style primary pill: dark ink on a filled face.</summary>
    public static PillButton Primary(string text, Color face, int heightPoints = 22, float fontSize = 8.5f)
        => Pill(text, Color.FromArgb(230, 12, 16, 26), face, Color.Empty, heightPoints, fontSize);

    /// <summary>A sunken rounded well holding one or more text lines.</summary>
    public static HudPanel Well(int width, params (string Text, Font Font, Color Ink)[] lines)
    {
        var pad = ScreenMetrics.Px(7);
        var well = new HudPanel
        {
            Width = Math.Max(1, width),
            Fill = Theme.Well,
            Stroke = Color.FromArgb(40, Theme.Stroke),
            Radius = ScreenMetrics.Pxf(6),
        };
        if (lines.Length == 0)
        {
            well.Height = pad * 2;
            return well;
        }
        var stack = new Stack(well, pad, pad, width - pad * 2);
        var first = true;
        foreach (var (text, font, ink) in lines)
        {
            stack.Add(Wrapped(text, font, ink, Theme.Well, stack.Width), first ? 0 : ScreenMetrics.Px(3));
            first = false;
        }
        well.Height = stack.Bottom + pad;
        return well;
    }

    /// <summary>A wrapping pill row; returns a flow panel whose height fits its wrapped rows.</summary>
    public static FlowLayoutPanel PillRow(int width, params Control[] pills)
    {
        var flow = new FlowLayoutPanel
        {
            Width = Math.Max(1, width),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = Theme.Card,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        foreach (var pill in pills) flow.Controls.Add(pill);
        flow.Height = Math.Max(ScreenMetrics.Px(22), flow.GetPreferredSize(new Size(flow.Width, 0)).Height);
        return flow;
    }

    /// <summary>Clamps a window origin into a work area, so a dialog cannot open off-screen.</summary>
    public static Point ClampToWorkspace(Point originPixels, Size sizePixels, Rectangle workArea)
    {
        var x = Math.Min(Math.Max(originPixels.X, workArea.Left), Math.Max(workArea.Left, workArea.Right - sizePixels.Width));
        var y = Math.Min(Math.Max(originPixels.Y, workArea.Top), Math.Max(workArea.Top, workArea.Bottom - sizePixels.Height));
        return new Point(x, y);
    }
}
