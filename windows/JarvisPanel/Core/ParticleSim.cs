namespace JarvisPanel.Core;

/// <summary>
/// One quad for the renderer. Coordinates are orb units (1 = 55.2pt, y up);
/// <c>Size</c> is the sprite side in the same units.
/// </summary>
public readonly record struct PointSprite(float X, float Y, float Size, float Alpha);

public enum MotionRegion { Core, Ring1, Ring2 }

/// <summary>Per-state motion parameters, verbatim from <c>states-v5.html</c> (<c>STATES</c>).</summary>
public sealed class MotionParams
{
    public MotionRegion Region { get; init; }
    public double Retarget { get; init; }
    public double Hop { get; init; }
    public (double Low, double High) Orbit { get; init; }
    public double Jitter { get; init; }
    public double KMul { get; init; }
    public double Base { get; init; }
    public double SpeedGlow { get; init; } = 0.35;
    public double Noise { get; init; }
    public bool Beat { get; init; }
    public bool Flicker { get; init; }

    public static MotionParams Params(OrbMotion m) => m switch
    {
        OrbMotion.Idle => new MotionParams
        {
            Region = MotionRegion.Core, Retarget = 0.10, Hop = Math.PI * 2, Orbit = (0, 0),
            Jitter = 0.012, KMul = 0.55, Base = 0.22,
        },
        OrbMotion.Awaiting => new MotionParams
        {
            Region = MotionRegion.Ring1, Retarget = 0.06, Hop = 0.6, Orbit = (0.04, 0.3),
            Jitter = 0.008, KMul = 0.8, Base = 0.32,
        },
        OrbMotion.Thinking => new MotionParams
        {
            Region = MotionRegion.Ring1, Retarget = 1.8, Hop = 0.9, Orbit = (0, 0),
            Jitter = 0.004, KMul = 1.7, Base = 0.2, SpeedGlow = 1.1,
        },
        OrbMotion.Attention => new MotionParams
        {
            Region = MotionRegion.Ring2, Retarget = 0.05, Hop = 0.5, Orbit = (0.02, 0.08),
            Jitter = 0.006, KMul = 0.9, Base = 0.35, Beat = true,
        },
        _ => new MotionParams
        {
            Region = MotionRegion.Ring1, Retarget = 0.7, Hop = 0.4, Orbit = (0, 0),
            Jitter = 0.004, KMul = 2.0, Base = 0.3, Noise = 2.6, Flicker = true,
        },
    };
}

/// <summary>
/// Independent spring-driven particles (spec §2.2). Each particle owns its
/// stiffness, damping, orbit speed and twinkle phase, and travels to its own
/// slot, so state changes read as many individuals moving rather than a shape
/// morphing.
/// </summary>
public sealed class ParticleSim
{
    public const double R0 = 0.40;
    public const double RingWidth = 0.30;
    public const double CoreMin = 0;
    public const double CoreMax = 0.40 * 0.93;
    public const double Ring1Min = 0.42;
    public const double Ring1Max = 0.68;
    public const double Ring2Min = 0.72;
    public const double Ring2Max = 0.98;
    /// <summary>Points per orb unit: 120pt orb, drawn at 92% of its radius.</summary>
    public const double UnitPoints = 55.2;
    /// <summary>Half of the 140pt orb window, in orb units.</summary>
    public const double WindowHalf = 70.0 / 55.2;
    private const double SpriteUnits = 3.6 / 55.2;

    private const double Tau = Math.PI * 2;
    private static readonly double Gold = Math.PI * (3 - Math.Sqrt(5));

    private struct Particle
    {
        public double X, Y, Vx, Vy;
        public double Tr, Tth;
        public double Ntr, Nth;
        public double? DepartAt;
        public double Om;
        public double K, Zeta;
        public double Ph1, Ph2, F1, F2, Tw, Sz;
        /// <summary>Fixed random coordinates used to lay out the dock strip.</summary>
        public double U, V;
    }

    private readonly List<Particle> _parts = new();
    private SeededRandom _rng;
    private int _lastBeatSlot = -1;

    public OrbMotion Motion { get; private set; } = OrbMotion.Idle;
    public DockEdge? Dock { get; private set; }
    public bool ReduceMotion { get; set; }

    public ParticleSim(int count, ulong seed)
    {
        _rng = new SeededRandom(seed);
        for (var i = 0; i < count; i++) _parts.Add(MakeParticle(i, count));
    }

    public int Count => _parts.Count;

    public IReadOnlyList<(double X, double Y)> Positions()
        => _parts.Select(p => (p.X, p.Y)).ToList();

    private Particle MakeParticle(int slotIndex, int n)
    {
        var slot = Slots(n, CoreMin, CoreMax)[slotIndex];
        var p = new Particle
        {
            X = Math.Cos(slot.Th) * slot.R,
            Y = Math.Sin(slot.Th) * slot.R,
            Tr = slot.R,
            Tth = slot.Th,
            K = _rng.Range(10, 28),
            Zeta = _rng.Range(0.55, 0.9),
            Ph1 = _rng.Range(0, Tau),
            Ph2 = _rng.Range(0, Tau),
            F1 = _rng.Range(0.6, 1.6),
            F2 = _rng.Range(0.6, 1.6),
            Tw = _rng.Range(0, Tau),
            Sz = _rng.Range(0.7, 1.3),
            U = _rng.Unit(),
            V = _rng.Unit(),
        };
        return p;
    }

