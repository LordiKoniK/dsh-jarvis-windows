using JarvisPanel.Core;

namespace JarvisPanel.UI;

/// <summary>
/// One monitor in layout units. The macOS sources work in points; Windows
/// reports physical pixels, so every rectangle is divided by the monitor's
/// DPI scale before it reaches <see cref="Placement"/>.
/// </summary>
internal sealed record MonitorMetrics(string DeviceName, RectD Bounds, RectD WorkArea, double Scale)
{
    public double ToPoints(double pixels) => pixels / Scale;
    public double ToPixels(double points) => points * Scale;
    public RectD BoundsPoints => new(Bounds.X / Scale, Bounds.Y / Scale, Bounds.Width / Scale, Bounds.Height / Scale);
    public RectD WorkAreaPoints => new(WorkArea.X / Scale, WorkArea.Y / Scale,
        WorkArea.Width / Scale, WorkArea.Height / Scale);
}

internal static class ScreenMetrics
{
    /// <summary>Points → physical pixels for the monitor the panel is on.</summary>
    public static double Scale { get; private set; } = 1.0;

    public static int Px(double points) => (int)Math.Round(points * Scale);
    public static float Pxf(double points) => (float)(points * Scale);
    public static double Pt(double pixels) => pixels / Scale;

    public static void SetScale(double scale) => Scale = scale <= 0 ? 1.0 : scale;

    /// <summary>Metrics for the monitor containing <paramref name="screenPointPx"/>.</summary>
    public static MonitorMetrics ForPoint(System.Drawing.Point screenPointPx)
    {
        var screen = Screen.FromPoint(screenPointPx);
        var scale = ScaleFor(screenPointPx);
        var b = screen.Bounds;
        var w = screen.WorkingArea;
        return new MonitorMetrics(screen.DeviceName,
            new RectD(b.X, b.Y, b.Width, b.Height),
            new RectD(w.X, w.Y, w.Width, w.Height),
            scale);
    }

    public static MonitorMetrics ForPrimary()
    {
        var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        var b = screen.Bounds;
        var w = screen.WorkingArea;
        return new MonitorMetrics(screen.DeviceName,
            new RectD(b.X, b.Y, b.Width, b.Height),
            new RectD(w.X, w.Y, w.Width, w.Height),
            Scale);
    }

    /// <summary>Effective DPI scale of the monitor under a screen point.</summary>
    public static double ScaleFor(System.Drawing.Point screenPointPx)
    {
        try
        {
            var monitor = NativeMethods.MonitorFromPoint(
                new NativeMethods.POINT(screenPointPx.X, screenPointPx.Y), NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero &&
                NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 &&
                dpiX > 0)
            {
                return dpiX / 96.0;
            }
        }
        catch (DllNotFoundException)
        {
            // shcore.dll is missing on very old systems; fall back to 96 DPI.
        }
        catch (EntryPointNotFoundException)
        {
        }
        return 1.0;
    }

    /// <summary>Workspace (screen minus taskbar) for the monitor under a point, in points.</summary>
    public static Workspace WorkspaceFor(System.Drawing.Point screenPointPx)
    {
        var m = ForPoint(screenPointPx);
        return Workspace.Make(m.BoundsPoints, m.WorkAreaPoints, null);
    }
}

/// <summary>
/// The bridge between Win32 pixels and shared-layout points.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both spaces are absolute screen coordinates.</b> <see cref="MonitorMetrics"/>
/// divides raw device pixels by the monitor scale to get points, so a point is the
/// device pixel divided by the scale and nothing else — the monitor's origin is
/// already part of both numbers. Conversion is therefore a bare multiply or divide,
/// never a multiply plus an origin.
/// </para>
/// <para>
/// The trap is that a monitor's origin is non-zero (often negative), so adding the
/// origin a second time looks correct on the primary display, where it is zero, and
/// silently displaces everything by a whole monitor on any other display.
/// <see cref="MonitorMetrics.BoundsPoints"/> and <see cref="MonitorMetrics.WorkAreaPoints"/>
/// are in this same space, and so is every rectangle <see cref="Placement"/> returns.
/// </para>
/// </remarks>
internal static class GeometryConversion
{
    /// <summary>Physical screen pixels of this rectangle in the current point scale.</summary>
    public static RectD ToPoints(this RectD pixels) => pixels.Scale(1 / ScreenMetrics.Scale);

    /// <summary>Absolute screen points → physical screen pixels.</summary>
    public static System.Drawing.Point ToPixels(this RectD frame, double scale)
        => new((int)Math.Round(frame.X * scale), (int)Math.Round(frame.Y * scale));

    /// <summary>Absolute screen points → physical screen pixels.</summary>
    public static System.Drawing.Point ToPixels(this PointD point, double scale)
        => new((int)Math.Round(point.X * scale), (int)Math.Round(point.Y * scale));

    /// <summary>Scales a rectangle's geometry about its own origin.</summary>
    public static RectD Scale(this RectD r, double factor)
        => new(r.X * factor, r.Y * factor, r.Width * factor, r.Height * factor);

    /// <summary>
    /// Whether the orb should hold its particles still. The macOS build asks the
    /// system through <c>SystemObserver</c>; on Windows the user's "show animations
    /// in Windows" accessibility flag is the same signal, and the panel's own
    /// setting has to agree before it applies.
    /// </summary>
    public static bool ReduceMotion(this PanelSettings settings)
    {
        if (!settings.FollowReduceMotion) return false;
        try
        {
            return !SystemInformation.IsMenuAnimationEnabled;
        }
        catch
        {
            return false;
        }
    }
}
