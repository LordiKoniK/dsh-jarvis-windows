namespace JarvisPanel.Core;

/// <summary>
/// Pure geometry for snapping and the hover/quick-bar layouts (spec §4, §5.3, §6).
/// The Swift original works in AppKit screen coordinates (y up); this port uses
/// Win32 screen coordinates (y down), so "top" means the smaller y value. Every
/// vertical comparison is mirrored accordingly, but the layout maths is otherwise
/// identical.
/// </summary>
public static class Placement
{
    public const double OrbWindow = 140;
    public const double OrbRadius = 60;
    /// <summary>Measured from the window edge, which sits 10pt outside the visible orb.</summary>
    public const double SnapDistance = 10;
    public const double DragThreshold = 4;
    public const double ButtonRadius = 88;
    public const double ButtonSize = 30;
    public const double ButtonSpacingDegrees = 30;
    public const double ReadoutGap = 58;
    public const double CapsuleGap = 8;
    public const double CapsuleInset = 56;
    public const double BarGap = 12;
    public const double ScreenMargin = 4;

    public static PointD ClampOrigin(RectD frame, RectD visible)
    {
        return new PointD(
            Math.Min(Math.Max(frame.X, visible.MinX), visible.MaxX - frame.Width),
            Math.Min(Math.Max(frame.Y, visible.MinY), visible.MaxY - frame.Height));
    }

    /// <summary>Keeps the frame inside the workspace and off the Dock.</summary>
    public static PointD ClampOrigin(RectD frame, Workspace ws)
    {
        var o = ClampOrigin(frame, ws.Bounds);
        if (ws.Dock is not { } dock || ws.DockSide is not { } side) return o;
        var f = new RectD(o.X, o.Y, frame.Width, frame.Height);
        if (!(f.MinX < dock.MaxX && f.MaxX > dock.MinX && f.MinY < dock.MaxY && f.MaxY > dock.MinY))
            return o;
        switch (side)
        {
            case DockSide.Right: o = new PointD(dock.MinX - frame.Width, o.Y); break;
            case DockSide.Left: o = new PointD(dock.MaxX, o.Y); break;
            case DockSide.Bottom: o = new PointD(o.X, dock.MaxY); break;
            case DockSide.Top: o = new PointD(o.X, dock.MinY - frame.Height); break;
        }
        return o;
    }

    public static (PointD Origin, DockEdge? Dock) Snap(RectD frame, RectD visible, (double Lo, double Hi)? notchX)
        => Snap(frame, new Workspace(visible, null), notchX);

    /// <summary>
    /// Clamps into the workspace, then snaps flush to an edge or corner when within
    /// <see cref="SnapDistance"/>. Beside the Dock the edge is the Dock's inner side;
    /// past its ends the orb reaches the screen edge. A notch over the top edge never docks.
    /// </summary>
    public static (PointD Origin, DockEdge? Dock) Snap(RectD frame, Workspace ws, (double Lo, double Hi)? notchX)
    {
        var o = ClampOrigin(frame, ws);
        var f = new RectD(o.X, o.Y, frame.Width, frame.Height);
        var limits = ws.Bounds;
        if (ws.Dock is { } dock && ws.DockSide is { } side)
        {
            var alongY = f.MinY < dock.MaxY && f.MaxY > dock.MinY;
            var alongX = f.MinX < dock.MaxX && f.MaxX > dock.MinX;
            switch (side)
            {
                case DockSide.Right when alongY:
                    limits = new RectD(limits.MinX, limits.MinY, dock.MinX - limits.MinX, limits.Height);
                    break;
                case DockSide.Left when alongY:
                    limits = new RectD(dock.MaxX, limits.MinY, limits.MaxX - dock.MaxX, limits.Height);
                    break;
                case DockSide.Bottom when alongX:
                    limits = new RectD(limits.MinX, dock.MaxY, limits.Width, limits.MaxY - dock.MaxY);
                    break;
                case DockSide.Top when alongX:
                    limits = new RectD(limits.MinX, limits.MinY, limits.Width, dock.MinY - limits.MinY);
                    break;
            }
        }

        var visible = limits;
        var left = f.MinX - visible.MinX <= SnapDistance;
        var right = visible.MaxX - f.MaxX <= SnapDistance;
        var top = f.MinY - visible.MinY <= SnapDistance;
        var bottom = visible.MaxY - f.MaxY <= SnapDistance;
        if (top && notchX is { } notch && f.MidX >= notch.Lo && f.MidX <= notch.Hi) top = false;

        DockEdge? edge;
        if (left && top) edge = DockEdge.TopLeft;
        else if (right && top) edge = DockEdge.TopRight;
        else if (left && bottom) edge = DockEdge.BottomLeft;
        else if (right && bottom) edge = DockEdge.BottomRight;
        else if (left) edge = DockEdge.Left;
        else if (right) edge = DockEdge.Right;
        else if (top) edge = DockEdge.Top;
        else if (bottom) edge = DockEdge.Bottom;
        else edge = null;

        if (edge is not { } e) return (o, null);

        switch (e)
        {
            case DockEdge.Left or DockEdge.TopLeft or DockEdge.BottomLeft:
                o = new PointD(visible.MinX, o.Y);
                break;
            case DockEdge.Right or DockEdge.TopRight or DockEdge.BottomRight:
                o = new PointD(visible.MaxX - frame.Width, o.Y);
                break;
        }
        switch (e)
        {
            case DockEdge.Top or DockEdge.TopLeft or DockEdge.TopRight:
                o = new PointD(o.X, visible.MinY);
                break;
            case DockEdge.Bottom or DockEdge.BottomLeft or DockEdge.BottomRight:
                o = new PointD(o.X, visible.MaxY - frame.Height);
                break;
        }
        return (o, e);
    }

