namespace Llampec.Settings;

public static class TileOrdering
{
    /// <summary>Keep valid distinct saved ids; append newly introduced actions in catalogue order.</summary>
    public static List<string> Normalize(IEnumerable<string> saved, IEnumerable<string> available)
    {
        var defaults = available.Distinct(StringComparer.Ordinal).ToList();
        var remaining = defaults.ToHashSet(StringComparer.Ordinal);
        var result = new List<string>(defaults.Count);
        foreach (string id in saved) if (remaining.Remove(id)) result.Add(id);
        result.AddRange(defaults.Where(remaining.Contains));
        return result;
    }
}
