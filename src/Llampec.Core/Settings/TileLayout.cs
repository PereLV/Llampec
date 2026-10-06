namespace Llampec.Settings;

/// <summary>Panel layout rules, independent of UI controls and file persistence.</summary>
public static class TileLayout
{
    public const int MinimumColumns = 1;
    public const int MaximumColumns = 6;
    public const int DefaultColumns = 3;

    public static int NormalizeColumns(int columns) => Math.Clamp(columns, MinimumColumns, MaximumColumns);

    /// <summary>
    /// Produce an independent editable snapshot. Keep the first occurrence of a category id
    /// and the first category assignment of each known action. Hidden actions are still known
    /// actions and retain their assignment; callers must pass the complete action catalog.
    /// Empty, named categories remain available for editing but produce no empty panel heading.
    /// </summary>
    public static List<TileCategory> NormalizeCategories(IEnumerable<TileCategory?>? categories,
        IEnumerable<string>? knownTileIds)
    {
        var known = ValidIds(knownTileIds).ToHashSet(StringComparer.Ordinal);
        var categoryIds = new HashSet<string>(StringComparer.Ordinal);
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<TileCategory>();
        foreach (var category in categories ?? [])
        {
            if (category is null || string.IsNullOrWhiteSpace(category.Name) || !categoryIds.Add(category.Id)) continue;
            var ids = new List<string>();
            foreach (string id in ValidIds(category.TileIds))
                if (known.Contains(id) && assigned.Add(id)) ids.Add(id);
            result.Add(new TileCategory { Id = category.Id, Name = category.Name, TileIds = ids });
        }
        return result;
    }

    /// <summary>
    /// User categories appear in their saved order, followed by actions without a category.
    /// TileOrder controls action order within every group, and newly added actions appear
    /// unassigned in catalog order. Categories never hide or remove an action.
    /// </summary>
    public static IReadOnlyList<TileGroup> BuildGroups(AppSettings settings, IEnumerable<string>? catalogIds)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var catalog = ValidIds(catalogIds).Distinct(StringComparer.Ordinal).ToList();
        var order = TileOrdering.Normalize(ValidIds(settings.TileOrder), catalog);
        var hidden = ValidIds(settings.HiddenTiles).ToHashSet(StringComparer.Ordinal);
        hidden.UnionWith(ValidIds(settings.DisabledModules));
        var visible = order.Where(id => !hidden.Contains(id)).ToList();
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        var groups = new List<TileGroup>();
        foreach (var category in NormalizeCategories(settings.TileCategories, catalog))
        {
            var membership = category.TileIds.ToHashSet(StringComparer.Ordinal);
            assigned.UnionWith(membership);
            var ids = visible.Where(membership.Contains).ToList();
            if (ids.Count != 0) groups.Add(new(category.Id, category.Name, ids));
        }
        var unassigned = visible.Where(id => !assigned.Contains(id)).ToList();
        if (unassigned.Count != 0) groups.Add(new(null, string.Empty, unassigned));
        return groups;
    }

    private static IEnumerable<string> ValidIds(IEnumerable<string>? ids)
        => (ids ?? []).Where(id => !string.IsNullOrWhiteSpace(id));
}