    /// <summary>Screen rects where hovering a collapsed strip slides the orb out.</summary>
    public static List<RectD> StripRects(RectD f, DockEdge edge)
    {
        const double t = 22, span = 120, arm = 100;
        return edge switch
        {
            DockEdge.Right => new List<RectD> { new(f.MaxX - t, f.MidY - span / 2, t, span) },
            DockEdge.Left => new List<RectD> { new(f.MinX, f.MidY - span / 2, t, span) },
            DockEdge.Top => new List<RectD> { new(f.MidX - span / 2, f.MinY, span, t) },
            DockEdge.Bottom => new List<RectD> { new(f.MidX - span / 2, f.MaxY - t, span, t) },
            DockEdge.TopRight => new List<RectD>
            {
                new(f.MaxX - t, f.MinY, t, arm),
                new(f.MaxX - arm, f.MinY, arm, t),
            },
            DockEdge.TopLeft => new List<RectD>
            {
                new(f.MinX, f.MinY, t, arm),
                new(f.MinX, f.MinY, arm, t),
            },
            DockEdge.BottomRight => new List<RectD>
            {
                new(f.MaxX - t, f.MaxY - arm, t, arm),
                new(f.MaxX - arm, f.MaxY - t, arm, t),
            },
            _ => new List<RectD>
            {
                new(f.MinX, f.MaxY - arm, t, arm),
                new(f.MinX, f.MaxY - t, arm, t),
            },
        };
    }

    /// <summary>
    /// Whether the cursor is on the orb window's live area. An opened strip keeps its
    /// collapsed area as well, so a cursor resting on the strip (outside the orb circle)
    /// doesn't make it collapse and reopen over and over.
    /// </summary>
    public static bool OverOrb(PointD p, RectD frame, DockEdge? strip, bool stripOpen)
    {
        var onStrip = strip is { } edge && StripRects(frame, edge).Any(r => r.Contains(p));
        if (strip is not null && !stripOpen) return onStrip;
        var dx = p.X - frame.MidX;
        var dy = p.Y - frame.MidY;
        return Math.Sqrt(dx * dx + dy * dy) <= OrbRadius || onStrip;
    }

    /// <summary>Interior side: the one with more horizontal room (ties go left).</summary>
    public static HSide InteriorSide(PointD center, RectD visible)
        => (visible.MaxX - center.X) > (center.X - visible.MinX) ? HSide.Right : HSide.Left;

