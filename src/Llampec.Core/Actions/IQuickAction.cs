namespace Llampec.Actions;

/// <summary>How a tile behaves and is drawn.</summary>
public enum ActionKind
{
    /// <summary>Fire-and-forget button (e.g. "Turn off display"). Has no persistent state.</summary>
    Button,

    /// <summary>Two-state switch drawn as a single tile (e.g. "Dark mode").</summary>
    Toggle,

    /// <summary>
    /// Split tile: the left part toggles the primary action, the chevron on the right opens a sub-page
    /// listing <see cref="IQuickAction.SubActions"/> (e.g. HDR per monitor, projection modes).
    /// </summary>
    ToggleWithSubpage,
}

/// <summary>Current state of an action, as last read from the system.</summary>
public enum ActionState
{
    /// <summary>The action has no state (buttons).</summary>
    None,
    Off,
    On,

    /// <summary>Some targets are on and others off (e.g. HDR enabled on one of two monitors).</summary>
    Mixed,
}

/// <summary>
/// A tile in the Llampec panel. Implement this (usually through <see cref="QuickActionBase"/>) and register
/// the class in <see cref="ActionCatalog"/>; nothing else needs to change.
/// </summary>
public interface IQuickAction
{
    /// <summary>Stable identifier, used in settings (tile order/visibility). Never localized.</summary>
    string Id { get; }

    /// <summary>Localized title shown under the tile.</summary>
    string Title { get; }

    /// <summary>Optional localized secondary text (e.g. current projection mode).</summary>
    string? Subtitle { get; }

    /// <summary>A single character from the Segoe Fluent Icons font.</summary>
    string Glyph { get; }

    /// <summary>
    /// Optional second glyph drawn small, over the bottom-right corner of <see cref="Glyph"/> (e.g. a
    /// power symbol badged onto a monitor icon). Null for a plain single-glyph tile.
    /// </summary>
    string? GlyphBadge { get; }

    ActionKind Kind { get; }

    /// <summary>False when the action cannot work on this machine right now (e.g. HDR with no HDR-capable display).</summary>
    bool IsAvailable { get; }

    ActionState State { get; }

    /// <summary>True while an <see cref="ExecuteAsync"/> call is in progress; the tile ignores input meanwhile.</summary>
    bool IsBusy { get; }

    /// <summary>Sub-page items for <see cref="ActionKind.ToggleWithSubpage"/>; empty otherwise.</summary>
    IReadOnlyList<IQuickAction> SubActions { get; }

    /// <summary>Raised on any property change. May be raised from any thread.</summary>
    event EventHandler? Changed;

    /// <summary>Re-read state from the system. Called when the panel opens and on relevant system events.</summary>
    void Refresh();

    /// <summary>Performs the primary action (toggle or button press).</summary>
    Task ExecuteAsync(CancellationToken cancellationToken);
}
