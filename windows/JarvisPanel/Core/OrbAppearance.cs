namespace JarvisPanel.Core;

public enum OrbMotion { Idle, Awaiting, Thinking, Speaking, Attention, Error }

/// <summary>
/// <c>Violet</c> is not a status: it marks voice-mini narrating another session so
/// that voice is never mistaken for Jarvis's own.
/// </summary>
public enum OrbTint { Cyan, Amber, Red, Violet }

public static class OrbTintInfo
{
    /// <summary>sRGB components 0…1.</summary>
    public static (double R, double G, double B) Rgb(this OrbTint t) => t switch
    {
        OrbTint.Cyan => (92 / 255.0, 225 / 255.0, 255 / 255.0),
        OrbTint.Amber => (255 / 255.0, 194 / 255.0, 77 / 255.0),
        OrbTint.Red => (255 / 255.0, 95 / 255.0, 95 / 255.0),
        _ => (178 / 255.0, 140 / 255.0, 255 / 255.0),
    };
}

/// <summary><c>Count == 0</c> with <c>UnreadDot</c> renders as a dot-only badge.</summary>
public readonly record struct Badge(int Count, OrbTint Tint, bool UnreadDot);

public readonly record struct OrbAppearance(OrbMotion Motion, OrbTint Tint, Badge? Badge);

/// <summary>
/// Spec §2.4 / §5.1: colour carries status (host error &gt; pending &gt; failed),
/// motion carries activity (speaking &gt; thinking &gt; status &gt; awaiting &gt; idle),
/// and the badge follows the same priority as the colour. With no status to show,
/// another session's narration turns the orb violet instead of cyan.
/// </summary>
public static class OrbStateResolver
{
    public static OrbAppearance Resolve(Snapshot? snapshot, bool connected, bool standby)
    {
        var counts = snapshot?.Counts ?? new Counts();
        var hostError = !connected || snapshot is null || snapshot.Error is not null;

        OrbTint status;
        if (hostError) status = OrbTint.Red;
        else if (counts.Pending > 0) status = OrbTint.Amber;
        else if (counts.Failed > 0) status = OrbTint.Red;
        else status = OrbTint.Cyan;

        var activity = snapshot?.Activity ?? Activity.Idle;
        var narrating = activity == Activity.Speaking && snapshot?.Voice.Source == SpeechSource.Session;
        var tint = status == OrbTint.Cyan && narrating ? OrbTint.Violet : status;

        OrbMotion motion;
        if (activity == Activity.Speaking) motion = OrbMotion.Speaking;
        else if (activity == Activity.Thinking) motion = OrbMotion.Thinking;
        else if (tint == OrbTint.Red) motion = OrbMotion.Error;
        else if (tint == OrbTint.Amber) motion = OrbMotion.Attention;
        else if (activity == Activity.Awaiting || standby) motion = OrbMotion.Awaiting;
        else motion = OrbMotion.Idle;

        Badge? badge;
        var dot = counts.Unread > 0;
        if (hostError) badge = null;
        else if (counts.Pending > 0) badge = new Badge(counts.Pending, status, dot);
        else if (counts.Failed > 0) badge = new Badge(counts.Failed, status, dot);
        else if (counts.Running > 0) badge = new Badge(counts.Running, status, dot);
        else if (dot) badge = new Badge(0, status, true);
        else badge = null;

        return new OrbAppearance(motion, tint, badge);
    }
}
