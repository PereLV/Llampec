using System.Windows.Threading;
using Llampec.Actions;
using Llampec.Settings;

namespace Llampec.ViewModels;

/// <summary>The panel: an ordered list of tiles and, optionally, an open sub-page.</summary>
public sealed class FlyoutViewModel : ObservableObject
{
    private TileViewModel? _subpage;

    public FlyoutViewModel(IReadOnlyList<IQuickAction> actions, AppSettings settings)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var ordered = OrderAndFilter(actions, settings);
        Tiles = ordered.Select(a => new TileViewModel(a, dispatcher, OpenSubpage)).ToList();
        BackCommand = new RelayCommand(CloseSubpage);
    }

    public IReadOnlyList<TileViewModel> Tiles { get; }

    /// <summary>The tile whose sub-page is open, or null when the main grid is shown.</summary>
    public TileViewModel? Subpage
    {
        get => _subpage;
        private set
        {
            if (Set(ref _subpage, value))
            {
                OnPropertyChanged(nameof(IsSubpageOpen));
            }
        }
    }

    public bool IsSubpageOpen => _subpage is not null;

    public RelayCommand BackCommand { get; }

    /// <summary>Called when the panel opens: every action re-reads its state.</summary>
    public void RefreshAll()
    {
        foreach (var tile in Tiles)
        {
            tile.Refresh();
        }
    }

    public void OpenSubpage(TileViewModel tile) => Subpage = tile;

    public void CloseSubpage() => Subpage = null;

    private static List<IQuickAction> OrderAndFilter(IReadOnlyList<IQuickAction> actions, AppSettings settings)
    {
        var hidden = new HashSet<string>(settings.HiddenTiles, StringComparer.Ordinal);
        var byId = actions.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var result = new List<IQuickAction>(actions.Count);

        foreach (string id in settings.TileOrder)
        {
            if (byId.Remove(id, out var action) && !hidden.Contains(id))
            {
                result.Add(action);
            }
        }

        result.AddRange(actions.Where(a => byId.ContainsKey(a.Id) && !hidden.Contains(a.Id)));
        return result;
    }
}
