using System.ComponentModel;

namespace Llampec.Flyout;

/// <summary>
/// Whether the most recent input was the keyboard. Tiles show their focus rectangle only while this is
/// true — like the native Quick Settings panel. WPF gives keyboard focus to the first tile as soon as the
/// window activates (even when the panel was opened with the mouse), but Windows only draws a visible
/// focus ring after real keyboard navigation (Tab / arrow keys), never for a mouse click. FlyoutWindow
/// flips this on any key press and off on any mouse press, and resets it to false whenever the panel opens.
/// </summary>
public sealed class FocusVisualTracker : INotifyPropertyChanged
{
    public static readonly FocusVisualTracker Instance = new();

    private bool _isKeyboardActive;

    public bool IsKeyboardActive
    {
        get => _isKeyboardActive;
        set
        {
            if (_isKeyboardActive == value)
            {
                return;
            }

            _isKeyboardActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsKeyboardActive)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private FocusVisualTracker()
    {
    }
}
