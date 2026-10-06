using System.Text.Json;
using Llampec.Settings;
using Xunit;

namespace Llampec.Tests;

public sealed class PanelLayoutDraftTests
{
    [Fact]
    public void DraftAndAppliedSettingsNeverShareMutableLayoutReferences()
    {
        var settings = new AppSettings
        {
            TileColumns = 4, TileOrder = ["a", "b"], HiddenTiles = ["b"], DisabledModules = ["a"],
            TileCategories = [new() { Id = "group", Name = "Original", TileIds = ["a"] }]
        };
        var draft = new PanelLayoutDraft(settings, ["a", "b"]);
        draft.Columns = 6;
        draft.Categories[0].Name = "Draft";
        draft.Categories[0].TileIds.Clear();
        draft.Order.Reverse();
        draft.HiddenTiles.Clear();
        draft.DisabledModules.Clear();
        Assert.Equal(4, settings.TileColumns);
        Assert.Equal("Original", settings.TileCategories[0].Name);
        Assert.Equal("a", Assert.Single(settings.TileCategories[0].TileIds));
        Assert.Equal(new[] { "a", "b" }, settings.TileOrder);
        Assert.Equal("b", Assert.Single(settings.HiddenTiles));
        Assert.Equal("a", Assert.Single(settings.DisabledModules));

        draft.ApplyTo(settings);
        Assert.Equal(6, settings.TileColumns);
        Assert.Equal("Draft", settings.TileCategories[0].Name);
        draft.Categories[0].Name = "Later";
        draft.Order.Clear();
        draft.DisabledModules.Add("a");
        Assert.Equal("Draft", settings.TileCategories[0].Name);
        Assert.Equal(new[] { "b", "a" }, settings.TileOrder);
        Assert.Empty(settings.DisabledModules);
        settings.TileCategories[0].TileIds.Add("b");
        Assert.Empty(draft.Categories[0].TileIds);
    }

    [Fact]
    public void EditorAlwaysIncludesEmptyCategoryAndUnassignedDropDestinations()
    {
        var settings = new AppSettings
        {
            DisabledModules = ["a"],
            TileCategories = [new() { Id = "empty", Name = "Empty" }]
        };
        var groups = new PanelLayoutDraft(settings, ["a"]).BuildGroups();
        Assert.Equal(new string?[] { "empty", null }, groups.Select(group => group.CategoryId));
        Assert.All(groups, group => Assert.Empty(group.TileIds));
        Assert.True(groups[^1].IsUnassigned);
        Assert.Empty(TileLayout.BuildGroups(settings, ["a"])); // Main panel omits empty headings.
    }

    [Fact]
    public void TileMovesUseTheDestinationGroupIndexAndPreserveEveryOtherGroupOrder()
    {
        var draft = new PanelLayoutDraft(new AppSettings
        {
            TileOrder = ["a", "b", "c", "d", "e"],
            TileCategories =
            [
                new() { Id = "first", Name = "First", TileIds = ["a", "c"] },
                new() { Id = "second", Name = "Second", TileIds = ["d"] }
            ]
        }, ["a", "b", "c", "d", "e"]);
        draft.MoveTile("e", "first", 1);
        AssertGroup(draft, "first", "a", "e", "c");
        AssertGroup(draft, null, "b");
        draft.MoveTile("a", "second", 0);
        AssertGroup(draft, "first", "e", "c");
        AssertGroup(draft, "second", "a", "d");
        draft.MoveTile("d", null, 0);
        AssertGroup(draft, null, "d", "b");
        draft.MoveTile("b", null, 0);
        AssertGroup(draft, null, "b", "d");
        draft.MoveTile("d", "first", 100);
        AssertGroup(draft, "first", "e", "c", "d");
        AssertGroup(draft, "second", "a");
        AssertGroup(draft, null, "b");
        var all = draft.BuildGroups().SelectMany(group => group.TileIds).ToArray();
        Assert.Equal(5, all.Length);
        Assert.Equal(5, all.Distinct().Count());
        var restored = new AppSettings();
        draft.ApplyTo(restored);
        Assert.Equal(new[] { "e", "c", "d" }, TileLayout.BuildGroups(restored, all)[0].TileIds);
    }

