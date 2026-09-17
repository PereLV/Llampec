using Llampec.Platform;
using Xunit;

namespace Llampec.Tests;

public class ProjectionModesTests
{
    [Fact]
    public void Set_accepts_the_current_topology()
    {
        // Re-applying the topology the machine is already in is a real SetDisplayConfig call but a visual
        // no-op -- unlike switching to a different topology, safe to run from an unattended test. This is
        // exactly the call ProjectionModeAction.ExecuteCoreAsync makes; it must not throw.
        var current = ProjectionModes.GetCurrent();
        ProjectionModes.Set(current);

        Assert.Equal(current, ProjectionModes.GetCurrent());
    }
}
