namespace Llampec.Platform;

/// <summary>Reserves a replacement before releasing the working shortcut or saving preferences.</summary>
public sealed class ConfigurableHotkey(int firstId, int secondId,
    Func<int, Hotkey, bool> register, Action<int> unregister) : IDisposable
{
    private Hotkey? _registered;
    public int? RegisteredId { get; private set; }
    public string? Error { get; private set; }

    public bool Initialize(string? text) => TrySet(text, _ => true);

    public bool TrySet(string? text, Func<string, bool> save)
    {
        Error = null;
        Hotkey? candidate = null;
        string normalized = "";
        if (!string.IsNullOrWhiteSpace(text))
        {
            if (!Hotkey.TryParse(text, out var parsed))
            {
                Error = "Enter a valid shortcut or leave it empty to disable it.";
                return false;
            }
            candidate = parsed;
            normalized = parsed.ToString();
        }

        int? nextId = RegisteredId;
        bool newlyRegistered = false;
        if (candidate is { } next && (_registered != candidate || RegisteredId is null))
        {
            nextId = RegisteredId == firstId ? secondId : firstId;
            if (!register(nextId.Value, next))
            {
                Error = "Shortcut is already in use.";
                return false;
            }
            newlyRegistered = true;
        }

        if (!save(normalized))
        {
            if (newlyRegistered) unregister(nextId!.Value);
            Error = "Could not save settings.";
            return false;
        }

        if (RegisteredId is { } previousId && (newlyRegistered || candidate is null))
            unregister(previousId);
        _registered = candidate;
        RegisteredId = candidate is null ? null : nextId;
        return true;
    }

    public void Dispose()
    {
        if (RegisteredId is { } id) unregister(id);
        RegisteredId = null;
        _registered = null;
    }
}
