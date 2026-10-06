using System.Numerics;
using Llampec.Actions;
using Llampec.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.System;
using FlyoutControl = Microsoft.UI.Xaml.Controls.Flyout;

namespace Llampec.Flyout;

/// <summary>
/// A disposable visual editor over an app-owned draft, drawn like the panel it edits:
/// each group is a block and each button is dragged by its own face. Views are reused
/// while editing, so a drag preview or menu move only repositions them. It reads
/// catalogue metadata, never actions or services, and leaves scrolling to the panel.
/// </summary>
public sealed class PanelLayoutEditor : Grid, IDisposable
{
    private const double DragThreshold = 8;
    private const string Unassigned = "";
    private readonly PanelLayoutDraft _draft;
    private readonly Action _layoutChanged;
    private readonly ResourceDictionary _styles;
    private readonly ComboBox _columns = new() { MinWidth = 64, MinHeight = 40 };
    private readonly TextBlock _error = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Dictionary<string, ModuleDescriptor> _catalog = ModuleCatalog.All.ToDictionary(module => module.Id, StringComparer.Ordinal);
    private readonly Dictionary<string, TileView> _tiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GroupView> _groups = new(StringComparer.Ordinal);
    private readonly List<GroupView> _shownGroups = [];
    private readonly Grid _groupsGrid = new() { RowSpacing = 12 };
    private readonly Canvas _dragLayer = new() { IsHitTestVisible = false };
    private readonly Border _tileSlot;
    private readonly Border _cardSlot;
    private readonly FrameworkElement _addTile;
    private readonly MenuFlyout _tileMenu = new();
    private readonly MenuFlyout _categoryMenu = new();
    private readonly MenuFlyout _addMenu = new();
    private readonly FlyoutControl _addCategory;
    private readonly FlyoutControl _renameCategory;
    private readonly ThemeShadow _liftShadow = new();
    private string? _renaming;
    private int _effectiveColumns;
    private bool _disposed;
    private string? _focusTile;
    private string? _focusCategory;
    private Press? _press;
    private Drag? _drag;
    private ScrollViewer? _viewport;
    private const double EdgeZone = 36;
    private static readonly TimeSpan SettleDuration = TimeSpan.FromMilliseconds(150);
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    // Both timers exist only during a drag: one-shot settling and edge scrolling.
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _settleTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _scrollTimer;
    private bool _settling;

    private sealed record TileView(string Id, Grid Root, ContentControl Body, Button Remove, Border Lift);
    private sealed class GroupView(string? categoryId, Border card, Grid header, TextBlock title, Grid tiles, Border empty, Button? more)
    {
        public string? CategoryId { get; } = categoryId;
        public Border Card { get; } = card;
        public Grid Header { get; } = header;
        public TextBlock Title { get; } = title;
        public Grid Tiles { get; } = tiles;
        public Border Empty { get; } = empty;
        public Button? More { get; } = more;
        /// <summary>Visible buttons, excluding a floating one and the drop slot.</summary>
        public int Count { get; set; }
    }
    private sealed record Press(uint PointerId, bool Mouse, Point Start, Point Grab, TileView? Tile, GroupView? Group);
    private sealed class Drag(Pointer pointer, bool mouse, Point grab, TileView? tile, GroupView? group)
    {
        public Pointer Pointer { get; } = pointer;
        public bool Mouse { get; } = mouse;
        public Point Grab { get; } = grab;
        public TileView? Tile { get; } = tile;
        public GroupView? Group { get; } = group;
        public FrameworkElement Element => Tile?.Root ?? (FrameworkElement)Group!.Card;
        public GroupView? Target { get; set; }
        public int Slot { get; set; }
        public Point RootPoint { get; set; }
    }

    public FrameworkElement Header { get; }
    public bool DragInProgress => _drag is not null;
    public event Action<bool>? DragStateChanged;
    public event Action<double>? DragScrollRequested;