    // MARK: Layout helpers

    private static List<(double R, double Th)> Slots(int n, double a, double b)
    {
        var list = new List<(double, double)>(n);
        for (var i = 0; i < n; i++)
        {
            var f = (i + 0.5) / n;
            var r = Math.Sqrt(a * a + (b * b - a * a) * f);
            var th = i * Gold % Tau;
            list.Add((r, th));
        }
        return list;
    }

    private static (double Min, double Max) RangeFor(MotionRegion region) => region switch
    {
        MotionRegion.Core => (CoreMin, CoreMax),
        MotionRegion.Ring1 => (Ring1Min, Ring1Max),
        _ => (Ring2Min, Ring2Max),
    };

    /// <summary>Double-beat heart pulse, period 1.6s.</summary>
    public static double Pulse(double t)
    {
        var ph = t % 1.6;
        return Math.Exp(-ph * 7) + (ph > 0.28 ? 0.7 * Math.Exp(-(ph - 0.28) * 7) : 0);
    }

    // MARK: State changes

    public void SetMotion(OrbMotion m, double t)
    {
        Motion = m;
        if (Dock is null) Assign(t);
    }

    public void SetDock(DockEdge? edge, double t)
    {
        if (edge == Dock) return;
        Dock = edge;
        if (edge is null) Assign(t);
    }

    public void SetCount(int n, double t)
    {
        if (n == _parts.Count || n <= 0) return;
        if (n < _parts.Count)
        {
            _parts.RemoveRange(n, _parts.Count - n);
        }
        else
        {
            for (var i = _parts.Count; i < n; i++) _parts.Add(MakeParticle(i, n));
        }
        if (Dock is null) Assign(t);
    }

    /// <summary>
    /// Sorted-angle matching so every particle travels radially to a nearby slot,
    /// departing with an angle-staggered delay.
    /// </summary>
    private void Assign(double t)
    {
        var st = MotionParams.Params(Motion);
        var n = _parts.Count;
        if (n == 0) return;
        var (rMin, rMax) = RangeFor(st.Region);
        var slots = Slots(n, rMin, rMax).OrderBy(s => s.Th).ToList();

        var order = new List<(int Index, double Angle)>(n);
        for (var i = 0; i < n; i++)
        {
            var a = Math.Atan2(_parts[i].Y, _parts[i].X);
            order.Add((i, a < 0 ? a + Tau : a));
        }
        order.Sort((x, y) => x.Angle.CompareTo(y.Angle));

        var offset = (int)(_rng.Unit() * n);
        for (var rank = 0; rank < order.Count; rank++)
        {
            var (i, a) = order[rank];
            var s = slots[(rank + offset) % n];
            var th = s.Th;
            while (th - a > Math.PI) th -= Tau;
            while (a - th > Math.PI) th += Tau;

            var p = _parts[i];
            p.Ntr = s.R;
            p.Nth = th;
            if (st.Orbit.High > 0)
            {
                var om = _rng.Range(st.Orbit.Low, st.Orbit.High);
                p.Om = om * (_rng.Unit() < 0.85 ? 1 : -1);
            }
            else
            {
                p.Om = 0;
            }

            if (ReduceMotion)
            {
                p.Tr = s.R;
                p.Tth = th;
                p.DepartAt = null;
                p.Om = 0;
                var r = s.R;
                p.X = Math.Cos(th) * r;
                p.Y = Math.Sin(th) * r;
                p.Vx = 0;
                p.Vy = 0;
            }
            else
            {
                p.DepartAt = t + a / Tau * 0.3 + _rng.Unit() * 0.25;
            }
            _parts[i] = p;
        }
    }

    // MARK: Stepping