    [Fact]
    public void RemovingCategoryAppendsItsVisualOrderAfterExistingUnassignedTiles()
    {
        var draft = new PanelLayoutDraft(new AppSettings
        {
            TileOrder = ["a1", "u1", "a2", "u2", "other"],
            TileCategories =
            [
                new() { Id = "remove", Name = "Remove", TileIds = ["a2", "a1"] },
                new() { Id = "keep", Name = "Keep", TileIds = ["other"] }
            ]
        }, ["a1", "a2", "u1", "u2", "other"]);
        AssertGroup(draft, "remove", "a1", "a2");
        draft.RemoveCategory("remove");
        AssertGroup(draft, "keep", "other");
        AssertGroup(draft, null, "u1", "u2", "a1", "a2");
        Assert.Equal(new[] { "u1", "u2", "other", "a1", "a2" }, draft.Order);
    }

    [Fact]
    public void CategoryMovesAndRenamesKeepStableIdentityAndRejectAmbiguousNames()
    {
        var draft = new PanelLayoutDraft(new AppSettings(), ["a"]);
        var first = draft.AddCategory("  First  ");
        var second = draft.AddCategory("Second");
        draft.MoveCategory(second.Id, 0);
        Assert.Equal(new[] { second.Id, first.Id }, draft.Categories.Select(category => category.Id));
        draft.RenameCategory(first.Id, "Renamed");
        Assert.Same(first, draft.Categories[1]);
        Assert.Equal("Renamed", first.Name);
        Assert.Throws<ArgumentException>(() => draft.AddCategory(" renamed "));
        Assert.Throws<ArgumentException>(() => draft.RenameCategory(first.Id, "SECOND"));
        Assert.Throws<ArgumentException>(() => draft.AddCategory(" "));
    }

    [Fact]
    public void DisablingIsExplicitAndEnablingAppendsWithoutACategoryWhilePreservingConfiguration()
    {
        var settings = new AppSettings
        {
            TileOrder = ["fullscreen", "theme", "hdr"],
            TileCategories = [new() { Id = "screens", Name = "Screens", TileIds = ["fullscreen"] }]
        };
        settings.Fullscreen.Hotkey = "Alt+F10";
        var config = settings.Fullscreen;
        var draft = new PanelLayoutDraft(settings, ["hdr", "theme", "fullscreen"]);
        draft.DisableModule("fullscreen");
        draft.DisableModule("fullscreen");
        Assert.Equal("fullscreen", Assert.Single(draft.DisabledModules));
        AssertGroup(draft, "screens");
        AssertGroup(draft, null, "theme", "hdr");
        Assert.Empty(settings.DisabledModules);
        draft.ApplyTo(settings);
        Assert.Same(config, settings.Fullscreen);
        Assert.Equal("Alt+F10", settings.Fullscreen.Hotkey);
        Assert.DoesNotContain("fullscreen", TileLayout.BuildGroups(settings, ["hdr", "theme", "fullscreen"])
            .SelectMany(group => group.TileIds));
        draft.EnableModule("fullscreen");
        Assert.Empty(draft.DisabledModules);
        AssertGroup(draft, "screens");
        AssertGroup(draft, null, "theme", "hdr", "fullscreen");
        draft.ApplyTo(settings);
        Assert.Same(config, settings.Fullscreen);
        Assert.Equal("Alt+F10", settings.Fullscreen.Hotkey);
    }

    [Fact]
    public void OpeningAndSavingLegacyHiddenTilesNeverDisablesTheirModules()
    {
        var settings = Deserialize("""{"hiddenTiles":["a"],"tileCategories":[{"id":"old","name":"Old","tileIds":["a"]}]}""");
        var draft = new PanelLayoutDraft(settings, ["a", "b"]);
        Assert.Empty(draft.DisabledModules);
        AssertGroup(draft, "old");
        AssertGroup(draft, null, "b");
        draft.ApplyTo(settings);
        Assert.Empty(settings.DisabledModules);
        Assert.Equal("a", Assert.Single(settings.HiddenTiles));
        Assert.Equal("a", Assert.Single(settings.TileCategories[0].TileIds));
        draft.EnableModule("a");
        Assert.Empty(draft.HiddenTiles);
        AssertGroup(draft, "old");
        AssertGroup(draft, null, "b", "a");
        draft.ApplyTo(settings);
        Assert.Empty(settings.DisabledModules);
        Assert.Empty(settings.HiddenTiles);
    }

