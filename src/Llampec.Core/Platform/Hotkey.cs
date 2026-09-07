using System.Globalization;
using Llampec.Interop;

namespace Llampec.Platform;

/// <summary>A global hotkey such as "Ctrl+Alt+Space", parsed from / formatted to its settings string.</summary>
public readonly record struct Hotkey(uint Modifiers, uint VirtualKey)
{
    public static readonly Hotkey Default = new(User32.MOD_CONTROL | User32.MOD_ALT, 0x20 /* VK_SPACE */);

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        uint mods = 0;
        uint vk = 0;
        foreach (string rawPart in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (rawPart.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": mods |= User32.MOD_CONTROL; break;
                case "ALT": mods |= User32.MOD_ALT; break;
                case "SHIFT": mods |= User32.MOD_SHIFT; break;
                case "WIN" or "WINDOWS": mods |= User32.MOD_WIN; break;
                default:
                    if (!TryParseKey(rawPart, out vk))
                    {
                        return false;
                    }

                    break;
            }
        }

        if (vk == 0 || mods == 0)
        {
            return false; // a global hotkey without modifiers would steal ordinary typing
        }

        hotkey = new Hotkey(mods, vk);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if ((Modifiers & User32.MOD_WIN) != 0) parts.Add("Win");
        if ((Modifiers & User32.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((Modifiers & User32.MOD_ALT) != 0) parts.Add("Alt");
        if ((Modifiers & User32.MOD_SHIFT) != 0) parts.Add("Shift");
        parts.Add(KeyName(VirtualKey));
        return string.Join('+', parts);
    }

    private static bool TryParseKey(string name, out uint vk)
    {
        string n = name.ToUpperInvariant();
        vk = n switch
        {
            "SPACE" => 0x20,
            "TAB" => 0x09,
            "ENTER" or "RETURN" => 0x0D,
            "ESC" or "ESCAPE" => 0x1B,
            "HOME" => 0x24,
            "END" => 0x23,
            "INSERT" or "INS" => 0x2D,
            "DELETE" or "DEL" => 0x2E,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            _ => 0,
        };

        if (vk != 0)
        {
            return true;
        }

        if (n.Length == 1 && (char.IsAsciiLetter(n[0]) || char.IsAsciiDigit(n[0])))
        {
            vk = n[0]; // VK codes for 0-9 and A-Z equal their ASCII values
            return true;
        }

        if (n.Length is 2 or 3 && n[0] == 'F' &&
            int.TryParse(n.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int f) && f is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + f - 1);
            return true;
        }

        return false;
    }

    private static string KeyName(uint vk) => vk switch
    {
        0x20 => "Space",
        0x09 => "Tab",
        0x0D => "Enter",
        0x1B => "Esc",
        0x24 => "Home",
        0x23 => "End",
        0x2D => "Insert",
        0x2E => "Delete",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x26 => "Up",
        0x28 => "Down",
        0x25 => "Left",
        0x27 => "Right",
        >= 0x70 and <= 0x87 => "F" + (vk - 0x70 + 1).ToString(CultureInfo.InvariantCulture),
        >= 0x30 and <= 0x5A => ((char)vk).ToString(),
        _ => "0x" + vk.ToString("X", CultureInfo.InvariantCulture),
    };
}
