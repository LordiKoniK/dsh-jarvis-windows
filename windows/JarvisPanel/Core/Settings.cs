using System.Text;
using System.Text.Json;

namespace JarvisPanel.Core;

public enum ParticleTier { Minimal, Lite, Standard }

public static class ParticleTierInfo
{
    public static int Count(this ParticleTier t) => t switch
    {
        ParticleTier.Minimal => 300,
        ParticleTier.Lite => 600,
        _ => 900,
    };

    /// <summary>UI shows names only, never particle counts.</summary>
    public static string Label(this ParticleTier t) => t switch
    {
        ParticleTier.Minimal => "Minimal",
        ParticleTier.Lite => "Lite",
        _ => "Standard",
    };

    public static string Raw(this ParticleTier t) => t switch
    {
        ParticleTier.Minimal => "minimal",
        ParticleTier.Lite => "lite",
        _ => "standard",
    };

    public static ParticleTier Parse(string? raw) => raw switch
    {
        "minimal" => ParticleTier.Minimal,
        "lite" => ParticleTier.Lite,
        _ => ParticleTier.Standard,
    };
}

public enum DockEdge { Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

public static class DockEdgeInfo
{
    public static bool IsCorner(this DockEdge e) => e is DockEdge.TopLeft or DockEdge.TopRight
        or DockEdge.BottomLeft or DockEdge.BottomRight;

    public static string Raw(this DockEdge e) => e switch
    {
        DockEdge.Left => "left",
        DockEdge.Right => "right",
        DockEdge.Top => "top",
        DockEdge.Bottom => "bottom",
        DockEdge.TopLeft => "topLeft",
        DockEdge.TopRight => "topRight",
        DockEdge.BottomLeft => "bottomLeft",
        _ => "bottomRight",
    };

    public static DockEdge? Parse(string? raw) => raw switch
    {
        "left" => DockEdge.Left,
        "right" => DockEdge.Right,
        "top" => DockEdge.Top,
        "bottom" => DockEdge.Bottom,
        "topLeft" => DockEdge.TopLeft,
        "topRight" => DockEdge.TopRight,
        "bottomLeft" => DockEdge.BottomLeft,
        "bottomRight" => DockEdge.BottomRight,
        _ => null,
    };
}

public sealed class SavedPosition
{
    public double X { get; set; }
    public double Y { get; set; }
    public DockEdge? Dock { get; set; }

    public SavedPosition Clone() => new() { X = X, Y = Y, Dock = Dock };
}

/// <summary>Mirrors Swift's <c>PanelSettings</c>; a bad value for one key falls back to its default.</summary>
public sealed class PanelSettings
{
    public const double OpacityMin = 0.15;
    public const double OpacityMax = 1.0;

    public double DshOpacity { get; set; } = 1.0;
    public double OtherOpacity { get; set; } = 0.35;
    public bool ProximityFade { get; set; } = true;
    public bool KeepProminent { get; set; } = true;
    public bool HideInFullscreen { get; set; } = true;
    public bool HideInMissionControl { get; set; } = true;
    public bool HideInAppLauncher { get; set; } = true;
    public List<string> HiddenApps { get; set; } = new();
    public ParticleTier Tier { get; set; } = ParticleTier.Standard;
    public bool FollowReduceMotion { get; set; } = true;
    public bool DockToStrip { get; set; } = true;
    /// <summary>Keyed by monitor device name so each display remembers its own spot.</summary>
    public Dictionary<string, SavedPosition> Positions { get; set; } = new(StringComparer.Ordinal);
    /// <summary>"jarvis" or a session id; null = never sent yet.</summary>
    public string? LastTarget { get; set; }
    public bool HistoryExpanded { get; set; }
    /// <summary>null selects the default Ctrl+Alt+J combination.</summary>
    public string? HotKey { get; set; }

    public PanelSettings Clone()
    {
        var c = new PanelSettings
        {
            DshOpacity = DshOpacity,
            OtherOpacity = OtherOpacity,
            ProximityFade = ProximityFade,
            KeepProminent = KeepProminent,
            HideInFullscreen = HideInFullscreen,
            HideInMissionControl = HideInMissionControl,
            HideInAppLauncher = HideInAppLauncher,
            HiddenApps = new List<string>(HiddenApps),
            Tier = Tier,
            FollowReduceMotion = FollowReduceMotion,
            DockToStrip = DockToStrip,
            Positions = new Dictionary<string, SavedPosition>(StringComparer.Ordinal),
            LastTarget = LastTarget,
            HistoryExpanded = HistoryExpanded,
            HotKey = HotKey,
        };
        foreach (var kv in Positions) c.Positions[kv.Key] = kv.Value.Clone();
        return c;
    }

    public PanelSettings Normalized()
    {
        var s = Clone();
        s.DshOpacity = Math.Min(Math.Max(s.DshOpacity, OpacityMin), OpacityMax);
        s.OtherOpacity = Math.Min(Math.Max(s.OtherOpacity, OpacityMin), OpacityMax);
        s.HotKey = HotKeySpec.TryParse(s.HotKey)?.DisplayString;
        return s;
    }

