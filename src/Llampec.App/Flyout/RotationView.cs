using Llampec.Platform;
using Llampec.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Llampec.Flyout;

/// <summary>Visual, immediate orientation choices over the application-owned service.</summary>
public sealed class RotationView : StackPanel, IDisposable
{
    private readonly DisplayOrientationService _service;
    private readonly Action<bool> _holdDismissal;
    private readonly ComboBox _display = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 40 };
    private readonly TextBlock _rotationState = Note();
    private readonly TextBlock _displayState = Note();
    private readonly InfoBar _message = new() { IsClosable = false };
    private readonly Button _recover = new() { MinHeight = 40 };
    private readonly Button _refresh = new() { MinHeight = 40 };
    private readonly List<(DisplayOrientation Orientation, ToggleButton Button)> _orientations = [];
    private string? _selectedDisplayId;
    private string? _displayListSignature;
    private string? _localError;
    private bool _updating;
    private bool _disposed;

    public RotationView(DisplayOrientationService service, Action<bool> holdDismissal)
    {
        _service = service;
        _holdDismissal = holdDismissal;
        Spacing = 12;
        _service.Refresh();
        _selectedDisplayId = _service.RecoveryDisplayId
            ?? _service.Snapshot.Displays.FirstOrDefault(d => d.IsInternal)?.Id
            ?? _service.Snapshot.Displays.FirstOrDefault()?.Id;
        _display.Header = T("Display");
        _display.SelectionChanged += (_, _) =>
        {
            if (_updating || _display.SelectedItem is not ComboBoxItem { Tag: string id }) return;
            _selectedDisplayId = id;
            Update();
        };
        Children.Add(_display);
        Children.Add(_displayState);
        var choices = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        choices.ColumnDefinitions.Add(new ColumnDefinition());
        choices.ColumnDefinitions.Add(new ColumnDefinition());
        choices.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        choices.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var orientation in Enum.GetValues<DisplayOrientation>())
        {
            var content = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(OrientationPreview(orientation));
            content.Children.Add(new TextBlock
            {
                Text = OrientationName(orientation), FontSize = 12, MaxWidth = 110,
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            });
            var button = new ToggleButton
            {
                Content = content, MinHeight = 108, Padding = new Thickness(8),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            AutomationProperties.SetName(button, OrientationName(orientation));
            button.Click += async (_, _) =>
            {
                if (_updating || _selectedDisplayId is not { } id) return;
                await RunAsync(() => _service.ChangeOrientationAsync(id, orientation));
            };
            _orientations.Add((orientation, button));
            Grid.SetRow(button, (int)orientation / 2);
            Grid.SetColumn(button, (int)orientation % 2);
            choices.Children.Add(button);
        }
        Children.Add(choices);
        Children.Add(_message);
        _recover.Content = T("Restore the previous orientation");
        _recover.Click += async (_, _) => await RunAsync(() => _service.RevertRecoveryAsync());
        Children.Add(_recover);
        Children.Add(_rotationState);
        _refresh.Content = T("Refresh status");
        _refresh.Click += (_, _) => _service.Refresh();
        Children.Add(_refresh);
        var settings = new HyperlinkButton
        {
            Content = T("Open display settings"),
            HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0, 8, 0, 8), MinHeight = 40,
        };
        settings.Click += async (_, _) =>
        {
            _localError = null;
            settings.IsEnabled = false;
            try
            {
                if (!await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:display")))
                    throw new InvalidOperationException("Windows did not open display settings.");
            }
            catch (Exception ex)
            {
                Llampec.Diagnostics.Log.Error("Could not open display settings", ex);
                if (!_disposed) _localError = "Could not open Windows Settings.";
            }
            finally
            {
                if (!_disposed) { settings.IsEnabled = true; Update(); }
            }
        };
        Children.Add(settings);
        _service.Changed += OnChanged;
        Update();
    }

    private static string T(string key) => UiText.Get(key);
    private static TextBlock Note() => new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private static string OrientationName(DisplayOrientation orientation) => T(orientation switch
    {
        DisplayOrientation.Landscape => "Landscape",
        DisplayOrientation.Portrait => "Portrait",
        DisplayOrientation.LandscapeFlipped => "Landscape (flipped)",
        DisplayOrientation.PortraitFlipped => "Portrait (flipped)",
        _ => "Unknown",
    });

    private static FrameworkElement OrientationPreview(DisplayOrientation orientation)
    {
        // The text baseline follows the short edge in portrait. Flipped choices
        // keep their lettering upside down to distinguish them visually.
        var frame = new Grid { Width = 64, Height = 56, IsHitTestVisible = false };
        var screen = new Grid
        {
            Width = 52, Height = 32, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Windows.Foundation.Point(.5, .5),
            RenderTransform = new RotateTransform { Angle = -(int)orientation * 90 },
        };
        screen.Children.Add(new PathIcon
        {
            Width = 52, Height = 32,
            Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry),
                "F0 M 4,1 H 48 A 3,3 0 0 1 51,4 V 28 A 3,3 0 0 1 48,31 H 4 A 3,3 0 0 1 1,28 V 4 A 3,3 0 0 1 4,1 Z " +
                "M 4,2.5 H 48 A 1.5,1.5 0 0 1 49.5,4 V 28 A 1.5,1.5 0 0 1 48,29.5 H 4 A 1.5,1.5 0 0 1 2.5,28 V 4 A 1.5,1.5 0 0 1 4,2.5 Z"),
        });
        screen.Children.Add(new TextBlock
        {
            Text = "AB", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Windows.Foundation.Point(.5, .5),
            RenderTransform = new RotateTransform
            {
                Angle = orientation is DisplayOrientation.Portrait or DisplayOrientation.PortraitFlipped ? 90 : 0,
            },
        });
        frame.Children.Add(screen);
        return frame;
    }

    private async Task RunAsync(Func<Task> operation)
    {
        _localError = null;
        _holdDismissal(true);
        try { await operation(); }
        catch (Exception ex)
        {
            Llampec.Diagnostics.Log.Error("Could not complete rotation operation", ex);
            if (!_disposed) _localError = "Could not change display orientation";
        }
        finally
        {
            _holdDismissal(false);
            if (!_disposed) Update();
        }
    }

    private void OnChanged(object? sender, EventArgs args)
    {
        if (DispatcherQueue.HasThreadAccess) Update(); else DispatcherQueue.TryEnqueue(Update);
    }

    private void Update()
    {
        if (_disposed) return;
        _updating = true;
        try
        {
            var snapshot = _service.Snapshot;
            if (_service.RecoveryDisplayId is { } recoveryDisplayId) _selectedDisplayId = recoveryDisplayId;
            // Keep the explicit identity if it disappears; never silently rotate another screen.
            string signature = string.Join("\n", snapshot.Displays.Select(d => $"{d.Id}|{d.Name}|{d.IsInternal}"));
            if (_displayListSignature != signature)
            {
                _displayListSignature = signature;
                _display.Items.Clear();
                foreach (var display in snapshot.Displays)
                    _display.Items.Add(new ComboBoxItem
                    {
                        Tag = display.Id,
                        Content = display.Name == "Display" ? T(display.IsInternal ? "Internal display" : "Display")
                            : display.IsInternal ? UiText.Format("Internal display · {0}", display.Name) : display.Name,
                    });
            }
            _display.SelectedItem = _display.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == _selectedDisplayId);
            var selected = _selectedDisplayId is { } id ? snapshot.Find(id) : null;
            _displayState.Text = selected is null
                ? T(_selectedDisplayId is null ? "No active displays" : "Selected display disconnected")
                : selected.IsCloned ? T("Extend these displays to change orientation separately")
                : !selected.CanOrient ? T("Orientation unavailable for this display")
                : UiText.Format("Current orientation: {0}", OrientationName(selected.Orientation));
            bool enabled = !_service.IsBusy && !_service.RecoveryPending;
            _display.IsEnabled = enabled;
            foreach (var (orientation, button) in _orientations)
            {
                button.IsChecked = selected?.Orientation == orientation;
                button.IsEnabled = enabled && selected is { CanOrient: true, IsCloned: false };
            }
            _rotationState.Text = T("Internal display automatic rotation") + ": " + string.Join(" · ", snapshot.Rotation.ReasonKeys.Select(T));
            _recover.Visibility = _service.RecoveryPending ? Visibility.Visible : Visibility.Collapsed;
            _recover.IsEnabled = !_service.IsBusy;
            _refresh.IsEnabled = !_service.IsBusy;
            string? messageKey = _localError ?? _service.MessageKey;
            // The selected card communicates success without adding another row after every tap.
            _message.IsOpen = messageKey is not null and not "Orientation applied";
            _message.Message = messageKey is { } message ? T(message) : "";
            _message.Severity = messageKey is "Previous orientation restored" ? InfoBarSeverity.Informational : InfoBarSeverity.Warning;
        }
        finally { _updating = false; }
    }

    public void Dispose()
    {
        _disposed = true;
        _service.Changed -= OnChanged;
    }
}
