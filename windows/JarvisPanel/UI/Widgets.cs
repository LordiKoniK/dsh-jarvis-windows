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
    /// <summary>WCAG AA for body text; the floor a caption may never drop below.</summary>
    private const double MinInkContrast = 4.5;

    /// <summary>How far the face and stroke move toward white while hovered.</summary>
    private const double HoverLift = 0.105;

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

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Lay an opaque base down on every paint. A Button is ControlStyles.Opaque,
        // so WinForms never calls OnPaintBackground here and each WM_PAINT starts
        // from the pixels the previous one left. PillFill is 8% white, so without
        // this base every repaint composited that 8% white again: a hover
        // lightened the capsule and no later paint, including the one MouseLeave
        // triggers, could bring it back, until the face was white and the
        // near-white caption vanished into it. Painting the base first makes the
        // paint idempotent: one paint and a hundred produce identical pixels.
        var behind = Parent?.BackColor ?? Theme.SurfaceOpaque;
        using (var back = new SolidBrush(behind)) g.FillRectangle(back, ClientRectangle);

        var rect = new RectangleF(0.5f, 0.5f, Math.Max(1, Width - 1f), Math.Max(1, Height - 1f));
        var radius = Radius < 0 ? Math.Min(rect.Width, rect.Height) / 2 : Radius;
        var state = StateNow();
        var fill = FillFor(state);
        Theme.FillRounded(g, rect, radius, fill);
        if (Stroke.A > 0) Theme.StrokeRounded(g, rect, radius, StrokeFor(state));

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
        TextRenderer.DrawText(g, Text, Font, box, InkFor(fill, behind, state), flags);
    }

    /// <summary>
    /// What the pill should look like right now, read from the live input state
    /// instead of remembered from MouseEnter/MouseLeave. A leave that never
    /// arrives (capture held by another window, the card moved out from under the
    /// cursor, a rebuild that swapped the control) would otherwise latch the
    /// highlight on forever, and no repaint could disprove it because the flag was
    /// the only evidence. Asking the cursor makes every paint authoritative on
    /// its own.
    /// </summary>
    private PillState StateNow()
    {
        if (!Enabled) return PillState.Disabled;
        if (!IsHandleCreated) return PillState.Rest;
        return RectangleToScreen(ClientRectangle).Contains(Control.MousePosition)
            ? PillState.Hover
            : PillState.Rest;
    }

    // These handlers only ask for a repaint; StateNow decides what it shows. That
    // keeps the event path as a hint and the cursor as the authority, so a missed
    // or duplicated event can never leave a stale highlight on screen.
    protected override void OnMouseEnter(EventArgs e)
    {
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        Invalidate();
        base.OnMouseLeave(e);
    }

    private Color FillFor(PillState state) => state switch
    {
        PillState.Hover => Lift(Fill),
        PillState.Disabled => Fade(Fill, 0.45),
        _ => Fill,
    };

    private Color StrokeFor(PillState state) => state switch
    {
        PillState.Hover => Lift(Stroke),
        PillState.Disabled => Fade(Stroke, 0.45),
        _ => Stroke,
    };

    /// <summary>
    /// A translucent face cannot be made lighter by moving its channels (white is
    /// already 255), so the lift goes into the alpha, which is what actually reads
    /// as a lighter capsule over a dark card. An opaque face (the amber primary
    /// pill) lifts its channels instead. Keeping the lift on the fill rather than
    /// on the ink is deliberate: near-white ink is the card's language, and a face
    /// that stays dark keeps that ink at roughly 9:1 instead of the 1:1 a white
    /// wash produces.
    /// </summary>
    private static Color Lift(Color c)
    {
        if (c.A is > 0 and < 255)
            return Color.FromArgb(Channel(c.A + (255 - c.A) * HoverLift), c.R, c.G, c.B);
        return Color.FromArgb(c.A,
            Channel(c.R + (255 - c.R) * HoverLift),
            Channel(c.G + (255 - c.G) * HoverLift),
            Channel(c.B + (255 - c.B) * HoverLift));
    }

    /// <summary>Dimming for the disabled state: only the alpha moves, so the hue survives.</summary>
    private static Color Fade(Color c, double factor) => Color.FromArgb(Channel(c.A * factor), c.R, c.G, c.B);

    private static int Channel(double v) => (int)Math.Min(255, Math.Max(0, Math.Round(v)));

    /// <summary>
    /// The caption colour for a face, checked against the pixels it will land on.
    /// The failure this exists to prevent is ink and fill drifting together: a
    /// near-white caption on a capsule that has been lifted toward white is
    /// unreadable no matter how carefully the palette was picked. Rest and hover
    /// keep the designed ink; the guard only fires when a state would otherwise be
    /// illegible, so the caption cannot become invisible in any state.
    /// </summary>
    private Color InkFor(Color fill, Color behind, PillState state)
    {
        var face = Over(fill, behind);
        // TextRenderer hands GDI a COLORREF, which has no alpha, so an ink colour is
        // drawn at full strength whatever its A is. The disabled dimming therefore
        // has to be blended into the RGB here - leaving it in the alpha (as the old
        // Shade(Ink, 0.5) did) silently did nothing.
        var ink = Opaque(Ink);
        if (state == PillState.Disabled) ink = Blend(ink, face, 0.5);
        if (Contrast(ink, face) >= MinInkContrast) return ink;

        var light = Color.White;
        var dark = Color.FromArgb(255, 8, 11, 18);
        return Contrast(light, face) >= Contrast(dark, face) ? light : dark;
    }

    /// <summary>Drops the alpha, because GDI will drop it anyway.</summary>
    private static Color Opaque(Color c) => Color.FromArgb(255, c.R, c.G, c.B);

    /// <summary>Moves a colour a fraction of the way toward another one.</summary>
    private static Color Blend(Color c, Color toward, double amount)
    {
        int Mix(int a, int b) => (int)Math.Round(a + (b - a) * amount);
        return Color.FromArgb(255, Mix(c.R, toward.R), Mix(c.G, toward.G), Mix(c.B, toward.B));
    }

    /// <summary>Composites a possibly translucent colour over an opaque one.</summary>
    private static Color Over(Color top, Color bottom)
    {
        if (top.A >= 255) return Color.FromArgb(255, top.R, top.G, top.B);
        var a = top.A / 255.0;
        int Mix(int t, int b) => (int)Math.Round(t * a + b * (1 - a));
        return Color.FromArgb(255, Mix(top.R, bottom.R), Mix(top.G, bottom.G), Mix(top.B, bottom.B));
    }

    /// <summary>WCAG contrast ratio between two opaque colours.</summary>
    private static double Contrast(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        if (la < lb) (la, lb) = (lb, la);
        return (la + 0.05) / (lb + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Linear(int v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    }

    /// <summary>The states a pill actually paints. There is no separate pressed
    /// look: a press used to be invisible and stays that way, because adding one
    /// would change how every card and the quick bar behave.</summary>
    private enum PillState { Rest, Hover, Disabled }
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
