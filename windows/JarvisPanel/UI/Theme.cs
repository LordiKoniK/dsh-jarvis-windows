using System.Drawing;
using System.Drawing.Drawing2D;
using JarvisPanel.Core;

namespace JarvisPanel.UI;

/// <summary>Colours, fonts and HUD surfaces shared by every window.</summary>
internal static class Theme
{
    public static readonly Color Cyan = Color.FromArgb(92, 225, 255);
    public static readonly Color Amber = Color.FromArgb(255, 194, 77);
    public static readonly Color Red = Color.FromArgb(255, 95, 95);
    public static readonly Color Violet = Color.FromArgb(178, 140, 255);

    public static readonly Color Text = Color.FromArgb(235, 255, 255, 255);
    public static readonly Color Dim = Color.FromArgb(140, 255, 255, 255);
    public static readonly Color Faint = Color.FromArgb(90, 255, 255, 255);

    /// <summary>Dark translucent HUD surface; Windows draws it opaque (see README).</summary>
    public static readonly Color Surface = Color.FromArgb(10, 16, 30);
    public static readonly Color SurfaceOpaque = Color.FromArgb(10, 16, 30);
    /// <summary>Opaque card face raised above <see cref="SurfaceOpaque"/> (Stage B cards).</summary>
    public static readonly Color Card = Color.FromArgb(22, 33, 56);
    /// <summary>Opaque sunken well for commands, questions and history bubbles.</summary>
    public static readonly Color Well = Color.FromArgb(12, 18, 32);
    public static readonly Color Stroke = Color.FromArgb(56, 92, 225, 255);
    public static readonly Color TrackFill = Color.FromArgb(28, 255, 255, 255);
    public static readonly Color PillFill = Color.FromArgb(20, 255, 255, 255);
    public static readonly Color PillStroke = Color.FromArgb(36, 255, 255, 255);

    public const int Radius = 12;

    private static string _family = "";
    private static string _monoFamily = "";

    /// <summary>
    /// First installed face from a preference list. Segoe UI Variable is the
    /// Windows 11 UI face and Segoe UI the Windows 10 one; the CJK faces come last
    /// because although the deployment is English, a session title or a workspace
    /// path can still carry Chinese and must not render as boxes.
    /// </summary>
    public static Font Font(float size, FontStyle style = FontStyle.Regular)
    {
        if (_family.Length == 0)
        {
            foreach (var name in new[] { "Segoe UI Variable Text", "Segoe UI", "Microsoft YaHei UI", "Microsoft YaHei" })
            {
                try
                {
                    using var probe = new FontFamily(name);
                    _family = name;
                    break;
                }
                catch (ArgumentException)
                {
                    // Not installed; try the next candidate.
                }
            }
            if (_family.Length == 0) _family = FontFamily.GenericSansSerif.Name;
        }
        return new Font(_family, size, style, GraphicsUnit.Point);
    }

    /// <summary>Monospaced face for commands and fingerprints, where alignment carries meaning.</summary>
    public static Font Mono(float size, FontStyle style = FontStyle.Regular)
    {
        if (_monoFamily.Length == 0)
        {
            foreach (var name in new[] { "Cascadia Mono", "Consolas", "Courier New" })
            {
                try
                {
                    using var probe = new FontFamily(name);
                    _monoFamily = name;
                    break;
                }
                catch (ArgumentException)
                {
                    // Not installed; try the next candidate.
                }
            }
            if (_monoFamily.Length == 0) _monoFamily = FontFamily.GenericMonospace.Name;
        }
        return new Font(_monoFamily, size, style, GraphicsUnit.Point);
    }

    public static Color Tint(OrbTint tint) => tint switch
    {
        OrbTint.Cyan => Cyan,
        OrbTint.Amber => Amber,
        OrbTint.Red => Red,
        _ => Violet,
    };

    public static Color Status(SessionStatus? status) => status switch
    {
        SessionStatus.Running => Cyan,
        SessionStatus.Waiting => Amber,
        SessionStatus.Failed => Red,
        SessionStatus.Done => Color.FromArgb(217, 255, 255, 255),
        _ => Faint,
    };

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRounded(Graphics g, RectangleF r, float radius, Color fill)
    {
        using var path = RoundedRect(r, radius);
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);
    }

    public static void StrokeRounded(Graphics g, RectangleF r, float radius, Color stroke, float width = 1f)
    {
        using var path = RoundedRect(r, radius);
        using var pen = new Pen(stroke, width);
        g.DrawPath(pen, path);
    }
}
