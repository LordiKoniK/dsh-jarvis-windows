namespace JarvisPanel.Core;

/// <summary>
/// Render rate for the orb. Transitions (docking, undocking, state changes) always
/// run at full rate — at 20 fps a docking particle jumps ~30pt per frame — and only
/// the settled strip drops to its low idle rate.
/// </summary>
public static class FramePacing
{
    /// <summary>Long enough for the slowest particle to depart (≤0.55s) and settle.</summary>
    public const double TransitionSeconds = 1.2;

    public static int Fps(bool docked, OrbMotion motion, bool transitioning)
    {
        if (transitioning) return 60;
        if (docked) return 20;
        return motion == OrbMotion.Idle ? 30 : 60;
    }
}

/// <summary>Tint crossfade: ease-in-out so a colour change drifts in rather than snapping.</summary>
public static class ColorFade
{
    /// <summary>Shorter than <see cref="FramePacing.TransitionSeconds"/>, so the fade always renders at full rate.</summary>
    public const double Seconds = 0.9;

    /// <summary>Blend factor 0…1 after <paramref name="elapsed"/> seconds (smoothstep).</summary>
    public static double Progress(double elapsed)
    {
        if (double.IsNaN(elapsed) || double.IsInfinity(elapsed)) return elapsed > 0 ? 1 : 0;
        var x = Math.Min(1, Math.Max(0, elapsed / Seconds));
        return x * x * (3 - 2 * x);
    }
}
