using System.Reflection;
using Llampec.Platform;
using Llampec.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Llampec.Flyout;

public sealed partial class FlyoutWindow
{
    private enum UtilityPage { None, Settings, Editor, Logitech }
    private UtilityPage _utilityPage;
    private LogitechMouseView? _logitechMouseView;
    private static string T(string key) => UiText.Get(key);

    private void UpdateLanguage()
    {
        Root.Language = UiText.Language;
        AutomationProperties.SetName(BackButton, T("Back"));
        AutomationProperties.SetName(MenuButton, T("Settings"));
        AutomationProperties.SetName(EditButton, T("Edit panel"));
        SaveEditorButton.Content = T("Save");
        CancelEditorButton.Content = T("Cancel");
    }

    private void ReleaseSubpage()
    {
        ReleasePanelEditor();
        foreach (var unsubscribe in _subUnsubscribe) unsubscribe();
        _subUnsubscribe.Clear();
        _scheduleView?.Dispose(); _scheduleView = null;
        _caffeineView?.Dispose(); _caffeineView = null;
        _alwaysOnTopView?.Dispose(); _alwaysOnTopView = null;
        _fullscreenView?.Dispose(); _fullscreenView = null;
        _rotationView?.Dispose(); _rotationView = null;
        _logitechMouseView?.Dispose(); _logitechMouseView = null;
        Subpage.Children.Clear();
    }

    private void RebuildTiles(int? columns = null)
    {
        foreach (var unsubscribe in _unsubscribe) unsubscribe();
        _unsubscribe.Clear();
        Tiles.Children.Clear(); Tiles.ColumnDefinitions.Clear(); Tiles.RowDefinitions.Clear();
        BuildTiles(columns);
    }

    private void ShowMainPage()
    {
        _utilityPage = UtilityPage.None;
        if (_model.Subpage is not null) _model.CloseSubpage();
        else OnModelChanged(_model, new System.ComponentModel.PropertyChangedEventArgs(nameof(_model.Subpage)));
    }

    private void BeginUtilityPage(UtilityPage page, string title)
    {
        _model.CloseSubpage();
        ReleaseSubpage();
        _utilityPage = page;
        EditButton.Visibility = Visibility.Collapsed;
        PageHeader.Visibility = BackButton.Visibility = Subpage.Visibility = Visibility.Visible;
        Tiles.Visibility = Visibility.Collapsed;
        Heading.Text = T(title);
        ErrorBar.IsOpen = false;
    }

    private void GoBack()
    {
        if (_utilityPage is UtilityPage.Editor or UtilityPage.Logitech) ShowSettings();
        else if (_utilityPage == UtilityPage.Settings) ShowMainPage();
        else _model.CloseSubpage();
    }

    private static TextBlock Note(string text) => new()
    {
        Text = T(text), FontSize = 12, TextWrapping = TextWrapping.Wrap,
        Opacity = 0.75,
    };

