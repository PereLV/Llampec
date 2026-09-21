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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartupRefreshRepairsAnOldLocationWhetherOrNotItsExecutableStillExists(bool oldExecutableExists)
    {
        string path = @"Software\Llampec\Tests\" + Guid.NewGuid();
        string oldExecutable = oldExecutableExists
            ? Environment.ProcessPath!
            : Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "Llampec.exe");
        Assert.Equal(oldExecutableExists, File.Exists(oldExecutable));
        const string currentExecutable = @"C:\Apps with spaces\Llampec.exe";
        string oldCommand = StartupRegistration.BuildCommand(oldExecutable);
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(path);
            key.SetValue("Llampec", oldCommand, RegistryValueKind.String);
            key.SetValue("Other app", "unchanged", RegistryValueKind.String);
            var startup = new StartupRegistration(currentExecutable, path);

            Assert.False(startup.IsRegistered);
            Assert.Equal(oldCommand, key.GetValue("Llampec"));
            Assert.True(startup.RefreshExistingRegistration());
            Assert.True(startup.IsRegistered);
            Assert.Equal(StartupRegistration.BuildCommand(currentExecutable), key.GetValue("Llampec"));
            Assert.Equal(RegistryValueKind.String, key.GetValueKind("Llampec"));
            Assert.False(startup.RefreshExistingRegistration());
            Assert.Equal("unchanged", key.GetValue("Other app"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
    }

    [Fact]
    public void StartupRefreshDoesNotCreateAMissingRegistryKey()
    {
        string path = @"Software\Llampec\Tests\" + Guid.NewGuid();
        try
        {
            var startup = new StartupRegistration(@"C:\Apps\Llampec.exe", path);

            Assert.False(startup.RefreshExistingRegistration());
            Assert.False(startup.IsRegistered);
            using var key = Registry.CurrentUser.OpenSubKey(path);
            Assert.Null(key);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
    }

    [Theory]
    [InlineData(null, RegistryValueKind.String)]
    [InlineData("", RegistryValueKind.String)]
    [InlineData(" \t ", RegistryValueKind.String)]
    [InlineData(42, RegistryValueKind.DWord)]
    public void StartupRefreshDoesNotEnableMissingOrInvalidRegistrations(object? storedValue, RegistryValueKind kind)
    {
        string path = @"Software\Llampec\Tests\" + Guid.NewGuid();
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(path);
            if (storedValue is not null) key.SetValue("Llampec", storedValue, kind);
            key.SetValue("Other app", "unchanged", RegistryValueKind.String);
            var startup = new StartupRegistration(@"C:\Apps\Llampec.exe", path);

            Assert.False(startup.IsRegistered);
            Assert.False(startup.RefreshExistingRegistration());
            Assert.False(startup.IsRegistered);
            Assert.Equal(storedValue, key.GetValue("Llampec"));
            if (storedValue is not null) Assert.Equal(kind, key.GetValueKind("Llampec"));
            Assert.Equal("unchanged", key.GetValue("Other app"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartupRefreshLeavesTheCurrentCommandUntouchedIgnoringCase(bool changeCase)
    {
        string path = @"Software\Llampec\Tests\" + Guid.NewGuid();
        const string executable = @"C:\Apps with spaces\Llampec.exe";
        string command = StartupRegistration.BuildCommand(executable);
        if (changeCase) command = command.ToUpperInvariant();
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(path);
            key.SetValue("Llampec", command, RegistryValueKind.String);
            var startup = new StartupRegistration(executable, path);

            Assert.True(startup.IsRegistered);
            Assert.False(startup.RefreshExistingRegistration());
            Assert.Equal(command, key.GetValue("Llampec"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
    }

    [Fact]
    public void DisablingStartupRemainsDisabledAfterRefreshAndPreservesOtherValues()
    {
        string path = @"Software\Llampec\Tests\" + Guid.NewGuid();
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(path);
            key.SetValue("Other app", "unchanged", RegistryValueKind.String);
            var startup = new StartupRegistration(@"C:\Apps\Llampec.exe", path);
            startup.SetRegistered(true);

            startup.SetRegistered(false);

            Assert.False(startup.RefreshExistingRegistration());
            Assert.False(startup.IsRegistered);
            Assert.Null(key.GetValue("Llampec"));
            Assert.Equal("unchanged", key.GetValue("Other app"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
    }
}
