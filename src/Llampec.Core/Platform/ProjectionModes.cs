using Llampec.Interop;

namespace Llampec.Platform;

/// <summary>The four display topologies Windows itself offers through Win+P / the "Project" panel.</summary>
public enum ProjectionTopology : uint
{
    /// <summary>"PC screen only".</summary>
    Internal = DisplayConfig.SDC_TOPOLOGY_INTERNAL,

    /// <summary>"Duplicate".</summary>
    Clone = DisplayConfig.SDC_TOPOLOGY_CLONE,

    /// <summary>"Extend".</summary>
    Extend = DisplayConfig.SDC_TOPOLOGY_EXTEND,

    /// <summary>"Second screen only".</summary>
    External = DisplayConfig.SDC_TOPOLOGY_EXTERNAL,
}

/// <summary>
/// Reads and switches the current projection topology through SetDisplayConfig/QueryDisplayConfig --
/// the same CCD API calls the Win+P flyout makes, so this replicates it exactly rather than approximating
/// it (e.g. no per-monitor enable/disable dance).
/// </summary>
public static class ProjectionModes
{
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>The topology Windows would restore on its own if nothing else changed it since.</summary>
    public static ProjectionTopology GetCurrent()
    {
        int result;
        uint pathCount, modeCount, topology;
        DisplayConfig.DISPLAYCONFIG_PATH_INFO[] paths;

        do
        {
            result = DisplayConfig.GetDisplayConfigBufferSizes(DisplayConfig.QDC_DATABASE_CURRENT, out pathCount, out modeCount);
            if (result != 0)
            {
                throw new InvalidOperationException($"GetDisplayConfigBufferSizes failed: {result}");
            }

            paths = new DisplayConfig.DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DisplayConfig.DISPLAYCONFIG_MODE_INFO_OPAQUE[modeCount];
            result = DisplayConfig.QueryDisplayConfigCurrentTopology(
                DisplayConfig.QDC_DATABASE_CURRENT, ref pathCount, paths, ref modeCount, modes, out topology);
        } while (result == ErrorInsufficientBuffer);

        if (result != 0)
        {
            throw new InvalidOperationException($"QueryDisplayConfig(QDC_DATABASE_CURRENT) failed: {result}");
        }

        return (ProjectionTopology)topology;
    }

    /// <summary>
    /// Switches to <paramref name="topology"/> using the last known-good source/target modes for it, exactly
    /// like picking an option from the Win+P menu (SDC_APPLY | SDC_TOPOLOGY_XXX, no supplied path array).
    /// SDC_ALLOW_CHANGES is deliberately not added here: despite the SetDisplayConfig documentation listing
    /// it as compatible with any flag combination, combining it with a topology-only call (no supplied path
    /// array) reliably fails with ERROR_INVALID_PARAMETER (87) in practice -- its own "common scenarios"
    /// table for a plain topology switch doesn't include it either.
    /// </summary>
    public static void Set(ProjectionTopology topology)
    {
        int result = DisplayConfig.SetDisplayConfig(0, 0, 0, 0, DisplayConfig.SDC_APPLY | (uint)topology);
        if (result != 0)
        {
            throw new InvalidOperationException($"SetDisplayConfig(0x{(uint)topology:x}) failed: {result}");
        }
    }
}