    public static HoverLayout Hover(PointD center, RectD visible, int buttonCount, SizeD listSize, SizeD capsuleSize)
    {
        var side = InteriorSide(center, visible);
        var baseDeg = side == HSide.Left ? 180.0 : 0.0;
        var sign = side == HSide.Left ? -1.0 : 1.0;
        var mid = (buttonCount - 1) / 2.0;
        var buttons = new List<PointD>(buttonCount);
        for (var i = 0; i < buttonCount; i++)
        {
            var deg = baseDeg + sign * (mid - i) * ButtonSpacingDegrees;
            var rad = deg * Math.PI / 180;
            buttons.Add(new PointD(center.X + Math.Cos(rad) * ButtonRadius, center.Y + Math.Sin(rad) * ButtonRadius));
        }

        var half = ButtonSize / 2;
        var buttonRects = buttons.Select(b => new RectD(b.X - half, b.Y - half, ButtonSize, ButtonSize)).ToList();
        bool Fits(RectD r) => visible.Contains(r) && !buttonRects.Any(b => b.Intersects(r));

        var listX = side == HSide.Left ? center.X + ReadoutGap : center.X - ReadoutGap - listSize.Width;
        var list = new RectD(listX, center.Y - listSize.Height / 2, listSize.Width, listSize.Height);

        ReadoutPlacement readout;
        bool readoutOnRight;
        if (Fits(list))
        {
            readout = ReadoutPlacement.List(list);
            readoutOnRight = side == HSide.Left;
        }
        else
        {
            var upperHalf = center.Y <= visible.MidY;
            var y = upperHalf
                ? center.Y - OrbRadius - CapsuleGap - capsuleSize.Height
                : center.Y + OrbRadius + CapsuleGap;
            var x = side == HSide.Left ? center.X + CapsuleInset - capsuleSize.Width : center.X - CapsuleInset;
            x = Math.Min(Math.Max(x, visible.MinX), visible.MaxX - capsuleSize.Width);
            var capsule = new RectD(x, y, capsuleSize.Width, capsuleSize.Height);
            readout = Fits(capsule) ? ReadoutPlacement.Capsule(capsule) : ReadoutPlacement.None;
            readoutOnRight = side == HSide.Right;
        }

        var bx = readoutOnRight ? 1.0 : -1.0;
        var badge = new PointD(center.X + bx * Math.Cos(Math.PI / 4) * OrbRadius,
                               center.Y - Math.Sin(Math.PI / 4) * OrbRadius);
        return new HoverLayout(side, buttons, readout, badge);
    }

    /// <summary>Audit card opens beyond the hover controls, leaving playback buttons accessible.</summary>
    public static RectD AutoApprovals(PointD center, RectD visible, HoverLayout hover, int count)
    {
        var occupied = new RectD(center.X - OrbRadius, center.Y - OrbRadius, OrbRadius * 2, OrbRadius * 2);
        foreach (var button in hover.Buttons)
            occupied = occupied.Union(new RectD(button.X - ButtonSize / 2, button.Y - ButtonSize / 2, ButtonSize, ButtonSize));
        if (hover.Readout.Rect is { } rect) occupied = occupied.Union(rect);

        var width = Math.Min(320, Math.Max(0, visible.Width - ScreenMargin * 2));
        var height = Math.Min(88 + Math.Min(5, Math.Max(0, count)) * 60, Math.Max(0, visible.Height - ScreenMargin * 2));
        var x = hover.Side == HSide.Left ? occupied.MinX - width - 12 : occupied.MaxX + 12;
        return new RectD(
            Math.Min(Math.Max(x, visible.MinX + ScreenMargin), visible.MaxX - width - ScreenMargin),
            Math.Min(Math.Max(center.Y - height / 2, visible.MinY + ScreenMargin), visible.MaxY - height - ScreenMargin),
            width, height);
    }

    public static QuickBarLayout QuickBar(PointD center, RectD visible, SizeD barSize)
    {
        var side = InteriorSide(center, visible);
        var x = side == HSide.Left
            ? center.X - OrbRadius - BarGap - barSize.Width
            : center.X + OrbRadius + BarGap;
        var y = center.Y - barSize.Height / 2;
        y = Math.Min(Math.Max(y, visible.MinY + ScreenMargin), visible.MaxY - ScreenMargin - barSize.Height);
        var bar = new RectD(x, y, barSize.Width, barSize.Height);
        var growsUp = center.Y > visible.MidY;
        var limit = growsUp ? bar.MinY - visible.MinY - 6 : visible.MaxY - bar.MaxY - 6;
        return new QuickBarLayout(side, bar, growsUp, Math.Max(0, limit));
    }

    /// <summary>
    /// Keeps <paramref name="frame"/> inside <paramref name="visible"/> on both
    /// sides. <see cref="QuickBar"/> clamps only the far side and assumes the
    /// visible rect starts at the orb's origin, which is true on a single monitor;
    /// on a desktop whose monitors sit left of or above the primary, the rect's
    /// origin is negative and the near side needs clamping too. A frame larger than
    /// the visible rect is pinned to its origin rather than pushed off the far side.
    /// </summary>
    public static PointD ClampBothSides(RectD frame, RectD visible)
    {
        var x = frame.MinX;
        if (x < visible.MinX) x = visible.MinX;
        var maxX = visible.MaxX - frame.Width;
        if (maxX >= visible.MinX && x > maxX) x = maxX;

        var y = frame.MinY;
        if (y < visible.MinY) y = visible.MinY;
        var maxY = visible.MaxY - frame.Height;
        if (maxY >= visible.MinY && y > maxY) y = maxY;

        return new PointD(x, y);
    }
}