    public void Step(double t, double dt)
    {
        if (Dock is { } dock)
        {
            StepDocked(dock, t, dt);
            return;
        }

        var st = MotionParams.Params(Motion);
        var (regionMin, regionMax) = RangeFor(st.Region);
        var still = ReduceMotion;

        var kick = 0.0;
        if (st.Beat && !still)
        {
            var ph = t % 1.6;
            var slot = (int)(t / 1.6) * 2 + (ph >= 0.28 ? 1 : 0);
            if (slot != _lastBeatSlot)
            {
                kick = ph >= 0.28 ? 0.7 : 1;
                _lastBeatSlot = slot;
            }
        }
        var jitter = still ? 0 : st.Jitter;
        var vmax = 2.4 * Math.Sqrt(st.KMul);

        for (var i = 0; i < _parts.Count; i++)
        {
            var p = _parts[i];
            if (p.DepartAt is { } at && t >= at)
            {
                p.Tr = p.Ntr;
                p.Tth = p.Nth;
                p.DepartAt = null;
            }
            if (p.DepartAt is null && !still)
            {
                if (st.Retarget > 0 && _rng.Unit() < st.Retarget * dt)
                {
                    p.Tr = Math.Sqrt(regionMin * regionMin
                                     + (regionMax * regionMax - regionMin * regionMin) * _rng.Unit());
                    p.Tth = st.Hop >= Tau ? _rng.Unit() * Tau : p.Tth + (_rng.Unit() - 0.5) * st.Hop;
                }
                p.Tth += p.Om * dt;
            }

            var tr = p.Tr;

            var tx = Math.Cos(p.Tth) * tr + jitter * Math.Sin(t * p.F1 + p.Ph1);
            var ty = Math.Sin(p.Tth) * tr + jitter * Math.Cos(t * p.F2 + p.Ph2);
            var k = p.K * st.KMul;
            var c = 2 * Math.Sqrt(k) * p.Zeta;
            var ax = (tx - p.X) * k - p.Vx * c;
            var ay = (ty - p.Y) * k - p.Vy * c;
            if (st.Noise > 0 && !still)
            {
                ax += (_rng.Unit() - 0.5) * st.Noise * 2;
                ay += (_rng.Unit() - 0.5) * st.Noise * 2;
            }
            p.Vx += ax * dt;
            p.Vy += ay * dt;
            if (kick > 0)
            {
                var d = Math.Max(Math.Sqrt(p.X * p.X + p.Y * p.Y), 1e-6);
                var f = kick * _rng.Range(0.15, 0.45);
                p.Vx += p.X / d * f;
                p.Vy += p.Y / d * f;
            }
            var sp = Math.Sqrt(p.Vx * p.Vx + p.Vy * p.Vy);
            if (sp > vmax)
            {
                p.Vx *= vmax / sp;
                p.Vy *= vmax / sp;
            }
            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;
            _parts[i] = p;
        }
    }

    /// <summary>
    /// Dock style B (<c>dock.html</c>): a thin particle strip along the edge, an
    /// L at corners.
    /// </summary>
    private void StepDocked(DockEdge edge, double t, double dt)
    {
        var jitter = ReduceMotion ? 0 : 0.7 / UnitPoints;

        for (var i = 0; i < _parts.Count; i++)
        {
            var p = _parts[i];
            var target = DockTarget(edge, p.U, p.V);
            var tx = target.X + jitter * Math.Sin(t * p.F1 + p.Ph1);
            var ty = target.Y + jitter * Math.Cos(t * p.F2 + p.Ph2);
            var k = p.K * 1.2;
            var c = 2 * Math.Sqrt(k) * p.Zeta;
            p.Vx += ((tx - p.X) * k - p.Vx * c) * dt;
            p.Vy += ((ty - p.Y) * k - p.Vy * c) * dt;
            var sp = Math.Sqrt(p.Vx * p.Vx + p.Vy * p.Vy);
            if (sp > 12)
            {
                p.Vx *= 12 / sp;
                p.Vy *= 12 / sp;
            }
            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;
            _parts[i] = p;
        }
    }

    public static (double X, double Y) DockTarget(DockEdge edge, double u, double v)
    {
        const double h = WindowHalf;
        var th = (2 + 5 * u) / UnitPoints;
        var along = (v - 0.5) * 96 / UnitPoints;
        var len = 78 / UnitPoints;
        var inset = 3 / UnitPoints;
        switch (edge)
        {
            case DockEdge.Right: return (h - th, along);
            case DockEdge.Left: return (-(h - th), along);
            case DockEdge.Top: return (along, h - th);
            case DockEdge.Bottom: return (along, -(h - th));
            default:
                var sx = edge is DockEdge.TopRight or DockEdge.BottomRight ? 1.0 : -1.0;
                var sy = edge is DockEdge.TopRight or DockEdge.TopLeft ? 1.0 : -1.0;
                return v < 0.5
                    ? (sx * (h - th), sy * (h - inset - v * 2 * len))
                    : (sx * (h - inset - (v - 0.5) * 2 * len), sy * (h - th));
        }
    }

    // MARK: Rendering data

    public void Sprites(double t, List<PointSprite> buffer)
    {
        var st = MotionParams.Params(Motion);
        var pulse = st.Beat ? Pulse(t) : 0;
        var docked = Dock is not null;
        buffer.Clear();
        foreach (var p in _parts)
        {
            var sp = Math.Sqrt(p.Vx * p.Vx + p.Vy * p.Vy);
            var a = st.Base + 0.18 * Math.Sin(t * 2.2 + p.Tw * 3) + st.SpeedGlow * sp + 0.45 * pulse;
            if (st.Flicker) a *= 0.5 + 0.5 * Math.Abs(Math.Sin(t * (8 + p.F1 * 6) + p.Tw));
            if (docked) a *= 0.85;
            a = Math.Min(1, Math.Max(0.08, a));
            buffer.Add(new PointSprite((float)p.X, (float)p.Y, (float)(SpriteUnits * p.Sz), (float)a));
        }
    }
}
