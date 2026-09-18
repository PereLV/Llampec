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
    private enum UtilityPage { None, Settings, Reorder }
    private UtilityPage _utilityPage;
    private static string T(string key) => UiText.Get(key);

    private void UpdateLanguage()
    {
        Root.Language = UiText.Language;
        AutomationProperties.SetName(BackButton, T("Back"));
        AutomationProperties.SetName(MenuButton, T("Settings"));
    }

    private void ReleaseSubpage()
    {
        foreach (var unsubscribe in _subUnsubscribe) unsubscribe();
        _subUnsubscribe.Clear();
        _scheduleView?.Dispose(); _scheduleView = null;
        _caffeineView?.Dispose(); _caffeineView = null;
        _alwaysOnTopView?.Dispose(); _alwaysOnTopView = null;
        Subpage.Children.Clear();
    }

    private void RebuildTiles()
    {
        foreach (var unsubscribe in _unsubscribe) unsubscribe();
        _unsubscribe.Clear();
        Tiles.Children.Clear(); Tiles.ColumnDefinitions.Clear(); Tiles.RowDefinitions.Clear();
        BuildTiles();
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
        PageHeader.Visibility = BackButton.Visibility = Subpage.Visibility = Visibility.Visible;
        Tiles.Visibility = Visibility.Collapsed;
        Heading.Text = T(title);
        ErrorBar.IsOpen = false;
    }

    private void GoBack()
    {
        if (_utilityPage == UtilityPage.Reorder) ShowSettings();
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
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
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
        Subpage.Children.Add(Note("Windows can also disable startup. Keep Llampec in this folder, or enable this option again after moving it."));
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
        var reorder = new Button { Content = T("Reorder buttons"), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 12, 0, 0) };
        reorder.Click += (_, _) => ShowReorder();
        Subpage.Children.Add(reorder);
        var about = new Button { Content = T("About Llampec"), HorizontalAlignment = HorizontalAlignment.Stretch };
        about.Click += (_, _) => ShowAbout();
        Subpage.Children.Add(about);
        Reposition();
    }

    private void ShowReorder()
    {
        BeginUtilityPage(UtilityPage.Reorder, "Reorder buttons");
        Subpage.Children.Add(Note("Drag rows to change their order, or select one and use the arrows. The panel fills from left to right."));
        var list = new ListView { CanDragItems = true, CanReorderItems = true, AllowDrop = true,
            SelectionMode = ListViewSelectionMode.Single, MaxHeight = 320 };
        AutomationProperties.SetName(list, T("Reorder buttons"));
        foreach (var tile in _model.Tiles)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            row.Children.Add(Icon("\uE700", 14));
            row.Children.Add(new TextBlock { Text = tile.Title, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            var item = new ListViewItem { Content = row, Tag = tile.Id, MinHeight = 44 };
            AutomationProperties.SetName(item, tile.Title);
            list.Items.Add(item);
        }
        Subpage.Children.Add(list);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var up = new Button { Content = T("Move up") };
        var down = new Button { Content = T("Move down") };
        void UpdateButtons()
        {
            up.IsEnabled = list.SelectedIndex > 0;
            down.IsEnabled = list.SelectedIndex >= 0 && list.SelectedIndex < list.Items.Count - 1;
        }
        void Move(int delta)
        {
            int from = list.SelectedIndex, to = from + delta;
            if (from < 0 || to < 0 || to >= list.Items.Count) return;
            var item = list.Items[from]; list.Items.RemoveAt(from); list.Items.Insert(to, item);
            list.SelectedIndex = to; list.ScrollIntoView(item); UpdateButtons();
        }
        up.Click += (_, _) => Move(-1); down.Click += (_, _) => Move(1);
        list.SelectionChanged += (_, _) => UpdateButtons();
        list.DragItemsCompleted += (_, _) => UpdateButtons();
        list.SelectedIndex = list.Items.Count > 0 ? 0 : -1;
        UpdateButtons();
        controls.Children.Add(up); controls.Children.Add(down); Subpage.Children.Add(controls);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var save = new Button { Content = T("Save"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var cancel = new Button { Content = T("Cancel") };
        save.Click += (_, _) =>
        {
            var settings = App.Current.Settings;
            var previous = settings.TileOrder;
            var draft = list.Items.Cast<ListViewItem>().Select(item => (string)item.Tag).ToList();
            settings.TileOrder = draft.Concat(previous.Where(id => !draft.Contains(id))).ToList();
            if (!SettingsStore.Save(settings))
            {
                settings.TileOrder = previous;
                ErrorBar.Message = T("Could not save settings. Check access to the Llampec settings folder."); ErrorBar.IsOpen = true;
                return;
            }
            _model.ApplyOrder(draft); RebuildTiles(); ShowMainPage();
        };
        cancel.Click += (_, _) => ShowSettings();
        actions.Children.Add(save); actions.Children.Add(cancel); Subpage.Children.Add(actions);
        Reposition();
    }

    private async void ShowAbout()
    {
        var assembly = typeof(App).Assembly;
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? assembly.GetName().Version?.ToString(3) ?? "";
        string copyright = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "PereLV";
        _dialogOpen = true;
        try
        {
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock
            {
                Text = $"{T("Version")} {version}\n{copyright}\nMIT License",
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(new HyperlinkButton
            {
                Content = "Llampec · GitHub", NavigateUri = new Uri("https://github.com/PereLV/Llampec"),
                Padding = new Thickness(0),
            });
            content.Children.Add(new TextBlock { Text = T("Acknowledgements"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            content.Children.Add(Note("Always on Top inspired by Microsoft PowerToys."));
            content.Children.Add(new HyperlinkButton
            {
                Content = "Microsoft PowerToys · MIT", NavigateUri = new Uri("https://github.com/microsoft/PowerToys"),
                Padding = new Thickness(0),
            });
            await new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme,
                Title = T("About Llampec"), Content = content,
                CloseButtonText = T("Close") }.ShowAsync();
        }
        finally { _dialogOpen = false; }
    }
}