    private void ShowSettings()
    {
        BeginUtilityPage(UtilityPage.Settings, "Settings");
        var language = new ComboBox { Header = T("Language"), HorizontalAlignment = HorizontalAlignment.Stretch };
        string?[] tags = [null, "es", "ca", "en"];
        language.Items.Add(T("Use Windows language"));
        language.Items.Add("Español (ESP)"); language.Items.Add("Català / Valencià (CAT/VAL)"); language.Items.Add("English (ENG)");
        language.SelectedIndex = App.Current.Settings.Language is { } savedLanguage
            ? Array.IndexOf(tags, UiText.ResolveLanguage(savedLanguage)) : 0;
        bool changingLanguage = false;
        language.SelectionChanged += (_, _) =>
        {
            if (changingLanguage) return;
            int index = language.SelectedIndex;
            if (index < 0) return;
            var settings = App.Current.Settings;
            string? previous = settings.Language;
            settings.Language = tags[index];
            if (!SettingsStore.Save(settings))
            {
                settings.Language = previous;
                changingLanguage = true;
                language.SelectedIndex = Math.Max(0, Array.IndexOf(tags, previous));
                changingLanguage = false;
                ErrorBar.Message = T("Could not save settings. Check access to the Llampec settings folder.");
                ErrorBar.IsOpen = true;
                return;
            }
            UiText.SetLanguage(settings.Language);
            UpdateLanguage(); RebuildTiles();
            // Recreate this small page immediately, including its accessible names.
            ShowSettings();
        };
        Subpage.Children.Add(language);

        var startup = new StartupRegistration(Environment.ProcessPath!);
        var start = new ToggleSwitch { Header = T("Start with Windows"), OnContent = T("On"), OffContent = T("Off"), Margin = new Thickness(0, 12, 0, 0) };
        try { start.IsOn = startup.IsRegistered; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException)
        { start.IsEnabled = false; ErrorBar.Message = T("Could not update startup registration."); ErrorBar.IsOpen = true; }
        bool updating = false;
        start.Toggled += (_, _) =>
        {
            if (updating) return;
            try { startup.SetRegistered(start.IsOn); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException)
            {
                updating = true; start.IsOn = !start.IsOn; updating = false;
                ErrorBar.Message = T("Could not update startup registration.") + " " + ex.Message; ErrorBar.IsOpen = true;
            }
        };
        Subpage.Children.Add(start);
        Subpage.Children.Add(Note("Opens in the notification area when you sign in."));
        Subpage.Children.Add(Note("Windows can also disable startup. After moving Llampec, open it once to update its startup location."));
        var windowsStartup = new HyperlinkButton { Content = T("Windows startup apps"), Padding = new Thickness(0) };
        windowsStartup.Click += async (_, _) =>
        {
            try
            {
                if (!await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:startupapps")))
                    throw new InvalidOperationException();
            }
            catch { ErrorBar.Message = T("Could not open Windows Settings."); ErrorBar.IsOpen = true; }
        };
        Subpage.Children.Add(windowsStartup);
        var reorder = new Button { Content = T("Buttons and categories"), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 12, 0, 0) };
        reorder.Click += (_, _) => ShowPanelEditor();
        Subpage.Children.Add(reorder);
        var logitech = new Button { Content = T("Logitech MX mouse"), HorizontalAlignment = HorizontalAlignment.Stretch };
        logitech.Click += (_, _) => ShowLogitechMouse();
        Subpage.Children.Add(logitech);
        var about = new Button { Content = T("About Llampec"), HorizontalAlignment = HorizontalAlignment.Stretch };
        about.Click += (_, _) => ShowAbout();
        Subpage.Children.Add(about);
        Reposition();
    }

    private void ShowLogitechMouse()
    {
        BeginUtilityPage(UtilityPage.Logitech, "Logitech MX mouse");
        _logitechMouseView = new LogitechMouseView(App.Current.LogitechMouse, Reposition);
        Subpage.Children.Add(_logitechMouseView);
        Reposition();
    }

private async void ShowAbout()
    {
        var assembly = typeof(App).Assembly;
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? assembly.GetName().Version?.ToString(3) ?? "";
        string copyright = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
            ?? "Copyright (c) 2026 Pere Esquerdo Ramis";
        _dialogOpen = true;
        try
        {
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock
            {
                Text = $"{T("Version")} {version}\n{copyright}\n{T("Open-source software · MIT License")}",
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(new HyperlinkButton
            {
                Content = T("Source code on GitHub"), NavigateUri = new Uri("https://github.com/PereLV/Llampec"),
                Padding = new Thickness(0),
            });
            content.Children.Add(new TextBlock { Text = T("Acknowledgements"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            content.Children.Add(Note("Always on Top inspired by Microsoft PowerToys."));
            content.Children.Add(new HyperlinkButton
            {
                Content = "Microsoft PowerToys · MIT", NavigateUri = new Uri("https://github.com/microsoft/PowerToys"),
                Padding = new Thickness(0),
            });
            content.Children.Add(Note("Logitech HID++ feature code adapted from Mouser."));
            content.Children.Add(new HyperlinkButton
            {
                Content = "Mouser · MIT", NavigateUri = new Uri("https://github.com/TomBadash/Mouser"),
                Padding = new Thickness(0),
            });
            await new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme,
                Title = T("About Llampec"), Content = content,
                CloseButtonText = T("Close") }.ShowAsync();
        }
        finally { _dialogOpen = false; }
    }
}
