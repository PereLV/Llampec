using System.Windows.Threading;
using Llampec.Actions;

namespace Llampec.ViewModels;

/// <summary>Presents one <see cref="IQuickAction"/> as a tile. Marshals action events to the UI thread.</summary>
public sealed class TileViewModel : ObservableObject
{
    private readonly Dispatcher _dispatcher;
    private readonly Action<TileViewModel>? _openSubpage;
    private readonly Action? _closePanel;

    public TileViewModel(IQuickAction action, Dispatcher dispatcher, Action<TileViewModel>? openSubpage, Action? closePanel)
    {
        Action = action;
        _dispatcher = dispatcher;
        _openSubpage = openSubpage;
        _closePanel = closePanel;

        ToggleCommand = new RelayCommand(Execute);
        OpenSubpageCommand = new RelayCommand(() => _openSubpage?.Invoke(this), () => HasSubpage);

        SubTiles = action.SubActions.Select(a => new TileViewModel(a, dispatcher, null, null)).ToList();
        action.Changed += OnActionChanged;
    }

    public IQuickAction Action { get; }

    public string Id => Action.Id;
    public string Title => Action.Title;
    public string? Subtitle => Action.Subtitle;
    public string Glyph => Action.Glyph;
    public string? GlyphBadge => Action.GlyphBadge;
    public bool HasGlyphBadge => !string.IsNullOrEmpty(Action.GlyphBadge);
    public bool IsAvailable => Action.IsAvailable;
    public bool IsBusy => Action.IsBusy;
    public bool IsOn => Action.State == ActionState.On;
    public bool IsMixed => Action.State == ActionState.Mixed;
    public bool IsButton => Action.Kind == ActionKind.Button;
    public bool HasSubpage => Action.Kind == ActionKind.ToggleWithSubpage;
    public bool HasSubtitle => !string.IsNullOrEmpty(Action.Subtitle);

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

        _ = Action.ExecuteAsync(CancellationToken.None);
    }

    public void Refresh()
    {
        Action.Refresh();
        foreach (var sub in SubTiles)
        {
            sub.Action.Refresh();
        }
    }

    private void OnActionChanged(object? sender, EventArgs e)
    {
        if (_dispatcher.CheckAccess())
        {
            RaiseAll();
        }
        else
        {
            _dispatcher.BeginInvoke(RaiseAll);
        }
    }

    private void RaiseAll()
    {
        // Cheap and simple: the tile re-reads every bound property.
        OnPropertyChanged(string.Empty);
    }
}
