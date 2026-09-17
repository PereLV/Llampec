using Llampec.Platform;
using Llampec.Settings;
using Microsoft.Win32;
using Xunit;

namespace Llampec.Tests;

public class SettingsTests
{
    [Theory]
    [InlineData("es-ES", "es")]
    [InlineData("ca-ES-valencia", "ca")]
    [InlineData("en-GB", "en")]
    [InlineData("fr-FR", "en")]
    public void LanguageUsesSupportedBaseTag(string tag, string expected) => Assert.Equal(expected, UiText.ResolveLanguage(tag));

    [Fact]
    public void EveryUiKeyHasSpanishAndCatalanTranslations()
    {
        foreach (string key in UiText.Keys)
            foreach (string language in new[] { "es", "ca" })
                Assert.False(string.IsNullOrWhiteSpace(UiText.Translate(key, language)));
        Assert.Equal("Pantalla ACME", UiText.Translate("Pantalla ACME", "ca"));
        Assert.Equal("Mode fosc", UiText.Translate("Dark mode", "ca"));
    }

    [Fact]
    public void ReorderDropsDuplicatesAndStaleIdsAndAppendsNewActions()
        => Assert.Equal(new[] { "theme", "hdr", "new" }, TileOrdering.Normalize(["theme", "gone", "theme"], ["hdr", "theme", "new"]));

    [Fact]
    public void StartupCommandQuotesPathAndStartsSilently()
        => Assert.Equal("\"C:\\Apps with spaces\\Llampec.exe\" --background", StartupRegistration.BuildCommand(@"C:\Apps with spaces\Llampec.exe"));

    [Theory]
    [InlineData("relative.exe")]
    [InlineData("C:\\bad\"path.exe")]
    public void InvalidStartupPathsAreRejected(string path)
        => Assert.Throws<ArgumentException>(() => StartupRegistration.BuildCommand(path));

    [Fact]
    public void StartupCanBeEnabledAndDisabledWithoutTouchingRealStartup()
    {
        string path = @"Software\Llampec\Tests\" + Guid.NewGuid();
        try
        {
            var startup = new StartupRegistration(@"C:\Apps with spaces\Llampec.exe", path);
            Assert.False(startup.IsRegistered);
            startup.SetRegistered(true);
            Assert.True(startup.IsRegistered);
            using (var key = Registry.CurrentUser.OpenSubKey(path))
                Assert.Equal("\"C:\\Apps with spaces\\Llampec.exe\" --background", key!.GetValue("Llampec"));
            startup.SetRegistered(false);
            Assert.False(startup.IsRegistered);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
    }
}
