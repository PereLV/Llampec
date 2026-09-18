using Llampec.Interop;
using Llampec.Platform;
using Xunit;

namespace Llampec.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Space", User32.MOD_CONTROL | User32.MOD_ALT, 0x20u)]
    [InlineData("win + shift + L", User32.MOD_WIN | User32.MOD_SHIFT, (uint)'L')]
    [InlineData("Ctrl+F12", User32.MOD_CONTROL, 0x7Bu)]
    public void Parses_valid_hotkeys(string text, uint modifiers, uint vk)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(modifiers, hotkey.Modifiers);
        Assert.Equal(vk, hotkey.VirtualKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Space")]          // no modifier: would steal ordinary typing
    [InlineData("Ctrl+Alt")]       // no key
    [InlineData("Ctrl+Banana")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl++T")]
    [InlineData("Ctrl+T+")]
    [InlineData("Ctrl+Control+T")]
    public void Rejects_invalid_hotkeys(string text) => Assert.False(Hotkey.TryParse(text, out _));

    [Fact]
    public void Round_trips_through_ToString()
    {
        Assert.True(Hotkey.TryParse("Ctrl+Alt+Space", out var hotkey));
        Assert.Equal("Ctrl+Alt+Space", hotkey.ToString());
        Assert.True(Hotkey.TryParse(hotkey.ToString(), out var again));
        Assert.Equal(hotkey, again);
    }
}
