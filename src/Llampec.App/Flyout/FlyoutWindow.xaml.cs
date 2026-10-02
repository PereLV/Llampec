using System.ComponentModel;
using Llampec.Actions;
using Llampec.Interop;
using Llampec.Settings;
using Llampec.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using VirtualKey = Windows.System.VirtualKey;
using System.Diagnostics;
using Llampec.Diagnostics;
using Microsoft.UI.Dispatching;

using Windows.UI.ViewManagement;

namespace Llampec.Flyout;

/// <summary>A lightweight window shell. Tile controls and backdrop are rebuilt after idle release.</summary>
public sealed partial class FlyoutWindow : Window, IDisposable
{
    private readonly FlyoutViewModel _model;
    private readonly List<Action> _unsubscribe = [];
    private readonly List<Action> _subUnsubscribe = [];
    private bool _dialogOpen;
    private bool _disposed;
    private readonly nint _hwnd;
    private ThemeScheduleView? _scheduleView;
    private CaffeineView? _caffeineView;
    private AlwaysOnTopView? _alwaysOnTopView;
    private RotationView? _rotationView;
    private int _observedPinCount;
    private bool _closing;
    private readonly PanelMotion _motion;
    private readonly PanelAcrylicBackdrop _panelBackdrop;
    private readonly DispatcherQueueTimer _idleTimer;
    private TaskCompletionSource? _panelHidden;
    private int _showVersion;
    private int _idleStage;
    private bool _transitioning;
    private bool _placementPending;
    private bool _showMenuAfterOpen;
    private float _slideDistance;
    private User32.POINT _anchor;
    private nint _anchorMonitor;
    private bool _hasAnchor;
    private readonly UISettings _uiSettings = new();

    public FlyoutWindow(FlyoutViewModel model)
    {
        InitializeComponent();
        _model = model;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _motion = new PanelMotion(Surface, DispatcherQueue);
        _idleTimer = DispatcherQueue.CreateTimer();
        _idleTimer.IsRepeating = false;
        _idleTimer.Tick += OnIdle;
        AppWindow.IsShownInSwitchers = false;
        var presenter = OverlappedPresenter.Create();
        // The visible frame and material belong to Surface so they can move as
        // one compositor visual. The HWND only provides a stationary viewport.
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        Dwm.SetInt(_hwnd, Dwm.DWMWA_WINDOW_CORNER_PREFERENCE, Dwm.DWMWCP_DONOTROUND);
        SystemBackdrop = new TransparentPanelBackdrop(_hwnd);
        _panelBackdrop = new PanelAcrylicBackdrop(Stage);
        PanelMaterial.SystemBackdrop = _panelBackdrop;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Llampec.ico"));
        Root.ActualThemeChanged += OnActualThemeChanged;
        ApplyTheme();
        UpdateLanguage();
        Root.Loaded += (_, _) => Reposition();
        Tiles.SizeChanged += OnPageSizeChanged;
        Subpage.SizeChanged += OnPageSizeChanged;
        model.RunWithPanelHiddenRequested += RunWithPanelHiddenAsync;
        model.PropertyChanged += OnModelChanged;
        Activated += (_, e) =>
        {
            // A mouse-down on our tray icon deactivates us BEFORE NIN_SELECT arrives.
            // Let that callback toggle once, rather than hiding here and reopening there.
            if (e.WindowActivationState == WindowActivationState.Deactivated && !_dialogOpen
                && !_model.Tiles.Any(t => t.Id == "rotation" && t.IsBusy))
            {
                bool trayPress = App.Current.IsPointerPressOnTrayIcon();
                Log.Info($"Panel deactivated: trayPress={trayPress}");
                if (!trayPress) HidePanel();
            }
        };
        AppWindow.Closing += (_, e) => { e.Cancel = true; HidePanel(); };
    }

    public void ApplyTheme()
    {
        Stage.RequestedTheme = (App.Current.PreviewTheme ?? App.Current.Settings.Theme) switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        UpdateFrameTheme();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        UpdateFrameTheme();
        if (!AppWindow.IsVisible) ScheduleIdleRelease();
    }

