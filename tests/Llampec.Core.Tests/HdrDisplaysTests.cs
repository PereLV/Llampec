using Llampec.Platform;
using Xunit;

namespace Llampec.Tests;

public class HdrDisplaysTests
{
    [Fact]
    public void GetDisplays_returns_one_entry_per_active_display_with_a_name()
    {
        // Read-only: never calls SetEnabled, which would really flip HDR on the machine running the test.
        var displays = HdrDisplays.GetDisplays();

        foreach (var display in displays)
        {
            Assert.False(string.IsNullOrWhiteSpace(display.Name));
        }
    }

    [Fact]
    public void TryGetColorState_agrees_with_GetDisplays_for_every_display()
    {
        var displays = HdrDisplays.GetDisplays();

        foreach (var display in displays)
        {
            bool ok = HdrDisplays.TryGetColorState(display.AdapterId, display.TargetId, out bool supported, out bool enabled);

            Assert.True(ok);
            Assert.Equal(display.Supported, supported);
            Assert.Equal(display.Enabled, enabled);
        }
    }
}
