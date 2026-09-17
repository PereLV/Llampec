using Microsoft.UI.Dispatching;
using Llampec.Actions;
using Llampec.Settings;

namespace Llampec.ViewModels;

/// <summary>Presents one <see cref="IQuickAction"/> as a tile. Marshals action events to the UI thread.</summary>
public sealed class TileViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcher;
    private readonly Action<TileViewModel>? _openSubpage;
    private readonly Action? _closePanel;

    public TileViewModel(IQuickAction action, DispatcherQueue dispatcher, Action<TileViewModel>? openSubpage, Action? closePanel, bool isRadioItem = false)
    {
        Action = action;
        _dispatcher = dispatcher;
        _openSubpage = openSubpage;
        _closePanel = closePanel;
        IsRadioItem = isRadioItem;

        ToggleCommand = new RelayCommand(Execute);
        OpenSubpageCommand = new RelayCommand(() => _openSubpage?.Invoke(this), () => HasSubpage);

        SubTiles = action.SubActions.Select(a => new TileViewModel(a, dispatcher, null, null, action.SubActionsAreExclusive)).ToList();
        action.Changed += OnActionChanged;
    }

    public IQuickAction Action { get; }

    /// <summary>True when this tile is one of a parent's <see cref="IQuickAction.SubActionsAreExclusive"/>
    /// sub-actions -- the sub-page renders it as a radio-button row instead of a toggle switch.</summary>
    public bool IsRadioItem { get; }

    public string Id => Action.Id;
    public string Title => UiText.Get(Action.Title);
    public string? Subtitle
    {
        get
        {
            if (Action is Llampec.Actions.Caffeine.CaffeineAction caffeine) return caffeine.StatusText;
            if (Id == "theme") return ThemeScheduleStatus.Format(!IsOn, App.Current.Scheduler?.Plan, DateTimeOffset.Now, UiText.Language);
            if (Action.Subtitle is not { } text) return null;
            string[] parts = text.Split(" of ", StringSplitOptions.None);
            return parts.Length == 2 && int.TryParse(parts[0], out int on) && int.TryParse(parts[1], out int total)
                ? UiText.Format("{0} of {1}", on, total) : UiText.Get(text);
        }
    }
    /// <summary>Fits the narrow grid; full target/time remains in Subtitle for tooltips and accessibility.</summary>
    public string? CompactSubtitle => Id == "theme"
        ? ThemeScheduleStatus.Format(!IsOn, App.Current.Scheduler?.Plan, DateTimeOffset.Now, UiText.Language, compact: true)
        : Subtitle;
    public string Glyph => Action.Glyph;
    public string? GlyphBadge => Action.GlyphBadge;
    public bool HasGlyphBadge => !string.IsNullOrEmpty(Action.GlyphBadge);
    public bool IsAvailable => Action.IsAvailable;
    public bool IsBusy => Action.IsBusy;
    public bool IsOn => Action.State == ActionState.On;
    public bool IsMixed => Action.State == ActionState.Mixed;
    public bool IsButton => Action.Kind == ActionKind.Button;
    public bool HasSubpage => Action.Kind == ActionKind.ToggleWithSubpage || Id == "theme";
    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);

    public IReadOnlyList<TileViewModel> SubTiles { get; }

    public RelayCommand ToggleCommand { get; }
    public RelayCommand OpenSubpageCommand { get; }

    private void Execute()
    {
        if (IsButton)
        {
            // Like the native panel: a one-shot action closes the panel first (and "Turn off display"
            // needs the panel gone before the screen goes dark).
            _closePanel?.Invoke();
        }

        _ = ExecuteAndRefreshThemeAsync();
    }

    private async Task ExecuteAndRefreshThemeAsync()
    {
        // Awaiting without ConfigureAwait(false) resumes on this (UI) thread, same as before.
        await Action.ExecuteAsync(CancellationToken.None);

        // A tile (e.g. "Dark mode") may have just changed the system theme. WM_SETTINGCHANGE should
        // broadcast that, but the broadcast is best-effort, and even when it arrives this panel is the
        // one open right now -- don't wait on it for our own repaint. Cheap no-op when nothing changed.
        App.Current.RefreshTheme();
    }

    public void Refresh()
    {
        Action.Refresh();
        foreach (var sub in SubTiles)
        {
            sub.Action.Refresh();
        }
        // Computed text (for example a scheduled countdown) can change independently
        // of the action's state and therefore without an Action.Changed notification.
        RaiseAll();
    }

    private void OnActionChanged(object? sender, EventArgs e)
    {
        if (_dispatcher.HasThreadAccess)
        {
            RaiseAll();
        }
        else
        {
            _dispatcher.TryEnqueue(RaiseAll);
        }
    }

    private void RaiseAll()
    {
        // Cheap and simple: the tile re-reads every bound property.
        OnPropertyChanged(string.Empty);
    }
}