public enum HSide { Left, Right }

public enum DockSide { Left, Right, Top, Bottom }

public enum ReadoutKind { List, Capsule, None }

public readonly record struct ReadoutPlacement(ReadoutKind Kind, RectD Rect)
{
    public static ReadoutPlacement None => new(ReadoutKind.None, default);
    public static ReadoutPlacement List(RectD r) => new(ReadoutKind.List, r);
    public static ReadoutPlacement Capsule(RectD r) => new(ReadoutKind.Capsule, r);
}

public sealed class HoverLayout
{
    public HoverLayout(HSide side, List<PointD> buttons, ReadoutPlacement readout, PointD badge)
    {
        Side = side;
        Buttons = buttons;
        Readout = readout;
        Badge = badge;
    }

    /// <summary>Side of the orb the buttons open toward (the screen interior).</summary>
    public HSide Side { get; }
    /// <summary>Button centers, top to bottom.</summary>
    public List<PointD> Buttons { get; }
    public ReadoutPlacement Readout { get; }
    public PointD Badge { get; }
}

public sealed class QuickBarLayout
{
    public QuickBarLayout(HSide side, RectD bar, bool growsUp, double stackLimit)
    {
        Side = side;
        Bar = bar;
        GrowsUp = growsUp;
        StackLimit = stackLimit;
    }

    public HSide Side { get; }
    public RectD Bar { get; }
    /// <summary>Pending cards and history stack upward from the bar (else downward).</summary>
    public bool GrowsUp { get; }
    /// <summary>Space left for the stack in its growth direction.</summary>
    public double StackLimit { get; }
}

/// <summary>
/// Where the orb may sit: the screen outside the taskbar, with the taskbar's own
/// footprint as an obstacle. On Windows <c>visible</c> is the work area, so the
/// taskbar edge is excluded automatically.
/// </summary>
public sealed class Workspace
{
    public Workspace(RectD bounds, RectD? dock)
    {
        Bounds = bounds;
        Dock = dock;
        DockSide = dock is { } d
            ? (d.Height > d.Width
                ? (d.MidX > bounds.MidX ? Core.DockSide.Right : Core.DockSide.Left)
                : (d.MidY < bounds.MidY ? Core.DockSide.Top : Core.DockSide.Bottom))
            : null;
    }

    public RectD Bounds { get; }
    public RectD? Dock { get; }
    public DockSide? DockSide { get; }

    /// <summary>
    /// <paramref name="dockLength"/> is the taskbar's extent along its edge (centered on
    /// the screen); null treats the whole edge as occupied.
    /// </summary>
    public static Workspace Make(RectD screen, RectD visible, double? dockLength)
    {
        var topInset = visible.MinY - screen.MinY;
        var bottomInset = screen.MaxY - visible.MaxY;
        var boundsTop = topInset > 1 ? visible.MinY : screen.MinY;
        var boundsBottom = bottomInset > 1 ? visible.MaxY : screen.MaxY;
        var bounds = new RectD(screen.MinX, boundsTop, screen.Width, boundsBottom - boundsTop);

        var right = screen.MaxX - visible.MaxX;
        var left = visible.MinX - screen.MinX;
        var bottom = bottomInset;
        var top = topInset;

        (double Lo, double Hi) Span(double center, double lower, double upper)
        {
            if (dockLength is not { } len) return (lower, upper);
            return (Math.Max(lower, center - len / 2), Math.Min(upper, center + len / 2));
        }

        RectD? dock = null;
        if (right > 1 || left > 1)
        {
            var (y0, y1) = Span(screen.MidY, bounds.MinY, bounds.MaxY);
            var x = right > 1 ? visible.MaxX : screen.MinX;
            dock = new RectD(x, y0, Math.Max(right, left), y1 - y0);
        }
        else if (bottom > 1)
        {
            var (x0, x1) = Span(screen.MidX, bounds.MinX, bounds.MaxX);
            dock = new RectD(x0, screen.MinY + screen.Height - bottom, x1 - x0, bottom);
        }
        else if (top > 1)
        {
            var (x0, x1) = Span(screen.MidX, bounds.MinX, bounds.MaxX);
            dock = new RectD(x0, screen.MinY, x1 - x0, top);
        }
        return new Workspace(bounds, dock);
    }
}
