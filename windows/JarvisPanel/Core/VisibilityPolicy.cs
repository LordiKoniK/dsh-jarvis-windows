namespace JarvisPanel.Core;

public sealed class VisibilityInput
{
    public string? FrontBundleId { get; set; }
    public bool Fullscreen { get; set; }
    public bool AppLauncher { get; set; }

    /// <summary>Cursor distance from the orb center in points; null = unknown.</summary>
    public double? CursorDistance { get; set; }

    public bool Hovering { get; set; }

    /// <summary>Quick bar open or a drag in progress.</summary>
    public bool Interacting { get; set; }

    /// <summary>Orb tint is amber or red (needs attention / error).</summary>
    public bool Prominent { get; set; }

    /// <summary>Collapsed into the edge strip, which is too thin to read when faded.</summary>
    public bool Strip { get; set; }

    /// <summary>Explicit user input remains visible even when the ambient orb would hide.</summary>
    public bool InputOpen { get; set; }

    public PanelSettings Settings { get; set; } = new();
}

public readonly record struct VisibilityOutput(bool Hidden, double Opacity);

/// <summary>
/// Spec §3. Mission Control is handled by the window's collection behavior,
/// not here. Mirrors <c>VisibilityPolicy.swift</c>.
/// </summary>
public static class VisibilityPolicy
{
    public const string DshPrefix = "ai.deepseek.dsh.desktop";
    public const double Proximity = 80.0;
    public const double ProminentFloor = 0.85;
    public const double StripFloor = 0.8;

    public static VisibilityOutput Evaluate(VisibilityInput i)
    {
        if (i.InputOpen) return new VisibilityOutput(false, 1);
        var s = i.Settings;
        var front = i.FrontBundleId ?? string.Empty;
        var hidden = (s.HideInFullscreen && i.Fullscreen)
            || (s.HideInAppLauncher && i.AppLauncher)
            || (!string.IsNullOrEmpty(front) && s.HiddenApps.Contains(front));
        if (hidden) return new VisibilityOutput(true, 0);

        var baseOpacity = front.StartsWith(DshPrefix, StringComparison.Ordinal) ? s.DshOpacity : s.OtherOpacity;
        if (s.KeepProminent && i.Prominent) baseOpacity = Math.Max(baseOpacity, ProminentFloor);
        if (i.Strip) baseOpacity = Math.Max(baseOpacity, StripFloor);
        if (i.Hovering || i.Interacting) return new VisibilityOutput(false, 1);
        if (s.ProximityFade && i.CursorDistance is { } d)
        {
            var radius = Placement.OrbRadius;
            var f = Math.Min(1, Math.Max(0, 1 - (d - radius) / Proximity));
            baseOpacity += (1 - baseOpacity) * f;
        }
        return new VisibilityOutput(false, baseOpacity);
    }
}
