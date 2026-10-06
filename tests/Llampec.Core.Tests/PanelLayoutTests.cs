using System.Text.Json;
using Llampec.Settings;
using Xunit;

namespace Llampec.Tests;

public sealed class PanelLayoutTests
{
    [Fact]
    public void OlderSettingsKeepThreeColumnsAndFlatSavedOrderAndHiddenTiles()
    {
        const string json = """{"tileOrder":["theme","hdr"],"hiddenTiles":["hdr"]}""";
        var settings = Deserialize(json);
        Assert.Equal(3, settings.TileColumns);
        Assert.Empty(settings.TileCategories);
        Assert.Equal(new[] { "theme", "hdr" }, settings.TileOrder);
        Assert.Equal(new[] { "hdr" }, settings.HiddenTiles);
        var group = Assert.Single(TileLayout.BuildGroups(settings, ["hdr", "theme", "fullscreen"]));
        Assert.True(group.IsUnassigned);
        Assert.Equal(new[] { "theme", "fullscreen" }, group.TileIds);
    }

    [Theory]
    [InlineData(-100, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(6, 6)]
    [InlineData(100, 6)]
    public void ColumnCountIsNormalizedDuringDeserialization(int stored, int expected)
        => Assert.Equal(expected, Deserialize($"{{\"tileColumns\":{stored}}}").TileColumns);

    [Theory]
    [InlineData("null")]
    [InlineData("\"invalid\"")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("2.5")]
    public void InvalidColumnValueDoesNotLoseExistingTilePreferences(string stored)
    {
        var settings = Deserialize($"{{\"tileColumns\":{stored},\"tileOrder\":[\"theme\"],\"hiddenTiles\":[\"hdr\"]}}");
        Assert.Equal(3, settings.TileColumns);
        Assert.Equal("theme", Assert.Single(settings.TileOrder));
        Assert.Equal("hdr", Assert.Single(settings.HiddenTiles));
    }

    [Fact]
    public void NullCollectionsAndCategoryFieldsDoNotBreakLayout()
    {
        const string json = """{"tileOrder":null,"hiddenTiles":null,"tileCategories":[null,{"id":null,"name":null,"tileIds":null}]}""";
        var settings = Deserialize(json);
        Assert.Empty(settings.TileOrder); Assert.Empty(settings.HiddenTiles);
        Assert.Empty(TileLayout.NormalizeCategories(settings.TileCategories, ["theme"]));
        Assert.Equal("theme", Assert.Single(Assert.Single(TileLayout.BuildGroups(settings, ["theme"])).TileIds));
        Assert.Empty(Deserialize("""{"tileCategories":null}""").TileCategories);
    }

    [Fact]
    public void NormalizationDropsStaleDuplicateAndNullIdsAndFirstCategoryWins()
    {
        var categories = new List<TileCategory?>
        {
            new() { Id = "first", Name = "  Work  ", TileIds = ["theme", "theme", "stale", null!, ""] },
            null,
            new() { Id = "second", Name = "Second", TileIds = ["theme", "hdr"] },
            new() { Id = "first", Name = "Duplicate identity", TileIds = ["fullscreen"] },
            new() { Id = "blank", Name = " ", TileIds = ["fullscreen"] }
        };
        var normalized = TileLayout.NormalizeCategories(categories, ["theme", "hdr", "fullscreen"]);
        Assert.Equal(new[] { "first", "second" }, normalized.Select(category => category.Id));
        Assert.Equal("Work", normalized[0].Name);
        Assert.Equal(new[] { "theme" }, normalized[0].TileIds);
        Assert.Equal(new[] { "hdr" }, normalized[1].TileIds);
        normalized[0].TileIds.Clear();
        Assert.Equal(5, categories[0]!.TileIds.Count); // Editing a draft cannot mutate saved settings.
    }

    [Fact]
    public void VisibleGroupsFollowCategoryOrderAndGlobalActionOrder()
    {
        var settings = new AppSettings
        {
            TileOrder = ["fullscreen", "theme", "hdr", "rotation"],
            TileCategories =
            [
                new() { Id = "displays", Name = "Screens", TileIds = ["hdr", "fullscreen"] },
                new() { Id = "appearance", Name = "Appearance", TileIds = ["theme"] }
            ]
        };
        var groups = TileLayout.BuildGroups(settings, ["hdr", "theme", "fullscreen", "rotation", "new-action"]);
        Assert.Equal(new string?[] { "displays", "appearance", null }, groups.Select(group => group.CategoryId));
        Assert.Equal(new[] { "fullscreen", "hdr" }, groups[0].TileIds);
        Assert.Equal(new[] { "theme" }, groups[1].TileIds);
        Assert.Equal(new[] { "rotation", "new-action" }, groups[2].TileIds);
        Assert.Equal(5, groups.SelectMany(group => group.TileIds).Distinct().Count());
    }

    [Fact]
    public void HiddenAssignmentsRemainSavedAndEmptyHeadingsAreOmitted()
    {
        var settings = new AppSettings
        {
            HiddenTiles = ["hdr"],
            TileCategories = [new() { Id = "screens", Name = "Screens", TileIds = ["hdr"] }]
        };
        var normalized = TileLayout.NormalizeCategories(settings.TileCategories, ["hdr", "theme"]);
        Assert.Equal("hdr", Assert.Single(Assert.Single(normalized).TileIds));
        var groups = TileLayout.BuildGroups(settings, ["hdr", "theme"]);
        Assert.True(Assert.Single(groups).IsUnassigned);
        Assert.Equal("theme", Assert.Single(groups[0].TileIds));
        Assert.Equal("hdr", Assert.Single(settings.TileCategories[0].TileIds));
        settings.HiddenTiles.Clear();
        Assert.Equal("screens", TileLayout.BuildGroups(settings, ["hdr", "theme"])[0].CategoryId);
    }

    [Fact]
    public void RenamingOrDeletingCategoryNeverLosesActionsOrChangesSavedGlobalOrder()
    {
        var category = new TileCategory { Name = "Old", TileIds = ["hdr", "theme"] };
        string id = category.Id;
        var settings = new AppSettings { TileOrder = ["theme", "hdr"], TileCategories = [category] };
        category.Name = "Renamed";
        var renamed = Assert.Single(TileLayout.BuildGroups(settings, ["hdr", "theme"]));
        Assert.Equal(id, renamed.CategoryId); Assert.Equal("Renamed", renamed.Name);
        Assert.Equal(new[] { "theme", "hdr" }, renamed.TileIds);
        settings.TileCategories.Clear();
        var unassigned = Assert.Single(TileLayout.BuildGroups(settings, ["hdr", "theme"]));
        Assert.True(unassigned.IsUnassigned);
        Assert.Equal(new[] { "theme", "hdr" }, unassigned.TileIds);
        Assert.Equal(new[] { "theme", "hdr" }, settings.TileOrder);
    }

    [Fact]
    public void CategoryIdentityAndHiddenMembershipSurviveJsonRoundTrip()
    {
        var category = new TileCategory { Name = "My tools", TileIds = ["hdr", "theme"] };
        var settings = new AppSettings { TileColumns = 5, TileCategories = [category], HiddenTiles = ["hdr"] };
        string json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);
        var restored = Deserialize(json);
        Assert.Equal(5, restored.TileColumns);
        Assert.Equal(category.Id, restored.TileCategories[0].Id);
        Assert.Equal("My tools", restored.TileCategories[0].Name);
        Assert.Equal(new[] { "hdr", "theme" }, restored.TileCategories[0].TileIds);
        Assert.Equal(new[] { "hdr" }, restored.HiddenTiles);
    }

    private static AppSettings Deserialize(string json)
        => JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)!;
}
