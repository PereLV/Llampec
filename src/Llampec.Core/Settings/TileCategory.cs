namespace Llampec.Settings;

/// <summary>A user-named group. Renaming changes its label, never its identity or action membership.</summary>
public sealed class TileCategory
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = string.Empty;
    private List<string> _tileIds = [];

    public string Id
    {
        get => _id;
        set => _id = string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value.Trim();
    }

    public string Name
    {
        get => _name;
        set => _name = value?.Trim() ?? string.Empty;
    }

    public List<string> TileIds
    {
        get => _tileIds;
        set => _tileIds = value ?? [];
    }
}

/// <summary>A visible group. A null category id identifies the actions without a user category.</summary>
public sealed record TileGroup(string? CategoryId, string Name, IReadOnlyList<string> TileIds)
{
    public bool IsUnassigned => CategoryId is null;
}