    public PanelLayoutEditor(PanelLayoutDraft draft, int effectiveColumns, Action layoutChanged, ResourceDictionary styles)
    {
        _draft = draft;
        _layoutChanged = layoutChanged;
        _styles = styles;
        _effectiveColumns = Math.Max(1, effectiveColumns);
        _groupsGrid.ChildrenTransitions = Reflow();
        Children.Add(_groupsGrid);
        Children.Add(_dragLayer);
        _tileSlot = new Border { Style = EditorStyle("EditorSlotStyle"), CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Top };
        _cardSlot = new Border { Style = EditorStyle("EditorSlotStyle"), CornerRadius = new CornerRadius(8) };
        _addTile = BuildAddModule();
        _addCategory = NameFlyout(rename: false);
        _renameCategory = NameFlyout(rename: true);
        _tileMenu.Opening += (_, _) => PopulateTileMenu();
        _categoryMenu.Opening += (_, _) => PopulateCategoryMenu();
        _addMenu.Opening += (_, _) => PopulateAddMenu();
        Header = BuildHeader();
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Polite);
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnPointerCanceled;
        PointerCaptureLost += OnPointerCaptureLost;
        Arrange();
    }

    public void SetColumns(int effectiveColumns)
    {
        if (_disposed) return;
        int value = Math.Max(1, effectiveColumns);
        if (_effectiveColumns == value) return;
        CancelDrag();
        _effectiveColumns = value;
        Arrange();
    }

    /// <summary>Escape cancels a drag before the panel handles its normal hide action.</summary>
    public bool CancelDrag()
    {
        _press = null;
        if (_drag is null) return false;
        // Once released, the drop is decided; finish its short settling instead.
        if (_settling) FinishSettle();
        else EndDrag(commit: false);
        return true;
    }

    private FrameworkElement BuildHeader()
    {
        var header = new StackPanel { Spacing = 8 };
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var columns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        columns.Children.Add(new TextBlock { Text = T("Columns"), VerticalAlignment = VerticalAlignment.Center });
        for (int value = TileLayout.MinimumColumns; value <= TileLayout.MaximumColumns; value++)
            _columns.Items.Add(value);
        _columns.SelectedIndex = _draft.Columns - TileLayout.MinimumColumns;
        AutomationProperties.SetName(_columns, T("Columns"));
        _columns.SelectionChanged += (_, _) =>
        {
            if (_disposed || _columns.SelectedItem is not int value || value == _draft.Columns) return;
            _draft.Columns = value;
            _layoutChanged();
        };
        columns.Children.Add(_columns);
        row.Children.Add(columns);
        var add = IconButton("\uE710", "Add category");
        Grid.SetColumn(add, 1);
        add.Flyout = _addCategory;
        row.Children.Add(add);
        // A single-column panel has only about 140 DIP of content width.
        // Adapt the fixed header rather than squeezing its touch targets.
        void ArrangeHeader()
        {
            bool narrow = row.ActualWidth > 0 && row.ActualWidth < 200;
            if (narrow && row.RowDefinitions.Count == 0)
            {
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                row.RowSpacing = 6;
                Grid.SetColumnSpan(columns, 2);
                Grid.SetColumn(add, 0); Grid.SetRow(add, 1); Grid.SetColumnSpan(add, 2);
                add.HorizontalAlignment = HorizontalAlignment.Stretch;
                add.Content = new TextBlock { Text = T("Add category"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
            }
            else if (!narrow && row.RowDefinitions.Count != 0)
            {
                row.RowDefinitions.Clear();
                Grid.SetColumnSpan(columns, 1);
                Grid.SetColumn(add, 1); Grid.SetRow(add, 0); Grid.SetColumnSpan(add, 1);
                add.Content = ActionIcons.Glyph("\uE710", 14);
            }
        }
        row.SizeChanged += (_, _) => ArrangeHeader();
        header.Children.Add(row);
        header.Children.Add(_error);
        return header;
    }

    /// <summary>
    /// Place the reused views from the draft, plus the active drag preview: the
    /// dragged view floats above and an empty slot marks where it will land.
    /// </summary>
    private void Arrange()
    {
        if (_disposed) return;
        var groups = _draft.BuildGroups();
        bool hasCategories = groups.Any(group => !group.IsUnassigned);
        string? floatingTile = _drag?.Tile?.Id;
        string? floatingCategory = _drag?.Group?.CategoryId;
        var cards = new List<FrameworkElement>();
        var shownGroups = new HashSet<string>(StringComparer.Ordinal);
        var shownTiles = new HashSet<string>(StringComparer.Ordinal);
        _shownGroups.Clear();
        var fills = new List<(GroupView View, TileGroup Group)>();
        int categoryIndex = 0;
        bool cardSlotPlaced = false;
        foreach (var group in groups)
        {
            shownGroups.Add(group.CategoryId ?? Unassigned);
            shownTiles.UnionWith(group.TileIds);
            if (floatingCategory is not null && group.CategoryId == floatingCategory) continue;
            if (floatingCategory is not null && !cardSlotPlaced && (group.IsUnassigned || categoryIndex == _drag!.Slot))
            {
                cards.Add(_cardSlot);
                cardSlotPlaced = true;
            }
            if (!group.IsUnassigned) categoryIndex++;
            var view = GroupFor(group, hasCategories);
            fills.Add((view, group));
            cards.Add(view.Card);
            _shownGroups.Add(view);
        }
        // Blocks first: a block returning from the drag layer must be in the tree
        // before its buttons are reconciled.
        Place(_groupsGrid, cards, 1);
        foreach (var (view, group) in fills) FillTiles(view, group, floatingTile);
        // Release views for deleted categories and disabled modules.
        foreach (string key in _groups.Keys.Where(key => !shownGroups.Contains(key)).ToArray()) _groups.Remove(key);
        foreach (string id in _tiles.Keys.Where(id => !shownTiles.Contains(id)).ToArray()) _tiles.Remove(id);
    }

    private void FillTiles(GroupView view, TileGroup group, string? floatingTile)
    {
        var items = new List<FrameworkElement>();
        foreach (string id in group.TileIds)
            if (id != floatingTile && _catalog.TryGetValue(id, out var module)) items.Add(TileFor(module).Root);
        view.Count = items.Count;
        if (_drag is { Tile: not null } drag && drag.Target == view)
            items.Insert(Math.Clamp(drag.Slot, 0, items.Count), _tileSlot);
        if (group.IsUnassigned) items.Add(_addTile);
        while (view.Tiles.ColumnDefinitions.Count < _effectiveColumns) view.Tiles.ColumnDefinitions.Add(new ColumnDefinition());
        while (view.Tiles.ColumnDefinitions.Count > _effectiveColumns) view.Tiles.ColumnDefinitions.RemoveAt(view.Tiles.ColumnDefinitions.Count - 1);
        if (items.Count == 0)
        {
            // An empty category is still a full-width drop destination.
            Grid.SetColumnSpan(view.Empty, _effectiveColumns);
            items.Add(view.Empty);
        }
        Place(view.Tiles, items, _effectiveColumns);
    }

    private static void Place(Grid grid, IReadOnlyList<FrameworkElement> items, int columns)
    {
        var wanted = items.ToHashSet<UIElement>();
        for (int index = grid.Children.Count - 1; index >= 0; index--)
            if (!wanted.Contains(grid.Children[index])) grid.Children.RemoveAt(index);
        int rows = Math.Max(1, (items.Count + columns - 1) / columns);
        while (grid.RowDefinitions.Count < rows) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        while (grid.RowDefinitions.Count > rows) grid.RowDefinitions.RemoveAt(grid.RowDefinitions.Count - 1);
        for (int index = 0; index < items.Count; index++)
        {
            var item = items[index];
            // Parent can read null while a panel is detached; membership is authoritative.
            bool present = grid.Children.Contains(item);
            if (!present && item.Parent is Panel parent) parent.Children.Remove(item);
            // Changing only the cell lets RepositionThemeTransition animate the reflow.
            Grid.SetRow(item, index / columns);
            Grid.SetColumn(item, index % columns);
            if (!present) grid.Children.Add(item);
        }
    }

    private GroupView GroupFor(TileGroup group, bool hasCategories)
    {
        string key = group.CategoryId ?? Unassigned;
        if (!_groups.TryGetValue(key, out var view)) _groups[key] = view = CreateGroup(group.CategoryId);
        string name = group.IsUnassigned ? T("No category") : group.Name;
        view.Title.Text = name;
        AutomationProperties.SetName(view.Card, name);
        if (view.More is not null) AutomationProperties.SetName(view.More, UiText.Format("{0} options", name));
        // Like the panel itself, a layout without categories has no heading.
        view.Header.Visibility = group.IsUnassigned && !hasCategories ? Visibility.Collapsed : Visibility.Visible;
        return view;
    }

    private GroupView CreateGroup(string? categoryId)
    {
        var header = new Grid { ColumnSpacing = 6, MinHeight = 24, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock
        {
            FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false,
        };
        header.Children.Add(title);
        Button? more = null;
        if (categoryId is not null)
        {
            more = IconButton("\uE712", "Category options");
            more.Tag = categoryId;
            more.Flyout = _categoryMenu;
            Grid.SetColumn(more, 1);
            header.Children.Add(more);
        }
        var tiles = new Grid { ColumnSpacing = 12, RowSpacing = 16, ChildrenTransitions = Reflow() };
        var empty = new Border
        {
            Style = EditorStyle("EditorSlotStyle"), CornerRadius = new CornerRadius(6), MinHeight = 48,
            Child = new TextBlock
            {
                Text = T("Drop buttons here"), FontSize = 12, TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8), Style = EditorStyle("TileStatusStyle"),
            },
        };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(header);
        content.Children.Add(tiles);
        var card = new Border { Style = EditorStyle("EditorCardStyle"), Child = content };
        var view = new GroupView(categoryId, card, header, title, tiles, empty, more);
        if (categoryId is not null)
        {
            // Only the heading moves a category; the rest of the block still pans by touch.
            header.ManipulationMode = ManipulationModes.None;
            ToolTipService.SetToolTip(header, T("Drag the heading to move this category."));
            header.PointerPressed += (_, args) => BeginPress(args, card, null, view);
        }
        return view;
    }

    private TileView TileFor(ModuleDescriptor module)
    {
        if (_tiles.TryGetValue(module.Id, out var existing)) return existing;
        string title = T(module.Title);
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(Face(module));
        stack.Children.Add(new TextBlock
        {
            Text = title, FontSize = 12, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        // Focusable for keyboard menus, but never focused by a press: a dragged
        // view leaves the tree, and losing focus there must not scroll the panel.
        var body = new ContentControl
        {
            Content = stack, Tag = module.Id, IsTabStop = true, UseSystemFocusVisuals = true,
            AllowFocusOnInteraction = false, ContextFlyout = _tileMenu, ManipulationMode = ManipulationModes.None,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(body, title);
        AutomationProperties.SetHelpText(body, T("Drag to move, or press Enter for options."));
        body.KeyDown += OnTileKeyDown;
        var remove = new Button { Style = EditorStyle("EditorRemoveStyle"), Content = ActionIcons.Glyph("\uE711", 10), Tag = module.Id };
        AutomationProperties.SetName(remove, UiText.Format("Disable {0}", title));
        ToolTipService.SetToolTip(remove, UiText.Format("Disable {0}", title));
        remove.Click += (_, _) => Disable(module.Id, keepFocusNearby: false);
        var lift = new Border { Style = EditorStyle("EditorTileLiftStyle"), Visibility = Visibility.Collapsed };
        var root = new Grid
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Top, ManipulationMode = ManipulationModes.None,
        };
        root.Children.Add(lift);
        root.Children.Add(body);
        root.Children.Add(remove);
        var view = new TileView(module.Id, root, body, remove, lift);
        root.PointerPressed += (_, args) => BeginPress(args, root, view, null);
        _tiles[module.Id] = view;
        return view;
    }

    /// <summary>The same face as the panel's tile, including the split options chevron.</summary>
    private FrameworkElement Face(ModuleDescriptor module)
    {
        var icon = new Border { Style = EditorStyle("EditorFaceStyle"), Child = ActionIcons.Tile(module.Id, module.Glyph, module.GlyphBadge) };
        if (!module.HasSubpage) { icon.CornerRadius = new CornerRadius(6); return icon; }
        icon.CornerRadius = new CornerRadius(6, 0, 0, 6);
        var chevron = new Border
        {
            Style = EditorStyle("EditorFaceStyle"), CornerRadius = new CornerRadius(0, 6, 6, 0),
            BorderThickness = new Thickness(0, 1, 1, 1), Child = ActionIcons.Glyph("\uE76C", 16),
        };
        var face = new Grid();
        face.ColumnDefinitions.Add(new ColumnDefinition());
        face.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(chevron, 1);
        face.Children.Add(icon);
        face.Children.Add(chevron);
        return face;
    }

    private FrameworkElement BuildAddModule()
    {
        var stack = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Top };
        var button = new Button
        {
            Content = ActionIcons.Glyph("\uE710"), Height = 48, Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch, CornerRadius = new CornerRadius(6), Flyout = _addMenu,
        };
        AutomationProperties.SetName(button, T("Add module"));
        stack.Children.Add(button);
        stack.Children.Add(new TextBlock { Text = T("Add module"), FontSize = 12, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, IsHitTestVisible = false });
        return stack;
    }

    // Menus are shared and filled when opened, so they always reflect the current draft.
    private void PopulateTileMenu()
    {
        _tileMenu.Items.Clear();
        if (_tileMenu.Target?.Tag is not string id) return;
        var groups = _draft.BuildGroups();
        if (groups.FirstOrDefault(group => group.TileIds.Contains(id)) is not { } group) return;
        void WithFocus(Action action) { _focusTile = id; _focusCategory = null; action(); }
        int index = group.TileIds.ToList().IndexOf(id);
        _tileMenu.Items.Add(Command("Move before", () => WithFocus(() => _draft.MoveTile(id, group.CategoryId, index - 1)), index > 0));
        _tileMenu.Items.Add(Command("Move after", () => WithFocus(() => _draft.MoveTile(id, group.CategoryId, index + 1)), index < group.TileIds.Count - 1));
        var categories = new MenuFlyoutSubItem { Text = T("Move to category") };
        foreach (var target in groups)
        {
            string? categoryId = target.CategoryId;
            categories.Items.Add(CommandText(target.IsUnassigned ? T("No category") : target.Name,
                () => WithFocus(() => _draft.MoveTile(id, categoryId, target.TileIds.Count)), categoryId != group.CategoryId));
        }
        _tileMenu.Items.Add(categories);
        _tileMenu.Items.Add(new MenuFlyoutSeparator());
        var disable = new MenuFlyoutItem { Text = T("Disable") };
        disable.Click += (_, _) => Disable(id, keepFocusNearby: true);
        _tileMenu.Items.Add(disable);
    }

    private void PopulateCategoryMenu()
    {
        _categoryMenu.Items.Clear();
        if (_categoryMenu.Target is not { Tag: string id } anchor) return;
        void WithFocus(Action action) { _focusCategory = id; _focusTile = null; action(); }
        int index = _draft.Categories.FindIndex(category => category.Id == id);
        _categoryMenu.Items.Add(Command("Move up", () => WithFocus(() => _draft.MoveCategory(id, index - 1)), index > 0));
        _categoryMenu.Items.Add(Command("Move down", () => WithFocus(() => _draft.MoveCategory(id, index + 1)), index >= 0 && index < _draft.Categories.Count - 1));
        var rename = new MenuFlyoutItem { Text = T("Rename") };
        rename.Click += (_, _) =>
        {
            _categoryMenu.Hide();
            _renaming = id;
            _renameCategory.ShowAt(anchor);
        };
        _categoryMenu.Items.Add(rename);
        _categoryMenu.Items.Add(Command("Delete category", () => WithFocus(() => _draft.RemoveCategory(id))));
    }

    private void PopulateAddMenu()
    {
        _addMenu.Items.Clear();
        var inactive = _draft.DisabledModules.Concat(_draft.HiddenTiles).ToHashSet(StringComparer.Ordinal);
        foreach (var module in ModuleCatalog.All.Where(module => inactive.Contains(module.Id)))
        {
            var item = new MenuFlyoutItem { Text = T(module.Title), Icon = new FontIcon { Glyph = module.Glyph, FontFamily = new FontFamily("Segoe Fluent Icons") } };
            string id = module.Id;
            item.Click += (_, _) => { _focusTile = id; _focusCategory = null; Change(() => _draft.EnableModule(id)); };
            _addMenu.Items.Add(item);
        }
        if (_addMenu.Items.Count == 0) _addMenu.Items.Add(new MenuFlyoutItem { Text = T("All modules are enabled"), IsEnabled = false });
    }

    private FlyoutControl NameFlyout(bool rename)
    {
        var content = new StackPanel { Spacing = 8, Width = 220 };
        var input = new TextBox { Header = T("Category name"), MaxLength = 80 };
        var error = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        var button = new Button { Content = T(rename ? "Rename" : "Add category"), HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 40 };
        var flyout = new FlyoutControl { Content = content };
        void Apply()
        {
            if (_disposed || DragInProgress) return;
            try
            {
                if (!rename) _focusCategory = _draft.AddCategory(input.Text).Id;
                else if (_renaming is { } id) { _draft.RenameCategory(id, input.Text); _focusCategory = id; }
                _focusTile = null;
                flyout.Hide();
                Refresh();
            }
            catch (ArgumentException exception)
            {
                error.Text = ErrorText(exception);
                error.Visibility = Visibility.Visible;
            }
        }
        button.Click += (_, _) => Apply();
        input.KeyDown += (_, args) => { if (args.Key == VirtualKey.Enter) { args.Handled = true; Apply(); } };
        flyout.Opening += (_, _) =>
        {
            error.Visibility = Visibility.Collapsed;
            input.Text = rename ? _draft.Categories.FirstOrDefault(category => category.Id == _renaming)?.Name ?? "" : "";
        };
        flyout.Opened += (_, _) => { input.Focus(FocusState.Programmatic); input.SelectAll(); };
        content.Children.Add(input); content.Children.Add(error); content.Children.Add(button);
        return flyout;
    }

    private void OnTileKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (sender is not ContentControl { Tag: string id } body || DragInProgress) return;
        if (args.Key is VirtualKey.Enter or VirtualKey.Space) { args.Handled = true; _tileMenu.ShowAt(body); }
        else if (args.Key == VirtualKey.Delete) { args.Handled = true; Disable(id, keepFocusNearby: true); }
    }

    private void Disable(string id, bool keepFocusNearby)
    {
        _focusTile = _focusCategory = null;
        // A keyboard removal moves focus to a neighbour rather than to the window.
        if (keepFocusNearby && _draft.BuildGroups().FirstOrDefault(group => group.TileIds.Contains(id)) is { } group)
        {
            int index = group.TileIds.ToList().IndexOf(id);
            _focusTile = index + 1 < group.TileIds.Count ? group.TileIds[index + 1] : index > 0 ? group.TileIds[index - 1] : null;
        }
        Change(() => _draft.DisableModule(id));
    }

    private void BeginPress(PointerRoutedEventArgs args, FrameworkElement element, TileView? tile, GroupView? group)
    {
        if (_disposed || _drag is not null) return;
        var point = args.GetCurrentPoint(this);
        bool mouse = args.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse;
        if (mouse ? !point.Properties.IsLeftButtonPressed : !point.IsInContact) return;
        // Pressed views opt out of panning; stop any inertia still running there.
        if (!mouse) element.CancelDirectManipulations();
        // Capture waits for the threshold, so a stationary touch can still open the context menu.
        _press = new(args.Pointer.PointerId, mouse, point.Position, args.GetCurrentPoint(element).Position, tile, group);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_drag is { } drag)
        {
            if (_settling || args.Pointer.PointerId != drag.Pointer.PointerId) return;
            args.Handled = true;
            if (drag.Mouse && !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { CancelDrag(); return; }
            drag.RootPoint = args.GetCurrentPoint(XamlRoot.Content).Position;
            UpdateDrag();
            UpdateEdgeScroll();
            return;
        }
        if (_press is not { } press || args.Pointer.PointerId != press.PointerId) return;
        var point = args.GetCurrentPoint(this);
        if (press.Mouse ? !point.Properties.IsLeftButtonPressed : !point.IsInContact) { _press = null; return; }
        double x = point.Position.X - press.Start.X, y = point.Position.Y - press.Start.Y;
        if (x * x + y * y < DragThreshold * DragThreshold) return;
        _press = null;
        if (!CapturePointer(args.Pointer)) return;
        args.Handled = true;
        StartDrag(press, args.Pointer, args.GetCurrentPoint(XamlRoot.Content).Position);
    }

    private void StartDrag(Press press, Pointer pointer, Point rootPoint)
    {
        var drag = new Drag(pointer, press.Mouse, press.Grab, press.Tile, press.Group) { RootPoint = rootPoint };
        var element = drag.Element;
        if (drag.Tile is { } tile)
        {
            var groups = _draft.BuildGroups();
            var source = groups.FirstOrDefault(group => group.TileIds.Contains(tile.Id));
            drag.Target = _shownGroups.FirstOrDefault(view => view.CategoryId == source?.CategoryId);
            drag.Slot = source?.TileIds.ToList().IndexOf(tile.Id) ?? 0;
            _tileSlot.Height = element.ActualHeight;
        }
        else
        {
            drag.Slot = _draft.Categories.FindIndex(category => category.Id == drag.Group!.CategoryId);
            _cardSlot.Height = element.ActualHeight;
        }
        _viewport = FindViewport();
        Lift(drag, lifted: true);
        _drag = drag;
        _columns.IsEnabled = false;
        // Arrange detaches the dragged view and puts the slot in its place.
        Arrange();
        _dragLayer.Children.Add(element);
        if (_viewport is not null) _viewport.ViewChanged += OnViewportChanged;
        UpdateDrag();
        DragStateChanged?.Invoke(true);
    }

    private void UpdateDrag()
    {
        if (_drag is not { } drag || XamlRoot?.Content is not { } root) return;
        Point point = root.TransformToVisual(this).TransformPoint(drag.RootPoint);
        Canvas.SetLeft(drag.Element, point.X - drag.Grab.X);
        Canvas.SetTop(drag.Element, point.Y - drag.Grab.Y);
        if (drag.Tile is not null)
        {
            var (target, slot) = TileTarget(point);
            if (target == drag.Target && slot == drag.Slot) return;
            drag.Target = target;
            drag.Slot = slot;
        }
        else
        {
            int slot = CategoryTarget(point.Y);
            if (slot == drag.Slot) return;
            drag.Slot = slot;
        }
        Arrange();
    }

    // Targets use layout slots rather than rendered positions, so reflow animations
    // in flight never feed back into the choice of destination.
    private (GroupView? Target, int Slot) TileTarget(Point point)
    {
        GroupView? best = null;
        double distance = double.MaxValue;
        foreach (var group in _shownGroups)
        {
            Rect bounds = LayoutInformation.GetLayoutSlot(group.Card);
            double away = point.Y < bounds.Top ? bounds.Top - point.Y : point.Y > bounds.Bottom ? point.Y - bounds.Bottom : 0;
            if (away < distance) { distance = away; best = group; }
        }
        if (best is null) return (null, 0);
        Rect card = LayoutInformation.GetLayoutSlot(best.Card);
        Point origin = best.Tiles.TransformToVisual(best.Card).TransformPoint(default);
        int column = CellIndex(point.X - card.X - origin.X, best.Tiles.ColumnDefinitions.Select(column => column.ActualWidth), best.Tiles.ColumnSpacing);
        int row = CellIndex(point.Y - card.Y - origin.Y, best.Tiles.RowDefinitions.Select(row => row.ActualHeight), best.Tiles.RowSpacing);
        return (best, Math.Clamp(row * _effectiveColumns + column, 0, best.Count));
    }

    private static int CellIndex(double position, IEnumerable<double> sizes, double spacing)
    {
        int index = 0;
        double end = 0;
        foreach (double size in sizes)
        {
            end += size;
            if (position < end + spacing / 2) return index;
            end += spacing;
            index++;
        }
        return Math.Max(0, index - 1);
    }

    /// <summary>
    /// Count the other categories whose centre is above the pointer, which holds the
    /// heading. Reflow only moves them away from it, so the choice cannot oscillate.
    /// </summary>
    private int CategoryTarget(double pointer)
    {
        int slot = 0;
        foreach (var group in _shownGroups)
        {
            if (group.CategoryId is null) continue;
            Rect bounds = LayoutInformation.GetLayoutSlot(group.Card);
            if (bounds.Y + bounds.Height / 2 < pointer) slot++;
        }
        return slot;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_press?.PointerId == args.Pointer.PointerId) _press = null;
        if (_settling || _drag is not { } drag || args.Pointer.PointerId != drag.Pointer.PointerId) return;
        args.Handled = true;
        Settle(drag);
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (_press?.PointerId == args.Pointer.PointerId) _press = null;
        if (!_settling && _drag?.Pointer.PointerId == args.Pointer.PointerId) { args.Handled = true; CancelDrag(); }
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        // Releasing the button also ends capture; a settling drop is already decided.
        if (!_settling && _drag?.Pointer.PointerId == args.Pointer.PointerId) CancelDrag();
    }

    /// <summary>Glide the floating view into its slot, then commit. The slot is where the committed layout puts it.</summary>
    private void Settle(Drag drag)
    {
        StopEdgeScroll();
        var element = drag.Element;
        FrameworkElement slot = drag.Tile is not null ? _tileSlot : _cardSlot;
        if (!_uiSettings.AnimationsEnabled || slot.XamlRoot is null) { EndDrag(commit: true); return; }
        Point to = slot.TransformToVisual(_dragLayer).TransformPoint(default);
        element.TranslationTransition = new Vector3Transition { Duration = SettleDuration };
        element.ScaleTransition = new Vector3Transition { Duration = SettleDuration };
        element.Translation = new Vector3((float)(to.X - Canvas.GetLeft(element)), (float)(to.Y - Canvas.GetTop(element)), 0);
        element.Scale = Vector3.One;
        _settling = true;
        if (_settleTimer is null)
        {
            _settleTimer = DispatcherQueue.CreateTimer();
            _settleTimer.IsRepeating = false;
            _settleTimer.Tick += (_, _) => FinishSettle();
        }
        _settleTimer.Interval = SettleDuration;
        _settleTimer.Start();
    }

    private void FinishSettle()
    {
        _settleTimer?.Stop();
        if (!_settling) return;
        _settling = false;
        EndDrag(commit: true);
    }

    private void OnViewportChanged(object? sender, ScrollViewerViewChangedEventArgs args) => UpdateDrag();

    private void EndDrag(bool commit)
    {
        if (_drag is not { } drag) return;
        // Clear state before release: PointerCaptureLost can be synchronous.
        _drag = null;
        _settling = false;
        _settleTimer?.Stop();
        StopEdgeScroll();
        string? error = null;
        if (commit)
        {
            try
            {
                if (drag.Tile is { } tile && drag.Target is { } target) _draft.MoveTile(tile.Id, target.CategoryId, drag.Slot);
                else if (drag.Group?.CategoryId is { } category) _draft.MoveCategory(category, drag.Slot);
            }
            catch (ArgumentException exception) { error = ErrorText(exception); }
        }
        _dragLayer.Children.Remove(drag.Element);
        Lift(drag, lifted: false);
        if (_viewport is not null) _viewport.ViewChanged -= OnViewportChanged;
        _viewport = null;
        _columns.IsEnabled = true;
        ReleasePointerCapture(drag.Pointer);
        if (_disposed) return;
        Arrange();
        if (error is not null) { _error.Text = error; _error.Visibility = Visibility.Visible; }
        else _error.Visibility = Visibility.Collapsed;
        DragStateChanged?.Invoke(false);
        _layoutChanged();
    }

    private void Lift(Drag drag, bool lifted)
    {
        var element = drag.Element;
        // Without this, resetting after settling would animate back from the slot.
        element.TranslationTransition = null;
        element.ScaleTransition = null;
        element.Width = lifted ? element.ActualWidth : double.NaN;
        element.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
        element.Scale = lifted ? new Vector3(1.04f, 1.04f, 1) : Vector3.One;
        element.Translation = lifted ? new Vector3(0, 0, 32) : Vector3.Zero;
        element.Shadow = lifted ? _liftShadow : null;
        if (drag.Tile is { } tile)
        {
            tile.Lift.Visibility = lifted ? Visibility.Visible : Visibility.Collapsed;
            tile.Remove.Visibility = lifted ? Visibility.Collapsed : Visibility.Visible;
        }
        else drag.Group!.Card.Style = EditorStyle(lifted ? "EditorLiftedCardStyle" : "EditorCardStyle");
    }

    private ScrollViewer? FindViewport()
    {
        DependencyObject? parent = this;
        while (parent is not null && parent is not ScrollViewer) parent = VisualTreeHelper.GetParent(parent);
        return parent as ScrollViewer;
    }

    /// <summary>
    /// Scroll while the pointer rests in an edge band, faster the deeper it is, as in
    /// Windows lists. The timer runs only then, and stops at either end of the view.
    /// </summary>
    private void UpdateEdgeScroll()
    {
        if (EdgeScrollStep() == 0) { StopEdgeScroll(); return; }
        if (_scrollTimer is null)
        {
            _scrollTimer = DispatcherQueue.CreateTimer();
            _scrollTimer.Interval = TimeSpan.FromMilliseconds(16);
            _scrollTimer.IsRepeating = true;
            _scrollTimer.Tick += (_, _) =>
            {
                double step = EdgeScrollStep();
                if (step == 0) StopEdgeScroll();
                // The viewport's ViewChanged then moves the preview with the content.
                else DragScrollRequested?.Invoke(step);
            };
        }
        if (!_scrollTimer.IsRunning) _scrollTimer.Start();
    }

    private void StopEdgeScroll() => _scrollTimer?.Stop();

    private double EdgeScrollStep()
    {
        if (DragScrollRequested is null || _settling || _drag is not { } drag || _viewport is not { } viewport
            || XamlRoot?.Content is not { } root) return 0;
        double y = root.TransformToVisual(viewport).TransformPoint(drag.RootPoint).Y;
        static double Speed(double depth) => 3 + 15 * Math.Clamp(depth / EdgeZone, 0, 1);
        if (y < EdgeZone && viewport.VerticalOffset > 0) return -Speed(EdgeZone - y);
        double bottom = viewport.ActualHeight - EdgeZone;
        if (y > bottom && viewport.VerticalOffset < viewport.ScrollableHeight) return Speed(y - bottom);
        return 0;
    }

    private void Change(Action action)
    {
        if (_disposed || DragInProgress) return;
        try { action(); Refresh(); }
        catch (ArgumentException exception) { _error.Text = ErrorText(exception); _error.Visibility = Visibility.Visible; }
    }

    private void Refresh()
    {
        if (_disposed) return;
        _error.Visibility = Visibility.Collapsed;
        Arrange();
        RestoreFocus();
        _layoutChanged();
    }

    private void RestoreFocus()
    {
        Control? focus = _focusTile is { } tile && _tiles.TryGetValue(tile, out var tileView) ? tileView.Body
            : _focusCategory is { } category && _groups.TryGetValue(category, out var group) ? group.More : null;
        _focusTile = _focusCategory = null;
        if (focus is null) return;
        // A moved view rejoins the tree on the next layout pass.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || focus.XamlRoot is null) return;
            focus.Focus(FocusState.Programmatic);
            focus.StartBringIntoView();
        });
    }

    private MenuFlyoutItem Command(string key, Action action, bool enabled = true) => CommandText(T(key), action, enabled);
    private MenuFlyoutItem CommandText(string text, Action action, bool enabled = true)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
        item.Click += (_, _) => Change(action);
        return item;
    }

    private static Button IconButton(string glyph, string key)
    {
        var button = new Button { Content = ActionIcons.Glyph(glyph, 14), MinWidth = 40, MinHeight = 40, Padding = new Thickness(4),
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"] };
        AutomationProperties.SetName(button, T(key));
        ToolTipService.SetToolTip(button, T(key));
        return button;
    }

    private static TransitionCollection Reflow() => [new RepositionThemeTransition { IsStaggeringEnabled = false }];
    private Style EditorStyle(string key) => (Style)_styles[key];
    private static string T(string key) => UiText.Get(key);
    private static string ErrorText(ArgumentException exception)
    {
        // ArgumentException appends its parameter name to Message; translate the
        // domain error rather than exposing a partly translated technical suffix.
        foreach (string key in new[] { "Enter a category name.", "Category names must be distinct." })
            if (exception.Message.StartsWith(key, StringComparison.Ordinal)) return T(key);
        return exception.Message;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _press = null;
        // A released drop is kept in the draft; an unreleased one is cancelled.
        EndDrag(commit: _settling);
        _settleTimer?.Stop();
        _scrollTimer?.Stop();
        foreach (var flyout in new FlyoutBase[] { _tileMenu, _categoryMenu, _addMenu, _addCategory, _renameCategory }) flyout.Hide();
        PointerMoved -= OnPointerMoved;
        PointerReleased -= OnPointerReleased;
        PointerCanceled -= OnPointerCanceled;
        PointerCaptureLost -= OnPointerCaptureLost;
        _groupsGrid.Children.Clear();
        _dragLayer.Children.Clear();
        _tiles.Clear();
        _groups.Clear();
        _shownGroups.Clear();
        DragStateChanged = null;
        DragScrollRequested = null;
    }
}
