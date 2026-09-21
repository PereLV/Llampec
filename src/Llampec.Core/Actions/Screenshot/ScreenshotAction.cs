using Llampec.Platform;

namespace Llampec.Actions.Screenshot;

/// <summary>Opens Windows' capture overlay after the panel has finished hiding.</summary>
public sealed class ScreenshotAction : QuickActionBase
{
    private readonly KeyboardShortcut _shortcut = new("Win+Shift+S");

    public override string Id => "screenshot";
    public override string Title => "Screenshot";
    public override string Glyph => "\uE8A7"; // Cut
    public override ActionKind Kind => ActionKind.Button;

    protected override Task ExecuteCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            Subtitle = _shortcut.Send() ? null : "Release modifier keys and try again.";
            if (Subtitle is null) Diagnostics.Log.Info("Windows screenshot shortcut sent.");
        }
        catch
        {
            Subtitle = "Could not open Windows screen capture.";
            throw;
        }
        return Task.CompletedTask;
    }
}
