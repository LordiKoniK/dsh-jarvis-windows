using System.Text.Json;

namespace JarvisPanel.Core;

/// <summary>Lenient readers over a JSON object, mirroring the Swift decoders.</summary>
public static class J
{
    public static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            _ => null,
        };
    }

    public static bool Bool(JsonElement e, string name, bool fallback = false)
    {
        if (e.ValueKind != JsonValueKind.Object) return fallback;
        if (!e.TryGetProperty(name, out var v)) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    public static int Int(JsonElement e, string name, int fallback = 0)
    {
        if (e.ValueKind != JsonValueKind.Object) return fallback;
        if (!e.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        return fallback;
    }

    public static double Dbl(JsonElement e, string name, double fallback = 0)
    {
        if (e.ValueKind != JsonValueKind.Object) return fallback;
        if (!e.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        return fallback;
    }

    public static JsonElement? Obj(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.Object ? v : null;
    }

    public static JsonElement? Arr(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.Array ? v : null;
    }

    public static string Trim(string? s) => (s ?? string.Empty).Trim();
}
