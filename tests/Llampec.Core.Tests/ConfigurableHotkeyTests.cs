using Llampec.Platform;
using Xunit;

namespace Llampec.Tests;

public class ConfigurableHotkeyTests
{
    [Fact]
    public void Conflict_preserves_existing_registration_and_does_not_save()
    {
        var active = new Dictionary<int, Hotkey>();
        using var binding = new ConfigurableHotkey(2, 3, (id, key) =>
        {
            if (key.VirtualKey == 'X') return false;
            return active.TryAdd(id, key);
        }, id => active.Remove(id));
        Assert.True(binding.Initialize("Ctrl+Alt+T"));
        bool saved = false;
        Assert.False(binding.TrySet("Ctrl+Alt+X", _ => saved = true));
        Assert.False(saved);
        Assert.Equal(2, binding.RegisteredId);
        Assert.Equal((uint)'T', Assert.Single(active).Value.VirtualKey);
    }

    [Fact]
    public void Save_failure_releases_candidate_and_retains_previous_shortcut()
    {
        var active = new Dictionary<int, Hotkey>();
        using var binding = new ConfigurableHotkey(2, 3, active.TryAdd, id => active.Remove(id));
        Assert.True(binding.Initialize("Ctrl+Alt+T"));
        Assert.False(binding.TrySet("Ctrl+Alt+P", _ => false));
        Assert.Equal("Could not save settings.", binding.Error);
        Assert.Equal((uint)'T', Assert.Single(active).Value.VirtualKey);
        Assert.False(binding.TrySet("", _ => false));
        Assert.Single(active);
    }

    [Fact]
    public void Replacement_is_acquired_before_save_and_old_binding_is_released_afterwards()
    {
        var active = new Dictionary<int, Hotkey>();
        using var binding = new ConfigurableHotkey(2, 3, active.TryAdd, id => active.Remove(id));
        Assert.True(binding.Initialize("Ctrl+Alt+T"));
        Assert.True(binding.TrySet("ctrl + alt + p", value =>
        {
            Assert.Equal("Ctrl+Alt+P", value);
            Assert.Equal(2, active.Count);
            return true;
        }));
        Assert.Equal(3, binding.RegisteredId);
        Assert.Equal((uint)'P', Assert.Single(active).Value.VirtualKey);
        Assert.True(binding.TrySet(" ", value => value.Length == 0));
        Assert.Null(binding.RegisteredId);
        Assert.Empty(active);
    }

    [Fact]
    public void Invalid_input_never_replaces_a_working_shortcut()
    {
        var active = new Dictionary<int, Hotkey>();
        using var binding = new ConfigurableHotkey(2, 3, active.TryAdd, id => active.Remove(id));
        Assert.True(binding.Initialize("Ctrl+Alt+T"));
        Assert.False(binding.TrySet("Ctrl+A+B", _ => throw new Exception("Must not save invalid input")));
        Assert.Equal((uint)'T', Assert.Single(active).Value.VirtualKey);
        binding.Dispose();
        Assert.Empty(active);
    }
}
