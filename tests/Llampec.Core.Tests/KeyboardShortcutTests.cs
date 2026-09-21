using System.Reflection;
using System.Runtime.InteropServices;
using Llampec.Platform;
using Xunit;

namespace Llampec.Tests;

public sealed class KeyboardShortcutTests
{
    [Theory]
    [InlineData("win+tab", "Win+Tab")]
    [InlineData(" control + shift + f24 ", "Ctrl+Shift+F24")]
    [InlineData("c", "C")]
    [InlineData("VolumeUp", "VOLUMEUP")]
    public void AcceptsChordsAndSingleKeys(string input, string normalized) =>
        Assert.Equal(normalized, new KeyboardShortcut(input).Text);

    [Theory]
    [InlineData("")]
    [InlineData("Win")]
    [InlineData("Ctrl+Control+C")]
    [InlineData("A+B")]
    [InlineData("Ctrl++C")]
    [InlineData("F25")]
    [InlineData("Ctrl+Unknown")]
    public void RejectsAmbiguousOrIncompleteChords(string input) =>
        Assert.Throws<ArgumentException>(() => new KeyboardShortcut(input));

    [Fact]
    public void InputLayoutMatchesWin32OnSupportedArchitectures()
    {
        Type input = typeof(KeyboardShortcut).GetNestedType("Input", BindingFlags.NonPublic)!;
        Type keyboard = typeof(KeyboardShortcut).GetNestedType("KeyboardInput", BindingFlags.NonPublic)!;
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, Marshal.SizeOf(input));
        Assert.Equal(IntPtr.Size == 8 ? 8 : 4, Marshal.OffsetOf(input, "Data").ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 24 : 16, Marshal.SizeOf(keyboard));
    }
}
