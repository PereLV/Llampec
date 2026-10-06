using Llampec.Settings;
using Microsoft.UI.Xaml;

namespace Llampec.Flyout;

public sealed partial class FlyoutWindow
{
    private PanelLayoutDraft? _layoutDraft;
    private PanelLayoutEditor? _panelEditor;
    private bool _savingEditor;

    private void OnEdit(object sender, RoutedEventArgs args) => ShowPanelEditor();

    private void ShowPanelEditor()
    {
        _layoutDraft ??= new PanelLayoutDraft(App.Current.Settings, _model.CatalogIds);
        BeginUtilityPage(UtilityPage.Editor, "Edit panel");
        BackButton.Visibility = Visibility.Collapsed;
        var (work, scale) = PlacementArea();
        _panelEditor = new PanelLayoutEditor(_layoutDraft, EffectiveTileColumns(work, scale), OnEditorLayoutChanged, Root.Resources);
        _panelEditor.DragScrollRequested += ScrollEditorDuringDrag;
        _panelEditor.DragStateChanged += OnEditorDragStateChanged;
        EditorHeaderHost.Content = _panelEditor.Header;
        EditorHeaderHost.Visibility = EditorActions.Visibility = Visibility.Visible;
        NormalFooterActions.Visibility = FooterBrand.Visibility = Visibility.Collapsed;
        Subpage.Children.Add(_panelEditor);
        App.Current.Scheduler?.SetStatusVisible(false);
        PanelScroll.ChangeView(null, 0, null, true);
        Reposition(allowHidden: true);
    }

    private void OnEditorLayoutChanged()
    {
        ErrorBar.IsOpen = false;
        Reposition();
    }

    private void ScrollEditorDuringDrag(double amount)
    {
        if (_panelEditor?.DragInProgress != true) return;
        double offset = Math.Clamp(PanelScroll.VerticalOffset + amount, 0, PanelScroll.ScrollableHeight);
        PanelScroll.ChangeView(null, offset, null, true);
    }

    private void OnEditorDragStateChanged(bool dragging)
    {
        _dialogOpen = _savingEditor || dragging;
        // Placement is frozen while dragging; catch up with the dropped layout.
        if (!dragging) Reposition();
    }

    private void ReleasePanelEditor()
    {
        if (_panelEditor is not null)
        {
            _panelEditor.DragScrollRequested -= ScrollEditorDuringDrag;
            _panelEditor.DragStateChanged -= OnEditorDragStateChanged;
            _panelEditor.Dispose();
            _panelEditor = null;
        }
        EditorHeaderHost.Content = null;
        EditorHeaderHost.Visibility = EditorActions.Visibility = Visibility.Collapsed;
        NormalFooterActions.Visibility = FooterBrand.Visibility = Visibility.Visible;
        if (!_savingEditor) _dialogOpen = false;
    }

    private async void OnSaveEditor(object sender, RoutedEventArgs args)
    {
        if (_layoutDraft is null || _savingEditor || _panelEditor?.DragInProgress == true) return;
        _savingEditor = _dialogOpen = true;
        SaveEditorButton.IsEnabled = CancelEditorButton.IsEnabled = false;
        PanelScroll.IsEnabled = false;
        EditorHeaderHost.IsEnabled = false;
        try
        {
            bool applied = await App.Current.TryApplyPanelLayoutAsync(_layoutDraft);
            if (_disposed) return;
            if (applied)
            {
                _layoutDraft = null;
                ShowMainPage();
                RebuildTiles();
                Reposition();
            }
            else
            {
                ErrorBar.Message = T(App.Current.ModuleChangeError ?? "Could not apply the module changes. Try again.");
                ErrorBar.IsOpen = true;
                Reposition();
            }
        }
        finally
        {
            _savingEditor = _dialogOpen = false;
            if (!_disposed)
            {
                SaveEditorButton.IsEnabled = CancelEditorButton.IsEnabled = true;
                EditorHeaderHost.IsEnabled = true;
                PanelScroll.IsEnabled = true;
            }
        }
    }

    private void OnCancelEditor(object sender, RoutedEventArgs args)
    {
        if (_savingEditor) return;
        _layoutDraft = null;
        ShowMainPage();
        if (Tiles.Children.Count == 0) BuildTiles();
        App.Current.Scheduler?.SetStatusVisible(true);
        Reposition();
    }
}