    public static PanelSettings Parse(JsonElement e)
    {
        var d = new PanelSettings();
        if (e.ValueKind != JsonValueKind.Object) return d;

        d.DshOpacity = J.Dbl(e, "dshOpacity", d.DshOpacity);
        d.OtherOpacity = J.Dbl(e, "otherOpacity", d.OtherOpacity);
        d.ProximityFade = J.Bool(e, "proximityFade", d.ProximityFade);
        d.KeepProminent = J.Bool(e, "keepProminent", d.KeepProminent);
        d.HideInFullscreen = J.Bool(e, "hideInFullscreen", d.HideInFullscreen);
        d.HideInMissionControl = J.Bool(e, "hideInMissionControl", d.HideInMissionControl);
        d.HideInAppLauncher = J.Bool(e, "hideInAppLauncher", d.HideInAppLauncher);
        d.Tier = ParticleTierInfo.Parse(J.Str(e, "tier"));
        d.FollowReduceMotion = J.Bool(e, "followReduceMotion", d.FollowReduceMotion);
        d.DockToStrip = J.Bool(e, "dockToStrip", d.DockToStrip);
        d.HistoryExpanded = J.Bool(e, "historyExpanded", d.HistoryExpanded);
        d.LastTarget = J.Str(e, "lastTarget");
        d.HotKey = J.Str(e, "hotKey");

        if (J.Arr(e, "hiddenApps") is { } apps)
        {
            foreach (var a in apps.EnumerateArray())
                if (a.ValueKind == JsonValueKind.String && a.GetString() is { } s) d.HiddenApps.Add(s);
        }

        if (J.Obj(e, "positions") is { } positions)
        {
            foreach (var prop in positions.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                var p = new SavedPosition
                {
                    X = J.Dbl(prop.Value, "x"),
                    Y = J.Dbl(prop.Value, "y"),
                    Dock = DockEdgeInfo.Parse(J.Str(prop.Value, "dock")),
                };
                d.Positions[prop.Name] = p;
            }
        }
        return d;
    }

    /// <summary>Pretty-printed with alphabetically sorted keys, like Swift's <c>.sortedKeys</c>.</summary>
    public string ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("dshOpacity", DshOpacity);
            w.WriteBoolean("dockToStrip", DockToStrip);
            w.WriteBoolean("followReduceMotion", FollowReduceMotion);
            w.WriteStartArray("hiddenApps");
            foreach (var a in HiddenApps) w.WriteStringValue(a);
            w.WriteEndArray();
            w.WriteBoolean("hideInAppLauncher", HideInAppLauncher);
            w.WriteBoolean("hideInFullscreen", HideInFullscreen);
            w.WriteBoolean("hideInMissionControl", HideInMissionControl);
            w.WriteBoolean("historyExpanded", HistoryExpanded);
            if (HotKey is null) w.WriteNull("hotKey"); else w.WriteString("hotKey", HotKey);
            w.WriteBoolean("keepProminent", KeepProminent);
            if (LastTarget is null) w.WriteNull("lastTarget"); else w.WriteString("lastTarget", LastTarget);
            w.WriteNumber("otherOpacity", OtherOpacity);
            w.WriteStartObject("positions");
            foreach (var kv in Positions.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                w.WriteStartObject(kv.Key);
                w.WriteNumber("x", kv.Value.X);
                w.WriteNumber("y", kv.Value.Y);
                if (kv.Value.Dock is { } dock) w.WriteString("dock", dock.Raw());
                else w.WriteNull("dock");
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteBoolean("proximityFade", ProximityFade);
            w.WriteString("tier", Tier.Raw());
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}

/// <summary>Reads/writes <c>%USERPROFILE%\.dsh\jarvis\panel.json</c>. A missing or corrupt file yields defaults.</summary>
public sealed class SettingsStore
{
    private readonly object _lock = new();
    private PanelSettings _current;

    public string Url { get; }

    public static string DefaultUrl => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "jarvis", "panel.json");

    public SettingsStore(string? url = null)
    {
        Url = url ?? DefaultUrl;
        _current = Load();
    }

    public PanelSettings Settings
    {
        get { lock (_lock) return _current; }
    }

    public void Update(Action<PanelSettings> change)
    {
        PanelSettings next;
        lock (_lock)
        {
            next = _current.Clone();
            change(next);
            next = next.Normalized();
            _current = next;
        }
        Persist(next);
    }

    private PanelSettings Load()
    {
        try
        {
            if (!File.Exists(Url)) return new PanelSettings();
            using var doc = JsonDocument.Parse(File.ReadAllText(Url));
            return PanelSettings.Parse(doc.RootElement).Normalized();
        }
        catch
        {
            return new PanelSettings();
        }
    }

    private void Persist(PanelSettings s)
    {
        try
        {
            var dir = Path.GetDirectoryName(Url);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(Url, s.ToJson());
        }
        catch
        {
            // Settings are best-effort; a read-only home must not crash the panel.
        }
    }
}
