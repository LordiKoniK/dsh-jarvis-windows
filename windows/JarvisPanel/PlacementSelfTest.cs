using JarvisPanel.Core;
using JarvisPanel.UI;

namespace JarvisPanel;

/// <summary>
/// A headless check of the quick bar's placement maths, run with
/// <c>jarvis-panel.exe --selftest</c>. Writes a report to
/// <c>%TEMP%\jarvis-selftest.txt</c> and exits 0 on success.
/// </summary>
/// <remarks>
/// <para>
/// The panel targets a multi-monitor desktop, but the machine building it may have
/// one screen — and a display whose origin is not zero is exactly where the
/// pixel/point convention is easiest to get wrong. This exercises the real
/// <see cref="Workspace"/>, <see cref="Placement"/> and
/// <see cref="GeometryConversion"/> against synthetic monitor geometry, so that
/// case is covered without a second monitor to test on. It earned its place: it
/// caught <c>ToPixels</c> adding the monitor origin on top of points that already
/// contained it, which put the quick bar a full monitor away from the orb.
/// </para>
/// <para>
/// It never touches a window, so it is safe in a headless run.
/// </para>
/// </remarks>
internal static class PlacementSelfTest
{
    private const double BarWidth = 320;
    private const double BarHeight = 44;

    private static int _failures;

    public static int Run()
    {
        var lines = new List<string> { "quick bar placement self-test (absolute screen points -> pixels)" };

        lines.Add("");
        lines.Add("== primary monitor, orb docked top-right (the observed configuration) ==");
        Check(lines, "1920x1080 @1.0x, orb top-right", 0, 0, 1920, 1080, 1.0, 1850, 96);

        lines.Add("");
        lines.Add("== secondary monitor to the LEFT (negative origin) ==");
        Check(lines, "1920x1080 @1.0x, orb at its right edge", -1920, 0, 1920, 1080, 1.0, -70, 96);
        Check(lines, "1920x1080 @1.0x, orb at its LEFT edge", -1920, 0, 1920, 1080, 1.0, -1850, 96);
        Check(lines, "1920x1080 @1.0x, orb mid-screen", -1920, 0, 1920, 1080, 1.0, -960, 800);
        Check(lines, "2560x1440 @1.0x, orb at its right edge", -2560, 0, 2560, 1440, 1.0, -70, 130);

        lines.Add("");
        lines.Add("== secondary monitor to the RIGHT (positive origin) ==");
        Check(lines, "1920x1080 @1.0x, orb at its left edge", 1920, 0, 1920, 1080, 1.0, 1990, 96);
        Check(lines, "1920x1080 @1.0x, orb mid-screen", 1920, 0, 1920, 1080, 1.0, 2880, 400);

        lines.Add("");
        lines.Add("== secondary monitor ABOVE (negative y origin) ==");
        Check(lines, "1920x1080 @1.0x, orb at its bottom edge", 0, -1080, 1920, 1080, 1.0, 960, -70);
        Check(lines, "1920x1080 @1.0x, orb mid-screen", 0, -1080, 1920, 1080, 1.0, 960, -540);

        lines.Add("");
        lines.Add("== scaled displays ==");
        Check(lines, "2560x1440 @1.5x, orb top-right", 0, 0, 2560, 1440, 1.5, 2460, 130);
        Check(lines, "3840x2160 @2.0x, orb top-right", 0, 0, 3840, 2160, 2.0, 3690, 200);
        Check(lines, "1920x1080 @1.25x, secondary left", -1920, 0, 1920, 1080, 1.25, -90, 120);

        lines.Add("");
        lines.Add(_failures == 0
            ? "RESULT: PASS - every placement lands on the correct monitor, at the correct distance from the orb."
            : $"RESULT: FAIL - {_failures} failure(s).");

        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "jarvis-selftest.txt"), lines);
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>Rebuilds exactly what <c>QuickBarWindow.Place</c> does, from explicit monitor geometry.</summary>
    private static void Check(List<string> lines, string name, int originX, int originY,
        int widthPx, int heightPx, double scale, int orbX, int orbY)
    {
        // MonitorMetrics.ToPoints / WorkAreaPoints, restated for a synthetic monitor.
        // A 40px taskbar is assumed along the bottom.
        var bounds = new RectD(originX / scale, originY / scale, widthPx / scale, heightPx / scale);
        var work = new RectD(originX / scale, originY / scale, widthPx / scale, heightPx / scale - 40 / scale);
        var workspace = Workspace.Make(bounds, work, null);

        // Absolute screen points in, absolute screen points out, then a bare multiply.
        var centre = new PointD(orbX / scale, orbY / scale);
        var layout = Placement.QuickBar(centre, workspace.Bounds, new SizeD(BarWidth, BarHeight));
        var origin = Placement.ClampBothSides(layout.Bar, workspace.Bounds);
        var got = origin.ToPixels(scale);

        var barW = (int)Math.Round(BarWidth * scale);
        var barH = (int)Math.Round(BarHeight * scale);
        var onMonitor = got.X >= originX && got.X + barW <= originX + widthPx
                        && got.Y >= originY && got.Y + barH <= originY + heightPx;

        // The layout's contract: the bar's near edge is OrbRadius + BarGap from the
        // orb's centre. OrbRadius (60), not OrbWindow/2 (70): the window carries a
        // 10pt transparent margin around the visible orb.
        var wantEdgeGap = (Placement.OrbRadius + Placement.BarGap) * scale;
        var edgeGap = layout.Side == HSide.Left ? orbX - (got.X + barW) : got.X - orbX;
        // A clamped bar may be pushed further out, so the gap is a lower bound.
        var gapOk = edgeGap >= wantEdgeGap - scale - 0.5;

        if (!onMonitor) _failures++;
        if (!gapOk) _failures++;

        lines.Add($"{name,-40} orb=({orbX,6},{orbY,5}) bar=({got.X,6},{got.Y,5}) " +
                  $"side={layout.Side,-5} edgeGap={edgeGap,5}px (min {wantEdgeGap:0.#}) " +
                  $"onMonitor={onMonitor} gapOk={gapOk}");
    }
}