    [Fact]
    public void EnablingRemovesBothLegacyHiddenAndExplicitDisabledFlagsOnlyForThatModule()
    {
        var draft = new PanelLayoutDraft(new AppSettings
        {
            TileOrder = ["a", "b", "c"], HiddenTiles = ["a", "c"], DisabledModules = ["a", "b"],
            TileCategories = [new() { Id = "group", Name = "Group", TileIds = ["a"] }]
        }, ["a", "b", "c"]);
        draft.EnableModule("a");
        Assert.Equal("b", Assert.Single(draft.DisabledModules));
        Assert.Equal("c", Assert.Single(draft.HiddenTiles));
        AssertGroup(draft, "group");
        AssertGroup(draft, null, "a");
        Assert.Equal(new[] { "b", "c", "a" }, draft.Order);
    }

    [Fact]
    public void NullMalformedAndFutureIdsAreSafeWithoutConvertingLegacyVisibility()
    {
        var settings = Deserialize("""{"disabledModules":null,"hiddenTiles":null,"tileOrder":null,"tileCategories":null}""");
        Assert.Empty(settings.DisabledModules);
        var draft = new PanelLayoutDraft(settings, ["a", null!, "a", "", "b"]);
        draft.Order.InsertRange(0, [null!, "stale", "a", "a"]);
        draft.DisabledModules.AddRange([null!, "", "future-module", "future-module"]);
        draft.HiddenTiles.AddRange([null!, "", "future-hidden", "future-hidden"]);
        draft.ApplyTo(settings);
        Assert.Equal(new[] { "a", "b" }, settings.TileOrder);
        Assert.Equal("future-module", Assert.Single(settings.DisabledModules));
        Assert.Equal("future-hidden", Assert.Single(settings.HiddenTiles));
        var restored = Deserialize(JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
        Assert.Equal(settings.DisabledModules, restored.DisabledModules);
        Assert.Equal(settings.HiddenTiles, restored.HiddenTiles);
    }

    [Fact]
    public void ApplyChangesOnlyPanelPreferencesAndRejectsInvalidNamesBeforeWritingAnything()
    {
        var settings = new AppSettings { Theme = AppTheme.Dark, Language = "ca", Hotkey = "Alt+F11", EnableLogFile = true };
        var caffeine = settings.Caffeine;
        var alwaysOnTop = settings.AlwaysOnTop;
        var fullscreen = settings.Fullscreen;
        var logitech = settings.Logitech;
        var schedule = settings.ThemeSchedule;
        var draft = new PanelLayoutDraft(settings, ["a"]);
        var first = draft.AddCategory("First");
        var second = draft.AddCategory("Second");
        draft.Columns = 100;
        second.Name = "FIRST";
        Assert.False(draft.TryValidate(out string? error));
        Assert.Equal("Category names must be distinct.", error);
        Assert.Throws<InvalidOperationException>(() => draft.ApplyTo(settings));
        Assert.Equal(3, settings.TileColumns);
        Assert.Empty(settings.TileCategories);
        second.Name = "";
        Assert.False(draft.TryValidate(out error));
        Assert.Equal("Enter a category name.", error);
        second.Name = "Second";
        Assert.True(draft.TryValidate(out error));
        Assert.Null(error);
        draft.ApplyTo(settings);
        Assert.Equal(6, settings.TileColumns);
        Assert.Equal(first.Id, settings.TileCategories[0].Id);
        Assert.Equal(AppTheme.Dark, settings.Theme);
        Assert.Equal("ca", settings.Language);
        Assert.Equal("Alt+F11", settings.Hotkey);
        Assert.True(settings.EnableLogFile);
        Assert.Same(caffeine, settings.Caffeine);
        Assert.Same(alwaysOnTop, settings.AlwaysOnTop);
        Assert.Same(fullscreen, settings.Fullscreen);
        Assert.Same(logitech, settings.Logitech);
        Assert.Same(schedule, settings.ThemeSchedule);
    }

    [Fact]
    public void UnknownMoveOrModuleCommandsCannotSilentlyDropAnExistingTile()
    {
        var draft = new PanelLayoutDraft(new AppSettings(), ["a", "b"]);
        Assert.Throws<ArgumentException>(() => draft.MoveTile("a", "missing-category", 0));
        Assert.Throws<ArgumentException>(() => draft.MoveTile("missing-module", null, 0));
        Assert.Throws<ArgumentException>(() => draft.DisableModule("missing-module"));
        Assert.Throws<ArgumentException>(() => draft.EnableModule("missing-module"));
        AssertGroup(draft, null, "a", "b");
    }

    private static void AssertGroup(PanelLayoutDraft draft, string? categoryId, params string[] expected)
        => Assert.Equal(expected, Assert.Single(draft.BuildGroups(), group => group.CategoryId == categoryId).TileIds);

    private static AppSettings Deserialize(string json)
        => JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)!;
}
