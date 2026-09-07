using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Llampec.Interop;
using Llampec.Platform;
using Llampec.Settings;
using Llampec.ViewModels;
using Microsoft.Win32;

namespace Llampec.Flyout;

/// <summary>
/// The panel. Created once at start-up and kept hidden, so opening it is instant. Closes when it loses
/// focus, like the native Quick Settings flyout.
/// </summary>
public partial class FlyoutWindow : Window
{
    private const int EdgeMargin = 12; // DIPs between the flyout and the work-area edge, as in Windows 11

    private readonly FlyoutViewModel _viewModel;
    private readonly AppSettings _settings;
    private nint _hwnd;
    private bool _hasBackdrop;
    private DateTime _shownAt;

    public FlyoutWindow(FlyoutViewModel viewModel, AppSettings settings)
    {
        _viewModel = viewModel;
        _settings = settings;
        InitializeComponent();
        DataContext = viewModel;
        Root.Opacity = 0;

        SizeChanged += (_, _) => { if (IsVisible) { Reposition(); } };
        Deactivated += (_, _) => HidePanel();
        viewModel.CloseRequested += (_, _) => HidePanel();
        PreviewKeyDown += OnPreviewKeyDown;
        App.Current.ThemeChanged += (_, _) => ApplyBackdrop();

        // Create the HWND now (SourceInitialized runs) so DWM attributes are set before the first Show().
        new WindowInteropHelper(this).EnsureHandle();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // When the handle is created early via EnsureHandle(), PresentationSource.FromVisual is still null
        // at this point, so take the handle from the interop helper instead.
        _hwnd = new WindowInteropHelper(this).Handle;
        ApplyTransparentBackground();

        // Hide from Alt+Tab.
        nint exStyle = User32.GetWindowLongPtr(_hwnd, User32.GWL_EXSTYLE);
        User32.SetWindowLongPtr(_hwnd, User32.GWL_EXSTYLE, exStyle | User32.WS_EX_TOOLWINDOW);

        Dwm.SetInt(_hwnd, Dwm.DWMWA_WINDOW_CORNER_PREFERENCE, Dwm.DWMWCP_ROUND);
        Dwm.SetUInt(_hwnd, Dwm.DWMWA_BORDER_COLOR, Dwm.DWMWA_COLOR_NONE); // we draw our own 1px stroke
        ApplyBackdrop();
    }

    /// <summary>Transparent client area so DWM's backdrop shows through. Safe to call more than once.</summary>
    private void ApplyTransparentBackground()
    {
        if (_hwnd != 0 && HwndSource.FromHwnd(_hwnd) is { } source)
        {
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
        }
    }

    private void ApplyBackdrop()
    {
        if (_hwnd == 0)
        {
            return;
        }

        Dwm.SetInt(_hwnd, Dwm.DWMWA_USE_IMMERSIVE_DARK_MODE, App.Current.IsDarkTheme ? 1 : 0);

        // Acrylic (transient) backdrop is what the native flyouts use. When the user disabled transparency
        // effects, DWM paints nothing, so fall back to a solid surface.
        _hasBackdrop = IsTransparencyEnabled();
        Dwm.SetInt(_hwnd, Dwm.DWMWA_SYSTEMBACKDROP_TYPE, _hasBackdrop ? Dwm.DWMSBT_TRANSIENTWINDOW : Dwm.DWMSBT_NONE);
        Root.Background = _hasBackdrop ? Brushes.Transparent : (Brush)FindResource("SolidBackgroundFillBaseBrush");
    }

    private static bool IsTransparencyEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SystemTheme.PersonalizeKey);
        return key?.GetValue("EnableTransparency") is not int v || v != 0;
    }

    public void Toggle()
    {
        if (IsVisible)
        {
            // Absorb duplicate activation events that arrive right after opening (tray click bounce).
            if (DateTime.UtcNow - _shownAt < TimeSpan.FromMilliseconds(300))
            {
                return;
            }

            HidePanel();
        }
        else
        {
            ShowPanel();
        }
    }

    public void ShowPanel()
    {
        if (IsVisible)
        {
            Activate();
            return;
        }

        _viewModel.CloseSubpage();
        _viewModel.RefreshAll();
        Root.Opacity = 0;
        Show();
        // DWM composes the first frame opaque unless the backdrop is (re)applied on the visible window
        // and the frame is refreshed; do it after Show() every time.
        ApplyTransparentBackground();
        Reposition();
        // A real attribute change (NONE -> acrylic) one frame after Show() makes DWM pick the backdrop up.
        Dwm.SetInt(_hwnd, Dwm.DWMWA_SYSTEMBACKDROP_TYPE, Dwm.DWMSBT_NONE);
        Dispatcher.BeginInvoke(() =>
        {
            ApplyBackdrop();
            User32.SetWindowPos(_hwnd, 0, 0, 0, 0, 0,
                User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE | User32.SWP_FRAMECHANGED);
        }, DispatcherPriority.Render);
        Activate();
        _shownAt = DateTime.UtcNow;
        AnimateIn();
    }

    public void HidePanel()
    {
        if (!IsVisible)
        {
            return;
        }

        Hide();
        if (_settings.TrimWorkingSetOnClose)
        {
            Dispatcher.BeginInvoke(Kernel32.TrimWorkingSet, DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>Bottom-right of the work area of the monitor under the cursor (physical pixels via SetWindowPos).</summary>
    private void Reposition()
    {
        if (_hwnd == 0)
        {
            return;
        }

        User32.GetCursorPos(out var cursor);
        var (work, scale) = User32.GetMonitorWorkAreaAt(cursor);
        int widthPx = (int)Math.Round(ActualWidth * scale);
        int heightPx = (int)Math.Round(ActualHeight * scale);
        int marginPx = (int)Math.Round(EdgeMargin * scale);

        int x = work.Right - widthPx - marginPx;
        int y = work.Bottom - heightPx - marginPx;
        User32.SetWindowPos(_hwnd, 0, x, y, 0, 0, User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
    }

    private void AnimateIn()
    {
        var transform = new TranslateTransform(0, 24);
        Root.RenderTransform = transform;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(180);
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(24, 0, duration) { EasingFunction = ease });
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (_viewModel.IsSubpageOpen)
        {
            _viewModel.CloseSubpage();
        }
        else
        {
            HidePanel();
        }

        e.Handled = true;
    }

    /// <summary>Tray context menu. <paramref name="screenPoint"/> is in physical pixels.</summary>
    public void ShowTrayMenu(Point screenPoint)
    {
        _ = screenPoint; // WPF places the menu at the cursor; the position is kept for keyboard invocation later
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };

        var settingsItem = new MenuItem { Header = "Settings", IsEnabled = false }; // step 7
        var startupItem = new MenuItem { Header = "Start with Windows", IsCheckable = true, IsEnabled = false }; // step 7
        var aboutItem = new MenuItem { Header = "About Llampec" };
        aboutItem.Click += (_, _) => MessageBox.Show(
            $"Llampec {typeof(App).Assembly.GetName().Version?.ToString(3)}\nMIT License — https://github.com/PereLV/Llampec",
            "Llampec", MessageBoxButton.OK, MessageBoxImage.Information);
        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => App.Current.ExitApplication();

        menu.Items.Add(settingsItem);
        menu.Items.Add(startupItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(aboutItem);
        menu.Items.Add(exitItem);

        // The menu must belong to a foreground window to dismiss on outside clicks.
        User32.SetForegroundWindow(_hwnd);
        menu.IsOpen = true;
    }
}
