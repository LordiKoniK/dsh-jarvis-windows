using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using JarvisPanel.Core;

namespace JarvisPanel.UI;

/// <summary>
/// A borderless topmost window painted through <c>UpdateLayeredWindow</c>, so it
/// composites with real per-pixel alpha instead of a colour key. The macOS
/// panels are transparent NSPanels; this is the Windows equivalent.
/// </summary>
/// <remarks>
/// Two consequences of <c>UpdateLayeredWindow</c> shape the design:
/// <list type="bullet">
/// <item>The window never paints itself — every pixel comes from the ARGB bitmap
/// flushed by <see cref="Flush"/>, so <c>OnPaint</c> is not used.</item>
/// <item>Hit testing follows the alpha channel: fully transparent pixels pass
/// mouse input through to whatever is underneath. Controls therefore draw a
/// nearly invisible hit shape (alpha 1) wherever they must be clickable.</item>
/// </list>
/// </remarks>
internal abstract class LayeredWindow : Form
{
    /// <summary>Alpha used for invisible-but-clickable pixels (1/255).</summary>
    protected const int HitAlpha = 1;

    private readonly int _widthPx;
    private readonly int _heightPx;

    protected Bitmap Canvas { get; }
    protected Graphics G { get; }

    protected LayeredWindow(int widthPx, int heightPx)
    {
        _widthPx = Math.Max(1, widthPx);
        _heightPx = Math.Max(1, heightPx);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        MinimizeBox = false;
        MaximizeBox = false;
        ControlBox = false;
        Text = "Jarvis";
        ClientSize = new Size(_widthPx, _heightPx);
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);

        // 32bppPArgb keeps GDI+ output premultiplied, which is exactly what
        // AC_SRC_ALPHA expects; straight ARGB would fringe on every soft edge.
        Canvas = new Bitmap(_widthPx, _heightPx, PixelFormat.Format32bppPArgb);
        G = Graphics.FromImage(Canvas);
        G.SmoothingMode = SmoothingMode.AntiAlias;
        G.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        G.InterpolationMode = InterpolationMode.HighQualityBicubic;
        G.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // Layered for per-pixel alpha, tool-window to stay out of Alt+Tab,
            // no-activate so showing the orb never steals focus from the user.
            cp.ExStyle |= NativeMethods.WS_EX_LAYERED
                          | NativeMethods.WS_EX_TOOLWINDOW
                          | NativeMethods.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    /// <summary>Repaint the whole canvas from scratch. Implementations must clear first.</summary>
    protected abstract void Render();

    /// <summary>Redraw and push the bitmap to the window.</summary>
    protected void Redraw()
    {
        // Reset first: Graphics.Clear is affected by the current transform, so a
        // left-over scale/flip from the previous frame would clear somewhere off
        // the canvas and leave the last frame showing through.
        G.ResetTransform();
        G.Clear(Color.Transparent);
        Render();
        Flush();
    }

    /// <summary>Push the current canvas to the screen through UpdateLayeredWindow.</summary>
    protected void Flush()
    {
        if (!IsHandleCreated) return;

        var destination = new NativeMethods.POINT(Left, Top);
        var size = new NativeMethods.SIZE(_widthPx, _heightPx);
        var source = new NativeMethods.POINT(0, 0);
        var blend = new NativeMethods.BLENDFUNCTION
        {
            BlendOp = NativeMethods.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = NativeMethods.AC_SRC_ALPHA,
        };

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        var memDc = NativeMethods.CreateCompatibleDC(screenDc);
        var hBitmap = Canvas.GetHbitmap(Color.FromArgb(0));
        var previous = NativeMethods.SelectObject(memDc, hBitmap);
        try
        {
            NativeMethods.UpdateLayeredWindow(Handle, screenDc, ref destination, ref size, memDc, ref source,
                0, ref blend, NativeMethods.ULW_ALPHA);
        }
        finally
        {
            NativeMethods.SelectObject(memDc, previous);
            NativeMethods.DeleteObject(hBitmap);
            NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>Move the window without letting WinForms repaint it.</summary>
    protected void MoveTo(int x, int y)
    {
        if (Left == x && Top == y) return;
        Location = new Point(x, y);
        Flush();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Nothing: the layered bitmap owns every pixel.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
    }

    protected override bool ShowWithoutActivation => true;

    /// <summary>Keep the window above other topmost windows without activating it.</summary>
    protected void RaiseTopmost()
    {
        if (!IsHandleCreated) return;
        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            G.Dispose();
            Canvas.Dispose();
        }
        base.Dispose(disposing);
    }
}
