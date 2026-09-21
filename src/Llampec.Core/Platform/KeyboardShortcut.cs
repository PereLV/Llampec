using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Llampec.Platform;

/// <summary>Emits one complete shortcut chord, preserving physical keyboard modifiers.</summary>
public sealed class KeyboardShortcut
{
    private readonly ushort[] _keys;
    public string Text { get; }

    public KeyboardShortcut(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(string.IsNullOrEmpty))
            throw new ArgumentException("Use a shortcut such as Win+Tab, Ctrl+C or F8.");
        var keys = parts.Select(ParseKey).ToArray();
        if (keys.Distinct().Count() != keys.Length || keys.SkipLast(1).Any(key => !IsModifier(key)) || IsModifier(keys[^1]))
            throw new ArgumentException("A shortcut must contain zero or more distinct modifiers followed by one key.");
        _keys = keys;
        Text = string.Join('+', parts.Select(part => part.ToUpperInvariant() switch
        {
            "CTRL" or "CONTROL" => "Ctrl", "ALT" => "Alt", "SHIFT" => "Shift", "WIN" or "WINDOWS" => "Win",
            "TAB" => "Tab", "ENTER" => "Enter", "ESC" or "ESCAPE" => "Escape", "SPACE" => "Space",
            _ => part.ToUpperInvariant()
        }));
    }

    public bool Send()
    {
        // Skip rather than release keys the user is physically holding. This
        // avoids interfering with an in-progress keyboard chord.
        ushort[] guards = [0x10, 0x11, 0x12, 0x5B, 0x5C, _keys[^1]];
        if (guards.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0))
        {
            return false;
        }
        var events = _keys.Select(key => MakeInput(key, false))
            .Concat(_keys.Reverse().Select(key => MakeInput(key, true))).ToArray();
        uint sent = SendInput((uint)events.Length, events, Marshal.SizeOf<Input>());
        if (sent == events.Length) return true;
        int error = Marshal.GetLastWin32Error();
        // Release only keys left down by the prefix that Windows accepted.
        var stillDown = new List<ushort>();
        foreach (var input in events.Take((int)sent))
        {
            ushort key = input.Data.Keyboard.VirtualKey;
            if ((input.Data.Keyboard.Flags & 2) == 0) stillDown.Add(key);
            else stillDown.Remove(key);
        }
        var releases = stillDown.AsEnumerable().Reverse().Select(key => MakeInput(key, true)).ToArray();
        bool released = releases.Length == 0 || SendInput((uint)releases.Length, releases, Marshal.SizeOf<Input>()) == releases.Length;
        throw new Win32Exception(error, $"SendInput inserted {sent}/{events.Length} events. The target may run elevated."
            + (released ? "" : " Synthetic key release also failed."));
    }

    private static bool IsModifier(ushort key) => key is 0x10 or 0x11 or 0x12 or 0x5B;

    private static ushort ParseKey(string text)
    {
        string key = text.ToUpperInvariant();
        if (key.Length == 1 && (key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')) return key[0];
        if (key.StartsWith('F') && int.TryParse(key.AsSpan(1), out int number) && number is >= 1 and <= 24)
            return (ushort)(0x70 + number - 1);
        return key switch
        {
            "CTRL" or "CONTROL" => 0x11, "ALT" => 0x12, "SHIFT" => 0x10, "WIN" or "WINDOWS" => 0x5B,
            "TAB" => 0x09, "ENTER" => 0x0D, "ESC" or "ESCAPE" => 0x1B, "SPACE" => 0x20,
            "BACKSPACE" => 0x08, "DELETE" => 0x2E, "INSERT" => 0x2D,
            "HOME" => 0x24, "END" => 0x23, "PAGEUP" => 0x21, "PAGEDOWN" => 0x22,
            "LEFT" => 0x25, "UP" => 0x26, "RIGHT" => 0x27, "DOWN" => 0x28,
            "VOLUMEUP" => 0xAF, "VOLUMEDOWN" => 0xAE, "VOLUMEMUTE" => 0xAD,
            "MEDIAPLAYPAUSE" => 0xB3, "MEDIANEXT" => 0xB0, "MEDIAPREVIOUS" => 0xB1,
            _ => throw new ArgumentException($"Unsupported shortcut key: {text}.")
        };
    }

    private static Input MakeInput(ushort key, bool release) => new()
    {
        Type = 1,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = key,
                Flags = (release ? 2u : 0u) | (key is >= 0x21 and <= 0x2E or 0x5B or >= 0xA6 and <= 0xB7 ? 1u : 0u)
            }
        }
    };

    // INPUT's union is sized by MOUSEINPUT, including pointer-sized dwExtraInfo.
    // Sequential outer layout inserts the correct x64/ARM64 union alignment.
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort VirtualKey; public ushort ScanCode; public uint Flags; public uint Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X; public int Y; public uint Data; public uint Flags; public uint Time; public nuint ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
