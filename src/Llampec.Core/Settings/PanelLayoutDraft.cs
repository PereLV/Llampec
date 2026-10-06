namespace Llampec.Settings;

/// <summary>
/// An independent panel-layout draft. Editing it never changes saved settings or
/// starts/stops services; ApplyTo copies only layout and module-visibility preferences.
/// </summary>
public sealed class PanelLayoutDraft
{
    private readonly List<string> _catalog;
    private readonly HashSet<string> _known;
    private int _columns;

    public PanelLayoutDraft(AppSettings settings, IEnumerable<string> catalogIds)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(catalogIds);
        _catalog = DistinctIds(catalogIds);
        _known = _catalog.ToHashSet(StringComparer.Ordinal);
        Columns = settings.TileColumns;
        Categories = TileLayout.NormalizeCategories(settings.TileCategories, _catalog);
        Order = TileOrdering.Normalize(ValidIds(settings.TileOrder), _catalog);
        DisabledModules = DistinctIds(settings.DisabledModules);
        // Old hidden buttons retain their services. Only an explicit DisableModule
        // adds to DisabledModules; EnableModule makes either kind visible again.
        HiddenTiles = DistinctIds(settings.HiddenTiles);
    }

    public int Columns
    {
        get => _columns;
        set => _columns = TileLayout.NormalizeColumns(value);
    }

    public List<TileCategory> Categories { get; }
    public List<string> Order { get; }
    public List<string> DisabledModules { get; }
    public List<string> HiddenTiles { get; }

    /// <summary>Move a tile to an index within its destination group after removing it from its old position.</summary>
    public void MoveTile(string id, string? categoryId, int index)
    {
        RequireKnown(id);
        TileCategory? destination = categoryId is null ? null : RequireCategory(categoryId);
        RemoveAssignments(id);
        NormalizeOrder();
        Order.Remove(id);
        destination?.TileIds.Add(id);
        var members = destination is null
            ? Categories.SelectMany(category => category.TileIds).ToHashSet(StringComparer.Ordinal)
            : destination.TileIds.ToHashSet(StringComparer.Ordinal);
        var hidden = HiddenTiles.Concat(DisabledModules).ToHashSet(StringComparer.Ordinal);
        var target = Order.Where(candidate => !hidden.Contains(candidate)
            && (destination is null ? !members.Contains(candidate) : members.Contains(candidate))).ToList();
        int position = Math.Clamp(index, 0, target.Count);
        int insertion = position < target.Count ? Order.IndexOf(target[position])
            : target.Count > 0 ? Order.IndexOf(target[^1]) + 1 : Order.Count;
        Order.Insert(insertion, id);
    }

    public void MoveCategory(string id, int index)
    {
        TileCategory category = RequireCategory(id);
        Categories.Remove(category);
        Categories.Insert(Math.Clamp(index, 0, Categories.Count), category);
    }

    public TileCategory AddCategory(string name)
    {
        string normalized = ValidateName(name, null);
        var category = new TileCategory { Name = normalized };
        Categories.Add(category);
        return category;
    }

    public void RenameCategory(string id, string name)
    {
        TileCategory category = RequireCategory(id);
        category.Name = ValidateName(name, id);
    }

    /// <summary>Keep the category's visual tile order, appending its tiles after existing unassigned tiles.</summary>
    public void RemoveCategory(string id)
    {
        TileCategory category = RequireCategory(id);
        NormalizeOrder();
        var members = category.TileIds.ToHashSet(StringComparer.Ordinal);
        var tiles = Order.Where(members.Contains).ToList();
        Categories.Remove(category);
        Order.RemoveAll(members.Contains);
        Order.AddRange(tiles);
    }

    public void DisableModule(string id)
    {
        RequireKnown(id);
        if (!DisabledModules.Contains(id, StringComparer.Ordinal)) DisabledModules.Add(id);
    }

    /// <summary>Enable either an explicitly disabled module or a legacy hidden button at the end without a category.</summary>
    public void EnableModule(string id)
    {
        RequireKnown(id);
        bool changed = DisabledModules.RemoveAll(candidate => candidate == id) != 0;
        changed |= HiddenTiles.RemoveAll(candidate => candidate == id) != 0;
        if (!changed) return;
        RemoveAssignments(id);
        NormalizeOrder();
        Order.Remove(id);
        Order.Add(id);
    }

    /// <summary>Include empty categories and an unassigned group so the editor always has drop destinations.</summary>
    public IReadOnlyList<TileGroup> BuildGroups()
    {
        var order = TileOrdering.Normalize(ValidIds(Order), _catalog);
        var hidden = ValidIds(HiddenTiles).Concat(ValidIds(DisabledModules)).ToHashSet(StringComparer.Ordinal);
        var visible = order.Where(id => !hidden.Contains(id)).ToList();
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        var groups = new List<TileGroup>();
        foreach (var category in TileLayout.NormalizeCategories(Categories, _catalog))
        {
            var membership = category.TileIds.ToHashSet(StringComparer.Ordinal);
            assigned.UnionWith(membership);
            groups.Add(new(category.Id, category.Name, visible.Where(membership.Contains).ToList()));
        }
        groups.Add(new(null, string.Empty, visible.Where(id => !assigned.Contains(id)).ToList()));
        return groups;
    }

    /// <summary>Return an existing English UiText key for an invalid category name; malformed id lists are normalized on save.</summary>
    public bool TryValidate(out string? error)
    {
        var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var category in Categories)
        {
            if (category is null || !ids.Add(category.Id)) continue;
            if (string.IsNullOrWhiteSpace(category.Name)) { error = "Enter a category name."; return false; }
            if (!names.Add(category.Name.Trim())) { error = "Category names must be distinct."; return false; }
        }
        error = null;
        return true;
    }

    public void ApplyTo(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!TryValidate(out string? error)) throw new InvalidOperationException(error);
        settings.TileColumns = Columns;
        settings.TileCategories = TileLayout.NormalizeCategories(Categories, _catalog);
        settings.TileOrder = TileOrdering.Normalize(ValidIds(Order), _catalog);
        settings.DisabledModules = DistinctIds(DisabledModules);
        settings.HiddenTiles = DistinctIds(HiddenTiles);
    }

    private void NormalizeOrder()
    {
        var normalized = TileOrdering.Normalize(ValidIds(Order), _catalog);
        Order.Clear();
        Order.AddRange(normalized);
    }

    private void RemoveAssignments(string id)
    {
        foreach (var category in Categories) category.TileIds.RemoveAll(candidate => candidate == id);
    }

    private TileCategory RequireCategory(string id)
        => Categories.FirstOrDefault(category => category.Id == id)
            ?? throw new ArgumentException("Unknown category.", nameof(id));

    private void RequireKnown(string id)
    {
        if (!_known.Contains(id)) throw new ArgumentException("Unknown module.", nameof(id));
    }

    private string ValidateName(string name, string? currentId)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Enter a category name.", nameof(name));
        string normalized = name.Trim();
        if (Categories.Any(category => category.Id != currentId
            && string.Equals(category.Name, normalized, StringComparison.CurrentCultureIgnoreCase)))
            throw new ArgumentException("Category names must be distinct.", nameof(name));
        return normalized;
    }

    private static List<string> DistinctIds(IEnumerable<string>? ids)
        => ValidIds(ids).Distinct(StringComparer.Ordinal).ToList();
    private static IEnumerable<string> ValidIds(IEnumerable<string>? ids)
        => (ids ?? []).Where(id => !string.IsNullOrWhiteSpace(id));
}