    private void UpdateFrameTheme()
    {
        // XAML theme does not automatically update the non-client DWM outline.
        // WinUI draws the same theme-aware stroke as its native flyouts. Suppress
        // the extra DWM outline, which otherwise leaves a bright edge in dark mode.
        Dwm.SetInt(_hwnd, Dwm.DWMWA_USE_IMMERSIVE_DARK_MODE, Root.ActualTheme == ElementTheme.Dark ? 1 : 0);
        Dwm.SetUInt(_hwnd, Dwm.DWMWA_BORDER_COLOR, Dwm.DWMWA_COLOR_NONE);
    }

    private static FontIcon Icon(string glyph, double size = 20) => ActionIcons.Glyph(glyph, size);

    private void BuildTiles()
    {
        for (int i = 0; i < 3; i++) Tiles.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < (_model.Tiles.Count + 2) / 3; i++) Tiles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < _model.Tiles.Count; i++)
        {
            var tile = _model.Tiles[i];
            var stack = new StackPanel { Spacing = 8 };
            var face = new Grid();
            face.ColumnDefinitions.Add(new ColumnDefinition());
            if (tile.HasSubpage) face.ColumnDefinitions.Add(new ColumnDefinition());
            var glyph = ActionIcons.Tile(tile.Id, tile.Glyph, tile.GlyphBadge);
            ButtonBase button = tile.IsButton ? new Button() : new ToggleButton();
            button.Content = glyph;
            button.Height = 48;
            button.Padding = new Thickness(0);
            button.CornerRadius = tile.HasSubpage ? new CornerRadius(6, 0, 0, 6) : new CornerRadius(6);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            AutomationProperties.SetName(button, tile.Title);
            button.Click += (_, _) => tile.ToggleCommand.Execute(null);
            face.Children.Add(button);
            Button? more = null;
            if (tile.HasSubpage)
            {
                more = new Button
                {
                    Content = Icon("\uE76C", 16), Padding = new Thickness(0), Height = 48,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    CornerRadius = new CornerRadius(0, 6, 6, 0), BorderThickness = new Thickness(0, 1, 1, 1),
                };
                AutomationProperties.SetName(more, UiText.Format("{0} options", tile.Title));
                more.Click += (_, _) =>
                {
                    _model.OpenSubpage(tile);
                };
                Grid.SetColumn(more, 1);
                face.Children.Add(more);
            }
            var title = new TextBlock { Text = tile.Title, FontSize = 12, TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTipService.SetToolTip(button, tile.Title);
            var subtitle = new TextBlock
            {
                FontSize = 12, TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Root.Resources["TileStatusStyle"],
            };
            var caption = new StackPanel { Spacing = 2 };
            caption.Children.Add(title);
            caption.Children.Add(subtitle);
            var pinnedCount = new TextBlock
            {
                FontSize = 11, TextAlignment = TextAlignment.Center,
                Style = (Style)Root.Resources["TileStatusStyle"],
                Visibility = Visibility.Collapsed,
            };
            if (tile.Id == "always-on-top")
            {
                subtitle.MaxLines = 2;
                subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
                caption.Children.Add(pinnedCount);
            }
            var busy = new ProgressBar
            {
                Height = 2, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(6, 0, 6, 2), IsHitTestVisible = false,
            };
            Grid.SetColumnSpan(busy, 2);
            face.Children.Add(busy);
            stack.Children.Add(face);
            stack.Children.Add(caption);
            void Update()
            {
                button.IsEnabled = tile.IsAvailable && !tile.IsBusy;
                if (button is ToggleButton toggle) toggle.IsChecked = tile.IsOn || tile.IsMixed;
                if (more is not null)
                    more.Style = tile.IsOn || tile.IsMixed ? (Style)Application.Current.Resources["AccentButtonStyle"] : null;
                subtitle.Text = tile.CompactSubtitle ?? "";
                subtitle.Visibility = tile.HasSubtitle ? Visibility.Visible : Visibility.Collapsed;
                ToolTipService.SetToolTip(subtitle, tile.Subtitle);
                ToolTipService.SetToolTip(button, string.IsNullOrEmpty(tile.Subtitle) ? tile.Title : $"{tile.Title}\n{tile.Subtitle}");
                busy.Visibility = tile.IsBusy ? Visibility.Visible : Visibility.Collapsed;
                busy.IsIndeterminate = tile.IsBusy;
                AutomationProperties.SetHelpText(button, tile.IsMixed ? T("Some displays are on") : tile.Subtitle ?? "");
                if (tile.Id == "always-on-top")
                {
                    int count = App.Current.AlwaysOnTop?.PinnedCount ?? 0;
                    pinnedCount.Text = UiText.Format("{0} pinned", count);
                    pinnedCount.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
                    AutomationProperties.SetHelpText(button, $"{tile.Subtitle}. {pinnedCount.Text}");
                }
            }
            Observe(tile, Update, _unsubscribe);
            Grid.SetColumn(stack, i % 3);
            Grid.SetRow(stack, i / 3);
            Tiles.Children.Add(stack);
        }
    }

    private static void Observe(TileViewModel tile, Action update, List<Action> subscriptions)
    {
        PropertyChangedEventHandler handler = (_, _) => update();
        tile.PropertyChanged += handler;
        subscriptions.Add(() => tile.PropertyChanged -= handler);
        update();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FlyoutViewModel.Subpage)) return;
        _utilityPage = UtilityPage.None;
        ReleaseSubpage();
        bool open = _model.Subpage is not null;
        PageHeader.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        Tiles.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        Subpage.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        Heading.Text = _model.Subpage?.Title ?? "Llampec";
        if (_model.Subpage is { } page)
        {
            if (page.Id == "theme")
            {
                _scheduleView = new ThemeScheduleView(hold => _dialogOpen = hold);
                Subpage.Children.Add(_scheduleView);
            }
            if (page.Action is Llampec.Actions.Caffeine.CaffeineAction caffeine)
            {
                _caffeineView = new CaffeineView(caffeine);
                Subpage.Children.Add(_caffeineView);
            }
            if (page.Id == "always-on-top" && App.Current.AlwaysOnTop is { } alwaysOnTop)
            {
                _alwaysOnTopView = new AlwaysOnTopView(alwaysOnTop, hold => _dialogOpen = hold);
                Subpage.Children.Add(_alwaysOnTopView);
            }
            if (page.Id == "rotation" && App.Current.Rotation is { } rotation)
            {
                _rotationView = new RotationView(rotation, hold => _dialogOpen = hold);
                Subpage.Children.Add(_rotationView);
            }
            foreach (var tile in page.SubTiles)
            {
                bool updating = false;
                if (tile.IsRadioItem)
                {
                    var radio = new RadioButton { Content = tile.Title, GroupName = page.Id, MinHeight = 44 };
                    radio.Click += (_, _) => { if (!updating) tile.ToggleCommand.Execute(null); };
                    Subpage.Children.Add(radio);
                    Observe(tile, () =>
                    {
                        updating = true;
                        radio.IsChecked = tile.IsOn;
                        radio.IsEnabled = tile.IsAvailable && !tile.IsBusy;
                        updating = false;
                    }, _subUnsubscribe);
                }
                else
                {
                    var toggle = new ToggleSwitch { Header = tile.Title, OnContent = T("On"), OffContent = T("Off"), HorizontalAlignment = HorizontalAlignment.Stretch };
                    toggle.Toggled += (_, _) => { if (!updating) tile.ToggleCommand.Execute(null); };
                    Subpage.Children.Add(toggle);
                    Observe(tile, () =>
                    {
                        updating = true;
                        toggle.IsOn = tile.IsOn;
                        toggle.IsEnabled = tile.IsAvailable && !tile.IsBusy;
                        updating = false;
                    }, _subUnsubscribe);
                }
            }
            BackButton.Focus(FocusState.Programmatic);
        }
        if (AppWindow.IsVisible) Reposition();
    }

    private void Reposition() => Reposition(allowHidden: false);

    private void Reposition(bool allowHidden)
    {
        // A transition owns its fixed viewport until completion.
        if (_disposed || !_hasAnchor || (!allowHidden && !AppWindow.IsVisible)) return;
        if (_transitioning) { _placementPending = true; return; }
        _placementPending = false;
        // Keep the opening monitor even if the pointer moves during layout/animation.
        var (work, scale) = PlacementArea();
        int margin = (int)Math.Round(12 * scale);
        int visibleWidth = Math.Min((int)Math.Round(360 * scale), Math.Max(1, work.Width - 2 * margin));
        int width = visibleWidth + 2 * margin;
        // Size from the actual wrapped captions/subtitles, rather than a fixed row estimate.
        // Keep the footer visible; only scroll when the monitor's work area requires it.
        Root.Measure(new Windows.Foundation.Size(visibleWidth / scale, double.PositiveInfinity));
        double heightDip = Root.DesiredSize.Height;
        int height = Math.Min((int)Math.Ceiling(heightDip * scale) + 2 * margin, work.Height);
        var bounds = new RectInt32(work.Right - width, work.Bottom - height, width, height);
        _slideDistance = (float)(height / scale);
        if (AppWindow.Size.Width != width || AppWindow.Size.Height != height
            || AppWindow.Position.X != bounds.X || AppWindow.Position.Y != bounds.Y)
            AppWindow.MoveAndResize(bounds);
    }

    private void CapturePlacement()
    {
        User32.GetCursorPos(out _anchor);
        _hasAnchor = true;
        _anchorMonitor = User32.MonitorFromPoint(_anchor, User32.MONITOR_DEFAULTTONEAREST);
        var (work, scale) = PlacementArea();
        // Adopt the target DPI before measuring. The entire host remains within
        // this work area throughout animation, even with vertically stacked screens.
        if (User32.GetDpiForWindow(_hwnd) != (uint)Math.Round(96 * scale))
            AppWindow.Move(new PointInt32(work.Left, work.Top));
        Log.Info($"Panel placement: dpi={User32.GetDpiForWindow(_hwnd)}; scale={scale}; transparent host");
    }

    private (User32.RECT WorkArea, double Scale) PlacementArea()
    {
        // Rotation can move the original cursor point onto a different display.
        // Keep the opening monitor while it exists, then fall back to the nearest.
        var info = new User32.MONITORINFOEXW { cbSize = (uint)Marshal.SizeOf<User32.MONITORINFOEXW>() };
        if (_anchorMonitor == 0 || !User32.GetMonitorInfo(_anchorMonitor, ref info))
        {
            _anchorMonitor = User32.MonitorFromPoint(_anchor, User32.MONITOR_DEFAULTTONEAREST);
            if (!User32.GetMonitorInfo(_anchorMonitor, ref info)) return User32.GetMonitorWorkAreaAt(_anchor);
        }
        _anchor.X = Math.Clamp(_anchor.X, info.rcMonitor.Left, Math.Max(info.rcMonitor.Left, info.rcMonitor.Right - 1));
        _anchor.Y = Math.Clamp(_anchor.Y, info.rcMonitor.Top, Math.Max(info.rcMonitor.Top, info.rcMonitor.Bottom - 1));
        double scale = User32.GetDpiForMonitor(_anchorMonitor, 0, out uint dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
        var work = info.rcWork;
        if (Llampec.Platform.TabletTaskbar.ReadMode() == Llampec.Platform.TaskbarMode.TabletOptimized)
        {
            // Tablet rcWork can reserve only the collapsed strip. Reserve the shell-reported
            // taskbar bounds so expanding it cannot cover our footer after a rotation.
            // ABM_GETTASKBARPOS reports the system taskbar, not an arbitrary monitor's bar.
            // https://learn.microsoft.com/windows/win32/shell/abm-gettaskbarpos
            var bar = new Shell32.APPBARDATA { cbSize = (uint)Marshal.SizeOf<Shell32.APPBARDATA>() };
            if (Shell32.SHAppBarMessage(Shell32.ABM_GETTASKBARPOS, ref bar) != 0 && bar.uEdge == 3
                && bar.rc.Left >= info.rcMonitor.Left && bar.rc.Right <= info.rcMonitor.Right
                && bar.rc.Right > bar.rc.Left && bar.rc.Top > work.Top
                && bar.rc.Bottom == info.rcMonitor.Bottom && bar.rc.Top < bar.rc.Bottom)
                work.Bottom = Math.Min(work.Bottom, bar.rc.Top);
        }
        return (work, scale);
    }

    public void OnDisplayEnvironmentChanged()
    {
        if (!_disposed && AppWindow.IsVisible) Reposition();
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (AppWindow.IsVisible) DispatcherQueue.TryEnqueue(Reposition);
    }

    public void Toggle()
    {
        if (_closing) { ShowPanel(); return; }
        if (AppWindow.IsVisible)
        {
            HidePanel();
        }
        else ShowPanel();
    }

    public void ShowPanel()
    {
        if (_disposed) return;
        ++_showVersion;
        // Repeated show requests must not snap an entry animation to its endpoint.
        if (AppWindow.IsVisible && !_closing)
        {
            Activate();
            User32.SetForegroundWindow(_hwnd);
            return;
        }
        bool fresh = !AppWindow.IsVisible;
        _panelHidden?.TrySetCanceled();
        _panelHidden = null;
        App.Current.AlwaysOnTop?.CaptureTarget();
        long started = Stopwatch.GetTimestamp();
        _idleTimer.Stop();
        _closing = false;
        if (fresh)
        {
            _motion.Cancel();
            _transitioning = false;
            CapturePlacement();
            ApplyTheme();
            if (Tiles.Children.Count == 0) BuildTiles();
            _panelBackdrop.Resume();
            ShowMainPage();
            try { _model.RefreshAll(); }
            catch (Exception ex)
            {
                ErrorBar.Message = ex.Message;
                ErrorBar.IsOpen = true;
            }
            App.Current.Scheduler?.SetStatusVisible(true);
            Reposition(allowHidden: true);
        }
        bool useMotion = _uiSettings.AnimationsEnabled;
        _transitioning = true;
        _motion.PrepareOpening(_slideDistance, fresh);
        AppWindow.Show();
        Activate();
        User32.SetForegroundWindow(_hwnd);
        Log.Info($"Panel prepared: {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms; animations={useMotion}; host={AppWindow.Position.X},{AppWindow.Position.Y},{AppWindow.Size.Width},{AppWindow.Size.Height}");
        _motion.Start(opening: true, useMotion, fresh, _slideDistance, () =>
        {
            _transitioning = false;
            if (_placementPending) Reposition();
            if (_showMenuAfterOpen)
            {
                _showMenuAfterOpen = false;
                ShowMenu();
            }
        });
    }

    public void HidePanel()
    {
        if (_disposed || !AppWindow.IsVisible || _closing) return;
        _showMenuAfterOpen = false;
        _closing = true;
        _transitioning = true;
        _motion.Start(opening: false, _uiSettings.AnimationsEnabled, fresh: false, _slideDistance, FinishHide);
    }

    private void FinishHide()
    {
        AppWindow.Hide();
        App.Current.AlwaysOnTop?.ReleaseTarget();
        App.Current.Scheduler?.SetStatusVisible(false);
        _closing = false;
        _transitioning = false;
        ShowMainPage();
        ScheduleIdleRelease();
        _panelHidden?.TrySetResult();
        _panelHidden = null;
    }

    public void ScheduleIdleRelease()
    {
        if (_disposed || AppWindow.IsVisible) return;
        _idleStage = 0;
        _idleTimer.Stop();
        _idleTimer.Interval = TimeSpan.FromSeconds(2);
        _idleTimer.Start();
    }

    private void OnIdle(DispatcherQueueTimer sender, object args)
    {
        if (_disposed || AppWindow.IsVisible || _model.Tiles.Any(t => t.IsBusy)) return;
        if (_idleStage++ == 0)
        {
            foreach (var unsubscribe in _unsubscribe) unsubscribe();
            _unsubscribe.Clear();
            Tiles.Children.Clear(); Tiles.ColumnDefinitions.Clear(); Tiles.RowDefinitions.Clear();
            // Keep the element's external backdrop link attached to its host.
            // Releasing it during hidden-window idle can destroy WinUI input-site
            // resources from finalization; the native surface lives until Dispose.
            _panelBackdrop.Suspend();
            ErrorBar.IsOpen = false;
            // Once per idle transition, never periodically. Collect the discarded control
            // tree, then give WinUI/finalizers time to release native references before trimming.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false);
            _idleTimer.Interval = TimeSpan.FromMilliseconds(500);
            _idleTimer.Start();
            return;
        }
        Log.Info("Idle: tile controls/acrylic controller released; native surface retained; returning resident pages to Windows.");
        if (!Kernel32.EmptyWorkingSet(-1)) Log.Warn("Could not trim the idle working set.");
    }

    private async Task RunWithPanelHiddenAsync(Func<Task> action)
    {
        if (_disposed) return;
        int version = _showVersion;
        if (AppWindow.IsVisible)
        {
            var completion = _panelHidden ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            HidePanel();
            await completion.Task;
        }
        // Check and invoke on the UI thread without another continuation between
        // them: reopening the panel cancels a capture even after the hide completed.
        if (!_disposed && !AppWindow.IsVisible && version == _showVersion) await action();
    }
    private void OnStagePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Surface).Position;
        if (point.X < 0 || point.Y < 0 || point.X > Surface.ActualWidth || point.Y > Surface.ActualHeight)
            HidePanel();
    }
    private void OnBack(object sender, RoutedEventArgs e) => GoBack();
    private void OnMenu(object sender, RoutedEventArgs e) => ShowSettings();
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || _dialogOpen) return;
        if (_utilityPage != UtilityPage.None || _model.IsSubpageOpen) GoBack(); else HidePanel();
        e.Handled = true;
    }

    public void ShowTrayMenu()
    {
        ShowPanel();
        // Anchor the menu after the panel surface reaches its final position.
        if (_transitioning) _showMenuAfterOpen = true;
        else DispatcherQueue.TryEnqueue(ShowMenu);
    }

    public void OpenAlwaysOnTop()
    {
        if (_disposed) return;
        ShowPanel();
        if (_model.Tiles.FirstOrDefault(tile => tile.Id == "always-on-top") is { } tile)
            _model.OpenSubpage(tile);
    }

    public void OnAlwaysOnTopChanged()
    {
        int count = App.Current.AlwaysOnTop?.PinnedCount ?? 0;
        bool added = count > _observedPinCount;
        _observedPinCount = count;
        // HWND_TOPMOST raises a newly pinned target even with NOACTIVATE. Keep the
        // active panel usable if that target covers it; never raise a background panel.
        if (added && !_disposed && !_closing && AppWindow.IsVisible && User32.GetForegroundWindow() == _hwnd)
            User32.SetWindowPos(_hwnd, -1, 0, 0, 0, 0,
                User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE | 0x0200 /* NOOWNERZORDER */);
    }

    private void ShowMenu()
    {
        if (_disposed || !AppWindow.IsVisible || _closing) return;
        if (_transitioning) { _showMenuAfterOpen = true; return; }
        var menu = new MenuFlyout();
        var settings = new MenuFlyoutItem { Text = T("Settings") };
        settings.Click += (_, _) => ShowSettings();
        menu.Items.Add(settings);
        var about = new MenuFlyoutItem { Text = T("About Llampec") };
        about.Click += (_, _) => ShowAbout();
        menu.Items.Add(about);
        menu.Items.Add(new MenuFlyoutSeparator());
        var exit = new MenuFlyoutItem { Text = T("Exit") };
        exit.Click += (_, _) => App.Current.ExitApplication();
        menu.Items.Add(exit);
        menu.ShowAt(MenuButton);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _panelHidden?.TrySetCanceled();
        _panelHidden = null;
        AppWindow.Hide();
        _motion.Dispose();
        _idleTimer.Stop();
        _idleTimer.Tick -= OnIdle;
        _scheduleView?.Dispose();
        _caffeineView?.Dispose();
        _alwaysOnTopView?.Dispose();
        _rotationView?.Dispose();
        _logitechMouseView?.Dispose();
        foreach (var unsubscribe in _unsubscribe.Concat(_subUnsubscribe)) unsubscribe();
        _model.PropertyChanged -= OnModelChanged;
        _model.RunWithPanelHiddenRequested -= RunWithPanelHiddenAsync;
        Root.ActualThemeChanged -= OnActualThemeChanged;
        Tiles.SizeChanged -= OnPageSizeChanged;
        Subpage.SizeChanged -= OnPageSizeChanged;
        _panelBackdrop.Dispose();
    }
}
