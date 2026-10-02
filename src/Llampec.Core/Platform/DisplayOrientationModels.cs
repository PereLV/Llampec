namespace Llampec.Platform;

public enum DisplayOrientation { Landscape, Portrait, LandscapeFlipped, PortraitFlipped }

[Flags]
public enum AutoRotationFlags : uint
{
    Enabled = 0, Disabled = 1, Suppressed = 2, RemoteSession = 4, MultipleMonitors = 8,
    NoSensor = 16, NotSupported = 32, Docked = 64, Laptop = 128,
}

public sealed record RotationState(bool IsKnown, AutoRotationFlags Flags)
{
    public bool IsLocked => IsKnown && Flags.HasFlag(AutoRotationFlags.Disabled);
    public bool HasSensor => IsKnown && (Flags & (AutoRotationFlags.NoSensor | AutoRotationFlags.NotSupported)) == 0;
    public bool CanToggle => IsKnown && (Flags & ~AutoRotationFlags.Disabled) == 0;
    /// <summary>
    /// A user lock, absent sensor, or hardware suspension prevents sensor rotation from undoing
    /// a manual choice. App suppression or remote-session status alone does not establish that
    /// posture; either can coexist with an independently reported hardware suspension or user lock.
    /// The display driver still decides whether the requested manual mode is supported.
    /// </summary>
    public bool CanSetOrientationWithoutChangingLock => IsKnown && ((uint)Flags & ~255u) == 0 &&
        (IsLocked || !HasSensor ||
            (Flags & (AutoRotationFlags.Laptop | AutoRotationFlags.Docked | AutoRotationFlags.MultipleMonitors)) != 0);
    public IReadOnlyList<string> ReasonKeys
    {
        get
        {
            if (!IsKnown) return ["Rotation state unavailable"];
            var reasons = new List<string>();
            if (Flags.HasFlag(AutoRotationFlags.RemoteSession)) reasons.Add("Rotation unavailable in a remote session");
            if (Flags.HasFlag(AutoRotationFlags.NoSensor)) reasons.Add("No orientation sensor");
            if (Flags.HasFlag(AutoRotationFlags.NotSupported)) reasons.Add("Rotation unsupported by this configuration");
            if (Flags.HasFlag(AutoRotationFlags.Laptop)) reasons.Add("Rotation paused while the keyboard is attached");
            if (Flags.HasFlag(AutoRotationFlags.Docked)) reasons.Add("Rotation paused while docked");
            if (Flags.HasFlag(AutoRotationFlags.MultipleMonitors)) reasons.Add("Rotation paused with multiple displays");
            if (Flags.HasFlag(AutoRotationFlags.Suppressed)) reasons.Add("Rotation paused by another app");
            if (((uint)Flags & ~255u) != 0) reasons.Add("Rotation state unavailable");
            if (reasons.Count == 0) reasons.Add(IsLocked ? "Rotation locked" : "Automatic rotation");
            return reasons;
        }
    }
}

/// <summary>Current physical-pixel mode, preserving position, refresh rate and pixel format.</summary>
public sealed record DisplayMode(uint Rotation, uint Width, uint Height, int X, int Y,
    uint BitsPerPixel, uint Frequency, uint Flags, uint FixedOutput)
{
    public bool NaturalPortrait => (Rotation % 2 == 0 ? Width : Height) < (Rotation % 2 == 0 ? Height : Width);
    public DisplayOrientation Orientation => (DisplayOrientation)((Rotation + (NaturalPortrait ? 1u : 0u)) % 4);
    public DisplayMode WithOrientation(DisplayOrientation orientation)
    {
        if (!Enum.IsDefined(orientation)) throw new ArgumentOutOfRangeException(nameof(orientation));
        uint rotation = ((uint)orientation + (NaturalPortrait ? 3u : 0u)) % 4;
        bool swap = rotation % 2 != Rotation % 2;
        return this with { Rotation = rotation, Width = swap ? Height : Width, Height = swap ? Width : Height };
    }
}

public sealed record DisplayInfo(string Id, string Name, string DeviceName, string SourceId,
    bool IsInternal, bool IsCloned, bool CanOrient, DisplayMode Mode, DisplayMode? SavedMode = null)
{
    public DisplayOrientation Orientation => Mode.Orientation;
}

public sealed record DisplaySnapshot(RotationState Rotation, IReadOnlyList<DisplayInfo> Displays)
{
    public static DisplaySnapshot Unavailable { get; } = new(new(false, default), []);
    public DisplayInfo? Find(string id) => Displays.FirstOrDefault(d => d.Id == id);

    // Include the complete topology and modes: rollback must never overwrite a later display change.
    public bool SameDisplays(DisplaySnapshot other, bool includeSavedModes = true) => Displays.Count == other.Displays.Count &&
        Displays.All(display => other.Find(display.Id) is { } current &&
            display.DeviceName == current.DeviceName && display.SourceId == current.SourceId &&
            display.IsCloned == current.IsCloned && display.IsInternal == current.IsInternal &&
            display.CanOrient == current.CanOrient && display.Mode == current.Mode &&
            (!includeSavedModes || display.SavedMode == current.SavedMode));
}

/// <summary>Injectable OS boundary. Apply must revalidate the complete expected display snapshot.</summary>
public interface IDisplayOrientationBackend
{
    DisplaySnapshot Capture();
    bool SendRotationLockShortcut();
    void TestOrientation(DisplaySnapshot expected, string displayId, DisplayMode mode);
    /// <summary>Applies and saves the selected mode in one OS operation.</summary>
    void ApplyAndSaveOrientation(DisplaySnapshot expected, string displayId, DisplayMode mode);
    /// <summary>Applies a temporary live mode without changing the saved mode.</summary>
    void ApplyOrientation(DisplaySnapshot expected, string displayId, DisplayMode mode);
}

public sealed class DisplayOrientationException(string messageKey, Exception? inner = null) : Exception(messageKey, inner)
{
    public string MessageKey { get; } = messageKey;
}
