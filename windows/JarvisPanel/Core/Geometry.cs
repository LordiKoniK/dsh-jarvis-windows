namespace JarvisPanel.Core;

/// <summary>
/// Double-precision point in screen coordinates. Windows screen space is y-down,
/// so "top" means the smaller y. The Swift sources use y-up; every ported formula
/// below flips the vertical terms explicitly so the on-screen result matches.
/// </summary>
public readonly record struct PointD(double X, double Y)
{
    public static PointD operator +(PointD a, PointD b) => new(a.X + b.X, a.Y + b.Y);
    public static PointD operator -(PointD a, PointD b) => new(a.X - b.X, a.Y - b.Y);
}

public readonly record struct SizeD(double Width, double Height);

/// <summary>Axis-aligned rectangle in y-down screen coordinates.</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double MinX => X;
    public double MinY => Y;
    public double MaxX => X + Width;
    public double MaxY => Y + Height;
    public double MidX => X + Width / 2;
    public double MidY => Y + Height / 2;
    public PointD Origin => new(X, Y);
    public SizeD Size => new(Width, Height);

    public static RectD FromLtrb(double left, double top, double right, double bottom)
        => new(left, top, right - left, bottom - top);

    public static RectD FromCenter(PointD center, double width, double height)
        => new(center.X - width / 2, center.Y - height / 2, width, height);

    public bool Contains(PointD p) => p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;

    public bool Contains(RectD r) => r.MinX >= MinX && r.MaxX <= MaxX && r.MinY >= MinY && r.MaxY <= MaxY;

    /// <summary>Half-open overlap test, matching CGRect.intersects for non-empty rects.</summary>
    public bool Intersects(RectD r) => r.MinX < MaxX && r.MaxX > MinX && r.MinY < MaxY && r.MaxY > MinY;

    public RectD Union(RectD r)
    {
        var left = Math.Min(MinX, r.MinX);
        var top = Math.Min(MinY, r.MinY);
        var right = Math.Max(MaxX, r.MaxX);
        var bottom = Math.Max(MaxY, r.MaxY);
        return FromLtrb(left, top, right, bottom);
    }

    public RectD WithOrigin(PointD p) => new(p.X, p.Y, Width, Height);

    public PointD ClampOrigin(RectD bounds)
        => new(Math.Min(Math.Max(X, bounds.MinX), bounds.MaxX - Width),
               Math.Min(Math.Max(Y, bounds.MinY), bounds.MaxY - Height));

    public override string ToString() => $"({X:0.#},{Y:0.#} {Width:0.#}x{Height:0.#})";
}
