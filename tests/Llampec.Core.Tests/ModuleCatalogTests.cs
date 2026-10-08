using Llampec.Actions;
using Xunit;

namespace Llampec.Tests;

public sealed class ModuleCatalogTests
{
    [Fact]
    public void StaticCatalogKeepsEveryExistingModuleIdAndOrderIncludingInactiveServices()
    {
        Assert.Equal(new[]
        {
            "hdr", "display-off", "theme", "projection", "taskbar-autohide", "touch-taskbar", "caffeine",
            "always-on-top", "rotation", "screenshot", "fullscreen", "power", "mouse", "camera"
        }, ModuleCatalog.All.Select(module => module.Id));
        Assert.Equal(14, ModuleCatalog.All.Select(module => module.Id).Distinct().Count());
        Assert.All(ModuleCatalog.All, module =>
        {
            Assert.False(string.IsNullOrWhiteSpace(module.Title));
            Assert.Equal(1, module.Glyph.Length);
            Assert.InRange(module.Glyph[0], '\uE000', '\uF8FF');
            if (module.GlyphBadge is not null)
            {
                Assert.Equal(1, module.GlyphBadge.Length);
                Assert.InRange(module.GlyphBadge[0], '\uE000', '\uF8FF');
            }
        });
        Assert.Equal("\uE708", Assert.Single(ModuleCatalog.All, module => module.Id == "theme").GlyphBadge);
        Assert.Equal(new[] { "hdr", "theme", "projection", "caffeine", "always-on-top", "rotation", "fullscreen", "power", "mouse" },
            ModuleCatalog.All.Where(module => module.HasSubpage).Select(module => module.Id));
    }

    [Fact]
    public void MetadataCollectionCannotBeModifiedByAnEditor()
    {
        var collection = Assert.IsAssignableFrom<ICollection<ModuleDescriptor>>(ModuleCatalog.All);
        Assert.True(collection.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => collection.Clear());
        Assert.Equal(14, ModuleCatalog.All.Count);
    }
}
