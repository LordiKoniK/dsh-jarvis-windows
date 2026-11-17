namespace JarvisPanel.Core;

/// <summary>
/// A global hotkey as a Windows virtual-key code plus Win32 modifier bits.
/// The macOS build records Carbon key codes; Windows records VK_* codes, so the
/// persisted <c>hotKey</c> string is written in Windows form ("Ctrl+Alt+J") while
/// both Windows and macOS glyph spellings ("⌃⌥J") are accepted on read.
/// </summary>
public readonly record struct HotKeySpec(uint VirtualKey, uint Modifiers)
{
    // Win32 RegisterHotKey modifier flags (MOD_ALT/MOD_CONTROL/MOD_SHIFT/MOD_WIN).
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint SupportedModifiers = ModAlt | ModControl | ModShift | ModWin;

    /// <summary>Ctrl+Alt+J — the same chord the macOS default binds.</summary>
    public static HotKeySpec Default => new(0x4A, ModControl | ModAlt);

    private static readonly Dictionary<uint, string> Keys = new()
    {
        [0x41] = "A", [0x42] = "B", [0x43] = "C", [0x44] = "D", [0x45] = "E",
        [0x46] = "F", [0x47] = "G", [0x48] = "H", [0x49] = "I", [0x4A] = "J",
        [0x4B] = "K", [0x4C] = "L", [0x4D] = "M", [0x4E] = "N", [0x4F] = "O",
        [0x50] = "P", [0x51] = "Q", [0x52] = "R", [0x53] = "S", [0x54] = "T",
        [0x55] = "U", [0x56] = "V", [0x57] = "W", [0x58] = "X", [0x59] = "Y",
        [0x5A] = "Z",
        [0x30] = "0", [0x31] = "1", [0x32] = "2", [0x33] = "3", [0x34] = "4",
        [0x35] = "5", [0x36] = "6", [0x37] = "7", [0x38] = "8", [0x39] = "9",
        [0x70] = "F1", [0x71] = "F2", [0x72] = "F3", [0x73] = "F4", [0x74] = "F5",
        [0x75] = "F6", [0x76] = "F7", [0x77] = "F8", [0x78] = "F9", [0x79] = "F10",
        [0x7A] = "F11", [0x7B] = "F12", [0x7C] = "F13", [0x7D] = "F14", [0x7E] = "F15",
        [0x7F] = "F16", [0x80] = "F17", [0x81] = "F18", [0x82] = "F19", [0x83] = "F20",
        [0x84] = "F21", [0x85] = "F22", [0x86] = "F23", [0x87] = "F24",
        [0x0D] = "Enter", [0x09] = "Tab", [0x20] = "Space", [0x2E] = "Delete",
        [0x1B] = "Escape", [0x08] = "Backspace", [0x24] = "Home", [0x23] = "End",
        [0x21] = "PageUp", [0x22] = "PageDown", [0x25] = "Left", [0x26] = "Up",
        [0x27] = "Right", [0x28] = "Down", [0x2D] = "Insert",
        [0xBA] = ";", [0xBB] = "=", [0xBC] = ",", [0xBD] = "-", [0xBE] = ".",
        [0xBF] = "/", [0xC0] = "`", [0xDB] = "[", [0xDC] = "\\", [0xDD] = "]",
        [0xDE] = "'",
    };

    private static readonly (char Glyph, uint Bit)[] GlyphModifiers =
    {
        ('⌃', ModControl), ('⌥', ModAlt), ('⇧', ModShift), ('⌘', ModWin),
    };

    public static HotKeySpec? TryCreate(uint vk, uint modifiers)
    {
        if (!Keys.ContainsKey(vk)) return null;
        if (modifiers == 0 || (modifiers & ~SupportedModifiers) != 0) return null;
        return new HotKeySpec(vk, modifiers);
    }

    /// <summary>Parses "Ctrl+Alt+J" or the macOS "⌃⌥J" spelling; null when unusable.</summary>
    public static HotKeySpec? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        var modifiers = 0u;

        // Glyph form first: consume any leading ⌃⌥⇧⌘ characters.
        var i = 0;
        while (i < s.Length)
        {
            var match = GlyphModifiers.FirstOrDefault(m => m.Glyph == s[i]);
            if (match.Glyph == '\0') break;
            if ((modifiers & match.Bit) != 0) return null;
            modifiers |= match.Bit;
            i++;
        }

        var keyPart = s[i..].Trim();
        if (keyPart.Length > 0 && modifiers != 0)
            return TryCreate(KeyFromLabel(keyPart), modifiers);

        // "Ctrl+Alt+J" form.
        var pieces = s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pieces.Length == 0) return null;
        modifiers = 0;
        foreach (var piece in pieces)
        {
            var bit = piece.ToLowerInvariant() switch
            {
                "ctrl" or "control" => ModControl,
                "alt" or "option" => ModAlt,
                "shift" => ModShift,
                "win" or "cmd" or "command" or "meta" => ModWin,
                _ => 0u,
            };
            if (bit != 0) modifiers |= bit;
        }
        var last = pieces[^1];
        if (modifiers == 0) return null;
        return TryCreate(KeyFromLabel(last), modifiers);
    }

    private static uint KeyFromLabel(string label)
    {
        foreach (var kv in Keys)
            if (string.Equals(kv.Value, label, StringComparison.OrdinalIgnoreCase)) return kv.Key;
        return 0;
    }

    public string DisplayString
    {
        get
        {
            var parts = new List<string>();
            if ((Modifiers & ModControl) != 0) parts.Add("Ctrl");
            if ((Modifiers & ModAlt) != 0) parts.Add("Alt");
            if ((Modifiers & ModShift) != 0) parts.Add("Shift");
            if ((Modifiers & ModWin) != 0) parts.Add("Win");
            parts.Add(Keys[VirtualKey]);
            return string.Join("+", parts);
        }
    }
}

public enum InputHotKeyAction { Open, Focus, Close }

public static class InputHotKey
{
    public static InputHotKeyAction Resolve(bool inputOpen, bool inputFocused) =>
        inputOpen ? (inputFocused ? InputHotKeyAction.Close : InputHotKeyAction.Focus)
                  : InputHotKeyAction.Open;
}
